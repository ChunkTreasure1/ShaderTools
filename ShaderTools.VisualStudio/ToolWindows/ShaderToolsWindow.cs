using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ShaderTools.VisualStudio.ToolWindows
{
    [Guid("B34AEC9D-66DF-42EE-9EA2-A712AE729A75")]
    public sealed class ShaderToolsWindow : ToolWindowPane
    {
        public ShaderToolsWindow() : base(null)
        {
            Caption = "Shader Explorer";
            Content = new ShaderToolsControl();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
