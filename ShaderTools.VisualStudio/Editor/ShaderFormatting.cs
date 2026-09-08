using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using ShaderTools.Core;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(ShaderFormatting))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ShaderFormatting
    {
        private readonly ITextDocumentFactoryService documents;
        private readonly ITextUndoHistoryRegistry undo;
        private readonly IEditorOperationsFactoryService editorOperations;
        private readonly ShaderFormatOnSave saveListener;
        private readonly HashSet<ITextBuffer> pending = [];
        private int viewCount;

        [ImportingConstructor]
        internal ShaderFormatting(ITextDocumentFactoryService documents, ITextUndoHistoryRegistry undo,
            IEditorOperationsFactoryService editorOperations, IVsEditorAdaptersFactoryService adapters)
        {
            this.documents = documents;
            this.undo = undo;
            this.editorOperations = editorOperations;
            saveListener = new ShaderFormatOnSave(this, adapters);
        }

        internal static bool Supports(ITextBuffer buffer)
        {
            return buffer != null &&
            (buffer.ContentType.IsOfType(ShaderContentTypes.Glsl) ||
             (ShaderOptions.Current.EnableHlsl && buffer.ContentType.IsOfType(ShaderContentTypes.Hlsl)));
        }

        internal void Attach(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (viewCount++ == 0)
            {
                saveListener.Start();
            }

            view.Closed += OnViewClosed;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ((IWpfTextView)sender).Closed -= OnViewClosed;
            if (--viewCount == 0)
            {
                saveListener.Stop();
            }
        }

        internal void Format(ITextBuffer buffer, IWpfTextView view = null, Span? selection = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Supports(buffer) || !documents.TryGetTextDocument(buffer, out ITextDocument document) ||
                buffer.IsReadOnly(new Span(0, buffer.CurrentSnapshot.Length)) || !pending.Add(buffer))
            {
                return;
            }

            try
            {
                ITextSnapshot snapshot = buffer.CurrentSnapshot;
                string path = document.FilePath;
                string text = snapshot.GetText();
                string formatDirectory = ShaderLanguageClient.FindFormatDirectory(ShaderOptions.Current.FormatDirectory);
                if (formatDirectory == null)
                {
                    throw new InvalidOperationException("clang-format.exe was not found. Set Tools > Options > Shader Tools > General > clang-format directory.");
                }

                // The native editor command must finish before VS saves the buffer. Do not depend
                // on LSP startup or on its asynchronously synchronized document snapshot.
                string formatted = ThreadHelper.JoinableTaskFactory.Run(() => Task.Run(() =>
                    ClangFormatter.FormatAsync(Path.Combine(formatDirectory, "clang-format.exe"), path, text,
                        CancellationToken.None, selection?.Start, selection?.Length)));

                if (text == formatted)
                {
                    return;
                }

                if (buffer.CurrentSnapshot != snapshot || document.FilePath != path)
                {
                    ShaderOutput.Log("Formatting skipped: the document changed while clang-format was running.");
                    return;
                }

                _=undo.TryGetHistory(buffer, out ITextUndoHistory history);
                using (ITextUndoTransaction transaction = history?.CreateTransaction("Format shader"))
                using (ITextEdit edit = buffer.CreateEdit(EditOptions.DefaultMinimalChange, null, this))
                {
                    IEditorOperations operations = view != null && !view.IsClosed ? editorOperations.GetEditorOperations(view) : null;
                    operations?.AddBeforeTextBufferChangePrimitive();
                    if (!edit.Replace(new Span(0, snapshot.Length), formatted))
                    {
                        return;
                    }

                    _=edit.Apply();
                    if (edit.Canceled)
                    {
                        return;
                    }

                    operations?.AddAfterTextBufferChangePrimitive();
                    transaction?.Complete();
                }
                ShaderOutput.Log("Formatted " + Path.GetFileName(path) + " with clang-format.");
            }
            catch (Exception ex)
            {
                // A failed formatter must not cancel Save or replace the document with its stdout.
                ShaderOutput.Log("Formatting failed: " + ex.Message);
                if (view != null)
                {
                    BackgroundWork.Run(ShaderOutput.ShowAsync, "ShaderTools/FormattingError");
                }
            }
            finally { _=pending.Remove(buffer); }
        }
    }
}
