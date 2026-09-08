using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ShaderTools.VisualStudio.Services
{
    // Own fire-and-forget work so VS can join it during extension teardown.
    internal static class BackgroundWork
    {
        private static readonly JoinableTaskCollection Tasks = ThreadHelper.JoinableTaskContext.CreateCollection();
        private static readonly JoinableTaskFactory Factory = ThreadHelper.JoinableTaskContext.CreateFactory(Tasks);
        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        public static CancellationToken Token => Lifetime.Token;

        public static void Run(Func<Task> operation, string name)
        {
            if (Lifetime.IsCancellationRequested) return;
            Factory.RunAsync(async () =>
            {
                try { if (!Lifetime.IsCancellationRequested) await operation(); }
                catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
            }).FileAndForget(name);
        }

        public static void Stop()
        {
            if (Lifetime.IsCancellationRequested) return;
            Lifetime.Cancel();
            Factory.Run(() => Tasks.JoinTillEmptyAsync());
        }
    }
}
