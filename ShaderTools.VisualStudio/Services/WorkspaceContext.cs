using System;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ShaderTools.Core.Protocol;

namespace ShaderTools.VisualStudio.Services
{
    internal static class WorkspaceContext
    {
        public static async Task<string> GetRootAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var solution = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution != null)
            {
                solution.GetSolutionInfo(out string directory, out _, out _);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return Path.GetFullPath(directory);
            }
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE)) as DTE;
            string activeFile = dte?.ActiveDocument?.FullName;
            return !string.IsNullOrEmpty(activeFile) ? ProtocolAdapter.FindWorkspaceRoot(activeFile) : "";
        }

        public static async Task<string> GetActiveFileAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE)) as DTE;
            return dte?.ActiveDocument?.FullName;
        }

        public static async Task OpenFileAsync(string path, int line = 0, int column = 0)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE)) as DTE;
            if (dte?.ActiveDocument?.Selection is TextSelection selection) selection.MoveToLineAndOffset(line + 1, column + 1);
        }
    }
}
