using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderTools.Core;
using ShaderTools.Core.Configuration;
using ShaderTools.Core.Protocol;

namespace ShaderTools.Bridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("Cannot run shader server: " + ex.Message); return 1; }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !new[] { "--server", "--workspace", "--settings", "--parent", "--format-directory" }.Contains(args[i]))
                    throw new ArgumentException("Expected --server PATH [--workspace PATH] [--settings PATH] [--parent PID] [--format-directory PATH].");
                arguments.Add(args[i], args[i + 1]);
            }
            if (!arguments.TryGetValue("--server", out string server) || !File.Exists(server))
                throw new FileNotFoundException("Shader language server is missing. Reinstall Shader Tools or check its server path.", server);
            arguments.TryGetValue("--workspace", out string root);
            arguments.TryGetValue("--settings", out string settingsPath);
            string configurationRoot = string.IsNullOrWhiteSpace(root) ? Environment.CurrentDirectory : Path.GetFullPath(root);
            var settingsGate = new object();
            // Last known good settings remain active during partial saves or invalid edits.
            JObject currentSettings = ShaderConfiguration.Load(configurationRoot, settingsPath).Settings;
            var adapter = new ProtocolAdapter(root, workspace =>
            {
                lock (settingsGate)
                {
                    try { currentSettings = ShaderConfiguration.Load(string.IsNullOrEmpty(workspace) ? configurationRoot : workspace, settingsPath).Settings; }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is ArgumentException || ex is UnauthorizedAccessException)
                    { Log("Settings unchanged: " + ex.Message); }
                    return (JObject)currentSettings.DeepClone();
                }
            }, Log);

            var start = new ProcessStartInfo(Path.GetFullPath(server))
            {
                Arguments = "--stdio --glsl --hlsl --wgsl --config " + WindowsCommandLine.Quote(currentSettings.ToString(Formatting.None)) +
                    " --cwd " + WindowsCommandLine.Quote(configurationRoot),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(server))!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (arguments.TryGetValue("--format-directory", out string formatDirectory) && Directory.Exists(formatDirectory))
                start.EnvironmentVariables["PATH"] = formatDirectory + Path.PathSeparator + start.EnvironmentVariables["PATH"];

            using (var job = ProcessJob.CreateKillOnClose())
            using (var lifetime = new CancellationTokenSource())
            using (var child = new Process { StartInfo = start })
            {
                child.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
                child.Start();
                try
                {
                    job.Add(child);
                }
                catch
                {
                    if (!child.HasExited) child.Kill();
                    throw;
                }
                child.BeginErrorReadLine();
                Log("Started shader-language-server 1.4.3 (PID " + child.Id + "). Workspace: " + configurationRoot);
                var relay = new LspRelay(adapter, Log);
                Task monitor = MonitorParentAsync(arguments, lifetime);
                using (var input = Console.OpenStandardInput())
                using (var output = Console.OpenStandardOutput())
                {
                    try
                    {
                        await relay.RunAsync(input, output, child.StandardInput.BaseStream, child.StandardOutput.BaseStream, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    finally
                    {
                        lifetime.Cancel();
                        // EOF asks the server to exit; enforce a deadline if it is compiling or hung.
                        try { child.StandardInput.Close(); } catch (IOException) { }
                        if (!child.WaitForExit(2000)) child.Kill();
                        child.WaitForExit(2000);
                        await monitor.ConfigureAwait(false);
                    }
                }
                return 0;
            }
        }

        private static async Task MonitorParentAsync(Dictionary<string, string> arguments, CancellationTokenSource lifetime)
        {
            if (!arguments.TryGetValue("--parent", out string parentText) || !int.TryParse(parentText, out int parentId)) return;
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                    while (!lifetime.IsCancellationRequested)
                    {
                        if (parent.HasExited) { lifetime.Cancel(); return; }
                        await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                    }
            }
            catch (OperationCanceledException) { }
            catch (ArgumentException) { lifetime.Cancel(); }
            catch (InvalidOperationException) { lifetime.Cancel(); }
        }

        private static void Log(string message) => Console.Error.WriteLine("[Shader Tools] " + message);
    }
}
