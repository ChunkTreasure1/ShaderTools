using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ShaderTools.VisualStudio.Services
{
    internal static class ShaderOutput
    {
        private static readonly Guid PaneId = new Guid("51660E2F-6B10-48A0-B2E3-89A369B51DEE");
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static IVsOutputWindowPane pane;
        private static int draining;

        public static void Log(string message)
        {
            Pending.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            while (Pending.Count > 500) Pending.TryDequeue(out _);
            if (Interlocked.Exchange(ref draining, 1) != 0) return;
            BackgroundWork.Run(async () =>
            {
                try
                {
                    await EnsurePaneAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    while (Pending.TryDequeue(out string line)) pane?.OutputStringThreadSafe(line);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
                finally { Interlocked.Exchange(ref draining, 0); }
            }, "ShaderTools/Output");
        }

        public static async Task ShowAsync()
        {
            await EnsurePaneAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Activate();
            var shell = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            var outputWindow = VSConstants.StandardToolWindows.Output;
            if (shell != null && ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref outputWindow, out IVsWindowFrame frame))) frame.Show();
        }

        private static async Task EnsurePaneAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (pane != null) return;
            var window = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (window == null) return;
            var id = PaneId;
            window.CreatePane(ref id, "Shader Tools", 1, 0);
            window.GetPane(ref id, out pane);
        }
    }
}
