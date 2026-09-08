using ShaderTools.Core.Editor;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ShaderTools.Tests;

public sealed class InlayHintCacheTests
{
    private const string Header = "float f(float a, int b, float s) { return a; }\nvoid main() {\n";
    private static string Text(string arguments) => Header + "f(" + arguments + "\n}";

    [Fact]
    public void FirstArgumentGetsAHintBeforeTheClosingParenthesisExists()
    {
        var cache = new InlayHintCache();
        cache.Clear(1, Text(""));
        Edit(cache, Text(""), Text("3"));
        Assert.Equal(new[] { "a:" }, cache.Hints.Select(h => h.Label));
        Assert.Equal(2, cache.Hints[0].Character);
    }

    [Fact]
    public void AddingAndDeletingSecondArgumentKeepsTheFirstHintIdentity()
    {
        var cache = Ready(Text("3.3"));
        var first = Assert.Single(cache.Hints).Identity;
        Edit(cache, Text("3.3"), Text("3.3, 2"));
        Assert.Equal(new[] { "a:", "b:" }, cache.Hints.Select(h => h.Label));
        Assert.Same(first, cache.Hints[0].Identity);
        Edit(cache, Text("3.3, 2"), Text("3.3, "));
        Assert.Same(first, Assert.Single(cache.Hints).Identity);
        Edit(cache, Text("3.3, "), Text(""));
        Assert.Empty(cache.Hints);
    }

    [Fact]
    public void ExtendingOrReplacingAnArgumentDoesNotHideItsLabel()
    {
        var cache = Ready(Text("3"));
        var identity = cache.Hints[0].Identity;
        Edit(cache, Text("3"), Text("3.3"));
        Assert.Same(identity, Assert.Single(cache.Hints).Identity);
        Edit(cache, Text("3.3"), Text("-value + other"));
        Assert.Same(identity, Assert.Single(cache.Hints).Identity);
    }

    [Fact]
    public void EmptyServerResponseCannotEraseHintsForAnUnfinishedCall()
    {
        var cache = Ready(Text("3.3, 2"));
        var first = cache.Hints[0].Identity;
        Assert.Empty(cache.Replace(1, Array.Empty<ParameterHint>(), Text("3.3, 2")));
        Assert.Equal(new[] { "a:", "b:" }, cache.Hints.Select(h => h.Label));
        Assert.Same(first, cache.Hints[0].Identity);
    }

    [Fact]
    public void AutoInsertedClosingParenthesisDoesNotRequireAllArguments()
        => Assert.Equal(new[] { "a:" }, Ready(Text("3)")).Hints.Select(h => h.Label));

    [Fact]
    public void DeletingAnEarlierArgumentRebindsFollowingArgumentToItsNewIndex()
    {
        var cache = Ready(Text("3.3, 2, 5)"));
        Edit(cache, Text("3.3, 2, 5)"), Text("2, 5)"));
        Assert.Equal(new[] { "a:", "b:" }, cache.Hints.Select(h => h.Label));
        Assert.Equal(new[] { 2, 5 }, cache.Hints.Select(h => h.Character));
    }

    [Fact]
    public void EmptyMiddleArgumentDoesNotRenumberLaterArguments()
        => Assert.Equal(new[] { "a:", "s:" }, Ready(Text("3.3, /* empty */ , 5")).Hints.Select(h => h.Label));

    [Fact]
    public void NestedCallsAndArrayCommasDoNotConsumeOuterParameterSlots()
    {
        var text = "float g(float x, float y) { return x; }\n" + Text("g(1, 2), items[index(3, 4)], 5");
        Assert.Equal(new[] { "a:", "x:", "y:", "b:", "s:" }, Ready(text).Hints.Select(h => h.Label));
    }

    [Fact]
    public void CommentsStringsAndPreprocessorLinesDoNotCreateFakeCalls()
    {
        var text = Header + "// f(1,2,3)\n/* f(1,2,3) */\n#define MACRO f(1,2,3)\nf(\"a,b)\", 2, 5)\n}";
        Assert.Equal(new[] { "a:", "b:", "s:" }, Ready(text).Hints.Select(h => h.Label));
    }

    [Fact]
    public void NewLinesMoveHintsWithoutReplacingTheirAdornmentIdentity()
    {
        var original = Text("3.3, 2");
        var cache = Ready(original);
        var identities = cache.Hints.Select(h => h.Identity).ToArray();
        Edit(cache, original, "\n\n" + original);
        Assert.All(cache.Hints, h => Assert.Equal(4, h.Line));
        Assert.Equal(identities, cache.Hints.Select(h => h.Identity));
    }

    [Fact]
    public void SignatureRenameUpdatesOtherCallsWithoutHidingTheirArguments()
    {
        var original = Text("3.3, 2");
        var cache = Ready(original);
        Edit(cache, original, original.Replace("float a", "float renamed"));
        Assert.Equal(new[] { "renamed:", "b:" }, cache.Hints.Select(h => h.Label));
    }

    [Fact]
    public void RenamingTheCalledFunctionDoesNotReuseItsOldParameterLabels()
    {
        var original = Text("3.3, 2");
        var cache = Ready(original);
        Edit(cache, original, original.Replace("\nf(", "\nunknown("));
        Assert.Empty(cache.Hints);
    }

    [Fact]
    public void AmbiguousOverloadsOnlyShowParameterNamesSharedByAllCandidates()
    {
        var text = "float f(float common, int left);\nfloat f(float common, float right);\nf(3, 2";
        Assert.Equal(new[] { "common:" }, Ready(text).Hints.Select(h => h.Label));
    }

    [Fact]
    public void ServerLabelsOverrideAnAmbiguousLocalOverload()
    {
        var text = "float f(float a);\nfloat f(int b);\nf(3)";
        var cache = Ready(text);
        cache.Replace(1, new[] { new ParameterHint(2, 2, "b:", false, true) }, text);
        Assert.Equal("b:", Assert.Single(cache.Hints).Label);
    }

    [Fact]
    public void KnownExternalSignatureSupportsTheFirstIncompleteCall()
    {
        var cache = new InlayHintCache();
        cache.SetExternalSignatures(new[] { "float mix(float x, float y, float a);" });
        cache.Replace(1, Array.Empty<ParameterHint>(), "mix(1, 2");
        Assert.Equal(new[] { "x:", "y:" }, cache.Hints.Select(h => h.Label));
    }

    [Fact]
    public void LocallyChangedSignatureOverridesAnOlderExternalCatalogEntry()
    {
        var cache = new InlayHintCache();
        cache.SetExternalSignatures(new[] { "float f(float oldName);" });
        cache.Replace(1, Array.Empty<ParameterHint>(), "float f(float newName);\nf(1");
        Assert.Equal("newName:", Assert.Single(cache.Hints).Label);
    }

    [Fact]
    public void UnknownFunctionRetainsServerHintAcrossEditsToOtherArguments()
    {
        var original = "unknown(3.3, 2)";
        var cache = new InlayHintCache();
        cache.Replace(1, new[] { new ParameterHint(0, 8, "first:", false, true), new ParameterHint(0, 13, "second:", false, true) }, original);
        var first = cache.Hints[0].Identity;
        Edit(cache, original, "unknown(3.3, )");
        Assert.Same(first, Assert.Single(cache.Hints).Identity);
    }

    [Fact]
    public void MultiCaretEditsMapAllCallPositionsFromTheSameBeforeSnapshot()
    {
        var text = Header + "f(3, 2); f(4, 5);\n}";
        var cache = Ready(text);
        var identities = cache.Hints.Select(h => h.Identity).ToArray();
        cache.ApplyEdits(1, 2, new[] { new InlayHintEdit(Header.Length + 10, 1, "44"), new InlayHintEdit(Header.Length + 2, 1, "33") }, Header + "f(33, 2); f(44, 5);\n}");
        Assert.Equal(identities, cache.Hints.Select(h => h.Identity));
    }

    [Fact]
    public void IdenticalResponseDoesNotInvalidateOrRecreateHints()
    {
        var text = Text("3.3, 2");
        var cache = Ready(text);
        var first = cache.Hints[0];
        Assert.Empty(cache.Replace(1, Array.Empty<ParameterHint>(), text));
        Assert.Same(first, cache.Hints[0]);
    }

    [Fact]
    public void ClearDropsHintsAndExternalSignaturesForThePreviousDocument()
    {
        var cache = new InlayHintCache();
        cache.SetExternalSignatures(new[] { "float f(float previous);" });
        cache.Clear(2, "f(1");
        cache.Replace(2, Array.Empty<ParameterHint>(), "f(1");
        Assert.Empty(cache.Hints);
    }

    [Fact]
    public void CompletionMarkdownProvidesAllOverloadsWithoutParsingDescriptionProse()
    {
        var response = new JObject { ["items"] = new JArray(new JObject
        {
            ["documentation"] = new JObject { ["kind"] = "markdown", ["value"] =
                "```Glsl\nfloat mix(float x, float y, float a);\nvec3 mix(vec3 x, vec3 y, float a);\n```\nDescription with misleading(fake args)" }
        }) };
        var cache = new InlayHintCache();
        cache.SetExternalSignatures(InlayHintCache.ReadCompletionSignatures(response));
        cache.Replace(1, Array.Empty<ParameterHint>(), "mix(1, 2");
        Assert.Equal(new[] { "x:", "y:" }, cache.Hints.Select(h => h.Label));
    }

    [Fact]
    public void UnknownFunctionKeepsItsOtherHintAfterAnEmptyReplyWithAnEmptyArgumentSlot()
    {
        var cache = new InlayHintCache();
        cache.Replace(1, new[] { new ParameterHint(0, 8, "first:", false, true) }, "unknown(3, 2)");
        Edit(cache, "unknown(3, 2)", "unknown(3, )");
        var first = Assert.Single(cache.Hints).Identity;
        cache.Replace(2, Array.Empty<ParameterHint>(), "unknown(3, )");
        Assert.Same(first, Assert.Single(cache.Hints).Identity);
    }

    [Fact]
    public void RemovingAParameterInvalidatesObsoleteLabelsAtDistantCalls()
    {
        var text = "float g(float first, float second);\ng(1, 2);";
        var cache = Ready(text);
        Edit(cache, text, text.Replace(", float second", ""));
        Assert.DoesNotContain(cache.Hints, h => h.Label == "second:");
    }

    [Fact]
    public void DuplicateServerPositionsDoNotBreakTheCache()
    {
        var hint = new ParameterHint(0, 2, "a:", false, true);
        var cache = new InlayHintCache();
        cache.Replace(1, new[] { hint, hint }, "f(1)");
        Assert.Single(cache.Hints);
    }

    [Fact]
    public void RenamingAMultilineCallInvalidatesHintsBelowTheEditedLine()
    {
        var text = Header + "f(\n3,\n2\n)\n}";
        var cache = Ready(text);
        var changed = cache.ApplyEdits(1, 2, new[] { new InlayHintEdit(Header.Length, 1, "unknown") },
            text.Replace("\nf(", "\nunknown("));
        Assert.Empty(cache.Hints);
        Assert.Contains(3, changed);
        Assert.Contains(4, changed);
    }

    [Theory]
    [InlineData("float4 shade(float3 color : COLOR0, float strength) : SV_Target { return 0; }\nshade(float3(1,2,3), 2", "color:", "strength:")]
    [InlineData("fn shade(color: vec3<f32>, strength: f32) -> f32 { return strength; }\nshade(vec3<f32>(1,2,3), 2", "color:", "strength:")]
    [InlineData("struct Material { vec3 color; float roughness; };\nMaterial(vec3(1,2,3), 2", "color:", "roughness:")]
    public void ShaderDeclarationsSupportSemanticsGenericsAndConstructors(string text, string first, string second)
        => Assert.Equal(new[] { first, second }, Ready(text).Hints.Select(h => h.Label));

    private static InlayHintCache Ready(string text)
    {
        var cache = new InlayHintCache();
        cache.Replace(1, Array.Empty<ParameterHint>(), text);
        return cache;
    }

    private static void Edit(InlayHintCache cache, string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var end = before.Length;
        var newEnd = after.Length;
        while (end > start && newEnd > start && before[end - 1] == after[newEnd - 1]) { end--; newEnd--; }
        cache.ApplyEdits(cache.Version, cache.Version + 1, new[] { new InlayHintEdit(start, end - start, after.Substring(start, newEnd - start)) }, after);
    }
}
