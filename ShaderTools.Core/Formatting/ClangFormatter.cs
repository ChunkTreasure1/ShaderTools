using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Core
{
    public static class ClangFormatter
    {
        // Use the editor snapshot on stdin and the real path only for configuration lookup.
        // Never format the saved file in place: it may not contain the user's latest edits.
        public static async Task<string> FormatAsync(string executable, string filePath, string text,
            CancellationToken cancellationToken, int? selectionStart = null, int? selectionLength = null)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (selectionStart.HasValue != selectionLength.HasValue ||
                selectionStart < 0 || selectionLength < 0 ||
                (selectionStart.HasValue && (selectionStart.Value > text.Length || selectionLength!.Value > text.Length - selectionStart.Value)))
            {
                throw new ArgumentOutOfRangeException(nameof(selectionStart));
            }

            cancellationToken.ThrowIfCancellationRequested();
            string arguments = "--style=file " + WindowsCommandLine.Quote("--assume-filename=" + filePath);
            if (selectionStart.HasValue)
            {
                int offset = Encoding.UTF8.GetByteCount(text.Substring(0, selectionStart.Value));
                int length = Encoding.UTF8.GetByteCount(text.Substring(selectionStart.Value, selectionLength!.Value));
                arguments += " --offset=" + offset.ToString(CultureInfo.InvariantCulture) + " --length=" + length.ToString(CultureInfo.InvariantCulture);
            }

            using Process process = new Process();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            process.StartInfo = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(filePath)
            };
            TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => exited.TrySetResult(true);
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start clang-format.");
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task input = WriteInputAsync(process, text);
            Task completed = Task.WhenAll(exited.Task, input, output, errors);
            try
            {
                Task limit = Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
                if (await Task.WhenAny(completed, limit).ConfigureAwait(false) != completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("clang-format did not finish within 5 seconds.");
                }

                // A formatter rejecting its config may close stdin early. Prefer its diagnostic
                // over the resulting broken-pipe error from the writer.
                _=await exited.Task.ConfigureAwait(false);
                string diagnostic = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("clang-format failed: " + diagnostic.Trim());
                }

                await completed.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await output.ConfigureAwait(false);
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                // Observe pipe errors even when cancellation or an early process exit won.
                _ = completed.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private static async Task WriteInputAsync(Process process, string text)
        {
            using StreamWriter writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            await writer.WriteAsync(text).ConfigureAwait(false);
        }
    }
}
