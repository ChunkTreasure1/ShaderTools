using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Services;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(INavigableSymbolSourceProvider))]
    [Name("Shader Tools navigation")]
    [ContentType(ShaderContentTypes.Glsl)]
    [ContentType(ShaderContentTypes.Hlsl)]
    [ContentType(ShaderContentTypes.Wgsl)]
    [Order(Before = "default")]
    internal sealed class ShaderNavigableSymbolSourceProvider : INavigableSymbolSourceProvider
    {
        [Import] internal ShaderEditorBufferManager Buffers { get; set; }
        [Import] internal ITextStructureNavigatorSelectorService Navigators { get; set; }

        public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer)
            => new ShaderNavigableSymbolSource(buffer, Buffers.GetOrCreate(buffer), Navigators.GetTextStructureNavigator(buffer));
    }

    internal sealed class ShaderNavigableSymbolSource : INavigableSymbolSource
    {
        private readonly ITextBuffer buffer;
        private readonly ShaderEditorBufferState document;
        private readonly ITextStructureNavigator navigator;
        private volatile bool disposed;

        internal ShaderNavigableSymbolSource(ITextBuffer buffer, ShaderEditorBufferState document, ITextStructureNavigator navigator)
        {
            this.buffer = buffer;
            this.document = document;
            this.navigator = navigator;
        }

        public async Task<INavigableSymbol> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken token)
        {
            var snapshot = triggerSpan.Snapshot;
            var client = ShaderLanguageClient.Instance;
            string uri = document.DocumentUri;
            if (disposed || triggerSpan.IsEmpty || snapshot != buffer.CurrentSnapshot ||
                string.IsNullOrEmpty(uri) || client?.IsReady != true) return null;

            var line = triggerSpan.Start.GetContainingLine();
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                request.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var result = await client.RequestAsync("textDocument/definition", new JObject
                    {
                        ["textDocument"] = new JObject { ["uri"] = uri },
                        ["position"] = new JObject
                        {
                            ["line"] = line.LineNumber,
                            ["character"] = triggerSpan.Start.Position - line.Start.Position
                        }
                    }, request.Token).ConfigureAwait(false);
                    if (disposed || request.IsCancellationRequested || snapshot != buffer.CurrentSnapshot ||
                        !ShaderEditorBufferState.UriEquals(uri, document.DocumentUri)) return null;

                    var target = DefinitionLocation.Read(result).FirstOrDefault();
                    if (target == null) return null;
                    var symbolSpan = triggerSpan;
                    if (LspTextRange.TryParse(target.OriginRange, out var origin) &&
                        origin.TryGetSpan(snapshot, out var span) && span.Contains(triggerSpan.Start.Position))
                        symbolSpan = new SnapshotSpan(snapshot, span);
                    else
                    {
                        var word = navigator.GetExtentOfWord(triggerSpan.Start);
                        if (word.IsSignificant) symbolSpan = word.Span;
                    }
                    return new ShaderNavigableSymbol(symbolSpan, target, () =>
                        !disposed && snapshot == buffer.CurrentSnapshot && ShaderEditorBufferState.UriEquals(uri, document.DocumentUri));
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException ||
                    ex is StreamJsonRpc.RemoteInvocationException || ex is StreamJsonRpc.ConnectionLostException)
                {
                    // Navigation is queried while Ctrl is held, including during LSP startup/restarts.
                    return null;
                }
            }
        }

        public void Dispose() { disposed = true; }
    }

    internal sealed class ShaderNavigableSymbol : INavigableSymbol
    {
        private readonly DefinitionLocation target;
        private readonly Func<bool> isCurrent;
        internal ShaderNavigableSymbol(SnapshotSpan span, DefinitionLocation target, Func<bool> isCurrent)
        {
            SymbolSpan = span;
            this.target = target;
            this.isCurrent = isCurrent;
        }

        public SnapshotSpan SymbolSpan { get; }
        public IEnumerable<INavigableRelationship> Relationships => new[] { PredefinedNavigableRelationships.Definition };

        public void Navigate(INavigableRelationship relationship)
        {
            if (relationship?.Name != PredefinedNavigableRelationships.Definition.Name || !isCurrent()) return;
            BackgroundWork.Run(async () =>
            {
                if (isCurrent()) await WorkspaceContext.OpenFileAsync(target.FilePath, target.Line, target.Character);
            }, "ShaderTools/Navigate");
        }
    }
}
