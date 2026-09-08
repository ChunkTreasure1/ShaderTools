using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(ITaggerProvider))]
    [ContentType(LanguageClient.ShaderContentTypes.Glsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Hlsl)]
    [TagType(typeof(ClassificationTag))]
    [Name("ShaderTools.InactiveCode")]
    internal sealed class ShaderInactiveCodeTaggerProvider : ITaggerProvider
    {
        private readonly ShaderEditorBufferManager manager;

        [ImportingConstructor]
        public ShaderInactiveCodeTaggerProvider(ShaderEditorBufferManager manager)
        {
            this.manager = manager;
        }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!typeof(T).IsAssignableFrom(typeof(ClassificationTag)))
            {
                return null;
            }

            ShaderInactiveCodeTagger tagger = manager.GetOrCreate(buffer).GetInactiveTagger();
            return tagger == null ? null : (ITagger<T>)(object)tagger;
        }
    }

    // Shared by all aggregators for this buffer. ShaderEditorBufferState owns
    // cleanup; an individual aggregator must not dispose the shared tagger.
    internal sealed class ShaderInactiveCodeTagger : ITagger<ClassificationTag>,
        IShaderOptionsListener, IShaderDocumentUriListener
    {
        private const int MaximumFragments = 8192;

        private readonly object gate = new object();
        private readonly ITextBuffer buffer;
        private string documentUri;
        private readonly ClassificationTag classificationTag;
        private DiagnosticSet diagnostics = DiagnosticSet.Empty;
        private int snapshotVersion = -1;
        private bool disposed;

        internal ShaderInactiveCodeTagger(
            ITextBuffer buffer,
            string documentUri,
            IClassificationType classificationType)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            this.documentUri = documentUri;
            classificationTag = new ClassificationTag(
                classificationType ?? throw new ArgumentNullException(nameof(classificationType)));
            buffer.Changed += OnBufferChanged;
            ShaderDiagnostics.Subscribe(documentUri, this);
            ShaderOptionChanges.Register(this);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans == null || spans.Count == 0 || !Options.ShaderOptions.Current.FadeInactiveCode)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;
            DiagnosticSet current;
            lock (gate)
            {
                if (disposed || snapshotVersion != snapshot.Version.VersionNumber)
                {
                    yield break;
                }

                current = diagnostics;
            }

            Span[] errorSpans = current.ErrorRanges
                .Select(range => range.TryGetSpan(snapshot, out Span span) ? (Span?)span : null)
                .Where(span => span.HasValue)
                .Select(span => span.Value)
                .OrderBy(span => span.Start)
                .ToArray();

            int fragmentCount = 0;
            foreach (LspTextRange inactiveRange in current.InactiveRanges)
            {
                if (!inactiveRange.TryGetSpan(snapshot, out Span inactiveSpan))
                {
                    continue;
                }

                foreach (Span fragment in ExcludeErrors(inactiveSpan, errorSpans))
                {
                    if (fragmentCount++ >= MaximumFragments)
                    {
                        yield break;
                    }

                    SnapshotSpan snapshotSpan = new SnapshotSpan(snapshot, fragment);
                    if (Intersects(spans, snapshotSpan))
                    {
                        yield return new TagSpan<ClassificationTag>(snapshotSpan, classificationTag);
                    }
                }
            }
        }

        internal void SetDiagnostics(string uri, DiagnosticSet value)
        {
            lock (gate)
            {
                if (disposed || !ShaderEditorBufferState.UriEquals(documentUri, uri))
                {
                    return;
                }

                diagnostics = value ?? DiagnosticSet.Empty;
                snapshotVersion = buffer.CurrentSnapshot.Version.VersionNumber;
            }

            RaiseTagsChanged();
        }

        internal void ClearDiagnostics()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                diagnostics = DiagnosticSet.Empty;
                snapshotVersion = buffer.CurrentSnapshot.Version.VersionNumber;
            }

            RaiseTagsChanged();
        }

        public void OnShaderOptionsChanged()
        {
            RaiseTagsChanged();
        }

        public void OnDocumentUriChanged(string oldUri, string newUri)
        {
            ShaderDiagnostics.Rebind(oldUri, newUri, this);
        }

        internal void ChangeDocumentUri(string newUri, DiagnosticSet current)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                documentUri = newUri;
                diagnostics = current ?? DiagnosticSet.Empty;
                snapshotVersion = buffer.CurrentSnapshot.Version.VersionNumber;
            }

            RaiseTagsChanged();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                diagnostics = DiagnosticSet.Empty;
                snapshotVersion = e.After.Version.VersionNumber;
            }

            RaiseTagsChanged();
        }

        private static IEnumerable<Span> ExcludeErrors(Span inactiveSpan, IReadOnlyList<Span> errorSpans)
        {
            int cursor = inactiveSpan.Start;
            foreach (Span errorSpan in errorSpans)
            {
                if (errorSpan.End <= cursor)
                {
                    continue;
                }

                if (errorSpan.Start >= inactiveSpan.End)
                {
                    break;
                }

                if (errorSpan.Start > cursor)
                {
                    yield return Span.FromBounds(cursor, Math.Min(errorSpan.Start, inactiveSpan.End));
                }

                cursor = Math.Max(cursor, errorSpan.End);
                if (cursor >= inactiveSpan.End)
                {
                    yield break;
                }
            }

            if (cursor < inactiveSpan.End)
            {
                yield return Span.FromBounds(cursor, inactiveSpan.End);
            }
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
            string uri;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                diagnostics = DiagnosticSet.Empty;
                uri = documentUri;
                documentUri = null;
            }

            buffer.Changed -= OnBufferChanged;
            ShaderDiagnostics.Unsubscribe(uri, this);
            ShaderOptionChanges.Unregister(this);
        }
    }
}
