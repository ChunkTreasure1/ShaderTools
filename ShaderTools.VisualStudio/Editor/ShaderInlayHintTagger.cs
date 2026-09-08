using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Editor;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ShaderTools.VisualStudio.Editor
{
    // IntraTextAdornmentTag instances contain WPF elements and therefore must be
    // produced per view. Sharing one through an ITaggerProvider is unsupported.
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(LanguageClient.ShaderContentTypes.Glsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Hlsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Wgsl)]
    [TextViewRole(PredefinedTextViewRoles.Structured)]
    [TagType(typeof(IntraTextAdornmentTag))]
    [Name("ShaderTools.ParameterHints")]
    internal sealed class ShaderInlayHintTaggerProvider : IViewTaggerProvider
    {
        private readonly ShaderEditorBufferManager manager;
        private readonly IClassificationFormatMapService formatMapService;

        [ImportingConstructor]
        public ShaderInlayHintTaggerProvider(
            ShaderEditorBufferManager manager,
            IClassificationFormatMapService formatMapService)
        {
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
            this.formatMapService = formatMapService ?? throw new ArgumentNullException(nameof(formatMapService));
        }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (typeof(T) != typeof(IntraTextAdornmentTag) ||
                textView is not IWpfTextView wpfView ||
                !ReferenceEquals(textView.TextBuffer, buffer))
            {
                return null;
            }

            ShaderEditorBufferState state = manager.GetOrCreate(buffer);
            string documentUri = state.DocumentUri;
            ShaderInlayHintTagger tagger = new ShaderInlayHintTagger(
                wpfView,
                buffer,
                state,
                documentUri,
                formatMapService.GetClassificationFormatMap(textView));
            state.RegisterUriListener(tagger, documentUri);
            return (ITagger<T>)(object)tagger;
        }
    }

    internal sealed class ShaderInlayHintTagger : ITagger<IntraTextAdornmentTag>, IDisposable,
        IShaderClientReadinessListener, IShaderOptionsListener, IShaderDocumentUriListener
    {
        private const int DebounceMilliseconds = 300;
        private const int MaximumHints = 2048;
        private const int MaximumLabelCharacters = 128;

        private readonly object gate = new object();
        private readonly IWpfTextView view;
        private readonly ITextBuffer buffer;
        private readonly ShaderEditorBufferState ownerState;
        private readonly IClassificationFormatMap formatMap;
        private string documentUri;
        private CancellationTokenSource refreshCancellation;
        private readonly InlayHintCache cache = new InlayHintCache();
        // WPF adornments belong to this view and are only accessed on its UI thread.
        private readonly Dictionary<object, IntraTextAdornmentTag> adornments =
            [];
        private long generation;
        private bool catalogLoaded;
        private bool disposed;

        internal ShaderInlayHintTagger(
            IWpfTextView view,
            ITextBuffer buffer,
            ShaderEditorBufferState ownerState,
            string documentUri,
            IClassificationFormatMap formatMap)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            this.ownerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
            this.documentUri = documentUri;
            this.formatMap = formatMap ?? throw new ArgumentNullException(nameof(formatMap));
            cache.Clear(buffer.CurrentSnapshot.Version.VersionNumber, buffer.CurrentSnapshot.GetText());

            buffer.Changed += OnBufferChanged;
            view.Closed += OnViewClosed;
            formatMap.ClassificationFormatMappingChanged += OnFormatMappingChanged;
            ShaderClientReadiness.Register(this);
            ShaderOptionChanges.Register(this);
            ScheduleRefresh(DebounceMilliseconds);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            if (spans == null || spans.Count == 0 || !Options.ShaderOptions.Current.EnableInlayHints)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;
            IReadOnlyList<ParameterHint> current;
            lock (gate)
            {
                if (disposed || cache.Version != snapshot.Version.VersionNumber)
                {
                    yield break;
                }

                current = cache.Hints;
            }

            PruneAdornments(current);
            foreach (ParameterHint hint in current)
            {
                if (!TryGetPosition(hint, snapshot, out int position) || !Contains(spans, position))
                {
                    continue;
                }

                if (!adornments.TryGetValue(hint.Identity, out IntraTextAdornmentTag tag))
                {
                    tag = new IntraTextAdornmentTag(new TextBlock
                    {
                        Text = hint.Label,
                        FontStyle = FontStyles.Italic,
                        Opacity = 0.65,
                        Margin = new Thickness(hint.PaddingLeft ? 3 : 0, 0, hint.PaddingRight ? 3 : 0, 0),
                        ToolTip = "Parameter name hint",
                    }, null, PositionAffinity.Successor);
                    adornments.Add(hint.Identity, tag);
                }
                Microsoft.VisualStudio.Text.Formatting.TextFormattingRunProperties properties = formatMap.DefaultTextProperties;
                TextBlock adornment = (TextBlock)tag.Adornment;
                adornment.FontFamily = properties.Typeface.FontFamily;
                adornment.FontSize = properties.FontRenderingEmSize * 0.9;
                adornment.Foreground = properties.ForegroundBrush;
                yield return new TagSpan<IntraTextAdornmentTag>(
                    new SnapshotSpan(snapshot, position, 0),
                    tag);
            }
        }

        public void OnClientReadinessChanged()
        {
            lock (gate)
            {
                catalogLoaded = false;
            }

            ScheduleRefresh(0);
        }

        public void OnShaderOptionsChanged()
        {
            lock (gate)
            {
                catalogLoaded = false;
            }

            ScheduleRefresh(0);
        }

        public void OnDocumentUriChanged(string oldUri, string newUri)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                documentUri = newUri;
            }

            ScheduleRefresh(0, clear: true);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            InlayHintEdit[] edits = e.Changes.Select(change => new InlayHintEdit(
                change.OldPosition, change.OldLength, change.NewText)).ToArray();
            bool mapped;
            IReadOnlyList<int> affectedLines;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                mapped = cache.Version == e.Before.Version.VersionNumber;
                affectedLines = cache.ApplyEdits(e.Before.Version.VersionNumber,
                    e.After.Version.VersionNumber, edits, e.After.GetText());
                // Include paths and defines can change the available function signatures.
                if (e.Changes.Any(change =>
                    e.Before.GetLineFromPosition(change.OldPosition).GetText().TrimStart().StartsWith("#") ||
                    e.After.GetLineFromPosition(change.NewPosition).GetText().TrimStart().StartsWith("#") ||
                    change.NewText.Contains("#") || change.OldText.Contains("#")))
                {
                    catalogLoaded = false;
                }
            }

            if (mapped)
            {
                int[] changedLines = e.Changes.SelectMany(change =>
                {
                    int first = e.After.GetLineFromPosition(change.NewPosition).LineNumber;
                    int last = e.After.GetLineFromPosition(change.NewEnd).LineNumber;
                    return Enumerable.Range(first, last - first + 1);
                }).Concat(affectedLines).Distinct().ToArray();
                RaiseTagsChanged(e.After, changedLines);
            }
            else
            {
                RaiseTagsChanged();
            }

            ScheduleRefresh(DebounceMilliseconds);
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void OnFormatMappingChanged(object sender, EventArgs e)
        {
            // Cached adornments still need new font/color properties after a theme change.
            RaiseTagsChanged();
        }

        private void ScheduleRefresh(int delayMilliseconds, bool clear = false)
        {
            CancellationTokenSource previousCancellation;
            CancellationTokenSource currentCancellation = null;
            ITextSnapshot snapshot = null;
            long requestGeneration = 0;
            bool enabled = Options.ShaderOptions.Current.EnableInlayHints;

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                previousCancellation = refreshCancellation;
                refreshCancellation = null;
                if (clear || !enabled)
                {
                    cache.Clear(buffer.CurrentSnapshot.Version.VersionNumber, buffer.CurrentSnapshot.GetText());
                    catalogLoaded = false;
                }
                generation++;

                if (enabled)
                {
                    currentCancellation = new CancellationTokenSource();
                    refreshCancellation = currentCancellation;
                    snapshot = buffer.CurrentSnapshot;
                    requestGeneration = generation;
                }
            }

            Cancel(previousCancellation);
            if (clear || !enabled)
            {
                RaiseTagsChanged();
            }

            if (currentCancellation != null)
            {
                _ = RefreshAsync(snapshot, requestGeneration, delayMilliseconds, currentCancellation);
            }
        }

        private async Task RefreshAsync(
            ITextSnapshot snapshot,
            long requestGeneration,
            int delayMilliseconds,
            CancellationTokenSource cancellation)
        {
            try
            {
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellation.Token).ConfigureAwait(false);
                }

                string requestUri;
                bool loadCatalog;
                lock (gate)
                {
                    if (disposed || requestGeneration != generation)
                    {
                        return;
                    }

                    requestUri = documentUri;
                    loadCatalog = !catalogLoaded;
                }

                if (string.IsNullOrEmpty(requestUri))
                {
                    return;
                }

                LanguageClient.ShaderLanguageClient client = LanguageClient.ShaderLanguageClient.Instance;
                if (client == null || !client.IsReady)
                {
                    return;
                }

                ITextSnapshotLine lastLine = snapshot.GetLineFromLineNumber(snapshot.LineCount - 1);
                JObject parameters = new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = requestUri },
                    ["range"] = new JObject
                    {
                        ["start"] = new JObject { ["line"] = 0, ["character"] = 0 },
                        ["end"] = new JObject
                        {
                            ["line"] = lastLine.LineNumber,
                            ["character"] = lastLine.Length,
                        },
                    },
                };
                JToken response = await client.RequestAsync(
                    "textDocument/inlayHint",
                    parameters,
                    cancellation.Token).ConfigureAwait(false);

                cancellation.Token.ThrowIfCancellationRequested();
                IReadOnlyList<ParameterHint> parsedHints = ParseHints(response, snapshot);
                IReadOnlyList<string> declarations = null;
                if (loadCatalog)
                {
                    try
                    {
                        JToken completion = await client.RequestAsync("textDocument/completion", new JObject
                        {
                            ["textDocument"] = new JObject { ["uri"] = requestUri },
                            ["position"] = new JObject { ["line"] = lastLine.LineNumber, ["character"] = lastLine.Length },
                            ["context"] = new JObject { ["triggerKind"] = 1 },
                        }, cancellation.Token).ConfigureAwait(false);
                        declarations = InlayHintCache.ReadCompletionSignatures(completion);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* Existing hints remain usable without the optional catalog. */ }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                IReadOnlyList<int> changedLines;
                lock (gate)
                {
                    if (disposed ||
                        !Options.ShaderOptions.Current.EnableInlayHints ||
                        requestGeneration != generation ||
                        !ReferenceEquals(snapshot, buffer.CurrentSnapshot))
                    {
                        return;
                    }

                    if (declarations != null)
                    {
                        cache.SetExternalSignatures(declarations);
                        catalogLoaded = true;
                    }
                    changedLines = cache.Replace(snapshot.Version.VersionNumber, parsedHints, snapshot.GetText());
                }

                RaiseTagsChanged(snapshot, changedLines);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception)
            {
                // Hints are optional and should never disrupt editing.
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(refreshCancellation, cancellation))
                    {
                        refreshCancellation = null;
                    }
                }

                cancellation.Dispose();
            }
        }

        private static IReadOnlyList<ParameterHint> ParseHints(JToken response, ITextSnapshot snapshot)
        {
            JArray array = response as JArray ?? response?["result"] as JArray;
            if (array == null || array.Count == 0)
            {
                return Array.Empty<ParameterHint>();
            }

            List<ParameterHint> result = new List<ParameterHint>(Math.Min(array.Count, MaximumHints));
            foreach (JToken token in array.Take(MaximumHints))
            {
                if (token is not JObject item ||
                    !TryReadPosition(item["position"], out LspTextPosition position) ||
                    position.Line >= snapshot.LineCount ||
                    position.Character > snapshot.GetLineFromLineNumber(position.Line).Length)
                {
                    continue;
                }

                int? kind = ReadInteger(item["kind"]);
                if (kind.HasValue && kind.Value != 2) // LSP InlayHintKind.Parameter
                {
                    continue;
                }

                string label = ReadLabel(item["label"]);
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                if (label.Length > MaximumLabelCharacters)
                {
                    label = label.Substring(0, MaximumLabelCharacters - 1) + "…";
                }

                result.Add(new ParameterHint(
                    position.Line,
                    position.Character,
                    label,
                    item.Value<bool?>("paddingLeft") == true,
                    item.Value<bool?>("paddingRight") == true));
            }

            return result;
        }

        private static bool TryReadPosition(JToken token, out LspTextPosition position)
        {
            position = default;
            if (token is not JObject value ||
                !LspTextRange.TryReadNonNegativeInt(value["line"], out int line) ||
                !LspTextRange.TryReadNonNegativeInt(value["character"], out int character))
            {
                return false;
            }

            position = new LspTextPosition(line, character);
            return true;
        }

        private static string ReadLabel(JToken token)
        {
            if (token?.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            if (token is not JArray parts)
            {
                return null;
            }

            return string.Concat(parts
                .OfType<JObject>()
                .Select(part => part.Value<string>("value"))
                .Where(value => value != null));
        }

        private static int? ReadInteger(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
            {
                return null;
            }

            long value = token.Value<long>();
            return value is >=int.MinValue and <=int.MaxValue ? (int?)value : null;
        }

        private static bool Contains(NormalizedSnapshotSpanCollection spans, int position)
        {
            foreach (SnapshotSpan span in spans)
            {
                if (position >= span.Start.Position && position <= span.End.Position)
                {
                    return true;
                }
            }

            return false;
        }

        private void RaiseTagsChanged()
        {
            ShaderUiNotifications.Post(() =>
            {
                EventHandler<SnapshotSpanEventArgs> handler;
                ITextSnapshot snapshot;
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    handler = TagsChanged;
                    snapshot = buffer.CurrentSnapshot;
                    PruneAdornments(cache.Hints);
                }

                handler?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snapshot, 0, snapshot.Length)));
            });
        }

        private void RaiseTagsChanged(ITextSnapshot snapshot, IReadOnlyList<int> changedLines)
        {
            if (changedLines.Count == 0)
            {
                return;
            }

            ShaderUiNotifications.Post(() =>
            {
                EventHandler<SnapshotSpanEventArgs> handler;
                ITextSnapshot current;
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    handler = TagsChanged;
                    current = buffer.CurrentSnapshot;
                    PruneAdornments(cache.Hints);
                }
                foreach (int number in changedLines)
                {
                    if (number < 0 || number >= snapshot.LineCount)
                    {
                        continue;
                    }

                    SnapshotSpan line = snapshot.GetLineFromLineNumber(number).ExtentIncludingLineBreak;
                    handler?.Invoke(this, new SnapshotSpanEventArgs(
                        line.TranslateTo(current, SpanTrackingMode.EdgeInclusive)));
                }
            });
        }

        private void PruneAdornments(IReadOnlyList<ParameterHint> current)
        {
            HashSet<object> identities = [.. current.Select(hint => hint.Identity)];
            foreach (object identity in adornments.Keys.Where(key => !identities.Contains(key)).ToArray())
            {
                _=adornments.Remove(identity);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                generation++;
                cancellation = refreshCancellation;
                refreshCancellation = null;
                cache.Clear(buffer.CurrentSnapshot.Version.VersionNumber);
            }

            buffer.Changed -= OnBufferChanged;
            view.Closed -= OnViewClosed;
            formatMap.ClassificationFormatMappingChanged -= OnFormatMappingChanged;
            ShaderClientReadiness.Unregister(this);
            ShaderOptionChanges.Unregister(this);
            ownerState.UnregisterUriListener(this);
            Cancel(cancellation);
            ShaderUiNotifications.Post(adornments.Clear);
        }

        private static void Cancel(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A superseded refresh may finish and dispose its token concurrently.
            }
        }

        private static bool TryGetPosition(ParameterHint hint, ITextSnapshot snapshot, out int position)
        {
            position = 0;
            if (hint.Line >= snapshot.LineCount)
            {
                return false;
            }

            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(hint.Line);
            if (hint.Character > line.Length)
            {
                return false;
            }

            position = line.Start.Position + hint.Character;
            return true;
        }
    }
}
