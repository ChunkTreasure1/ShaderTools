using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderTools.Core.Editor;

// The editor owns synchronization and rejects responses for superseded requests.
public sealed class InlayHintCache
{
    private ShaderCallSyntax syntax = new ShaderCallSyntax("");
    private readonly Dictionary<string, List<string[]>> externalSignatures = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
    public int Version { get; private set; } = -1;
    public IReadOnlyList<ParameterHint> Hints { get; private set; } = Array.Empty<ParameterHint>();

    public static IReadOnlyList<string> ReadCompletionSignatures(JToken? response)
    {
        JArray? items = response as JArray ?? (response as JObject)?["items"] as JArray;
        if (items == null)
        {
            return Array.Empty<string>();
        }

        List<string> declarations = [];
        foreach (JObject? item in items.OfType<JObject>().Take(8192))
        {
            JToken? documentation = item["documentation"];
            string? markdown = documentation?.Type == JTokenType.String ? documentation.Value<string>() :
                (documentation as JObject)?.Value<string>("value");
            if (markdown == null || !markdown.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            int start = markdown.IndexOf('\n');
            int end = start < 0 ? -1 : markdown.IndexOf("```", start + 1, StringComparison.Ordinal);
            if (end > start && end - start <= 65536)
            {
                declarations.Add(markdown.Substring(start + 1, end - start - 1));
            }
        }
        return declarations;
    }

    public void Clear(int version, string text = "")
    {
        Hints = Array.Empty<ParameterHint>();
        Version = version;
        syntax = new ShaderCallSyntax(text);
        externalSignatures.Clear();
    }

    public void SetExternalSignatures(IEnumerable<string> declarations)
    {
        externalSignatures.Clear();
        foreach (string declaration in declarations)
        {
            ShaderCallSyntax parsed = new ShaderCallSyntax(declaration);
            foreach (KeyValuePair<string, List<string[]>> entry in parsed.Signatures)
            {
                if (!externalSignatures.TryGetValue(entry.Key, out List<string[]>? overloads))
                {
                    externalSignatures.Add(entry.Key, overloads = []);
                }

                overloads.AddRange(entry.Value);
            }
            // Constructor signatures in completion documentation have no return type.
            foreach (ShaderCallSyntax.Call? call in parsed.Calls.Values.Where(call => call.Closed))
            {
                string[] names = ShaderCallSyntax.ParameterNames(call);
                if (names.Length == 0 || names.Any(string.IsNullOrEmpty))
                {
                    continue;
                }

                if (!externalSignatures.TryGetValue(call.Name, out List<string[]>? overloads))
                {
                    externalSignatures.Add(call.Name, overloads = []);
                }

                overloads.Add(names);
            }
        }
    }

    public IReadOnlyList<int> ApplyEdits(int beforeVersion, int afterVersion, IReadOnlyList<InlayHintEdit> edits, string text)
    {
        ShaderCallSyntax next = new ShaderCallSyntax(text);
        HashSet<string> changedSignatures = new HashSet<string>(syntax.Signatures.Keys.Concat(next.Signatures.Keys)
            .Where(name => !syntax.Signatures.TryGetValue(name, out List<string[]>? before) ||
                !next.Signatures.TryGetValue(name, out List<string[]>? after) ||
                !before.Select(s => string.Join("\0", s)).SequenceEqual(after.Select(s => string.Join("\0", s)))), StringComparer.Ordinal);
        List<ParameterHint> retained = new List<ParameterHint>(Hints.Count);
        if (Version == beforeVersion)
        {
            foreach (ParameterHint hint in Hints)
            {
                int offset = syntax.Offset(hint.Line, hint.Character);
                if (syntax.ArgumentsByStart.TryGetValue(offset, out (ShaderCallSyntax.Call Call, ShaderCallSyntax.Argument Argument) previous))
                {
                    ShaderCallSyntax.Call oldCall = previous.Call;
                    ShaderCallSyntax.Argument argument = previous.Argument;
                    int? nameStart = MapPosition(oldCall.NameStart, edits);
                    if (nameStart.HasValue && next.Calls.TryGetValue(nameStart.Value, out ShaderCallSyntax.Call? call) && call.Name == oldCall.Name &&
                        argument.Index < call.Arguments.Count && call.Arguments[argument.Index].Tokens.Count > 0)
                    {
                        retained.Add(next.Hint(call.Arguments[argument.Index], hint.Label, hint.Identity, hint.PaddingLeft, hint.PaddingRight));
                    }
                }
            }
        }
        HashSet<object> retainedIdentities = [.. retained.Select(h => h.Identity)];
        HashSet<int> removedLines = [];
        foreach (ParameterHint? hint in Hints.Where(h => !retainedIdentities.Contains(h.Identity)))
        {
            int? mapped = MapPosition(syntax.Offset(hint.Line, hint.Character), edits);
            if (mapped.HasValue && mapped.Value >= 0 && mapped.Value <= text.Length)
            {
                _=removedLines.Add(next.LineAtOffset(mapped.Value));
            }
        }
        syntax = next;
        Version = afterVersion;
        Hints = retained;
        // Local declarations reflect signature edits immediately, including distant calls.
        return Merge(WithFallback(retained, changedSignatures)).Concat(removedLines).Distinct().OrderBy(line => line).ToArray();
    }

    private static int? MapPosition(int position, IReadOnlyList<InlayHintEdit> edits)
    {
        int delta = 0;
        foreach (InlayHintEdit edit in edits)
        {
            if (position >= edit.Start && position < edit.Start + edit.OldLength)
            {
                return null;
            }

            if (edit.Start <= position)
            {
                delta += edit.NewText.Length - edit.OldLength;
            }
        }
        return position + delta;
    }

    private IReadOnlyList<ParameterHint> WithFallback(IReadOnlyList<ParameterHint> authoritative, HashSet<string>? changedSignatures = null)
    {
        Dictionary<(int Line, int Character), ParameterHint> result = authoritative.GroupBy(hint => (hint.Line, hint.Character)).ToDictionary(group => group.Key, group => group.First());
        foreach (ShaderCallSyntax.Call call in syntax.Calls.Values)
        {
            if (changedSignatures?.Contains(call.Name) == true)
            {
                foreach (ShaderCallSyntax.Argument? argument in call.Arguments.Where(a => a.Tokens.Count > 0))
                {
                    ParameterHint position = syntax.Hint(argument, "");
                    _=result.Remove((position.Line, position.Character));
                }
            }

            bool local = syntax.Signatures.TryGetValue(call.Name, out List<string[]>? signatures);
            if (!local)
            {
                _=externalSignatures.TryGetValue(call.Name, out signatures);
            }

            if (signatures == null)
            {
                continue;
            }

            string[][] candidates = signatures.Where(s => s.Length >= call.Arguments.Count).ToArray();
            if (call.Closed && candidates.Any(s => s.Length == call.Arguments.Count))
            {
                candidates = candidates.Where(s => s.Length == call.Arguments.Count).ToArray();
            }

            if (candidates.Length == 0)
            {
                continue;
            }

            foreach (ShaderCallSyntax.Argument? argument in call.Arguments.Where(a => a.Tokens.Count > 0))
            {
                string[] names = candidates.Select(s => s[argument.Index]).Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length != 1 || string.IsNullOrEmpty(names[0]))
                {
                    continue;
                }

                ParameterHint hint = syntax.Hint(argument, names[0] + ":");
                (int Line, int Character) key = (hint.Line, hint.Character);
                if (!result.ContainsKey(key))
                {
                    result[key] = hint;
                }
            }
        }
        return result.Values.Where(h => h.Label.Length <= 128).Take(2048).ToArray();
    }

    // Keep display identities for equal hints and only invalidate changed lines.
    // A full server response also updates distant calls after a signature edit.
    public IReadOnlyList<int> Replace(int version, IReadOnlyList<ParameterHint> incoming, string text)
    {
        syntax = new ShaderCallSyntax(text);
        Version = version;
        List<ParameterHint> combined = incoming.ToList();
        HashSet<(int, int)> occupied = [.. incoming.Select(h => (h.Line, h.Character))];
        // The server omits incomplete call expressions. Keep their known argument labels.
        foreach (ParameterHint hint in Hints)
        {
            if (occupied.Contains((hint.Line, hint.Character)))
            {
                continue;
            }

            int offset = syntax.Offset(hint.Line, hint.Character);
            if (syntax.ArgumentsByStart.TryGetValue(offset, out (ShaderCallSyntax.Call Call, ShaderCallSyntax.Argument Argument) current) &&
                (!current.Call.Closed || current.Call.Arguments.Any(a => a.Tokens.Count == 0)))
            {
                combined.Add(hint);
            }
        }
        return Merge(WithFallback(combined));
    }

    private IReadOnlyList<int> Merge(IReadOnlyList<ParameterHint> incoming)
    {
        Dictionary<(int, int, string, bool, bool), Queue<ParameterHint>> previous = [];
        foreach (ParameterHint hint in Hints)
        {
            if (!previous.TryGetValue(hint.Key, out Queue<ParameterHint>? matches))
            {
                previous.Add(hint.Key, matches = new Queue<ParameterHint>());
            }

            matches.Enqueue(hint);
        }

        HashSet<int> changedLines = [];
        List<ParameterHint> merged = new List<ParameterHint>(incoming.Count);
        foreach (ParameterHint hint in incoming)
        {
            if (previous.TryGetValue(hint.Key, out Queue<ParameterHint>? matches) && matches.Count > 0)
            {
                merged.Add(matches.Dequeue());
            }
            else
            {
                merged.Add(hint);
                _=changedLines.Add(hint.Line);
            }
        }
        foreach (Queue<ParameterHint> matches in previous.Values)
        {
            foreach (ParameterHint removed in matches)
            {
                _=changedLines.Add(removed.Line);
            }
        }

        Hints = merged.OrderBy(hint => hint.Line).ThenBy(hint => hint.Character).ToArray();
        return changedLines.OrderBy(line => line).ToArray();
    }
}

public readonly struct InlayHintEdit
{
    public InlayHintEdit(int start, int oldLength, string newText)
    {
        if (start < 0 || oldLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        Start = start;
        OldLength = oldLength;
        NewText = newText ?? throw new ArgumentNullException(nameof(newText));
    }

    public int Start { get; }
    public int OldLength { get; }
    public string NewText { get; }
}

public sealed class ParameterHint
{
    public ParameterHint(int line, int character, string label, bool paddingLeft, bool paddingRight)
        : this(line, character, label, paddingLeft, paddingRight, new object()) { }

    internal ParameterHint(int line, int character, string label, bool paddingLeft, bool paddingRight, object identity)
    {
        if (line < 0 || character < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        Line = line;
        Character = character;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        PaddingLeft = paddingLeft;
        PaddingRight = paddingRight;
        Identity = identity;
    }

    public int Line { get; }
    public int Character { get; }
    public string Label { get; }
    public bool PaddingLeft { get; }
    public bool PaddingRight { get; }
    public object Identity { get; }
    internal (int, int, string, bool, bool) Key => (Line, Character, Label, PaddingLeft, PaddingRight);
}
