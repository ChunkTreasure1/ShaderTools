using ShaderTools.Core.Integration;
using Xunit;

namespace ShaderTools.Tests;

public sealed class VisualAssistTests
{
    private static readonly string[] Owned = ["shader-tools-hlsl", "shader-tools-glsl"];

    [Theory]
    [InlineData(null, "shader-tools-hlsl;shader-tools-glsl")]
    [InlineData("", "shader-tools-hlsl;shader-tools-glsl")]
    [InlineData("cmake;", "cmake;shader-tools-hlsl;shader-tools-glsl")]
    [InlineData("Shader-Tools-HLSL; cmake", "Shader-Tools-HLSL;cmake;shader-tools-glsl")]
    [InlineData("shader-tools-hlsl;shader-tools-glsl", "shader-tools-hlsl;shader-tools-glsl")]
    public void IncludingAddsMissingContentTypesAndKeepsOthers(string? current, string expected)
    {
        Assert.Equal(expected, VisualAssistContentTypes.Update(current, Owned, include: true));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("shader-tools-hlsl;shader-tools-glsl", "")]
    [InlineData("cmake;SHADER-TOOLS-GLSL;;qml;shader-tools-hlsl", "cmake;qml")]
    public void ExcludingRemovesOnlyShaderToolsContentTypes(string? current, string expected)
    {
        Assert.Equal(expected, VisualAssistContentTypes.Update(current, Owned, include: false));
    }

    [Theory]
    [InlineData(17, @"Software\Whole Tomato\Visual Assist X\VaNet17")]
    [InlineData(18, @"Software\Whole Tomato\Visual Assist X\VaNet18")]
    public void KeyPathFollowsVisualStudioMajorVersion(int major, string expected)
    {
        Assert.Equal(expected, VisualAssistContentTypes.GetKeyPath(major));
    }
}
