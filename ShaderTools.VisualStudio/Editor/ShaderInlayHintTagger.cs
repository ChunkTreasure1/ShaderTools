using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

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
                !(textView is IWpfTextView wpfView) ||
                !ReferenceEquals(textView.TextBuffer, buffer))
            {
                return null;
            }

            var state = manager.GetOrCreate(buffer);
            var documentUri = state.DocumentUri;
            var tagger = new ShaderInlayHintTagger(
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
        private IReadOnlyList<InlayHintData> hints = Array.Empty<InlayHintData>();
        private int snapshotVersion = -1;
        private long generation;
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

            buffer.Changed += OnBufferChanged;
            view.Closed += OnViewClosed;
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

            var snapshot = spans[0].Snapshot;
            IReadOnlyList<InlayHintData> current;
            lock (gate)
            {
                if (disposed || snapshotVersion != snapshot.Version.VersionNumber)
                {
                    yield break;
                }

                current = hints;
            }

            foreach (var hint in current)
            {
                if (!hint.TryGetPosition(snapshot, out var position) || !Contains(spans, position))
                {
                    continue;
                }

                var properties = formatMap.DefaultTextProperties;
                var adornment = new TextBlock
                {
                    Text = hint.Label,
                    FontFamily = properties.Typeface.FontFamily,
                    FontSize = properties.FontRenderingEmSize * 0.9,
                    FontStyle = FontStyles.Italic,
                    Foreground = properties.ForegroundBrush,
                    Opacity = 0.65,
                    Margin = new Thickness(hint.PaddingLeft ? 3 : 0, 0, hint.PaddingRight ? 3 : 0, 0),
                    ToolTip = "Parameter name hint",
                };
                var tag = new IntraTextAdornmentTag(adornment, null, PositionAffinity.Successor);
                yield return new TagSpan<IntraTextAdornmentTag>(
                    new SnapshotSpan(snapshot, position, 0),
                    tag);
            }
        }

        public void OnClientReadinessChanged()
        {
            ScheduleRefresh(0);
        }

        public void OnShaderOptionsChanged()
        {
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

            ScheduleRefresh(0);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            ScheduleRefresh(DebounceMilliseconds);
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void ScheduleRefresh(int delayMilliseconds)
        {
            CancellationTokenSource previousCancellation;
            CancellationTokenSource currentCancellation = null;
            ITextSnapshot snapshot = null;
            long requestGeneration = 0;
            var enabled = Options.ShaderOptions.Current.EnableInlayHints;

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                previousCancellation = refreshCancellation;
                refreshCancellation = null;
                hints = Array.Empty<InlayHintData>();
                snapshotVersion = buffer.CurrentSnapshot.Version.VersionNumber;
                generation++;

                if (enabled)
                {
                    currentCancellation = new CancellationTokenSource();
                    refreshCancellation = currentCancellation;
                    snapshot = buffer.CurrentSnapshot;
                    snapshotVersion = snapshot.Version.VersionNumber;
                    requestGeneration = generation;
                }
            }

            Cancel(previousCancellation);
            RaiseTagsChanged();
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
                lock (gate)
                {
                    if (disposed || requestGeneration != generation)
                    {
                        return;
                    }

                    requestUri = documentUri;
                }

                if (string.IsNullOrEmpty(requestUri))
                {
                    return;
                }

                var client = LanguageClient.ShaderLanguageClient.Instance;
                if (client == null || !client.IsReady)
                {
                    return;
                }

                var lastLine = snapshot.GetLineFromLineNumber(snapshot.LineCount - 1);
                var parameters = new JObject
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
                var response = await client.RequestAsync(
                    "textDocument/inlayHint",
                    parameters,
                    cancellation.Token).ConfigureAwait(false);

                cancellation.Token.ThrowIfCancellationRequested();
                var parsedHints = ParseHints(response);
                lock (gate)
                {
                    if (disposed ||
                        !Options.ShaderOptions.Current.EnableInlayHints ||
                        requestGeneration != generation ||
                        !ReferenceEquals(snapshot, buffer.CurrentSnapshot))
                    {
                        return;
                    }

                    hints = parsedHints;
                    snapshotVersion = snapshot.Version.VersionNumber;
                }

                RaiseTagsChanged();
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

        private static IReadOnlyList<InlayHintData> ParseHints(JToken response)
        {
            var array = response as JArray ?? response?["result"] as JArray;
            if (array == null || array.Count == 0)
            {
                return Array.Empty<InlayHintData>();
            }

            var result = new List<InlayHintData>(Math.Min(array.Count, MaximumHints));
            foreach (var token in array.Take(MaximumHints))
            {
                if (!(token is JObject item) ||
                    !TryReadPosition(item["position"], out var position))
                {
                    continue;
                }

                var kind = ReadInteger(item["kind"]);
                if (kind.HasValue && kind.Value != 2) // LSP InlayHintKind.Parameter
                {
                    continue;
                }

                var label = ReadLabel(item["label"]);
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                if (label.Length > MaximumLabelCharacters)
                {
                    label = label.Substring(0, MaximumLabelCharacters - 1) + "…";
                }

                result.Add(new InlayHintData(
                    position,
                    label,
                    item.Value<bool?>("paddingLeft") == true,
                    item.Value<bool?>("paddingRight") == true));
            }

            return result;
        }

        private static bool TryReadPosition(JToken token, out LspTextPosition position)
        {
            position = default;
            if (!(token is JObject value) ||
                !LspTextRange.TryReadNonNegativeInt(value["line"], out var line) ||
                !LspTextRange.TryReadNonNegativeInt(value["character"], out var character))
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

            if (!(token is JArray parts))
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

            var value = token.Value<long>();
            return value >= int.MinValue && value <= int.MaxValue ? (int?)value : null;
        }

        private static bool Contains(NormalizedSnapshotSpanCollection spans, int position)
        {
            foreach (var span in spans)
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
                }

                handler?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snapshot, 0, snapshot.Length)));
            });
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
                hints = Array.Empty<InlayHintData>();
            }

            buffer.Changed -= OnBufferChanged;
            view.Closed -= OnViewClosed;
            ShaderClientReadiness.Unregister(this);
            ShaderOptionChanges.Unregister(this);
            ownerState.UnregisterUriListener(this);
            Cancel(cancellation);
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

        private readonly struct InlayHintData
        {
            internal InlayHintData(
                LspTextPosition position,
                string label,
                bool paddingLeft,
                bool paddingRight)
            {
                Position = position;
                Label = label;
                PaddingLeft = paddingLeft;
                PaddingRight = paddingRight;
            }

            internal LspTextPosition Position { get; }

            internal string Label { get; }

            internal bool PaddingLeft { get; }

            internal bool PaddingRight { get; }

            internal bool TryGetPosition(ITextSnapshot snapshot, out int position)
            {
                position = 0;
                if (Position.Line < 0 || Position.Line >= snapshot.LineCount)
                {
                    return false;
                }

                var line = snapshot.GetLineFromLineNumber(Position.Line);
                if (Position.Character < 0 || Position.Character > line.Length)
                {
                    return false;
                }

                position = line.Start.Position + Position.Character;
                return true;
            }
        }
    }
}
