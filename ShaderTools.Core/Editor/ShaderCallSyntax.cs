using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderTools.Core.Editor;

// A tolerant lexical view for presentation while the user is between valid ASTs.
// It deliberately does not attempt type-based overload resolution.
internal sealed class ShaderCallSyntax
{
    internal sealed class Argument
    {
        internal int Start, End, Index;
        internal List<Token> Tokens = new List<Token>();
    }

    internal sealed class Call
    {
        internal string Name = "";
        internal int NameStart;
        internal bool Closed;
        internal List<Argument> Arguments = new List<Argument>();
    }

    internal sealed class Token
    {
        internal string Text = "";
        internal int Start, End;
        internal bool Identifier;
    }

    internal readonly Dictionary<int, Call> Calls = new Dictionary<int, Call>();
    internal readonly Dictionary<int, (Call Call, Argument Argument)> ArgumentsByStart = new Dictionary<int, (Call, Argument)>();
    internal readonly Dictionary<string, List<string[]>> Signatures = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
    private readonly List<int> lineStarts = new List<int> { 0 };

    internal ShaderCallSyntax(string text)
    {
        for (var p = 0; p < text.Length; p++)
        {
            if (text[p] == '\r') { if (p + 1 < text.Length && text[p + 1] == '\n') p++; lineStarts.Add(p + 1); }
            else if (text[p] == '\n') lineStarts.Add(p + 1);
        }
        var tokens = Lex(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "(" || i == 0) continue;
            var nameIndex = i - 1;
            if (tokens[nameIndex].Text == ">")
            {
                var depth = 1;
                while (--nameIndex >= 0 && depth > 0)
                {
                    if (tokens[nameIndex].Text == ">") depth++;
                    if (tokens[nameIndex].Text == "<") depth--;
                }
            }
            if (nameIndex < 0 || !tokens[nameIndex].Identifier || IsControl(tokens[nameIndex].Text)) continue;
            var call = ReadCall(tokens, i);
            call.Name = tokens[nameIndex].Text;
            call.NameStart = tokens[nameIndex].Start;
            var declaration = nameIndex > 0 && tokens[nameIndex - 1].Identifier && !IsControl(tokens[nameIndex - 1].Text);
            if (declaration)
            {
                if (!Signatures.ContainsKey(call.Name)) Signatures.Add(call.Name, new List<string[]>());
                if (call.Closed)
                    Signatures[call.Name].Add(ParameterNames(call, tokens[nameIndex - 1].Text == "fn"));
            }
            else Calls[call.NameStart] = call;
        }
        foreach (var call in Calls.Values)
            foreach (var argument in call.Arguments.Where(a => a.Tokens.Count > 0))
                ArgumentsByStart[argument.Start] = (call, argument);

        // GLSL struct constructors use field names as parameter names.
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Text != "struct" || !tokens[i + 1].Identifier || tokens[i + 2].Text != "{") continue;
            var fields = new List<string>();
            var field = new List<Token>();
            var end = i + 3;
            for (; end < tokens.Count && tokens[end].Text != "}"; end++)
            {
                if (tokens[end].Text == ";")
                {
                    if (!field.Any(t => t.Text == "("))
                    {
                        var identifiers = field.TakeWhile(t => t.Text != "[" && t.Text != ":").Where(t => t.Identifier).ToArray();
                        if (identifiers.Length >= 2) fields.Add(identifiers.Last().Text);
                    }
                    field.Clear();
                }
                else field.Add(tokens[end]);
            }
            if (end < tokens.Count) Signatures[tokens[i + 1].Text] = new List<string[]> { fields.ToArray() };
        }
    }

    internal static string[] ParameterNames(Call declaration, bool wgsl = false)
    {
        if (declaration.Arguments.Count == 1 && declaration.Arguments[0].Tokens.Count == 1 && declaration.Arguments[0].Tokens[0].Text == "void")
            return Array.Empty<string>();
        return declaration.Arguments.Select(argument =>
        {
            var identifiers = argument.Tokens.TakeWhile(t => t.Text != "[" && t.Text != ":" && t.Text != "=").Where(t => t.Identifier).ToArray();
            return wgsl ? identifiers.FirstOrDefault()?.Text ?? "" : identifiers.Length >= 2 ? identifiers.Last().Text : "";
        }).ToArray();
    }

    internal int Offset(int line, int character) => line >= 0 && line < lineStarts.Count ? lineStarts[line] + character : -1;
    internal int LineAtOffset(int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }
    internal ParameterHint Hint(Argument argument, string label, object? identity = null, bool paddingLeft = false, bool paddingRight = true)
    {
        var line = LineAtOffset(argument.Start);
        return new ParameterHint(line, argument.Start - lineStarts[line], label, paddingLeft, paddingRight, identity ?? new object());
    }

    private static Call ReadCall(List<Token> tokens, int open)
    {
        var call = new Call();
        var current = new Argument();
        var parens = 0;
        var brackets = 0;
        var braces = 0;
        var angles = 0;
        for (var i = open + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var top = parens == 0 && brackets == 0 && braces == 0 && angles == 0;
            if (top && (token.Text == ")" || token.Text == ";" || token.Text == "}"))
            {
                call.Closed = token.Text == ")";
                break;
            }
            if (top && token.Text == ",")
            {
                current.Index = call.Arguments.Count;
                call.Arguments.Add(current);
                current = new Argument();
                continue;
            }
            if (token.Text == "(") parens++;
            else if (token.Text == ")" && parens > 0) parens--;
            else if (token.Text == "[") brackets++;
            else if (token.Text == "]" && brackets > 0) brackets--;
            else if (token.Text == "{") braces++;
            else if (token.Text == "}" && braces > 0) braces--;
            else if (token.Text == "<" && i > 0 && (angles > 0 || IsGeneric(tokens[i - 1].Text))) angles++;
            else if (token.Text == ">" && angles > 0) angles--;
            if (current.Tokens.Count == 0) current.Start = token.Start;
            current.End = token.End;
            current.Tokens.Add(token);
        }
        if (current.Tokens.Count > 0 || call.Arguments.Count > 0)
        {
            current.Index = call.Arguments.Count;
            call.Arguments.Add(current);
        }
        return call;
    }

    private static bool IsControl(string name) => name == "if" || name == "for" || name == "while" || name == "switch" ||
        name == "return" || name == "else" || name == "case" || name == "sizeof" || name == "discard";
    private static bool IsGeneric(string name) => name == "array" || name == "vector" || name == "matrix" || name == "bitcast" ||
        name.StartsWith("vec", StringComparison.Ordinal) || name.StartsWith("mat", StringComparison.Ordinal);

    private static List<Token> Lex(string text)
    {
        var result = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '#' || (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(text.Length, i + 2);
                continue;
            }
            var start = i++;
            var identifier = char.IsLetter(text[start]) || text[start] == '_';
            if (identifier) { while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++; }
            else if (text[start] == '"' || text[start] == '\'')
            {
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i = Math.Min(text.Length, i + 2); continue; }
                    if (text[i++] == text[start]) break;
                }
            }
            result.Add(new Token { Text = text.Substring(start, i - start), Start = start, End = i, Identifier = identifier });
        }
        return result;
    }
}
