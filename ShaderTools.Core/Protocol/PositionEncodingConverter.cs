using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShaderTools.Core.Protocol
{
    /// <summary>
    /// Adapts Visual Studio's UTF-16 LSP positions to the Unicode-scalar positions used by
    /// shader-language-server 1.4.3, and converts server results back to UTF-16.
    /// </summary>
    public sealed class PositionEncodingConverter
    {
        private const int MaximumOpenDocuments = 256;
        private const int MaximumPendingRequests = 512;
        private const int MaximumDiskDocuments = 128;

        private readonly object gate = new object();
        private readonly Dictionary<string, string> openDocuments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RequestContext> pendingRequests = new Dictionary<string, RequestContext>(StringComparer.Ordinal);
        private readonly LinkedList<string> pendingOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> pendingNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DiskDocument> diskDocuments = new Dictionary<string, DiskDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> diskOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> diskNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);

        public void ProcessClientMessage(JObject message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (gate)
            {
                var method = (string?)message["method"];
                if (method == null)
                {
                    return;
                }

                switch (method)
                {
                    case "textDocument/didOpen":
                        ProcessDidOpen(message);
                        return;
                    case "textDocument/didChange":
                        ProcessDidChange(message);
                        return;
                    case "textDocument/didClose":
                        ProcessDidClose(message);
                        return;
                    case "$/cancelRequest":
                        RemovePending(message.SelectToken("params.id"));
                        return;
                    case "shutdown":
                    case "exit":
                        Clear();
                        return;
                }

                var parameters = message["params"] as JObject;
                var uri = (string?)parameters?.SelectToken("textDocument.uri");
                var text = GetText(uri, null);
                if (message["id"] != null && NeedsResponseConversion(method))
                {
                    AddPending(message["id"]!, new RequestContext(method, NormalizeUri(uri), text));
                }

                ConvertClientParameters(method, parameters, text);
            }
        }

        public void ProcessServerMessage(JObject message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (gate)
            {
                var method = (string?)message["method"];
                if (method != null)
                {
                    ProcessServerNotificationOrRequest(method, message);
                    return;
                }

                if (message["id"] == null || (message["result"] == null && message["error"] == null))
                {
                    return;
                }

                var key = RequestKey(message["id"]!);
                if (!pendingRequests.TryGetValue(key, out var context))
                {
                    return;
                }

                RemovePendingKey(key);
                var result = message["result"];
                if (result == null || result.Type == JTokenType.Null)
                {
                    return;
                }

                ConvertServerResult(context, result);
            }
        }

        private void ProcessDidOpen(JObject message)
        {
            var document = message.SelectToken("params.textDocument") as JObject;
            var uri = NormalizeUri((string?)document?["uri"]);
            var text = (string?)document?["text"];
            if (uri == null || text == null)
            {
                return;
            }

            SetOpenDocument(uri, text);
        }

        private void ProcessDidChange(JObject message)
        {
            var parameters = message["params"] as JObject;
            var uri = NormalizeUri((string?)parameters?.SelectToken("textDocument.uri"));
            var changes = parameters?["contentChanges"] as JArray;
            if (uri == null || changes == null)
            {
                return;
            }

            openDocuments.TryGetValue(uri, out var workingText);
            var convertedChanges = (JArray)changes.DeepClone();
            for (var index = 0; index < changes.Count; index++)
            {
                if (!(changes[index] is JObject original) || !(convertedChanges[index] is JObject converted))
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                var replacement = (string?)original["text"];
                if (replacement == null)
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                var originalRange = original["range"] as JObject;
                if (originalRange == null)
                {
                    if (original["range"] != null && original["range"]!.Type != JTokenType.Null)
                    {
                        InvalidateOpenDocument(uri);
                        return;
                    }

                    workingText = replacement;
                    continue;
                }

                if (workingText == null || !(converted["range"] is JObject convertedRange))
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                if (!TryConvertRange(convertedRange, workingText, toServer: true) ||
                    !TryApplyUtf16Edit(workingText, originalRange, replacement, out workingText))
                {
                    InvalidateOpenDocument(uri);
                    return;
                }
            }

            parameters!["contentChanges"] = convertedChanges;
            if (workingText != null)
            {
                SetOpenDocument(uri, workingText);
            }
        }

        private void ProcessDidClose(JObject message)
        {
            var uri = NormalizeUri((string?)message.SelectToken("params.textDocument.uri"));
            if (uri == null)
            {
                return;
            }

            openDocuments.Remove(uri);
            var removals = new List<string>();
            foreach (var pair in pendingRequests)
            {
                if (string.Equals(pair.Value.Uri, uri, StringComparison.OrdinalIgnoreCase))
                {
                    removals.Add(pair.Key);
                }
            }

            foreach (var key in removals)
            {
                RemovePendingKey(key);
            }
        }

        private void ConvertClientParameters(string method, JObject? parameters, string? text)
        {
            if (parameters == null || text == null)
            {
                return;
            }

            switch (method)
            {
                case "textDocument/completion":
                case "textDocument/hover":
                case "textDocument/signatureHelp":
                case "textDocument/definition":
                case "textDocument/declaration":
                case "textDocument/typeDefinition":
                case "textDocument/implementation":
                case "textDocument/references":
                case "textDocument/documentHighlight":
                case "textDocument/rename":
                case "textDocument/prepareRename":
                case "textDocument/onTypeFormatting":
                    TryConvertPosition(parameters["position"] as JObject, text, toServer: true);
                    break;
                case "textDocument/rangeFormatting":
                case "textDocument/inlayHint":
                case "textDocument/semanticTokens/range":
                case "textDocument/colorPresentation":
                    TryConvertRange(parameters["range"] as JObject, text, toServer: true);
                    break;
                case "textDocument/codeAction":
                    TryConvertRange(parameters["range"] as JObject, text, toServer: true);
                    ConvertDiagnostics(parameters.SelectToken("context.diagnostics") as JArray, null, text, null, toServer: true);
                    break;
                case "textDocument/selectionRange":
                    ConvertPositions(parameters["positions"] as JArray, text, toServer: true);
                    break;
            }
        }

        private void ProcessServerNotificationOrRequest(string method, JObject message)
        {
            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    var uri = (string?)message.SelectToken("params.uri");
                    ConvertDiagnostics(
                        message.SelectToken("params.diagnostics") as JArray,
                        uri,
                        GetText(uri, null),
                        null);
                    break;
                case "workspace/applyEdit":
                    ConvertWorkspaceEdit(message.SelectToken("params.edit") as JObject, null);
                    break;
            }
        }

        private void ConvertServerResult(RequestContext context, JToken result)
        {
            var text = context.Text ?? GetText(context.Uri, context);
            switch (context.Method)
            {
                case "textDocument/completion":
                    ConvertCompletion(result, text);
                    break;
                case "textDocument/hover":
                    TryConvertRange((result as JObject)?["range"] as JObject, text, toServer: false);
                    break;
                case "textDocument/foldingRange":
                    ConvertFoldingRanges(result as JArray, text);
                    break;
                case "textDocument/inlayHint":
                    ConvertInlayHints(result as JArray, context, text);
                    break;
                case "textDocument/documentSymbol":
                    ConvertDocumentSymbols(result as JArray, text, context);
                    break;
                case "workspace/symbol":
                    ConvertWorkspaceSymbols(result as JArray, context);
                    break;
                case "textDocument/definition":
                case "textDocument/declaration":
                case "textDocument/typeDefinition":
                case "textDocument/implementation":
                case "textDocument/references":
                    ConvertLocations(result, context, text);
                    break;
                case "textDocument/documentHighlight":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/formatting":
                case "textDocument/rangeFormatting":
                case "textDocument/onTypeFormatting":
                    ConvertTextEdits(result as JArray, text);
                    break;
                case "textDocument/prepareRename":
                    if (result is JObject prepareResult)
                    {
                        if (prepareResult["start"] != null)
                        {
                            TryConvertRange(prepareResult, text, toServer: false);
                        }
                        else
                        {
                            TryConvertRange(prepareResult["range"] as JObject, text, toServer: false);
                        }
                    }
                    break;
                case "textDocument/rename":
                    ConvertWorkspaceEdit(result as JObject, context);
                    break;
                case "textDocument/selectionRange":
                    ConvertSelectionRanges(result as JArray, text);
                    break;
                case "textDocument/codeAction":
                    ConvertCodeActions(result as JArray, context, text);
                    break;
                case "textDocument/documentColor":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/colorPresentation":
                    ConvertColorPresentations(result as JArray, text);
                    break;
                case "textDocument/documentLink":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/diagnostic":
                    ConvertDocumentDiagnosticReport(result as JObject, context.Uri, context);
                    break;
                case "workspace/diagnostic":
                    ConvertWorkspaceDiagnosticReport(result as JObject, context);
                    break;
                case "textDocument/semanticTokens/full":
                case "textDocument/semanticTokens/range":
                    ConvertSemanticTokens(result as JObject, text);
                    break;
            }
        }

        private static bool NeedsResponseConversion(string method)
        {
            switch (method)
            {
                case "textDocument/completion":
                case "textDocument/hover":
                case "textDocument/foldingRange":
                case "textDocument/inlayHint":
                case "textDocument/documentSymbol":
                case "workspace/symbol":
                case "textDocument/definition":
                case "textDocument/declaration":
                case "textDocument/typeDefinition":
                case "textDocument/implementation":
                case "textDocument/references":
                case "textDocument/documentHighlight":
                case "textDocument/formatting":
                case "textDocument/rangeFormatting":
                case "textDocument/onTypeFormatting":
                case "textDocument/prepareRename":
                case "textDocument/rename":
                case "textDocument/selectionRange":
                case "textDocument/codeAction":
                case "textDocument/documentColor":
                case "textDocument/colorPresentation":
                case "textDocument/documentLink":
                case "textDocument/diagnostic":
                case "workspace/diagnostic":
                case "textDocument/semanticTokens/full":
                case "textDocument/semanticTokens/range":
                    return true;
                default:
                    return false;
            }
        }

        private void ConvertCompletion(JToken result, string? text)
        {
            if (text == null)
            {
                return;
            }

            var items = result as JArray ?? (result as JObject)?["items"] as JArray;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item is JObject completion)
                    {
                        ConvertCompletionEdit(completion["textEdit"] as JObject, text);
                        ConvertTextEdits(completion["additionalTextEdits"] as JArray, text);
                    }
                }
            }

            var defaults = (result as JObject)?["itemDefaults"] as JObject;
            ConvertCompletionEdit(defaults?["editRange"] as JObject, text);
        }

        private static void ConvertCompletionEdit(JObject? edit, string text)
        {
            if (edit == null)
            {
                return;
            }

            if (edit["start"] != null)
            {
                TryConvertRange(edit, text, toServer: false);
            }
            else
            {
                TryConvertRange(edit["range"] as JObject, text, toServer: false);
                TryConvertRange(edit["insert"] as JObject, text, toServer: false);
                TryConvertRange(edit["replace"] as JObject, text, toServer: false);
            }
        }

        private static void ConvertFoldingRanges(JArray? ranges, string? text)
        {
            if (ranges == null || text == null)
            {
                return;
            }

            foreach (var token in ranges)
            {
                if (!(token is JObject range))
                {
                    continue;
                }

                TryConvertLineCharacter(range, "startLine", "startCharacter", text);
                TryConvertLineCharacter(range, "endLine", "endCharacter", text);
            }
        }

        private void ConvertInlayHints(JArray? hints, RequestContext context, string? text)
        {
            if (hints == null || text == null)
            {
                return;
            }

            foreach (var token in hints)
            {
                if (!(token is JObject hint))
                {
                    continue;
                }

                TryConvertPosition(hint["position"] as JObject, text, toServer: false);
                ConvertTextEdits(hint["textEdits"] as JArray, text);
                if (hint["label"] is JArray labelParts)
                {
                    foreach (var labelToken in labelParts)
                    {
                        if (labelToken is JObject label && label["location"] is JObject location)
                        {
                            ConvertLocation(location, context, text);
                        }
                    }
                }
            }
        }

        private void ConvertDocumentSymbols(JArray? symbols, string? text, RequestContext context)
        {
            if (symbols == null)
            {
                return;
            }

            foreach (var token in symbols)
            {
                if (!(token is JObject symbol))
                {
                    continue;
                }

                if (symbol["location"] is JObject location)
                {
                    ConvertLocation(location, context, text);
                }
                else if (text != null)
                {
                    TryConvertRange(symbol["range"] as JObject, text, toServer: false);
                    TryConvertRange(symbol["selectionRange"] as JObject, text, toServer: false);
                }

                ConvertDocumentSymbols(symbol["children"] as JArray, text, context);
            }
        }

        private void ConvertWorkspaceSymbols(JArray? symbols, RequestContext context)
        {
            if (symbols == null)
            {
                return;
            }

            foreach (var token in symbols)
            {
                if (token is JObject symbol && symbol["location"] is JObject location)
                {
                    ConvertLocation(location, context, null);
                }
            }
        }

        private void ConvertLocations(JToken result, RequestContext context, string? sourceText)
        {
            if (result is JArray locations)
            {
                foreach (var token in locations)
                {
                    if (token is JObject location)
                    {
                        ConvertLocation(location, context, sourceText);
                    }
                }
            }
            else if (result is JObject location)
            {
                ConvertLocation(location, context, sourceText);
            }
        }

        private void ConvertLocation(JObject location, RequestContext? context, string? sourceText)
        {
            if (location["targetUri"] != null)
            {
                TryConvertRange(location["originSelectionRange"] as JObject, sourceText, toServer: false);
                var targetUri = (string?)location["targetUri"];
                var targetText = GetText(targetUri, context);
                TryConvertRange(location["targetRange"] as JObject, targetText, toServer: false);
                TryConvertRange(location["targetSelectionRange"] as JObject, targetText, toServer: false);
                return;
            }

            var uri = (string?)location["uri"];
            var text = GetText(uri, context);
            TryConvertRange(location["range"] as JObject, text, toServer: false);
        }

        private static void ConvertTextEdits(JArray? edits, string? text)
        {
            if (edits == null || text == null)
            {
                return;
            }

            foreach (var token in edits)
            {
                if (token is JObject edit)
                {
                    TryConvertRange(edit["range"] as JObject, text, toServer: false);
                }
            }
        }

        private static void ConvertRangesOnArray(JArray? values, string propertyName, string? text)
        {
            if (values == null || text == null)
            {
                return;
            }

            foreach (var token in values)
            {
                if (token is JObject value)
                {
                    TryConvertRange(value[propertyName] as JObject, text, toServer: false);
                }
            }
        }

        private static void ConvertPositions(JArray? positions, string text, bool toServer)
        {
            if (positions == null)
            {
                return;
            }

            foreach (var token in positions)
            {
                TryConvertPosition(token as JObject, text, toServer);
            }
        }

        private static void ConvertSelectionRanges(JArray? values, string? text)
        {
            if (values == null || text == null)
            {
                return;
            }

            foreach (var token in values)
            {
                var current = token as JObject;
                while (current != null)
                {
                    TryConvertRange(current["range"] as JObject, text, toServer: false);
                    current = current["parent"] as JObject;
                }
            }
        }

        private void ConvertCodeActions(JArray? actions, RequestContext context, string? sourceText)
        {
            if (actions == null)
            {
                return;
            }

            foreach (var token in actions)
            {
                if (!(token is JObject action))
                {
                    continue;
                }

                ConvertDiagnostics(action["diagnostics"] as JArray, context.Uri, sourceText, context);
                ConvertWorkspaceEdit(action["edit"] as JObject, context);
            }
        }

        private static void ConvertColorPresentations(JArray? presentations, string? text)
        {
            if (presentations == null || text == null)
            {
                return;
            }

            foreach (var token in presentations)
            {
                if (token is JObject presentation)
                {
                    if (presentation["textEdit"] is JObject textEdit)
                    {
                        TryConvertRange(textEdit["range"] as JObject, text, toServer: false);
                    }

                    ConvertTextEdits(presentation["additionalTextEdits"] as JArray, text);
                }
            }
        }

        private void ConvertWorkspaceEdit(JObject? edit, RequestContext? context)
        {
            if (edit == null)
            {
                return;
            }

            if (edit["changes"] is JObject changes)
            {
                foreach (var property in changes.Properties())
                {
                    ConvertTextEdits(property.Value as JArray, GetText(property.Name, context));
                }
            }

            if (edit["documentChanges"] is JArray documentChanges)
            {
                foreach (var token in documentChanges)
                {
                    if (token is JObject documentEdit)
                    {
                        var uri = (string?)documentEdit.SelectToken("textDocument.uri");
                        ConvertTextEdits(documentEdit["edits"] as JArray, GetText(uri, context));
                    }
                }
            }
        }

        private void ConvertDiagnostics(JArray? diagnostics, string? uri, string? text, RequestContext? context, bool toServer = false)
        {
            if (diagnostics == null)
            {
                return;
            }

            var documentText = text ?? GetText(uri, context);

            foreach (var token in diagnostics)
            {
                if (!(token is JObject diagnostic))
                {
                    continue;
                }

                TryConvertRange(diagnostic["range"] as JObject, documentText, toServer);
                if (diagnostic["relatedInformation"] is JArray relatedInformation)
                {
                    foreach (var relatedToken in relatedInformation)
                    {
                        if (relatedToken is JObject related && related["location"] is JObject location)
                        {
                            if (toServer)
                            {
                                var relatedUri = (string?)location["uri"];
                                TryConvertRange(location["range"] as JObject, GetText(relatedUri, context), toServer: true);
                            }
                            else
                            {
                                ConvertLocation(location, context, text);
                            }
                        }
                    }
                }
            }
        }

        private void ConvertDocumentDiagnosticReport(JObject? report, string? uri, RequestContext context)
        {
            if (report == null)
            {
                return;
            }

            ConvertDiagnostics(report["items"] as JArray, uri, GetText(uri, context), context);
            if (report["relatedDocuments"] is JObject related)
            {
                foreach (var property in related.Properties())
                {
                    ConvertDocumentDiagnosticReport(property.Value as JObject, property.Name, context);
                }
            }
        }

        private void ConvertWorkspaceDiagnosticReport(JObject? report, RequestContext context)
        {
            if (!(report?["items"] is JArray items))
            {
                return;
            }

            foreach (var token in items)
            {
                if (token is JObject documentReport)
                {
                    var uri = (string?)documentReport["uri"];
                    ConvertDiagnostics(documentReport["items"] as JArray, uri, GetText(uri, context), context);
                }
            }
        }

        private static void ConvertSemanticTokens(JObject? result, string? text)
        {
            if (!(result?["data"] is JArray data) || text == null || data.Count % 5 != 0)
            {
                return;
            }

            var converted = (JArray)data.DeepClone();
            var scalarLine = 0;
            var scalarStart = 0;
            var previousUtf16Line = 0;
            var previousUtf16Start = 0;
            for (var index = 0; index < data.Count; index += 5)
            {
                if (!TryReadNonNegativeInt(data[index], out var deltaLine) ||
                    !TryReadNonNegativeInt(data[index + 1], out var deltaStart) ||
                    !TryReadNonNegativeInt(data[index + 2], out var scalarLength))
                {
                    return;
                }

                scalarLine += deltaLine;
                scalarStart = deltaLine == 0 ? scalarStart + deltaStart : deltaStart;
                if (!TryScalarToUtf16(text, scalarLine, scalarStart, out var utf16Start) ||
                    !TryUtf8LengthToUtf16(text, scalarLine, utf16Start, scalarLength, out var utf16Length))
                {
                    return;
                }

                converted[index] = scalarLine - previousUtf16Line;
                converted[index + 1] = scalarLine == previousUtf16Line ? utf16Start - previousUtf16Start : utf16Start;
                converted[index + 2] = utf16Length;
                previousUtf16Line = scalarLine;
                previousUtf16Start = utf16Start;
            }

            result["data"] = converted;
        }

        private static bool TryUtf8LengthToUtf16(string text, int line, int utf16Start, int utf8Length, out int utf16Length)
        {
            utf16Length = 0;
            if (!TryGetLine(text, line, out var lineStart, out var lineLength) || utf16Start > lineLength)
            {
                return false;
            }

            var offset = utf16Start;
            var bytes = 0;
            while (bytes < utf8Length && offset < lineLength)
            {
                var character = text[lineStart + offset];
                int scalarBytes;
                if (char.IsHighSurrogate(character))
                {
                    if (offset + 1 >= lineLength || !char.IsLowSurrogate(text[lineStart + offset + 1]))
                    {
                        return false;
                    }

                    scalarBytes = 4;
                    offset += 2;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
                else
                {
                    scalarBytes = character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                    offset++;
                }

                bytes += scalarBytes;
                if (bytes > utf8Length)
                {
                    return false;
                }
            }

            utf16Length = offset - utf16Start;
            return bytes == utf8Length;
        }

        private static bool TryConvertRange(JObject? range, string? text, bool toServer)
        {
            if (range == null || text == null || !(range["start"] is JObject start) || !(range["end"] is JObject end))
            {
                return false;
            }

            if (!TryConvertedCharacter(start, text, toServer, out var convertedStart) ||
                !TryConvertedCharacter(end, text, toServer, out var convertedEnd))
            {
                return false;
            }

            start["character"] = convertedStart;
            end["character"] = convertedEnd;
            return true;
        }

        private static bool TryConvertPosition(JObject? position, string? text, bool toServer)
        {
            if (position == null || text == null || !TryConvertedCharacter(position, text, toServer, out var converted))
            {
                return false;
            }

            position["character"] = converted;
            return true;
        }

        private static bool TryConvertedCharacter(JObject position, string text, bool toServer, out int converted)
        {
            converted = 0;
            if (!TryReadNonNegativeInt(position["line"], out var line) ||
                !TryReadNonNegativeInt(position["character"], out var character))
            {
                return false;
            }

            return toServer
                ? TryUtf16ToScalar(text, line, character, out converted)
                : TryScalarToUtf16(text, line, character, out converted);
        }

        private static void TryConvertLineCharacter(JObject range, string lineProperty, string characterProperty, string text)
        {
            if (range[characterProperty] == null ||
                !TryReadNonNegativeInt(range[lineProperty], out var line) ||
                !TryReadNonNegativeInt(range[characterProperty], out var character) ||
                !TryScalarToUtf16(text, line, character, out var converted))
            {
                return;
            }

            range[characterProperty] = converted;
        }

        private static bool TryUtf16ToScalar(string text, int line, int utf16Character, out int scalarCharacter)
        {
            scalarCharacter = 0;
            if (!TryGetLine(text, line, out var lineStart, out var lineLength) || utf16Character > lineLength)
            {
                return false;
            }

            if (utf16Character > 0 && utf16Character < lineLength &&
                char.IsHighSurrogate(text[lineStart + utf16Character - 1]) &&
                char.IsLowSurrogate(text[lineStart + utf16Character]))
            {
                return false;
            }

            var offset = 0;
            while (offset < utf16Character)
            {
                if (offset + 1 < utf16Character &&
                    char.IsHighSurrogate(text[lineStart + offset]) &&
                    char.IsLowSurrogate(text[lineStart + offset + 1]))
                {
                    offset += 2;
                }
                else
                {
                    offset++;
                }

                scalarCharacter++;
            }

            return true;
        }

        private static bool TryScalarToUtf16(string text, int line, int scalarCharacter, out int utf16Character)
        {
            utf16Character = 0;
            if (!TryGetLine(text, line, out var lineStart, out var lineLength))
            {
                return false;
            }

            var scalar = 0;
            while (utf16Character < lineLength && scalar < scalarCharacter)
            {
                if (utf16Character + 1 < lineLength &&
                    char.IsHighSurrogate(text[lineStart + utf16Character]) &&
                    char.IsLowSurrogate(text[lineStart + utf16Character + 1]))
                {
                    utf16Character += 2;
                }
                else
                {
                    utf16Character++;
                }

                scalar++;
            }

            return scalar == scalarCharacter;
        }

        private static bool TryApplyUtf16Edit(string text, JObject range, string replacement, out string updated)
        {
            updated = text;
            if (!(range["start"] is JObject start) || !(range["end"] is JObject end) ||
                !TryAbsoluteUtf16Offset(text, start, out var startOffset) ||
                !TryAbsoluteUtf16Offset(text, end, out var endOffset) ||
                endOffset < startOffset)
            {
                return false;
            }

            updated = text.Substring(0, startOffset) + replacement + text.Substring(endOffset);
            return true;
        }

        private static bool TryAbsoluteUtf16Offset(string text, JObject position, out int offset)
        {
            offset = 0;
            if (!TryReadNonNegativeInt(position["line"], out var line) ||
                !TryReadNonNegativeInt(position["character"], out var character) ||
                !TryGetLine(text, line, out var lineStart, out var lineLength) ||
                character > lineLength)
            {
                return false;
            }

            if (character > 0 && character < lineLength &&
                char.IsHighSurrogate(text[lineStart + character - 1]) &&
                char.IsLowSurrogate(text[lineStart + character]))
            {
                return false;
            }

            offset = lineStart + character;
            return true;
        }

        private static bool TryGetLine(string text, int requestedLine, out int start, out int length)
        {
            start = 0;
            length = 0;
            var line = 0;
            while (line < requestedLine)
            {
                var newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    return false;
                }

                start = newline + 1;
                line++;
            }

            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            length = end - start;
            return true;
        }

        private static bool TryReadNonNegativeInt(JToken? token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            var number = token.Value<long>();
            if (number < 0 || number > int.MaxValue)
            {
                return false;
            }

            value = (int)number;
            return true;
        }

        private string? GetText(string? uri, RequestContext? context)
        {
            var normalized = NormalizeUri(uri);
            if (normalized == null)
            {
                return null;
            }

            if (context?.Text != null && string.Equals(context.Uri, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return context.Text;
            }

            if (openDocuments.TryGetValue(normalized, out var openText))
            {
                return openText;
            }

            return GetDiskText(normalized);
        }

        private string? GetDiskText(string normalizedUri)
        {
            if (!Uri.TryCreate(normalizedUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            {
                return null;
            }

            try
            {
                var info = new FileInfo(uri.LocalPath);
                if (!info.Exists)
                {
                    RemoveDiskDocument(normalizedUri);
                    return null;
                }

                var lastWrite = info.LastWriteTimeUtc.Ticks;
                var length = info.Length;
                if (diskDocuments.TryGetValue(normalizedUri, out var cached) &&
                    cached.LastWriteTicks == lastWrite && cached.Length == length)
                {
                    TouchDiskDocument(normalizedUri);
                    return cached.Text;
                }

                var text = File.ReadAllText(info.FullName);
                AddDiskDocument(normalizedUri, new DiskDocument(lastWrite, length, text));
                return text;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is SecurityException)
            {
                RemoveDiskDocument(normalizedUri);
                return null;
            }
        }

        private void SetOpenDocument(string normalizedUri, string text)
        {
            if (!openDocuments.ContainsKey(normalizedUri) && openDocuments.Count >= MaximumOpenDocuments)
            {
                string? oldest = null;
                using (var enumerator = openDocuments.Keys.GetEnumerator())
                {
                    if (enumerator.MoveNext())
                    {
                        oldest = enumerator.Current;
                    }
                }

                if (oldest != null)
                {
                    openDocuments.Remove(oldest);
                }
            }

            openDocuments[normalizedUri] = text;
        }

        private void InvalidateOpenDocument(string normalizedUri)
        {
            openDocuments.Remove(normalizedUri);
        }

        private void AddPending(JToken id, RequestContext context)
        {
            var key = RequestKey(id);
            if (pendingNodes.TryGetValue(key, out var existing))
            {
                pendingOrder.Remove(existing);
            }

            pendingRequests[key] = context;
            pendingNodes[key] = pendingOrder.AddLast(key);
            while (pendingRequests.Count > MaximumPendingRequests && pendingOrder.First != null)
            {
                RemovePendingKey(pendingOrder.First.Value);
            }
        }

        private void RemovePending(JToken? id)
        {
            if (id != null)
            {
                RemovePendingKey(RequestKey(id));
            }
        }

        private void RemovePendingKey(string key)
        {
            pendingRequests.Remove(key);
            if (pendingNodes.TryGetValue(key, out var node))
            {
                pendingOrder.Remove(node);
                pendingNodes.Remove(key);
            }
        }

        private static string RequestKey(JToken id)
        {
            return ((int)id.Type).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + id.ToString(Formatting.None);
        }

        private void AddDiskDocument(string uri, DiskDocument document)
        {
            diskDocuments[uri] = document;
            TouchDiskDocument(uri);
            while (diskDocuments.Count > MaximumDiskDocuments && diskOrder.First != null)
            {
                RemoveDiskDocument(diskOrder.First.Value);
            }
        }

        private void TouchDiskDocument(string uri)
        {
            if (diskNodes.TryGetValue(uri, out var existing))
            {
                diskOrder.Remove(existing);
            }

            var node = diskOrder.AddLast(uri);
            diskNodes[uri] = node;
        }

        private void RemoveDiskDocument(string uri)
        {
            diskDocuments.Remove(uri);
            if (diskNodes.TryGetValue(uri, out var node))
            {
                diskOrder.Remove(node);
                diskNodes.Remove(uri);
            }
        }

        private void Clear()
        {
            openDocuments.Clear();
            pendingRequests.Clear();
            pendingOrder.Clear();
            pendingNodes.Clear();
            diskDocuments.Clear();
            diskNodes.Clear();
            diskOrder.Clear();
        }

        private static string? NormalizeUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return uri.AbsoluteUri;
        }

        private sealed class RequestContext
        {
            public RequestContext(string method, string? uri, string? text)
            {
                Method = method;
                Uri = uri;
                Text = text;
            }

            public string Method { get; }

            public string? Uri { get; }

            public string? Text { get; }
        }

        private sealed class DiskDocument
        {
            public DiskDocument(long lastWriteTicks, long length, string text)
            {
                LastWriteTicks = lastWriteTicks;
                Length = length;
                Text = text;
            }

            public long LastWriteTicks { get; }

            public long Length { get; }

            public string Text { get; }
        }
    }
}
