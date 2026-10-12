using Microsoft.Win32;
using ShaderTools.Core.Integration;
using ShaderTools.VisualStudio.LanguageClient;
using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace ShaderTools.VisualStudio.Services
{
    // Visual Assist ignores buffers whose content type is not on its list, which
    // includes Shader Tools' content types. It reads the list at startup, so
    // changes take effect after Visual Studio restarts.
    internal static class VisualAssistIntegration
    {
        private static readonly string[] ContentTypes = { ShaderContentTypes.Hlsl, ShaderContentTypes.Glsl };

        // Returns true when the Visual Assist setting was changed.
        public static bool Apply(bool enabled)
        {
            try
            {
                // Visual Assist keys its settings by the IDE's major file version.
                int major;
                using (Process process = Process.GetCurrentProcess())
                {
                    major = process.MainModule.FileVersionInfo.FileMajorPart;
                }

                using RegistryKey key = Registry.CurrentUser.OpenSubKey(VisualAssistContentTypes.GetKeyPath(major), writable: true);
                if (key == null)
                {
                    return false;
                }

                string current = key.GetValue(VisualAssistContentTypes.ValueName) as string ?? "";
                string updated = VisualAssistContentTypes.Update(current, ContentTypes, enabled);
                if (string.Equals(current, updated, StringComparison.Ordinal))
                {
                    return false;
                }

                if (updated.Length == 0)
                {
                    key.DeleteValue(VisualAssistContentTypes.ValueName, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(VisualAssistContentTypes.ValueName, updated, RegistryValueKind.String);
                }

                ShaderOutput.Log((enabled ? "Enabled" : "Disabled") + " Visual Assist in shader files. Restart Visual Studio to apply.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShaderOutput.Log("Visual Assist integration: " + ex.Message);
                return false;
            }
        }
    }
}
