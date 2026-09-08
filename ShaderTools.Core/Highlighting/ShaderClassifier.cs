using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace ShaderTools.Core.Highlighting;

public static class ShaderClassifier
{
    public static IReadOnlyList<ShaderClassificationSpan> Classify(
        string text,
        bool hlsl = false,
        CancellationToken cancellationToken = default)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tokens = Lexer.Tokenize(text, cancellationToken);
        var analyzer = new Analyzer(tokens, hlsl, cancellationToken);
        analyzer.Analyze();

        var result = new List<ShaderClassificationSpan>(tokens.Count);
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind.HasValue && token.LexicalKind != LexicalKind.Punctuation)
            {
                result.Add(new ShaderClassificationSpan(token.Start, token.Length, token.Kind.Value));
            }
        }

        return new ReadOnlyCollection<ShaderClassificationSpan>(result);
    }

    private enum LexicalKind
    {
        Identifier,
        Number,
        String,
        Comment,
        Directive,
        Punctuation,
    }

    private enum ScopeKind
    {
        Root,
        Block,
        Structure,
        Interface,
    }

    private sealed class Token
    {
        public Token(int start, int length, int line, string text, LexicalKind lexicalKind, ShaderTokenKind? kind = null, string? directiveName = null)
        {
            Start = start;
            Length = length;
            Line = line;
            Text = text;
            LexicalKind = lexicalKind;
            Kind = kind;
            DirectiveName = directiveName;
        }

        public int Start { get; }

        public int Length { get; }

        public int Line { get; }

        public string Text { get; }

        public LexicalKind LexicalKind { get; }

        public ShaderTokenKind? Kind { get; set; }

        public string? DirectiveName { get; }

        public Scope? Scope { get; set; }

        public bool IsPreprocessor { get; set; }
    }

    private sealed class Scope
    {
        public Scope(Scope? parent, ScopeKind kind, int openTokenIndex)
        {
            Parent = parent;
            Kind = kind;
            OpenTokenIndex = openTokenIndex;
        }

        public Scope? Parent { get; set; }

        public ScopeKind Kind { get; set; }

        public int OpenTokenIndex { get; }

        public int CloseTokenIndex { get; set; } = -1;
    }

    private sealed class Symbol
    {
        public Symbol(string name, ShaderTokenKind kind, Scope scope, int declarationIndex)
        {
            Name = name;
            Kind = kind;
            Scope = scope;
            DeclarationIndex = declarationIndex;
        }

        public string Name { get; }

        public ShaderTokenKind Kind { get; }

        public Scope Scope { get; }

        public int DeclarationIndex { get; }
    }

    private static class Lexer
    {
        public static List<Token> Tokenize(string text, CancellationToken cancellationToken)
        {
            var tokens = new List<Token>();
            var index = 0;
            var line = 0;
            var atLineStart = true;
            string? currentDirective = null;

            while (index < text.Length)
            {
                CheckCancellation(index, cancellationToken);
                var character = text[index];
                // Line splicing happens before preprocessing. Keep the same
                // logical line while preserving offsets in the original text.
                if (character == '\\' && index + 1 < text.Length &&
                    (text[index + 1] == '\n' || text[index + 1] == '\r' && index + 2 < text.Length && text[index + 2] == '\n'))
                {
                    index += text[index + 1] == '\r' ? 3 : 2;
                    continue;
                }
                if (char.IsWhiteSpace(character))
                {
                    if (character == '\n')
                    {
                        line++;
                        atLineStart = true;
                        currentDirective = null;
                    }

                    index++;
                    continue;
                }

                if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    var start = index;
                    index += 2;
                    while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                    {
                        CheckCancellation(index, cancellationToken);
                        index++;
                    }

                    tokens.Add(new Token(start, index - start, line, text.Substring(start, index - start), LexicalKind.Comment, ShaderTokenKind.Comment));
                    atLineStart = false;
                    continue;
                }

                if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
                {
                    var start = index;
                    var startLine = line;
                    index += 2;
                    while (index < text.Length)
                    {
                        CheckCancellation(index, cancellationToken);
                        if (text[index] == '\n')
                        {
                            line++;
                            currentDirective = null;
                        }

                        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                        {
                            index += 2;
                            break;
                        }

                        index++;
                    }

                    tokens.Add(new Token(start, index - start, startLine, text.Substring(start, index - start), LexicalKind.Comment, ShaderTokenKind.Comment));
                    atLineStart = false;
                    continue;
                }

                if (atLineStart && character == '#')
                {
                    var start = index;
                    var startLine = line;
                    index++;
                    while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
                    {
                        CheckCancellation(index, cancellationToken);
                        index++;
                    }

                    var nameStart = index;
                    while (index < text.Length && IsIdentifierPart(text[index]))
                    {
                        CheckCancellation(index, cancellationToken);
                        index++;
                    }

                    var directiveName = text.Substring(nameStart, index - nameStart);
                    var directiveLength = index - start;
                    tokens.Add(new Token(start, directiveLength, startLine, text.Substring(start, directiveLength), LexicalKind.Directive, ShaderTokenKind.PreprocessorKeyword, directiveName));
                    currentDirective = directiveName;
                    atLineStart = false;
                    continue;
                }

                if ((character == '"' || character == '\'') ||
                    (character == '<' && string.Equals(currentDirective, "include", StringComparison.Ordinal)))
                {
                    var start = index;
                    var startLine = line;
                    var terminator = character == '<' ? '>' : character;
                    index++;
                    while (index < text.Length)
                    {
                        CheckCancellation(index, cancellationToken);
                        var current = text[index];
                        if (current == '\n')
                        {
                            line++;
                            currentDirective = null;
                        }

                        if (current == '\\' && terminator != '>' && index + 1 < text.Length)
                        {
                            if (text[index + 1] == '\n')
                            {
                                line++;
                            }

                            index += 2;
                            continue;
                        }

                        index++;
                        if (current == terminator)
                        {
                            break;
                        }
                    }

                    tokens.Add(new Token(start, index - start, startLine, text.Substring(start, index - start), LexicalKind.String, ShaderTokenKind.String));
                    atLineStart = false;
                    continue;
                }

                if (IsIdentifierStart(character))
                {
                    var start = index++;
                    while (index < text.Length && IsIdentifierPart(text[index]))
                    {
                        CheckCancellation(index, cancellationToken);
                        index++;
                    }

                    tokens.Add(new Token(start, index - start, line, text.Substring(start, index - start), LexicalKind.Identifier));
                    atLineStart = false;
                    continue;
                }

                if (char.IsDigit(character) || (character == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
                {
                    var start = index;
                    index = ScanNumber(text, index, cancellationToken);
                    tokens.Add(new Token(start, index - start, line, text.Substring(start, index - start), LexicalKind.Number, ShaderTokenKind.Number));
                    atLineStart = false;
                    continue;
                }

                tokens.Add(new Token(index, 1, line, text.Substring(index, 1), LexicalKind.Punctuation));
                index++;
                atLineStart = false;
            }

            return tokens;
        }

        private static int ScanNumber(string text, int index, CancellationToken cancellationToken)
        {
            if (index + 1 < text.Length && text[index] == '0' && (text[index + 1] == 'x' || text[index + 1] == 'X'))
            {
                index += 2;
                while (index < text.Length && (IsHexDigit(text[index]) || text[index] == '_'))
                {
                    CheckCancellation(index, cancellationToken);
                    index++;
                }
            }
            else
            {
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                if (index < text.Length && text[index] == '.')
                {
                    index++;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
                    {
                        index++;
                    }
                }

                if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
                {
                    index++;
                    if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                    {
                        index++;
                    }

                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
                    {
                        CheckCancellation(index, cancellationToken);
                        index++;
                    }
                }
            }

            while (index < text.Length && (text[index] == 'f' || text[index] == 'F' || text[index] == 'l' || text[index] == 'L' || text[index] == 'u' || text[index] == 'U'))
            {
                index++;
            }

            return index;
        }

        private static bool IsHexDigit(char value) =>
            char.IsDigit(value) || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');

        private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

        private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

        private static void CheckCancellation(int index, CancellationToken cancellationToken)
        {
            if ((index & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class Analyzer
    {
        private static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "discard", "goto",
        };

        private static readonly HashSet<string> ScalarKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "void", "bool", "int", "uint", "float", "double", "half", "short", "ushort", "long", "ulong", "char",
        };

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "layout",
            "in", "out", "inout", "uniform", "buffer", "shared", "attribute", "varying", "const", "centroid", "sample", "patch",
            "flat", "smooth", "noperspective", "invariant", "precise", "coherent", "volatile", "restrict", "readonly", "writeonly",
            "highp", "mediump", "lowp", "precision", "subroutine", "struct", "true", "false", "namespace", "typedef", "using",
            "static", "extern", "inline", "groupshared", "cbuffer", "tbuffer", "register", "packoffset", "row_major", "column_major",
            "nointerpolation", "linear", "snorm", "unorm", "globallycoherent", "numthreads", "technique", "technique11", "pass",
        };

        private static readonly HashSet<string> ExactGlslTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "atomic_uint", "subpassInput", "subpassInputMS", "isubpassInput", "isubpassInputMS", "usubpassInput", "usubpassInputMS",
            "accelerationStructureEXT", "rayQueryEXT",
        };

        private static readonly HashSet<string> ExactHlslTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "matrix", "vector", "SamplerState", "SamplerComparisonState", "Buffer", "RWBuffer", "ByteAddressBuffer", "RWByteAddressBuffer",
            "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer", "ConstantBuffer", "Texture1D",
            "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube",
            "TextureCubeArray", "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray", "RWTexture3D", "RaytracingAccelerationStructure",
        };

        private readonly List<Token> tokens;
        private readonly bool hlsl;
        private readonly CancellationToken cancellationToken;
        private readonly Scope root = new Scope(null, ScopeKind.Root, -1);
        private readonly Dictionary<int, Scope> scopesByOpen = new Dictionary<int, Scope>();
        private readonly Dictionary<int, int> parentheses = new Dictionary<int, int>();
        private readonly HashSet<string> customTypes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> functions = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> macros = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Symbol> symbols = new List<Symbol>();
        private readonly Dictionary<string, List<Symbol>> symbolsByName = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);

        public Analyzer(List<Token> tokens, bool hlsl, CancellationToken cancellationToken)
        {
            this.tokens = tokens;
            this.hlsl = hlsl;
            this.cancellationToken = cancellationToken;
        }

        public void Analyze()
        {
            AssignLexicalKinds();
            MarkDirectiveTokens();
            BuildScopesAndPairs();
            ClassifyLayoutQualifiers();
            BuildLoopScopes();
            ClassifyPreprocessor();
            IdentifyAggregateScopes();
            IdentifyFunctions();
            IdentifyVariables();
            IdentifyAggregateInstances();
            ResolveIdentifiers();
        }

        private void AssignLexicalKinds()
        {
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var token = tokens[index];
                if (token.LexicalKind != LexicalKind.Identifier)
                {
                    continue;
                }

                if (ControlKeywords.Contains(token.Text))
                {
                    token.Kind = ShaderTokenKind.ControlKeyword;
                }
                else if (ScalarKeywords.Contains(token.Text) || Keywords.Contains(token.Text))
                {
                    token.Kind = ShaderTokenKind.Keyword;
                }
                else if (IsBuiltInType(token.Text))
                {
                    token.Kind = ShaderTokenKind.Type;
                }
                else if (!hlsl && token.Text.StartsWith("gl_", StringComparison.Ordinal))
                {
                    token.Kind = ShaderTokenKind.GlobalVariable;
                }
            }
        }

        private void MarkDirectiveTokens()
        {
            var directiveLine = -1;
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var token = tokens[index];
                if (token.LexicalKind == LexicalKind.Directive)
                {
                    directiveLine = token.Line;
                }

                token.IsPreprocessor = token.Line == directiveLine;
            }
        }

        private void BuildScopesAndPairs()
        {
            var current = root;
            var braceStack = new Stack<Scope>();
            var parenthesisStack = new Stack<int>();
            var directiveParentheses = new Stack<int>();
            var directiveLine = -1;
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var token = tokens[index];
                token.Scope = current;
                if (token.IsPreprocessor)
                {
                    if (directiveLine != token.Line)
                    {
                        directiveParentheses.Clear();
                        directiveLine = token.Line;
                    }

                    if (token.Text == "(") directiveParentheses.Push(index);
                    else if (token.Text == ")" && directiveParentheses.Count > 0) parentheses[directiveParentheses.Pop()] = index;
                    continue;
                }

                if (token.Text == "{")
                {
                    var child = new Scope(current, ScopeKind.Block, index);
                    scopesByOpen[index] = child;
                    braceStack.Push(child);
                    current = child;
                }
                else if (token.Text == "}" && braceStack.Count > 0)
                {
                    var closing = braceStack.Pop();
                    closing.CloseTokenIndex = index;
                    token.Scope = closing;
                    current = closing.Parent ?? root;
                }
                else if (token.Text == "(")
                {
                    parenthesisStack.Push(index);
                }
                else if (token.Text == ")" && parenthesisStack.Count > 0)
                {
                    var opening = parenthesisStack.Pop();
                    parentheses[opening] = index;
                }
            }
        }

        private void BuildLoopScopes()
        {
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                if (tokens[index].IsPreprocessor || tokens[index].Text != "for")
                {
                    continue;
                }

                var open = NextSignificant(index + 1);
                if (open < 0 || !parentheses.TryGetValue(open, out var close))
                {
                    continue;
                }

                var body = NextSignificant(close + 1);
                var end = FindStatementEnd(body);
                var parent = tokens[index].Scope ?? root;
                var loop = new Scope(parent, ScopeKind.Block, open) { CloseTokenIndex = end };
                for (var nested = open + 1; nested <= end; nested++)
                {
                    CheckCancellation(nested);
                    if (tokens[nested].Scope == parent)
                    {
                        tokens[nested].Scope = loop;
                    }

                    if (scopesByOpen.TryGetValue(nested, out var child) && child.Parent == parent)
                    {
                        child.Parent = loop;
                    }
                }
            }
        }

        private void ClassifyLayoutQualifiers()
        {
            if (hlsl) return;
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                if (tokens[index].Text != "layout" || tokens[index].IsPreprocessor) continue;
                var open = NextSignificant(index + 1);
                if (open < 0 || !parentheses.TryGetValue(open, out var close)) continue;
                for (var item = open + 1; item < close; item++)
                {
                    CheckCancellation(item);
                    var previous = PreviousSignificant(item - 1);
                    if (tokens[item].LexicalKind == LexicalKind.Identifier && previous >= 0 &&
                        (tokens[previous].Text == "(" || tokens[previous].Text == ","))
                        tokens[item].Kind = ShaderTokenKind.Keyword;
                }
            }
        }

        private int FindStatementEnd(int start)
        {
            for (var index = start; index >= 0 && index < tokens.Count; index++)
            {
                CheckCancellation(index);
                if (parentheses.TryGetValue(index, out var close))
                {
                    index = close;
                    continue;
                }

                if (scopesByOpen.TryGetValue(index, out var scope))
                {
                    if (scope.CloseTokenIndex < 0)
                    {
                        return tokens.Count - 1;
                    }

                    index = scope.CloseTokenIndex;
                }
                else if (tokens[index].Text != ";" && tokens[index].Text != "}")
                {
                    continue;
                }

                var next = NextSignificant(index + 1);
                if (next >= 0 && tokens[next].Text == "else")
                {
                    index = next;
                    continue;
                }

                return index;
            }

            return tokens.Count - 1;
        }

        private void ClassifyPreprocessor()
        {
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var directive = tokens[index];
                if (directive.LexicalKind != LexicalKind.Directive || !string.Equals(directive.DirectiveName, "define", StringComparison.Ordinal))
                {
                    continue;
                }

                var nameIndex = NextIdentifier(index + 1, directive.Line);
                if (nameIndex < 0)
                {
                    continue;
                }

                var name = tokens[nameIndex];
                name.Kind = ShaderTokenKind.Macro;
                macros.Add(name.Text);
                var parametersForMacro = new HashSet<string>(StringComparer.Ordinal);
                var open = nameIndex + 1 < tokens.Count ? nameIndex + 1 : -1;
                if (open >= 0 && tokens[open].Start == name.Start + name.Length && tokens[open].Text == "(" && parentheses.TryGetValue(open, out var close))
                {
                    for (var parameterIndex = open + 1; parameterIndex < close; parameterIndex++)
                    {
                        CheckCancellation(parameterIndex);
                        var parameter = tokens[parameterIndex];
                        if (parameter.LexicalKind == LexicalKind.Identifier)
                        {
                            parameter.Kind = ShaderTokenKind.Parameter;
                            parametersForMacro.Add(parameter.Text);
                        }
                    }

                    for (var bodyIndex = close + 1; bodyIndex < tokens.Count && tokens[bodyIndex].Line == directive.Line; bodyIndex++)
                    {
                        CheckCancellation(bodyIndex);
                        if (tokens[bodyIndex].LexicalKind == LexicalKind.Identifier && parametersForMacro.Contains(tokens[bodyIndex].Text))
                        {
                            tokens[bodyIndex].Kind = ShaderTokenKind.Parameter;
                        }
                    }
                }
            }

            foreach (var token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (token.LexicalKind == LexicalKind.Identifier && token.Kind != ShaderTokenKind.Parameter && macros.Contains(token.Text))
                {
                    token.Kind = ShaderTokenKind.Macro;
                }
            }
        }

        private void IdentifyAggregateScopes()
        {
            foreach (var pair in scopesByOpen)
            {
                CheckCancellation(pair.Key);
                var openIndex = pair.Key;
                var scope = pair.Value;
                var previous = PreviousSignificant(openIndex - 1);
                if (hlsl && previous >= 0 && tokens[previous].Text == ")")
                {
                    // cbuffer Scene : register(b0) { ... }
                    foreach (var parenthesesPair in parentheses)
                    {
                        if (parenthesesPair.Value != previous) continue;
                        var register = PreviousSignificant(parenthesesPair.Key - 1);
                        var colon = register >= 0 ? PreviousSignificant(register - 1) : -1;
                        if (register >= 0 && tokens[register].Text == "register" && colon >= 0 && tokens[colon].Text == ":")
                            previous = PreviousSignificant(colon - 1);
                        break;
                    }
                }
                if (previous < 0 || tokens[previous].LexicalKind != LexicalKind.Identifier)
                {
                    continue;
                }

                var beforeName = PreviousSignificant(previous - 1);
                if (beforeName >= 0 && tokens[beforeName].Text == "struct")
                {
                    tokens[previous].Kind = ShaderTokenKind.Type;
                    customTypes.Add(tokens[previous].Text);
                    scope.Kind = ScopeKind.Structure;
                    continue;
                }

                if (HasInterfaceQualifier(previous - 1))
                {
                    tokens[previous].Kind = ShaderTokenKind.Type;
                    customTypes.Add(tokens[previous].Text);
                    scope.Kind = ScopeKind.Interface;
                }
            }
        }

        private bool HasInterfaceQualifier(int index)
        {
            var inspected = 0;
            for (; index >= 0 && inspected < 64; index--, inspected++)
            {
                var token = tokens[index];
                if (token.Text == ";" || token.Text == "{" || token.Text == "}")
                {
                    return false;
                }

                if (token.Text == "uniform" || token.Text == "buffer" || token.Text == "in" || token.Text == "out" || token.Text == "cbuffer" || token.Text == "tbuffer")
                {
                    return true;
                }
            }

            return false;
        }

        private void IdentifyFunctions()
        {
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var name = tokens[index];
                if (name.IsPreprocessor || name.LexicalKind != LexicalKind.Identifier || IsPrecededByDot(index))
                {
                    continue;
                }

                var open = NextSignificant(index + 1);
                if (open < 0 || tokens[open].Text != "(" || !parentheses.TryGetValue(open, out var close))
                {
                    continue;
                }

                var returnType = PreviousSignificant(index - 1);
                if (returnType < 0 || !IsTypeLike(tokens[returnType]) || tokens[returnType].Text == "void" && tokens[returnType].Kind != ShaderTokenKind.Keyword)
                {
                    continue;
                }

                var after = NextSignificant(close + 1);
                var bodyOpen = after;
                if (hlsl && after >= 0 && tokens[after].Text == ":")
                {
                    var semantic = NextIdentifier(after + 1, tokens[after].Line);
                    if (semantic >= 0)
                    {
                        tokens[semantic].Kind = ShaderTokenKind.Semantic;
                        bodyOpen = NextSignificant(semantic + 1);
                    }
                }

                if (bodyOpen < 0 || tokens[bodyOpen].Text != "{" && tokens[bodyOpen].Text != ";")
                {
                    continue;
                }

                name.Kind = ShaderTokenKind.Function;
                functions.Add(name.Text);
                Scope? bodyScope = null;
                if (tokens[bodyOpen].Text == "{" && scopesByOpen.TryGetValue(bodyOpen, out var foundScope))
                {
                    bodyScope = foundScope;
                }

                IdentifyParameters(open + 1, close, bodyScope);
            }
        }

        private void IdentifyParameters(int start, int end, Scope? bodyScope)
        {
            var segmentStart = start;
            var parenthesisDepth = 0;
            var bracketDepth = 0;
            var genericDepth = 0;
            for (var index = start; index <= end; index++)
            {
                CheckCancellation(index);
                var atEnd = index == end;
                if (!atEnd)
                {
                    if (tokens[index].Text == "(") parenthesisDepth++;
                    else if (tokens[index].Text == ")") parenthesisDepth--;
                    else if (tokens[index].Text == "[") bracketDepth++;
                    else if (tokens[index].Text == "]") bracketDepth--;
                    else if (hlsl && tokens[index].Text == "<" && (genericDepth > 0 || index > start && IsTypeLike(tokens[index - 1]))) genericDepth++;
                    else if (tokens[index].Text == ">" && genericDepth > 0) genericDepth--;
                }

                if (atEnd || tokens[index].Text == "," && parenthesisDepth == 0 && bracketDepth == 0 && genericDepth == 0)
                {
                    IdentifyParameterSegment(segmentStart, index, bodyScope);
                    segmentStart = index + 1;
                }
            }
        }

        private void IdentifyParameterSegment(int start, int end, Scope? bodyScope)
        {
            var colon = -1;
            for (var index = start; index < end; index++)
            {
                CheckCancellation(index);
                if (tokens[index].Text == ":")
                {
                    colon = index;
                    break;
                }
            }

            if (hlsl && colon >= 0)
            {
                var semantic = NextIdentifier(colon + 1, tokens[colon].Line);
                if (semantic >= 0 && semantic < end)
                {
                    tokens[semantic].Kind = ShaderTokenKind.Semantic;
                }
            }

            var limit = colon >= 0 ? colon : end;
            var candidate = -1;
            for (var index = start; index < limit; index++)
            {
                CheckCancellation(index);
                if (IsTypeLike(tokens[index]))
                {
                    var declarator = FindDeclarator(index + 1);
                    if (declarator >= 0 && declarator < limit && tokens[declarator].LexicalKind == LexicalKind.Identifier &&
                        !IsTypeLike(tokens[declarator]) && tokens[declarator].Kind != ShaderTokenKind.Keyword)
                    {
                        candidate = declarator;
                        break;
                    }
                }
            }

            if (candidate < 0)
            {
                return;
            }

            tokens[candidate].Kind = ShaderTokenKind.Parameter;
            if (bodyScope != null)
            {
                symbols.Add(new Symbol(tokens[candidate].Text, ShaderTokenKind.Parameter, bodyScope, candidate));
            }
        }

        private void IdentifyVariables()
        {
            for (var typeIndex = 0; typeIndex < tokens.Count; typeIndex++)
            {
                CheckCancellation(typeIndex);
                var type = tokens[typeIndex];
                if (type.IsPreprocessor || !IsTypeLike(type) || type.Text == "void" || IsPrecededByDot(typeIndex))
                {
                    continue;
                }

                var candidate = FindDeclarator(typeIndex + 1);
                if (candidate < 0 || tokens[candidate].LexicalKind != LexicalKind.Identifier)
                {
                    continue;
                }

                var afterCandidate = NextSignificant(candidate + 1);
                if (afterCandidate >= 0 && tokens[afterCandidate].Text == "(")
                {
                    continue;
                }

                if (tokens[candidate].Kind == ShaderTokenKind.Parameter || tokens[candidate].Kind == ShaderTokenKind.Function || tokens[candidate].Kind == ShaderTokenKind.Type)
                {
                    continue;
                }


                var kind = DeclarationKind(tokens[candidate].Scope ?? root);
                Declare(candidate, kind, tokens[candidate].Scope ?? root);
                IdentifyAdditionalDeclarators(candidate + 1, tokens[candidate].Scope ?? root, kind);
            }
        }

        private int FindDeclarator(int start)
        {
            var index = NextSignificant(start);
            if (index < 0)
            {
                return -1;
            }

            if (tokens[index].Text == "<")
            {
                var depth = 0;
                for (; index < tokens.Count; index++)
                {
                    CheckCancellation(index);
                    if (tokens[index].Text == "<") depth++;
                    else if (tokens[index].Text == ">")
                    {
                        depth--;
                        if (depth == 0)
                        {
                            index = NextSignificant(index + 1);
                            break;
                        }
                    }
                }
            }

            while (index >= 0 && index < tokens.Count && (tokens[index].Text == "*" || tokens[index].Text == "&"))
            {
                index = NextSignificant(index + 1);
            }

            return index < tokens.Count ? index : -1;
        }

        private void IdentifyAdditionalDeclarators(int start, Scope scope, ShaderTokenKind kind)
        {
            var parenthesesDepth = 0;
            var bracketsDepth = 0;
            var bracesDepth = 0;
            for (var index = start; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                if (tokens[index].Scope != scope && bracesDepth == 0)
                {
                    return;
                }

                switch (tokens[index].Text)
                {
                    case "(": parenthesesDepth++; break;
                    case ")": parenthesesDepth--; break;
                    case "[": bracketsDepth++; break;
                    case "]": bracketsDepth--; break;
                    case "{": bracesDepth++; break;
                    case "}":
                        if (bracesDepth == 0) return;
                        bracesDepth--;
                        break;
                    case ";" when parenthesesDepth == 0 && bracketsDepth == 0 && bracesDepth == 0:
                        return;
                    case "," when parenthesesDepth == 0 && bracketsDepth == 0 && bracesDepth == 0:
                        var candidate = NextSignificant(index + 1);
                        if (candidate >= 0 && tokens[candidate].LexicalKind == LexicalKind.Identifier)
                        {
                            Declare(candidate, kind, scope);
                        }
                        break;
                }
            }
        }

        private void IdentifyAggregateInstances()
        {
            foreach (var scope in scopesByOpen.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((scope.Kind != ScopeKind.Interface && scope.Kind != ScopeKind.Structure) || scope.CloseTokenIndex < 0)
                {
                    continue;
                }

                var instance = NextSignificant(scope.CloseTokenIndex + 1);
                if (instance >= 0 && tokens[instance].LexicalKind == LexicalKind.Identifier)
                {
                    var owner = scope.Parent ?? root;
                    var kind = DeclarationKind(owner);
                    Declare(instance, kind, owner);
                    IdentifyAdditionalDeclarators(instance + 1, owner, kind);
                }
                else if (scope.Kind == ScopeKind.Interface)
                {
                    foreach (var symbol in symbols.ToArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (symbol.Scope == scope && symbol.Kind == ShaderTokenKind.Field)
                        {
                            symbols.Add(new Symbol(symbol.Name, ShaderTokenKind.Field, scope.Parent ?? root, symbol.DeclarationIndex));
                        }
                    }
                }
            }
        }

        private void ResolveIdentifiers()
        {
            foreach (var symbol in symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!symbolsByName.TryGetValue(symbol.Name, out var declarations))
                    symbolsByName.Add(symbol.Name, declarations = new List<Symbol>());
                declarations.Add(symbol);
            }
            for (var index = 0; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                var token = tokens[index];
                if (token.LexicalKind != LexicalKind.Identifier || token.Kind.HasValue)
                {
                    continue;
                }

                if (macros.Contains(token.Text))
                {
                    token.Kind = ShaderTokenKind.Macro;
                }
                else if (customTypes.Contains(token.Text))
                {
                    token.Kind = ShaderTokenKind.Type;
                }
                else if (IsPrecededByDot(index))
                {
                    token.Kind = IsFollowedByOpenParenthesis(index) ? ShaderTokenKind.Function : ShaderTokenKind.Field;
                }
                else
                {
                    var symbol = FindSymbol(token.Text, token.Scope ?? root, index);
                    token.Kind = symbol?.Kind ?? (functions.Contains(token.Text) || IsFollowedByOpenParenthesis(index)
                        ? ShaderTokenKind.Function : ShaderTokenKind.Identifier);
                }
            }
        }

        private Symbol? FindSymbol(string name, Scope scope, int useIndex)
        {
            if (!symbolsByName.TryGetValue(name, out var declarations)) return null;
            for (var current = scope; current != null; current = current.Parent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Symbol? best = null;
                foreach (var symbol in declarations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (symbol.Scope != current || !string.Equals(symbol.Name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (current.Kind != ScopeKind.Root && symbol.DeclarationIndex > useIndex)
                    {
                        continue;
                    }

                    if (best == null || symbol.DeclarationIndex > best.DeclarationIndex)
                    {
                        best = symbol;
                    }
                }

                if (best != null)
                {
                    return best;
                }
            }

            return null;
        }

        private void Declare(int index, ShaderTokenKind kind, Scope scope)
        {
            tokens[index].Kind = kind;
            symbols.Add(new Symbol(tokens[index].Text, kind, scope, index));
            if (hlsl)
            {
                var colon = NextSignificant(index + 1);
                while (colon >= 0 && tokens[colon].Text == "[")
                {
                    var depth = 1;
                    while (++colon < tokens.Count && depth > 0)
                    {
                        CheckCancellation(colon);
                        if (tokens[colon].Text == "[") depth++;
                        else if (tokens[colon].Text == "]") depth--;
                    }
                    colon = NextSignificant(colon);
                }
                if (colon >= 0 && tokens[colon].Text == ":")
                {
                    var semantic = NextIdentifier(colon + 1, tokens[colon].Line);
                    if (semantic >= 0 && tokens[semantic].Text != "register" && tokens[semantic].Text != "packoffset")
                    {
                        tokens[semantic].Kind = ShaderTokenKind.Semantic;
                    }
                }
            }
        }

        private static ShaderTokenKind DeclarationKind(Scope scope)
        {
            if (scope.Kind == ScopeKind.Structure || scope.Kind == ScopeKind.Interface)
            {
                return ShaderTokenKind.Field;
            }

            return scope.Kind == ScopeKind.Root ? ShaderTokenKind.GlobalVariable : ShaderTokenKind.LocalVariable;
        }

        private bool IsBuiltInType(string value)
        {
            if (ExactGlslTypes.Contains(value) || IsGlslResourceType(value))
            {
                return true;
            }

            if (IsGlslVectorOrMatrix(value))
            {
                return true;
            }

            return hlsl && (ExactHlslTypes.Contains(value) || IsHlslVectorOrMatrix(value));
        }

        private static bool IsGlslVectorOrMatrix(string value)
        {
            foreach (var prefix in new[] { "vec", "dvec", "bvec", "ivec", "uvec" })
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return value.Length == prefix.Length + 1 && IsDimension(value[prefix.Length], '2');
                }
            }

            foreach (var prefix in new[] { "mat", "dmat" })
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return IsDimensionSuffix(value, prefix.Length, '2');
                }
            }

            return false;
        }

        private static bool IsHlslVectorOrMatrix(string value)
        {
            var prefixes = new[] { "bool", "int", "uint", "dword", "half", "float", "double", "min16float", "min10float", "min16int", "min12int", "min16uint" };
            foreach (var prefix in prefixes)
            {
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length)
                {
                    continue;
                }

                return IsDimensionSuffix(value, prefix.Length, '1');
            }

            return false;
        }

        private static bool IsDimension(char value, char minimum) => value >= minimum && value <= '4';

        private static bool IsDimensionSuffix(string value, int start, char minimum) =>
            value.Length == start + 1 && IsDimension(value[start], minimum) ||
            value.Length == start + 3 && IsDimension(value[start], minimum) && value[start + 1] == 'x' && IsDimension(value[start + 2], minimum);

        private static bool IsGlslResourceType(string value)
        {
            foreach (var prefix in new[] { "sampler", "isampler", "usampler", "image", "iimage", "uimage" })
            {
                if (!value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (value.Substring(prefix.Length))
                {
                    case "1D": case "2D": case "3D": case "Cube":
                    case "2DRect": case "1DArray": case "2DArray": case "CubeArray":
                    case "Buffer": case "2DMS": case "2DMSArray":
                    case "1DShadow": case "2DShadow": case "CubeShadow": case "2DRectShadow":
                    case "1DArrayShadow": case "2DArrayShadow": case "CubeArrayShadow": case "ExternalOES":
                        return true;
                    default:
                        return false;
                }
            }

            return false;
        }

        private bool IsTypeLike(Token token) =>
            token.Kind == ShaderTokenKind.Type || ScalarKeywords.Contains(token.Text) || customTypes.Contains(token.Text);

        private bool IsPrecededByDot(int index)
        {
            var previous = PreviousSignificant(index - 1);
            return previous >= 0 && tokens[previous].Text == ".";
        }

        private bool IsFollowedByOpenParenthesis(int index)
        {
            var next = NextSignificant(index + 1);
            return next >= 0 && tokens[next].Text == "(";
        }

        private int NextIdentifier(int start, int line)
        {
            for (var index = start; index < tokens.Count && tokens[index].Line == line; index++)
            {
                CheckCancellation(index);
                if (tokens[index].LexicalKind == LexicalKind.Identifier)
                {
                    return index;
                }
            }

            return -1;
        }

        private int NextSignificant(int start)
        {
            for (var index = start; index < tokens.Count; index++)
            {
                CheckCancellation(index);
                if (tokens[index].LexicalKind != LexicalKind.Comment && !tokens[index].IsPreprocessor)
                {
                    return index;
                }
            }

            return -1;
        }

        private int PreviousSignificant(int start)
        {
            for (var index = start; index >= 0; index--)
            {
                CheckCancellation(index);
                if (tokens[index].LexicalKind != LexicalKind.Comment && !tokens[index].IsPreprocessor)
                {
                    return index;
                }
            }

            return -1;
        }

        private void CheckCancellation(int index)
        {
            if ((index & 0x7f) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
