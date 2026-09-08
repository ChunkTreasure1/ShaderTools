using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ShaderTools.VisualStudio.Editor
{
    internal static class ShaderUiNotifications
    {
        private static readonly JoinableTaskCollection PendingTasks =
            ThreadHelper.JoinableTaskContext.CreateCollection();
        private static readonly JoinableTaskFactory Factory =
            ThreadHelper.JoinableTaskContext.CreateFactory(PendingTasks);

        internal static void Post(Action action)
        {
            if (action == null)
            {
                return;
            }

            _ = Factory.RunAsync(async () =>
            {
                await Factory.SwitchToMainThreadAsync();
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // A view can close between queuing and delivering a notification.
                }
            });
        }
    }

    internal interface IShaderOptionsListener
    {
        void OnShaderOptionsChanged();
    }

    internal static class ShaderOptionChanges
    {
        private static readonly object Gate = new object();
        private static readonly List<WeakReference<IShaderOptionsListener>> Listeners =
            new List<WeakReference<IShaderOptionsListener>>();

        static ShaderOptionChanges()
        {
            Options.ShaderOptions.Changed += OnChanged;
        }

        internal static void Register(IShaderOptionsListener listener)
        {
            if (listener == null)
            {
                return;
            }

            lock (Gate)
            {
                RemoveDeadOr(listener);
                Listeners.Add(new WeakReference<IShaderOptionsListener>(listener));
            }
        }

        internal static void Unregister(IShaderOptionsListener listener)
        {
            lock (Gate)
            {
                RemoveDeadOr(listener);
            }
        }

        private static void OnChanged(object sender, EventArgs e)
        {
            IShaderOptionsListener[] live;
            lock (Gate)
            {
                RemoveDeadOr(null);
                live = Listeners.Select(reference =>
                {
                    reference.TryGetTarget(out var target);
                    return target;
                }).Where(target => target != null).ToArray();
            }

            foreach (var listener in live)
            {
                listener.OnShaderOptionsChanged();
            }
        }

        private static void RemoveDeadOr(IShaderOptionsListener listener)
        {
            for (var index = Listeners.Count - 1; index >= 0; index--)
            {
                if (!Listeners[index].TryGetTarget(out var target) || ReferenceEquals(target, listener))
                {
                    Listeners.RemoveAt(index);
                }
            }
        }
    }
}
