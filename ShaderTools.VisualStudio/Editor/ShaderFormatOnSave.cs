using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using System;
using System.Runtime.InteropServices;

namespace ShaderTools.VisualStudio.Editor
{
    // RDT notifications cover Save, Save All and the save prompt when closing a document.
    // Formatting happens before VS writes the file, without a second save or a dirty buffer.
    internal sealed class ShaderFormatOnSave : IVsRunningDocTableEvents3
    {
        private readonly ShaderFormatting formatting;
        private readonly IVsEditorAdaptersFactoryService adapters;
        private IVsRunningDocumentTable documents;
        private uint cookie;

        internal ShaderFormatOnSave(ShaderFormatting formatting, IVsEditorAdaptersFactoryService adapters)
        {
            this.formatting = formatting;
            this.adapters = adapters;
        }

        internal void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            documents = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (documents != null)
            {
                _=ErrorHandler.ThrowOnFailure(documents.AdviseRunningDocTableEvents(this, out cookie));
            }
        }

        internal void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (cookie != 0)
            {
                _=(documents?.UnadviseRunningDocTableEvents(cookie));
            }

            documents = null;
            cookie = 0;
        }

        public int OnBeforeSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!ShaderOptions.Current.FormatOnSave || documents == null)
            {
                return VSConstants.S_OK;
            }

            IntPtr data = IntPtr.Zero;
            try
            {
                _=ErrorHandler.ThrowOnFailure(documents.GetDocumentInfo(docCookie, out _, out _, out _, out _, out _, out _, out data));
                if (data != IntPtr.Zero && Marshal.GetObjectForIUnknown(data) is IVsTextBuffer nativeBuffer)
                {
                    ITextBuffer buffer = adapters.GetDocumentBuffer(nativeBuffer);
                    if (ShaderFormatting.Supports(buffer))
                    {
                        formatting.Format(buffer);
                    }
                }
            }
            catch (Exception ex) { ShaderOutput.Log("Formatting on save failed: " + ex.Message); }
            finally
            {
                if (data != IntPtr.Zero)
                {
                    _=Marshal.Release(data);
                }
            }
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterAttributeChange(uint docCookie, uint attributes)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterAttributeChangeEx(uint docCookie, uint attributes, IVsHierarchy oldHierarchy, uint oldItem, string oldName,
                    IVsHierarchy newHierarchy, uint newItem, string newName)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame frame)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint lockType, uint readLocks, uint editLocks)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeDocumentWindowShow(uint docCookie, int firstShow, IVsWindowFrame frame)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint lockType, uint readLocks, uint editLocks)
        {
            return VSConstants.S_OK;
        }
    }
}
