using ShaderTools.Core.Highlighting;
using Xunit;

namespace ShaderTools.Tests;

public sealed class HighlightingTests
{
    [Fact]
    public void ClassifiesUserFragmentShaderDeclarationsAndReferencesConsistently()
    {
        const string shader = """
            out vec4 FragColor;
            in vec3 ourColor;
            in vec2 TexCoord;
            uniform sampler2D texture1;
            void main(){FragColor=mix(texture(texture1,TexCoord),texture(texture2,TexCoord),0.2);}
            """;

        var tokens = Classified(shader);

        AssertKinds(tokens, "FragColor", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "ourColor", ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "TexCoord", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "texture1", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "texture2", ShaderTokenKind.Identifier);
        AssertKinds(tokens, "main", ShaderTokenKind.Function);
        AssertKinds(tokens, "mix", ShaderTokenKind.Function);
        AssertKinds(tokens, "texture", ShaderTokenKind.Function, ShaderTokenKind.Function);
        AssertKinds(tokens, "vec4", ShaderTokenKind.Type);
        AssertKinds(tokens, "vec3", ShaderTokenKind.Type);
        AssertKinds(tokens, "vec2", ShaderTokenKind.Type);
        AssertKinds(tokens, "sampler2D", ShaderTokenKind.Type);
        AssertKinds(tokens, "void", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "out", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "in", ShaderTokenKind.Keyword, ShaderTokenKind.Keyword);
        AssertKinds(tokens, "uniform", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "0.2", ShaderTokenKind.Number);
    }

    [Fact]
    public void ClassifiesCompactLayoutShaderAndBuiltinGlobal()
    {
        const string shader = "layout(location=0)in vec3 aPos;void main(){gl_Position=vec4(aPos,1.0);}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "layout", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "location", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "in", ShaderTokenKind.Keyword);
        AssertKinds(tokens, "aPos", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "gl_Position", ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "vec4", ShaderTokenKind.Type);
        AssertKinds(tokens, "0", ShaderTokenKind.Number);
        AssertKinds(tokens, "1.0", ShaderTokenKind.Number);
    }

    [Fact]
    public void SeparatesPreprocessorMacrosStringsCommentsAndScientificNumbers()
    {
        const string shader = """
            #version 450
            #define SCALE(x) ((x) * 1e-2)
            #include "common.glsl"
            void main() {
                /* vec4 hidden
                   SCALE(hidden) */
                float local = .5e+2;
                local = SCALE(local);
            }
            """;

        var tokens = Classified(shader);

        AssertKinds(tokens, "#version", ShaderTokenKind.PreprocessorKeyword);
        AssertKinds(tokens, "#define", ShaderTokenKind.PreprocessorKeyword);
        AssertKinds(tokens, "#include", ShaderTokenKind.PreprocessorKeyword);
        AssertKinds(tokens, "450", ShaderTokenKind.Number);
        AssertKinds(tokens, "SCALE", ShaderTokenKind.Macro, ShaderTokenKind.Macro);
        AssertKinds(tokens, "common.glsl", new[] { ShaderTokenKind.String }, includeDelimiters: true);
        AssertKinds(tokens, "1e-2", ShaderTokenKind.Number);
        AssertKinds(tokens, ".5e+2", ShaderTokenKind.Number);
        Assert.Single(tokens, token => token.Kind == ShaderTokenKind.Comment);
        Assert.DoesNotContain(tokens, token => token.Text == "hidden" || token.Text == "vec4" && token.Start > shader.IndexOf("/*", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvesParametersLocalsGlobalsAndNestedShadowing()
    {
        const string shader = "float value; float fn(float value){float result=value;{float value=1.;result=value;}return value;}";

        var tokens = Classified(shader);

        AssertKinds(
            tokens,
            "value",
            ShaderTokenKind.GlobalVariable,
            ShaderTokenKind.Parameter,
            ShaderTokenKind.Parameter,
            ShaderTokenKind.LocalVariable,
            ShaderTokenKind.LocalVariable,
            ShaderTokenKind.Parameter);
        AssertKinds(tokens, "result", ShaderTokenKind.LocalVariable, ShaderTokenKind.LocalVariable);
        AssertKinds(tokens, "fn", ShaderTokenKind.Function);
        AssertKinds(tokens, "float", Enumerable.Repeat(ShaderTokenKind.Keyword, 5).ToArray());
        AssertKinds(tokens, "return", ShaderTokenKind.ControlKeyword);
    }

    [Fact]
    public void ClassifiesStructInterfaceFieldsAndSwizzles()
    {
        const string shader = "struct Light{vec3 color;float intensity;};uniform Scene{Light light;vec4 tint;}scene;void main(){vec3 c=scene.light.color.xyz;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "Light", ShaderTokenKind.Type, ShaderTokenKind.Type);
        AssertKinds(tokens, "Scene", ShaderTokenKind.Type);
        AssertKinds(tokens, "color", ShaderTokenKind.Field, ShaderTokenKind.Field);
        AssertKinds(tokens, "intensity", ShaderTokenKind.Field);
        AssertKinds(tokens, "light", ShaderTokenKind.Field, ShaderTokenKind.Field);
        AssertKinds(tokens, "tint", ShaderTokenKind.Field);
        AssertKinds(tokens, "xyz", ShaderTokenKind.Field);
        AssertKinds(tokens, "scene", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "c", ShaderTokenKind.LocalVariable);
    }

    [Fact]
    public void ClassifiesHlslResourceTypesFunctionsFieldsParametersAndSemantics()
    {
        const string shader = """
            Texture2D<float4> Albedo : register(t0);
            SamplerState LinearSampler : register(s0);
            struct PSInput { float4 position : SV_Position; float2 uv : TEXCOORD0; };
            float4 main(PSInput input) : SV_Target { return Albedo.Sample(LinearSampler,input.uv); }
            """;

        var tokens = Classified(shader, hlsl: true);

        AssertKinds(tokens, "Texture2D", ShaderTokenKind.Type);
        AssertKinds(tokens, "SamplerState", ShaderTokenKind.Type);
        AssertKinds(tokens, "float4", ShaderTokenKind.Type, ShaderTokenKind.Type, ShaderTokenKind.Type);
        AssertKinds(tokens, "float2", ShaderTokenKind.Type);
        AssertKinds(tokens, "PSInput", ShaderTokenKind.Type, ShaderTokenKind.Type);
        AssertKinds(tokens, "Albedo", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "LinearSampler", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "position", ShaderTokenKind.Field);
        AssertKinds(tokens, "uv", ShaderTokenKind.Field, ShaderTokenKind.Field);
        AssertKinds(tokens, "Sample", ShaderTokenKind.Function);
        AssertKinds(tokens, "input", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
        AssertKinds(tokens, "main", ShaderTokenKind.Function);
        AssertKinds(tokens, "register", ShaderTokenKind.Keyword, ShaderTokenKind.Keyword);
        AssertKinds(tokens, "SV_Position", ShaderTokenKind.Semantic);
        AssertKinds(tokens, "TEXCOORD0", ShaderTokenKind.Semantic);
        AssertKinds(tokens, "SV_Target", ShaderTokenKind.Semantic);
    }

    [Fact]
    public void BuiltinTypePrefixesDoNotOverrideUserVariableNames()
    {
        const string shader = "float samplerValue; float vector2; float mat; void main(){samplerValue=vector2+mat;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "samplerValue", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "vector2", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "mat", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void ParameterArraySizeIsAReferenceRatherThanTheParameterName()
    {
        const string shader = "const int Count=3; float fn(float values[Count]){return values[Count-1];}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "values", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
        AssertKinds(tokens, "Count", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void HlslDefaultArgumentIsAReferenceRatherThanTheParameterName()
    {
        const string shader = "float DefaultValue; float fn(float value=DefaultValue){return value;}";

        var tokens = Classified(shader, hlsl: true);

        AssertKinds(tokens, "value", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
        AssertKinds(tokens, "DefaultValue", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void LoopInitializerShadowingEndsAtTheLoopBody()
    {
        const string shader = "int i; void main(){for(int i=0;i<2;i++){float x=i;} int after=i;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "i", ShaderTokenKind.GlobalVariable, ShaderTokenKind.LocalVariable,
            ShaderTokenKind.LocalVariable, ShaderTokenKind.LocalVariable, ShaderTokenKind.LocalVariable,
            ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void StructInstanceDeclarationsAndReferencesShareTheirKind()
    {
        const string shader = "struct Light{vec3 color;}light,otherLight; void main(){vec3 c=light.color+otherLight.color;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "light", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
        AssertKinds(tokens, "otherLight", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void MacroReplacementBracesDoNotCreateShaderScopes()
    {
        const string shader = "#define OPEN {\nfloat global; void main(){global=1.;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "OPEN", ShaderTokenKind.Macro);
        AssertKinds(tokens, "global", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void ObjectMacroParenthesesDoNotDeclareParameters()
    {
        const string shader = "float amount;\n#define SCALE (amount)\nvoid main(){amount=SCALE;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "SCALE", ShaderTokenKind.Macro, ShaderTokenKind.Macro);
        AssertKinds(tokens, "amount", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void HlslConstantBufferRegisterClausePreservesFieldKinds()
    {
        const string shader = "cbuffer Scene : register(b0) {float4 tint;}; float4 main():SV_Target{return tint;}";

        var tokens = Classified(shader, hlsl: true);

        AssertKinds(tokens, "Scene", ShaderTokenKind.Type);
        AssertKinds(tokens, "tint", ShaderTokenKind.Field, ShaderTokenKind.Field);
    }

    [Fact]
    public void HlslGenericParameterCommasDoNotSplitParameterDeclarations()
    {
        const string shader = "float4 fn(Texture2DMS<float4,4> imageData,float2 uv){return imageData.Load(uv,0);}";

        var tokens = Classified(shader, hlsl: true);

        AssertKinds(tokens, "imageData", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
        AssertKinds(tokens, "uv", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
    }

    [Fact]
    public void HlslSemanticsAreRecognizedFromDeclarationsRatherThanTernaryColons()
    {
        const string shader = "struct Input{float2 uv[2] : texcoord0;}; float B; float fn(bool flag){return flag?1.:B;}";

        var tokens = Classified(shader, hlsl: true);

        AssertKinds(tokens, "texcoord0", ShaderTokenKind.Semantic);
        AssertKinds(tokens, "B", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void LocalVariablesCanShadowFunctionNames()
    {
        const string shader = "float light(){return 1.;} void main(){float light=1.;float x=light;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "light", ShaderTokenKind.Function, ShaderTokenKind.LocalVariable, ShaderTokenKind.LocalVariable);
    }

    [Fact]
    public void LayoutNamesAreKeywordsOnlyInsideLayoutQualifiers()
    {
        const string shader = "layout(location=0)out float color;float location; void main(){color=location;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "location", ShaderTokenKind.Keyword, ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void ContinuedMacroParametersAndBracesStayInsideTheDirective()
    {
        const string shader = "#define OPEN(x) \\\r\n { x\r\nfloat global;void main(){global=1.;}";

        var tokens = Classified(shader);

        AssertKinds(tokens, "x", ShaderTokenKind.Parameter, ShaderTokenKind.Parameter);
        AssertKinds(tokens, "global", ShaderTokenKind.GlobalVariable, ShaderTokenKind.GlobalVariable);
    }

    [Fact]
    public void IncompleteMultilineTokensTerminateAndProduceOrderedNonOverlappingUtf16Spans()
    {
        const string shader = "void main(){\r\n\"😀 unfinished\r\n/* comment";

        var spans = ShaderClassifier.Classify(shader);

        Assert.Contains(spans, span => span.Kind == ShaderTokenKind.String && shader.Substring(span.Start, span.Length).StartsWith("\"😀", StringComparison.Ordinal));
        Assert.All(spans, span => Assert.InRange(span.Start + span.Length, 0, shader.Length));
        for (var index = 1; index < spans.Count; index++)
        {
            Assert.True(spans[index - 1].Start + spans[index - 1].Length <= spans[index].Start);
        }
    }

    [Fact]
    public void HonorsCancellationDuringLargeIncompleteToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ShaderClassifier.Classify("/*" + new string('x', 1_000_000), cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData("Texture2D<float4")]
    [InlineData("float4 main(Texture2DMS<float4,")]
    [InlineData("void main(){for(int i=0;")]
    [InlineData("cbuffer Scene : register(")]
    public void IncompleteDeclarationsRemainSafeDuringTyping(string shader)
    {
        var spans = ShaderClassifier.Classify(shader, hlsl: true);
        Assert.All(spans, span => Assert.InRange(span.Start + span.Length, 0, shader.Length));
    }

    private static IReadOnlyList<ClassifiedToken> Classified(string text, bool hlsl = false) =>
        ShaderClassifier.Classify(text, hlsl)
            .Select(span => new ClassifiedToken(span.Start, text.Substring(span.Start, span.Length), span.Kind))
            .ToArray();

    private static void AssertKinds(
        IReadOnlyList<ClassifiedToken> tokens,
        string text,
        params ShaderTokenKind[] expected) => AssertKinds(tokens, text, expected, includeDelimiters: false);

    private static void AssertKinds(
        IReadOnlyList<ClassifiedToken> tokens,
        string text,
        ShaderTokenKind[] expected,
        bool includeDelimiters)
    {
        var actual = tokens
            .Where(token => includeDelimiters ? token.Text.Trim('"') == text : token.Text == text)
            .Select(token => token.Kind)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private readonly record struct ClassifiedToken(int Start, string Text, ShaderTokenKind Kind);
}
