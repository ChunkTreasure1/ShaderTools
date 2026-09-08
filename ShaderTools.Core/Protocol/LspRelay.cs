using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ShaderTools.Core.Protocol
{
    /// <summary>Forwards standard LSP unchanged, except for the explicit VS adaptations.</summary>
    public sealed class LspRelay
    {
        private readonly ProtocolAdapter adapter;
        private readonly PositionEncodingConverter positions = new PositionEncodingConverter();
        private readonly SemaphoreSlim serverWriter = new SemaphoreSlim(1, 1);
        private readonly Action<string> log;

        public LspRelay(ProtocolAdapter adapter, Action<string>? log = null)
        {
            this.adapter = adapter;
            this.log = log ?? (_ => { });
        }

        // The caller owns the streams and closes them when this task returns. This also unblocks
        // ReadAsync on .NET Framework's console streams, which do not support mid-read cancellation.
        public async Task RunAsync(Stream clientInput, Stream clientOutput, Stream serverInput, Stream serverOutput, CancellationToken token)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var fromClient = PumpClientAsync(clientInput, serverInput, cancellation.Token);
                var fromServer = PumpServerAsync(serverOutput, serverInput, clientOutput, cancellation.Token);
                var cancelled = Task.Delay(Timeout.Infinite, cancellation.Token);
                var finished = await Task.WhenAny(fromClient, fromServer, cancelled).ConfigureAwait(false);
                cancellation.Cancel();
                Observe(fromClient);
                Observe(fromServer);
                await finished.ConfigureAwait(false);
            }
        }

        private async Task PumpClientAsync(Stream source, Stream serverInput, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var message = await LspFraming.ReadAsync(source, token).ConfigureAwait(false);
                if (message == null) return;
                positions.ProcessClientMessage(message);
                await SendServerAsync(serverInput, adapter.AdaptClientMessage(message), token).ConfigureAwait(false);
                if ((string?)message["method"] == "initialized")
                    await NotifyConfigurationAsync(serverInput, token).ConfigureAwait(false);
                if ((string?)message["method"] == "exit") return;
            }
        }

        private async Task PumpServerAsync(Stream source, Stream serverInput, Stream clientOutput, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var message = await LspFraming.ReadAsync(source, token).ConfigureAwait(false);
                if (message == null) return;
                positions.ProcessServerMessage(message);
                JObject? response;
                try { response = adapter.GetServerRequestResponse(message); }
                catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException)
                {
                    log(ex.Message);
                    response = new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]?.DeepClone(),
                        ["error"] = new JObject { ["code"] = -32603, ["message"] = ex.Message } };
                }
                if (response != null) await SendServerAsync(serverInput, response, token).ConfigureAwait(false);
                else
                {
                    await LspFraming.WriteAsync(clientOutput, message, token).ConfigureAwait(false);
                    // VS middleware handles outbound traffic. A custom inbound notification feeds
                    // editor adornments while the original diagnostics still reach native Error List.
                    if ((string?)message["method"] == "textDocument/publishDiagnostics")
                        await LspFraming.WriteAsync(clientOutput, new JObject { ["jsonrpc"] = "2.0", ["method"] = "shaderTools/diagnostics", ["params"] = message["params"]!.DeepClone() }, token).ConfigureAwait(false);
                }
            }
        }

        public Task NotifyConfigurationAsync(Stream serverInput, CancellationToken token) => SendServerAsync(serverInput,
            new JObject { ["jsonrpc"] = "2.0", ["method"] = "workspace/didChangeConfiguration", ["params"] = new JObject { ["settings"] = new JObject() } }, token);

        private async Task SendServerAsync(Stream stream, JObject message, CancellationToken token)
        {
            await serverWriter.WaitAsync(token).ConfigureAwait(false);
            try { await LspFraming.WriteAsync(stream, message, token).ConfigureAwait(false); }
            finally { serverWriter.Release(); }
        }

        private void Observe(Task task) => _ = task.ContinueWith(t => log("LSP connection ended: " + t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
