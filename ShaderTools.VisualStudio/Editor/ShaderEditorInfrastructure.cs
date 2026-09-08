using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;

namespace ShaderTools.VisualStudio.Editor
{
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ShaderEditorBufferManager
    {
        private readonly object gate = new object();
        private readonly ConditionalWeakTable<ITextBuffer, ShaderEditorBufferState> states =
            new ConditionalWeakTable<ITextBuffer, ShaderEditorBufferState>();
        private readonly ITextDocumentFactoryService documentFactory;
        private readonly IClassificationType inactiveClassification;

        [ImportingConstructor]
        public ShaderEditorBufferManager(
            ITextDocumentFactoryService documentFactory,
            IClassificationTypeRegistryService classificationRegistry)
        {
            this.documentFactory = documentFactory ?? throw new ArgumentNullException(nameof(documentFactory));
            if (classificationRegistry == null)
            {
                throw new ArgumentNullException(nameof(classificationRegistry));
            }

            inactiveClassification = classificationRegistry.GetClassificationType(ShaderInactiveCodeFormat.ClassificationName);
            this.documentFactory.TextDocumentCreated += OnTextDocumentCreated;
            this.documentFactory.TextDocumentDisposed += OnTextDocumentDisposed;
        }

        internal ShaderEditorBufferState GetOrCreate(ITextBuffer buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            ShaderEditorBufferState state;
            bool created = false;
            lock (gate)
            {
                if (!states.TryGetValue(buffer, out state))
                {
                    state = new ShaderEditorBufferState(buffer, inactiveClassification);
                    states.Add(buffer, state);
                    created = true;
                }
            }

            // Re-query after publishing the state. This closes the race where the
            // document is created between an initial lookup and state registration.
            if (created && documentFactory.TryGetTextDocument(buffer, out ITextDocument document))
            {
                state.AttachDocument(document);
            }

            return state;
        }

        internal void Connect(ITextBuffer buffer)
        {
            GetOrCreate(buffer).Connect();
        }

        internal void Disconnect(ITextBuffer buffer)
        {
            ShaderEditorBufferState state = null;
            lock (gate)
            {
                if (states.TryGetValue(buffer, out ShaderEditorBufferState existing) && existing.Disconnect())
                {
                    _=states.Remove(buffer);
                    state = existing;
                }
            }

            state?.Dispose();
        }

        private void OnTextDocumentCreated(object sender, TextDocumentEventArgs e)
        {
            ITextDocument document = e?.TextDocument;
            if (document == null)
            {
                return;
            }

            ShaderEditorBufferState state;
            lock (gate)
            {
                _=states.TryGetValue(document.TextBuffer, out state);
            }

            state?.AttachDocument(document);
        }

        private void OnTextDocumentDisposed(object sender, TextDocumentEventArgs e)
        {
            ITextDocument document = e?.TextDocument;
            if (document == null)
            {
                return;
            }

            ShaderEditorBufferState state;
            lock (gate)
            {
                _=states.TryGetValue(document.TextBuffer, out state);
            }

            state?.DetachDocument(document);
        }
    }

    internal sealed class ShaderEditorBufferState : IDisposable
    {
        private readonly object gate = new object();
        private readonly ITextBuffer buffer;
        private readonly IClassificationType inactiveClassification;
        private readonly List<WeakReference<IShaderDocumentUriListener>> uriListeners =
            [];
        private ITextDocument document;
        private string documentUri;
        private ShaderFoldingTagger foldingTagger;
        private ShaderInactiveCodeTagger inactiveTagger;
        private int connections;
        private bool disposed;

        internal ShaderEditorBufferState(
            ITextBuffer buffer,
            IClassificationType inactiveClassification)
        {
            this.buffer = buffer;
            this.inactiveClassification = inactiveClassification;
        }

        internal ShaderFoldingTagger GetFoldingTagger()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return null;
                }

                return foldingTagger ??= new ShaderFoldingTagger(buffer, documentUri);
            }
        }

        internal string DocumentUri
        {
            get
            {
                lock (gate)
                {
                    return documentUri;
                }
            }
        }

        internal void RegisterUriListener(IShaderDocumentUriListener listener, string knownUri)
        {
            string currentUri;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                RemoveDeadUriListenersOr(listener);
                uriListeners.Add(new WeakReference<IShaderDocumentUriListener>(listener));
                currentUri = documentUri;
            }

            if (!UriEquals(knownUri, currentUri))
            {
                listener.OnDocumentUriChanged(knownUri, currentUri);
            }
        }

        internal void UnregisterUriListener(IShaderDocumentUriListener listener)
        {
            lock (gate)
            {
                RemoveDeadUriListenersOr(listener);
            }
        }

        internal void AttachDocument(ITextDocument value)
        {
            if (value == null || !ReferenceEquals(value.TextBuffer, buffer))
            {
                return;
            }

            ITextDocument previousDocument;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                previousDocument = document;
                if (!ReferenceEquals(previousDocument, value))
                {
                    if (previousDocument != null)
                    {
                        previousDocument.FileActionOccurred -= OnFileActionOccurred;
                    }

                    document = value;
                    value.FileActionOccurred += OnFileActionOccurred;
                }
            }

            UpdateDocumentUri(value, NormalizeFilePath(value.FilePath));
        }

        internal void DetachDocument(ITextDocument value)
        {
            string oldUri;
            IShaderDocumentUriListener[] listeners;
            lock (gate)
            {
                if (disposed || !ReferenceEquals(document, value))
                {
                    return;
                }

                document.FileActionOccurred -= OnFileActionOccurred;
                document = null;
                oldUri = documentUri;
                documentUri = null;
                listeners = GetUriListeners();
            }

            NotifyUriChanged(listeners, oldUri, null);
        }

        internal ShaderInactiveCodeTagger GetInactiveTagger()
        {
            lock (gate)
            {
                if (disposed || inactiveClassification == null)
                {
                    return null;
                }

                return inactiveTagger ??= new ShaderInactiveCodeTagger(buffer, documentUri, inactiveClassification);
            }
        }

        internal void Connect()
        {
            lock (gate)
            {
                if (!disposed)
                {
                    connections++;
                }
            }
        }

        internal bool Disconnect()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return false;
                }

                if (connections > 0)
                {
                    connections--;
                }

                return connections == 0;
            }
        }

        public void Dispose()
        {
            ShaderFoldingTagger folding;
            ShaderInactiveCodeTagger inactive;
            ITextDocument currentDocument;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                folding = foldingTagger;
                inactive = inactiveTagger;
                currentDocument = document;
                document = null;
                documentUri = null;
                foldingTagger = null;
                inactiveTagger = null;
                uriListeners.Clear();
            }

            if (currentDocument != null)
            {
                currentDocument.FileActionOccurred -= OnFileActionOccurred;
            }

            folding?.Dispose();
            inactive?.Dispose();
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (sender is not ITextDocument source)
            {
                return;
            }

            string path = string.IsNullOrWhiteSpace(e?.FilePath) ? source.FilePath : e.FilePath;
            UpdateDocumentUri(source, NormalizeFilePath(path));
        }

        private void UpdateDocumentUri(ITextDocument source, string newUri)
        {
            string oldUri;
            IShaderDocumentUriListener[] listeners;
            lock (gate)
            {
                if (disposed || !ReferenceEquals(document, source) || UriEquals(documentUri, newUri))
                {
                    return;
                }

                oldUri = documentUri;
                documentUri = newUri;
                listeners = GetUriListeners();
            }

            NotifyUriChanged(listeners, oldUri, newUri);
        }

        private IShaderDocumentUriListener[] GetUriListeners()
        {
            List<IShaderDocumentUriListener> result = [];
            if (foldingTagger != null)
            {
                result.Add(foldingTagger);
            }

            if (inactiveTagger != null)
            {
                result.Add(inactiveTagger);
            }

            for (int index = uriListeners.Count - 1; index >= 0; index--)
            {
                if (uriListeners[index].TryGetTarget(out IShaderDocumentUriListener listener))
                {
                    result.Add(listener);
                }
                else
                {
                    uriListeners.RemoveAt(index);
                }
            }

            return result.ToArray();
        }

        private void RemoveDeadUriListenersOr(IShaderDocumentUriListener listener)
        {
            for (int index = uriListeners.Count - 1; index >= 0; index--)
            {
                if (!uriListeners[index].TryGetTarget(out IShaderDocumentUriListener target) || ReferenceEquals(target, listener))
                {
                    uriListeners.RemoveAt(index);
                }
            }
        }

        private static void NotifyUriChanged(
            IEnumerable<IShaderDocumentUriListener> listeners,
            string oldUri,
            string newUri)
        {
            if (UriEquals(oldUri, newUri))
            {
                return;
            }

            foreach (IShaderDocumentUriListener listener in listeners)
            {
                listener.OnDocumentUriChanged(oldUri, newUri);
            }
        }

        private static string NormalizeFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                return ShaderDiagnostics.NormalizeUri(new Uri(filePath, UriKind.Absolute).AbsoluteUri);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        internal static bool UriEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal interface IShaderDocumentUriListener
    {
        void OnDocumentUriChanged(string oldUri, string newUri);
    }

    [Export(typeof(ITextViewConnectionListener))]
    [ContentType(LanguageClient.ShaderContentTypes.Glsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Hlsl)]
    [ContentType(LanguageClient.ShaderContentTypes.Wgsl)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ShaderEditorConnectionListener : ITextViewConnectionListener
    {
        private readonly ShaderEditorBufferManager manager;

        [ImportingConstructor]
        public ShaderEditorConnectionListener(ShaderEditorBufferManager manager)
        {
            this.manager = manager;
        }

        public void SubjectBuffersConnected(
            ITextView textView,
            ConnectionReason reason,
            IReadOnlyCollection<ITextBuffer> subjectBuffers)
        {
            foreach (ITextBuffer buffer in subjectBuffers)
            {
                manager.Connect(buffer);
            }
        }

        public void SubjectBuffersDisconnected(
            ITextView textView,
            ConnectionReason reason,
            IReadOnlyCollection<ITextBuffer> subjectBuffers)
        {
            foreach (ITextBuffer buffer in subjectBuffers)
            {
                manager.Disconnect(buffer);
            }
        }
    }
}
