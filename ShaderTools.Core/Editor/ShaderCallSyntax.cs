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
        internal List<Token> Tokens = [];
    }

    internal sealed class Call
    {
        internal string Name = "";
        internal int NameStart;
        internal bool Closed;
        internal List<Argument> Arguments = [];
    }

    internal sealed class Token
    {
        internal string Text = "";
        internal int Start, End;
        internal bool Identifier;
    }

    internal readonly Dictionary<int, Call> Calls = [];
    internal readonly Dictionary<int, (Call Call, Argument Argument)> ArgumentsByStart = [];
    internal readonly Dictionary<string, List<string[]>> Signatures = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
    private readonly List<int> lineStarts = [0];

    internal ShaderCallSyntax(string text)
    {
        for (int p = 0; p < text.Length; p++)
        {
            if (text[p] == '\r') { if (p + 1 < text.Length && text[p + 1] == '\n') { p++; } lineStarts.Add(p + 1); }
            else if (text[p] == '\n')
            {
                lineStarts.Add(p + 1);
            }
        }
        List<Token> tokens = Lex(text);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "(" || i == 0)
            {
                continue;
            }

            int nameIndex = i - 1;
            if (tokens[nameIndex].Text == ">")
            {
                int depth = 1;
                while (--nameIndex >= 0 && depth > 0)
                {
                    if (tokens[nameIndex].Text == ">")
                    {
                        depth++;
                    }

                    if (tokens[nameIndex].Text == "<")
                    {
                        depth--;
                    }
                }
            }
            if (nameIndex < 0 || !tokens[nameIndex].Identifier || IsControl(tokens[nameIndex].Text))
            {
                continue;
            }

            Call call = ReadCall(tokens, i);
            call.Name = tokens[nameIndex].Text;
            call.NameStart = tokens[nameIndex].Start;
            bool declaration = nameIndex > 0 && tokens[nameIndex - 1].Identifier && !IsControl(tokens[nameIndex - 1].Text);
            if (declaration)
            {
                if (!Signatures.ContainsKey(call.Name))
                {
                    Signatures.Add(call.Name, []);
                }

                if (call.Closed)
                {
                    Signatures[call.Name].Add(ParameterNames(call, tokens[nameIndex - 1].Text == "fn"));
                }
            }
            else
            {
                Calls[call.NameStart] = call;
            }
        }
        foreach (Call call in Calls.Values)
        {
            foreach (Argument? argument in call.Arguments.Where(a => a.Tokens.Count > 0))
            {
                ArgumentsByStart[argument.Start] = (call, argument);
            }
        }

        // GLSL struct constructors use field names as parameter names.
        for (int i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Text != "struct" || !tokens[i + 1].Identifier || tokens[i + 2].Text != "{")
            {
                continue;
            }

            List<string> fields = [];
            List<Token> field = [];
            int end = i + 3;
            for (; end < tokens.Count && tokens[end].Text != "}"; end++)
            {
                if (tokens[end].Text == ";")
                {
                    if (!field.Any(t => t.Text == "("))
                    {
                        Token[] identifiers = field.TakeWhile(t => t.Text is not "[" and not ":").Where(t => t.Identifier).ToArray();
                        if (identifiers.Length >= 2)
                        {
                            fields.Add(identifiers.Last().Text);
                        }
                    }
                    field.Clear();
                }
                else
                {
                    field.Add(tokens[end]);
                }
            }
            if (end < tokens.Count)
            {
                Signatures[tokens[i + 1].Text] = [fields.ToArray()];
            }
        }
    }

    internal static string[] ParameterNames(Call declaration, bool wgsl = false)
    {
        if (declaration.Arguments.Count == 1 && declaration.Arguments[0].Tokens.Count == 1 && declaration.Arguments[0].Tokens[0].Text == "void")
        {
            return Array.Empty<string>();
        }

        return declaration.Arguments.Select(argument =>
        {
            Token[] identifiers = argument.Tokens.TakeWhile(t => t.Text is not "[" and not ":" and not "=").Where(t => t.Identifier).ToArray();
            return wgsl ? identifiers.FirstOrDefault()?.Text ?? "" : identifiers.Length >= 2 ? identifiers.Last().Text : "";
        }).ToArray();
    }

    internal int Offset(int line, int character)
    {
        return line >= 0 && line < lineStarts.Count ? lineStarts[line] + character : -1;
    }

    internal int LineAtOffset(int offset)
    {
        int index = lineStarts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }
    internal ParameterHint Hint(Argument argument, string label, object? identity = null, bool paddingLeft = false, bool paddingRight = true)
    {
        int line = LineAtOffset(argument.Start);
        return new ParameterHint(line, argument.Start - lineStarts[line], label, paddingLeft, paddingRight, identity ?? new object());
    }

    private static Call ReadCall(List<Token> tokens, int open)
    {
        Call call = new Call();
        Argument current = new Argument();
        int parens = 0;
        int brackets = 0;
        int braces = 0;
        int angles = 0;
        for (int i = open + 1; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            bool top = parens == 0 && brackets == 0 && braces == 0 && angles == 0;
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
            if (token.Text == "(")
            {
                parens++;
            }
            else if (token.Text == ")" && parens > 0)
            {
                parens--;
            }
            else if (token.Text == "[")
            {
                brackets++;
            }
            else if (token.Text == "]" && brackets > 0)
            {
                brackets--;
            }
            else if (token.Text == "{")
            {
                braces++;
            }
            else if (token.Text == "}" && braces > 0)
            {
                braces--;
            }
            else if (token.Text == "<" && i > 0 && (angles > 0 || IsGeneric(tokens[i - 1].Text)))
            {
                angles++;
            }
            else if (token.Text == ">" && angles > 0)
            {
                angles--;
            }

            if (current.Tokens.Count == 0)
            {
                current.Start = token.Start;
            }

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

    private static bool IsControl(string name)
    {
        return name is "if" or "for" or "while" or "switch" or
        "return" or "else" or "case" or "sizeof" or "discard";
    }

    private static bool IsGeneric(string name)
    {
        return name == "array" || name == "vector" || name == "matrix" || name == "bitcast" ||
            name.StartsWith("vec", StringComparison.Ordinal) || name.StartsWith("mat", StringComparison.Ordinal);
    }

    private static List<Token> Lex(string text)
    {
        List<Token> result = [];
        for (int i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '#' || (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }

                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(text.Length, i + 2);
                continue;
            }
            int start = i++;
            bool identifier = char.IsLetter(text[start]) || text[start] == '_';
            if (identifier)
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }
            else if (text[start] is '"' or '\'')
            {
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i = Math.Min(text.Length, i + 2); continue; }
                    if (text[i++] == text[start])
                    {
                        break;
                    }
                }
            }
            result.Add(new Token { Text = text.Substring(start, i - start), Start = start, End = i, Identifier = identifier });
        }
        return result;
    }
}
