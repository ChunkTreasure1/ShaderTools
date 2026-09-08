using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

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
            while (Pending.Count > 500)
            {
                _=Pending.TryDequeue(out _);
            }

            if (Interlocked.Exchange(ref draining, 1) != 0)
            {
                return;
            }

            BackgroundWork.Run(async () =>
            {
                try
                {
                    await EnsurePaneAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    while (Pending.TryDequeue(out string line))
                    {
                        _=(pane?.OutputStringThreadSafe(line));
                    }
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
                finally { _=Interlocked.Exchange(ref draining, 0); }
            }, "ShaderTools/Output");
        }

        public static async Task ShowAsync()
        {
            await EnsurePaneAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _=(pane?.Activate());
            Guid outputWindow = VSConstants.StandardToolWindows.Output;
            if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell)) is IVsUIShell shell && ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref outputWindow, out IVsWindowFrame frame)))
            {
                _=frame.Show();
            }
        }

        private static async Task EnsurePaneAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (pane != null)
            {
                return;
            }

            if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsOutputWindow)) is not IVsOutputWindow window)
            {
                return;
            }

            Guid id = PaneId;
            _=window.CreatePane(ref id, "Shader Tools", 1, 0);
            _=window.GetPane(ref id, out pane);
        }
    }
}
