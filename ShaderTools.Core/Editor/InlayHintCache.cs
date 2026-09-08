using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

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
        var items = response as JArray ?? (response as JObject)?["items"] as JArray;
        if (items == null) return Array.Empty<string>();
        var declarations = new List<string>();
        foreach (var item in items.OfType<JObject>().Take(8192))
        {
            var documentation = item["documentation"];
            var markdown = documentation?.Type == JTokenType.String ? documentation.Value<string>() :
                (documentation as JObject)?.Value<string>("value");
            if (markdown == null || !markdown.StartsWith("```", StringComparison.Ordinal)) continue;
            var start = markdown.IndexOf('\n');
            var end = start < 0 ? -1 : markdown.IndexOf("```", start + 1, StringComparison.Ordinal);
            if (end > start && end - start <= 65536)
                declarations.Add(markdown.Substring(start + 1, end - start - 1));
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
        foreach (var declaration in declarations)
        {
            var parsed = new ShaderCallSyntax(declaration);
            foreach (var entry in parsed.Signatures)
            {
                if (!externalSignatures.TryGetValue(entry.Key, out var overloads))
                    externalSignatures.Add(entry.Key, overloads = new List<string[]>());
                overloads.AddRange(entry.Value);
            }
            // Constructor signatures in completion documentation have no return type.
            foreach (var call in parsed.Calls.Values.Where(call => call.Closed))
            {
                var names = ShaderCallSyntax.ParameterNames(call);
                if (names.Length == 0 || names.Any(string.IsNullOrEmpty)) continue;
                if (!externalSignatures.TryGetValue(call.Name, out var overloads))
                    externalSignatures.Add(call.Name, overloads = new List<string[]>());
                overloads.Add(names);
            }
        }
    }

    public IReadOnlyList<int> ApplyEdits(int beforeVersion, int afterVersion, IReadOnlyList<InlayHintEdit> edits, string text)
    {
        var next = new ShaderCallSyntax(text);
        var changedSignatures = new HashSet<string>(syntax.Signatures.Keys.Concat(next.Signatures.Keys)
            .Where(name => !syntax.Signatures.TryGetValue(name, out var before) ||
                !next.Signatures.TryGetValue(name, out var after) ||
                !before.Select(s => string.Join("\0", s)).SequenceEqual(after.Select(s => string.Join("\0", s)))), StringComparer.Ordinal);
        var retained = new List<ParameterHint>(Hints.Count);
        if (Version == beforeVersion)
        {
            foreach (var hint in Hints)
            {
                var offset = syntax.Offset(hint.Line, hint.Character);
                if (syntax.ArgumentsByStart.TryGetValue(offset, out var previous))
                {
                    var oldCall = previous.Call;
                    var argument = previous.Argument;
                    var nameStart = MapPosition(oldCall.NameStart, edits);
                    if (nameStart.HasValue && next.Calls.TryGetValue(nameStart.Value, out var call) && call.Name == oldCall.Name &&
                        argument.Index < call.Arguments.Count && call.Arguments[argument.Index].Tokens.Count > 0)
                        retained.Add(next.Hint(call.Arguments[argument.Index], hint.Label, hint.Identity, hint.PaddingLeft, hint.PaddingRight));
                }
            }
        }
        var retainedIdentities = new HashSet<object>(retained.Select(h => h.Identity));
        var removedLines = new HashSet<int>();
        foreach (var hint in Hints.Where(h => !retainedIdentities.Contains(h.Identity)))
        {
            var mapped = MapPosition(syntax.Offset(hint.Line, hint.Character), edits);
            if (mapped.HasValue && mapped.Value >= 0 && mapped.Value <= text.Length)
                removedLines.Add(next.LineAtOffset(mapped.Value));
        }
        syntax = next;
        Version = afterVersion;
        Hints = retained;
        // Local declarations reflect signature edits immediately, including distant calls.
        return Merge(WithFallback(retained, changedSignatures)).Concat(removedLines).Distinct().OrderBy(line => line).ToArray();
    }

    private static int? MapPosition(int position, IReadOnlyList<InlayHintEdit> edits)
    {
        var delta = 0;
        foreach (var edit in edits)
        {
            if (position >= edit.Start && position < edit.Start + edit.OldLength) return null;
            if (edit.Start <= position) delta += edit.NewText.Length - edit.OldLength;
        }
        return position + delta;
    }

    private IReadOnlyList<ParameterHint> WithFallback(IReadOnlyList<ParameterHint> authoritative, HashSet<string>? changedSignatures = null)
    {
        var result = authoritative.GroupBy(hint => (hint.Line, hint.Character)).ToDictionary(group => group.Key, group => group.First());
        foreach (var call in syntax.Calls.Values)
        {
            if (changedSignatures?.Contains(call.Name) == true)
                foreach (var argument in call.Arguments.Where(a => a.Tokens.Count > 0))
                {
                    var position = syntax.Hint(argument, "");
                    result.Remove((position.Line, position.Character));
                }
            var local = syntax.Signatures.TryGetValue(call.Name, out var signatures);
            if (!local) externalSignatures.TryGetValue(call.Name, out signatures);
            if (signatures == null) continue;
            var candidates = signatures.Where(s => s.Length >= call.Arguments.Count).ToArray();
            if (call.Closed && candidates.Any(s => s.Length == call.Arguments.Count))
                candidates = candidates.Where(s => s.Length == call.Arguments.Count).ToArray();
            if (candidates.Length == 0) continue;
            foreach (var argument in call.Arguments.Where(a => a.Tokens.Count > 0))
            {
                var names = candidates.Select(s => s[argument.Index]).Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length != 1 || string.IsNullOrEmpty(names[0])) continue;
                var hint = syntax.Hint(argument, names[0] + ":");
                var key = (hint.Line, hint.Character);
                if (!result.ContainsKey(key)) result[key] = hint;
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
        var combined = incoming.ToList();
        var occupied = new HashSet<(int, int)>(incoming.Select(h => (h.Line, h.Character)));
        // The server omits incomplete call expressions. Keep their known argument labels.
        foreach (var hint in Hints)
        {
            if (occupied.Contains((hint.Line, hint.Character))) continue;
            var offset = syntax.Offset(hint.Line, hint.Character);
            if (syntax.ArgumentsByStart.TryGetValue(offset, out var current) &&
                (!current.Call.Closed || current.Call.Arguments.Any(a => a.Tokens.Count == 0))) combined.Add(hint);
        }
        return Merge(WithFallback(combined));
    }

    private IReadOnlyList<int> Merge(IReadOnlyList<ParameterHint> incoming)
    {
        var previous = new Dictionary<(int, int, string, bool, bool), Queue<ParameterHint>>();
        foreach (var hint in Hints)
        {
            if (!previous.TryGetValue(hint.Key, out var matches))
                previous.Add(hint.Key, matches = new Queue<ParameterHint>());
            matches.Enqueue(hint);
        }

        var changedLines = new HashSet<int>();
        var merged = new List<ParameterHint>(incoming.Count);
        foreach (var hint in incoming)
        {
            if (previous.TryGetValue(hint.Key, out var matches) && matches.Count > 0)
                merged.Add(matches.Dequeue());
            else
            {
                merged.Add(hint);
                changedLines.Add(hint.Line);
            }
        }
        foreach (var matches in previous.Values)
            foreach (var removed in matches) changedLines.Add(removed.Line);

        Hints = merged.OrderBy(hint => hint.Line).ThenBy(hint => hint.Character).ToArray();
        return changedLines.OrderBy(line => line).ToArray();
    }
}

public readonly struct InlayHintEdit
{
    public InlayHintEdit(int start, int oldLength, string newText)
    {
        if (start < 0 || oldLength < 0) throw new ArgumentOutOfRangeException(nameof(start));
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
        if (line < 0 || character < 0) throw new ArgumentOutOfRangeException(nameof(line));
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
