using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ShaderTools.VisualStudio.LanguageClient;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Services
{
    // Solution and Open Folder have different event interfaces in Visual Studio.
    internal sealed class WorkspaceMonitor : IVsSolutionEvents, IVsSolutionEvents7, IDisposable
    {
        private IVsSolution solution;
        private uint cookie;
        private WindowEvents windowEvents;
        private readonly Action changed;
        internal WorkspaceMonitor(Action changed) { this.changed = changed; }

        internal async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            solution = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution != null)
            {
                _=ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out cookie));
            }

            DTE dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE)) as DTE;
            windowEvents = dte?.Events.WindowEvents;
            if (windowEvents != null)
            {
                windowEvents.WindowActivated += OnWindowActivated;
            }
        }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (solution != null && cookie != 0)
            {
                _=solution.UnadviseSolutionEvents(cookie);
            }

            solution = null;
            cookie = 0;
            if (windowEvents != null)
            {
                windowEvents.WindowActivated -= OnWindowActivated;
            }

            windowEvents = null;
        }
        private void OnWindowActivated(Window focused, Window previous)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Already open loose files do not send didOpen when the user switches tabs.
                // The client compares roots, so a normal solution tab switch is inexpensive.
                if (ShaderContentTypes.ForPath(focused?.Document?.FullName) != null)
                {
                    changed();
                }
            }
            catch (COMException) { } // VS can invalidate a closing document during this callback.
        }
        public int OnAfterOpenSolution(object reserved, int newSolution) { changed(); return VSConstants.S_OK; }
        public int OnAfterCloseSolution(object reserved) { changed(); return VSConstants.S_OK; }
        public void OnAfterOpenFolder(string path)
        {
            changed();
        }

        public void OnAfterCloseFolder(string path)
        {
            changed();
        }

        public void OnBeforeCloseFolder(string path) { }
        public void OnQueryCloseFolder(string path, ref int cancel) { }
        public void OnAfterLoadAllDeferredProjects() { }
        public int OnAfterOpenProject(IVsHierarchy hierarchy, int added)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseProject(IVsHierarchy hierarchy, int removing, ref int cancel)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseProject(IVsHierarchy hierarchy, int removed)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterLoadProject(IVsHierarchy stub, IVsHierarchy real)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryUnloadProject(IVsHierarchy real, ref int cancel)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeUnloadProject(IVsHierarchy real, IVsHierarchy stub)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution(object reserved, ref int cancel)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object reserved)
        {
            return VSConstants.S_OK;
        }
    }
}
