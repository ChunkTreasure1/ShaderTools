using Microsoft.VisualStudio.Shell;
using ShaderTools.VisualStudio.Commands;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using ShaderTools.VisualStudio.ToolWindows;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(ShaderOptions), "Shader Tools", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(ShaderToolsWindow))]
    public sealed class ShaderToolsPackage : AsyncPackage
    {
        public const string PackageGuidString = "ffe86866-e88b-483c-ba23-3346fff7bde5";
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await ShaderCommands.InitializeAsync(this);
        }
        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                ShaderLanguageClient.Instance?.Dispose();
                BackgroundWork.Stop();
            }
            base.Dispose(disposing);
        }
    }
}
