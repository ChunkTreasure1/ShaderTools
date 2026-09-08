using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(ITaggerProvider))]
    [ContentType(LanguageClient.ShaderContentTypes.Glsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Hlsl)]
    [TagType(typeof(IOutliningRegionTag))]
    [Name("ShaderTools.Folding")]
    internal sealed class ShaderFoldingTaggerProvider : ITaggerProvider
    {
        private readonly ShaderEditorBufferManager manager;

        [ImportingConstructor]
        public ShaderFoldingTaggerProvider(ShaderEditorBufferManager manager)
        {
            this.manager = manager;
        }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (typeof(T) != typeof(IOutliningRegionTag))
            {
                return null;
            }

            ShaderFoldingTagger tagger = manager.GetOrCreate(buffer).GetFoldingTagger();
            return tagger == null ? null : (ITagger<T>)(object)tagger;
        }
    }

    // Shared by all aggregators for this buffer. ShaderEditorBufferState owns
    // cleanup; an individual aggregator must not dispose the shared tagger.
    internal sealed class ShaderFoldingTagger : ITagger<IOutliningRegionTag>,
        IShaderClientReadinessListener, IShaderDocumentUriListener
    {
        private const int DebounceMilliseconds = 250;
        private const int MaximumRanges = 4096;
        private const int MaximumHintCharacters = 1024;

        private readonly object gate = new object();
        private readonly ITextBuffer buffer;
        private string documentUri;
        private CancellationTokenSource refreshCancellation;
        private IReadOnlyList<FoldingRegion> regions = Array.Empty<FoldingRegion>();
        private int snapshotVersion = -1;
        private long generation;
        private bool disposed;

        internal ShaderFoldingTagger(ITextBuffer buffer, string documentUri)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            this.documentUri = documentUri;
            buffer.Changed += OnBufferChanged;
            ShaderClientReadiness.Register(this);
            ScheduleRefresh(DebounceMilliseconds);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans == null || spans.Count == 0)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;
            IReadOnlyList<FoldingRegion> currentRegions;
            lock (gate)
            {
                if (disposed || snapshotVersion != snapshot.Version.VersionNumber)
                {
                    yield break;
                }

                currentRegions = regions;
            }

            foreach (FoldingRegion region in currentRegions)
            {
                if (!region.TryGetSpan(snapshot, out SnapshotSpan span) || !Intersects(spans, span))
                {
                    continue;
                }

                int hintLength = Math.Min(span.Length, MaximumHintCharacters);
                string hint = hintLength > 0 ? snapshot.GetText(span.Start, hintLength) : region.CollapsedText;
                OutliningRegionTag tag = new OutliningRegionTag(
                    isDefaultCollapsed: false,
                    isImplementation: false,
                    collapsedForm: region.CollapsedText,
                    collapsedHintForm: hint);
                yield return new TagSpan<IOutliningRegionTag>(span, tag);
            }
        }

        public void OnClientReadinessChanged()
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

        private void ScheduleRefresh(int delayMilliseconds)
        {
            CancellationTokenSource previousCancellation;
            CancellationTokenSource currentCancellation;
            ITextSnapshot snapshot;
            long requestGeneration;

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                previousCancellation = refreshCancellation;
                currentCancellation = new CancellationTokenSource();
                refreshCancellation = currentCancellation;
                snapshot = buffer.CurrentSnapshot;
                requestGeneration = ++generation;
                regions = Array.Empty<FoldingRegion>();
                snapshotVersion = snapshot.Version.VersionNumber;
            }

            Cancel(previousCancellation);
            RaiseTagsChanged();
            _ = RefreshAsync(snapshot, requestGeneration, delayMilliseconds, currentCancellation);
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

                LanguageClient.ShaderLanguageClient client = LanguageClient.ShaderLanguageClient.Instance;
                if (client == null || !client.IsReady)
                {
                    return;
                }

                JObject parameters = new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = requestUri },
                };
                JToken response = await client.RequestAsync(
                    "textDocument/foldingRange",
                    parameters,
                    cancellation.Token).ConfigureAwait(false);

                cancellation.Token.ThrowIfCancellationRequested();
                IReadOnlyList<FoldingRegion> parsedRegions = ParseRegions(response);

                lock (gate)
                {
                    if (disposed ||
                        requestGeneration != generation ||
                        !ReferenceEquals(snapshot, buffer.CurrentSnapshot))
                    {
                        return;
                    }

                    regions = parsedRegions;
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
                // Editor adornments are optional. The language client's own logging remains
                // the authoritative place for transport and server failures.
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

        private static IReadOnlyList<FoldingRegion> ParseRegions(JToken response)
        {
            JArray array = response as JArray ?? response?["result"] as JArray;
            if (array == null || array.Count == 0)
            {
                return Array.Empty<FoldingRegion>();
            }

            List<FoldingRegion> result = new List<FoldingRegion>(Math.Min(array.Count, MaximumRanges));
            foreach (JToken token in array.Take(MaximumRanges))
            {
                if (token is not JObject item ||
                    !LspTextRange.TryReadNonNegativeInt(item["startLine"], out int startLine) ||
                    !LspTextRange.TryReadNonNegativeInt(item["endLine"], out int endLine) ||
                    endLine <= startLine)
                {
                    continue;
                }

                int startCharacter = ReadOptionalCharacter(item["startCharacter"], 0);
                int endCharacter = ReadOptionalCharacter(item["endCharacter"], int.MaxValue);
                string collapsedText = item.Value<string>("collapsedText");
                result.Add(new FoldingRegion(
                    startLine,
                    startCharacter,
                    endLine,
                    endCharacter,
                    string.IsNullOrWhiteSpace(collapsedText) ? "..." : collapsedText));
            }

            return result;
        }

        private static int ReadOptionalCharacter(JToken token, int fallback)
        {
            return LspTextRange.TryReadNonNegativeInt(token, out int value) ? value : fallback;
        }

        private static bool Intersects(NormalizedSnapshotSpanCollection requestedSpans, SnapshotSpan candidate)
        {
            foreach (SnapshotSpan requestedSpan in requestedSpans)
            {
                if (requestedSpan.IntersectsWith(candidate))
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

                handler?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
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
                regions = Array.Empty<FoldingRegion>();
            }

            buffer.Changed -= OnBufferChanged;
            ShaderClientReadiness.Unregister(this);
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

        private readonly struct FoldingRegion
        {
            internal FoldingRegion(
                int startLine,
                int startCharacter,
                int endLine,
                int endCharacter,
                string collapsedText)
            {
                StartLine = startLine;
                StartCharacter = startCharacter;
                EndLine = endLine;
                EndCharacter = endCharacter;
                CollapsedText = collapsedText;
            }

            internal int StartLine { get; }

            internal int StartCharacter { get; }

            internal int EndLine { get; }

            internal int EndCharacter { get; }

            internal string CollapsedText { get; }

            internal bool TryGetSpan(ITextSnapshot snapshot, out SnapshotSpan span)
            {
                span = default;
                if (StartLine < 0 || EndLine >= snapshot.LineCount || EndLine <= StartLine)
                {
                    return false;
                }

                ITextSnapshotLine startLine = snapshot.GetLineFromLineNumber(StartLine);
                ITextSnapshotLine endLine = snapshot.GetLineFromLineNumber(EndLine);
                int startCharacter = Math.Min(StartCharacter, startLine.Length);
                int endCharacter = EndCharacter == int.MaxValue
                    ? endLine.Length
                    : Math.Min(EndCharacter, endLine.Length);
                int start = startLine.Start.Position + startCharacter;
                int end = endLine.Start.Position + endCharacter;
                if (end <= start)
                {
                    return false;
                }

                span = new SnapshotSpan(snapshot, Span.FromBounds(start, end));
                return true;
            }
        }
    }

    internal static class ShaderClientReadiness
    {
        private static readonly object Gate = new object();
        private static readonly List<WeakReference<IShaderClientReadinessListener>> Listeners =
            [];

        static ShaderClientReadiness()
        {
            LanguageClient.ShaderLanguageClient.ReadyChanged += OnReadyChanged;
        }

        internal static void Register(IShaderClientReadinessListener listener)
        {
            lock (Gate)
            {
                Prune();
                Listeners.Add(new WeakReference<IShaderClientReadinessListener>(listener));
            }
        }

        internal static void Unregister(IShaderClientReadinessListener listener)
        {
            lock (Gate)
            {
                for (int index = Listeners.Count - 1; index >= 0; index--)
                {
                    if (!Listeners[index].TryGetTarget(out IShaderClientReadinessListener target) || ReferenceEquals(target, listener))
                    {
                        Listeners.RemoveAt(index);
                    }
                }
            }
        }

        private static void OnReadyChanged(object sender, EventArgs e)
        {
            IShaderClientReadinessListener[] liveListeners;
            lock (Gate)
            {
                Prune();
                liveListeners = Listeners
                    .Select(reference =>
                    {
                        _=reference.TryGetTarget(out IShaderClientReadinessListener target);
                        return target;
                    })
                    .Where(listener => listener != null)
                    .ToArray();
            }

            foreach (IShaderClientReadinessListener listener in liveListeners)
            {
                listener.OnClientReadinessChanged();
            }
        }

        private static void Prune()
        {
            for (int index = Listeners.Count - 1; index >= 0; index--)
            {
                if (!Listeners[index].TryGetTarget(out _))
                {
                    Listeners.RemoveAt(index);
                }
            }
        }
    }

    internal interface IShaderClientReadinessListener
    {
        void OnClientReadinessChanged();
    }
}
