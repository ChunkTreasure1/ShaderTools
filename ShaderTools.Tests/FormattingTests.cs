using System.Text;
using ShaderTools.Core;
using Xunit;

namespace ShaderTools.Tests;

public sealed class ClangFormatFactAttribute : FactAttribute
{
    public ClangFormatFactAttribute()
    {
        if (FormattingTests.FormatterPath == null)
            Skip = "clang-format is not installed; set SHADERTOOLS_CLANG_FORMAT to test native formatting.";
    }
}

public sealed class FormattingTests : IDisposable
{
    internal static readonly string? FormatterPath = FindFormatter();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Shader Tools formatting " + Guid.NewGuid().ToString("N"));

    public FormattingTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "шейдеры"));
        File.WriteAllText(Path.Combine(directory, ".clang-format"), "BasedOnStyle: LLVM\nIndentWidth: 4\nBreakBeforeBraces: Attach\nAllowShortFunctionsOnASingleLine: None\n", new UTF8Encoding(false));
    }

    [ClangFormatFact]
    public async Task FormatsUnsavedTextUsingTheDocumentsParentConfiguration()
    {
        string path = Path.Combine(directory, "шейдеры", "test shader.frag");
        File.WriteAllText(path, "// This saved content must not be used.\n");
        const string text = "// Цвет 😀\r\nvoid main()\r\n{\r\nfloat value=1.;\r\n}\r\n";
        string formatted = await Format(path, text);
        Assert.Equal("// Цвет 😀\r\nvoid main() {\r\n    float value = 1.;\r\n}\r\n", formatted);
        Assert.Equal("// This saved content must not be used.\n", File.ReadAllText(path));
        Assert.Equal(formatted, await Format(path, formatted));
    }

    [ClangFormatFact]
    public async Task FormatsOnlyTheSelectedFunctionWithUtf8Offsets()
    {
        const string text = "// Цвет 😀\nvoid first() {\n  float a=1.;\n}\n\nvoid second() {\nfloat b=2.;\n}\n";
        int start = text.IndexOf("void second", StringComparison.Ordinal);
        string formatted = await Format(Path.Combine(directory, "range.frag"), text, start, text.Length - start);
        Assert.StartsWith(text[..start], formatted);
        Assert.EndsWith("void second() {\n    float b = 2.;\n}\n", formatted);
    }

    [ClangFormatFact]
    public async Task FormatsHlslIncludesAndSemantics()
    {
        const string text = "float4 tint(float3 color : COLOR0) : SV_Target0 {return float4(color,1.0);}\n";
        string formatted = await Format(Path.Combine(directory, "test.ps.hlsl"), text);
        Assert.Contains("    return float4(color, 1.0);", formatted);
        Assert.Contains("SV_Target0", formatted);
    }

    [ClangFormatFact]
    public async Task ReportsInvalidConfigurationWithoutReturningReplacementText()
    {
        File.WriteAllText(Path.Combine(directory, ".clang-format"), "NotAClangOption: true\n");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Format(Path.Combine(directory, "broken.frag"), "void main(){}"));
        Assert.Contains("NotAClangOption", error.Message);
    }

    [ClangFormatFact]
    public async Task CancellationDoesNotFormatOrSaveTheDocument()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClangFormatter.FormatAsync(FormatterPath!, Path.Combine(directory, "cancel.frag"), "void main(){}", cancellation.Token));
        Assert.False(File.Exists(Path.Combine(directory, "cancel.frag")));
    }

    [Fact]
    public async Task RejectsInvalidSelectionsBeforeStartingAProcess()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClangFormatter.FormatAsync("unused", Path.Combine(directory, "a.frag"), "abc", CancellationToken.None, 2, 2));
    }

    private static Task<string> Format(string path, string text, int? start = null, int? length = null) =>
        ClangFormatter.FormatAsync(FormatterPath!, path, text, CancellationToken.None, start, length);

    private static string? FindFormatter()
    {
        IEnumerable<string?> candidates = new[]
        {
            Environment.GetEnvironmentVariable("SHADERTOOLS_CLANG_FORMAT"),
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\clang-format.exe",
            @"C:\Program Files\LLVM\bin\clang-format.exe"
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path.Trim('"'), "clang-format.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
