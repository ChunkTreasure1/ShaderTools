using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using Xunit;

namespace ShaderTools.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShaderTools.Tests", Guid.NewGuid().ToString("N"));

    public ConfigurationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void LoadWithoutFilesReturnsCompleteServerDefaultsAndDoesNotWrite()
    {
        var configuration = ShaderConfiguration.Load(_root);

        Assert.Equal(Path.GetFullPath(_root), configuration.WorkspaceRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), ShaderConfiguration.FileName), configuration.ConfigurationPath);
        Assert.False(File.Exists(configuration.ConfigurationPath));
        Assert.True(configuration.Settings.Value<bool>("validate"));
        Assert.True(configuration.Settings.Value<bool>("symbols"));
        Assert.False(configuration.Settings.Value<bool>("symbolDiagnostics"));
        Assert.True(configuration.Settings.Value<bool>("validateConfig"));
        Assert.Equal("info", configuration.Settings.Value<string>("severity"));
        Assert.Equal("ShaderModel6_8", configuration.Settings.SelectToken("hlsl.shaderModel")?.Value<string>());
        Assert.Equal("Vulkan1_3", configuration.Settings.SelectToken("glsl.targetClient")?.Value<string>());
        Assert.Equal("SPIRV1_6", configuration.Settings.SelectToken("glsl.spirvVersion")?.Value<string>());
        Assert.Equal("off", configuration.Settings.SelectToken("trace.server")?.Value<string>());
        Assert.Empty((JArray)configuration.Settings["includes"]!);
        Assert.Empty((JObject)configuration.Settings["defines"]!);
        Assert.Empty((JObject)configuration.Settings["pathRemapping"]!);
    }

    [Fact]
    public void LoadDeepMergesUserThenProjectAndResolvesPathsAtEachSource()
    {
        var userDirectory = Path.Combine(_root, "profile");
        Directory.CreateDirectory(userDirectory);
        var userPath = Path.Combine(userDirectory, "settings.json");
        File.WriteAllText(userPath, """
            {
              "includes": ["user-includes"],
              "defines": { "FROM_USER": "1", "WINNER": "user" },
              "pathRemapping": { "/engine": "user-engine" },
              "hlsl": { "version": "V2018", "enabled": false },
              "serverPath": "ignored.exe"
            }
            """);
        File.WriteAllText(Path.Combine(_root, ShaderConfiguration.FileName), """
            {
              "includes": ["project-includes"],
              "defines": { "FROM_PROJECT": "1", "WINNER": "project" },
              "pathRemapping": { "/game": "project-game" },
              "glsl": { "preamble": "shaders/preamble.glsl" },
              "configOverride": "config/compiler.json",
              "severity": "warning"
            }
            """);

        var settings = ShaderConfiguration.Load(_root, userPath).Settings;

        Assert.Equal(new[] { Path.Combine(_root, "project-includes") }, settings["includes"]!.Values<string>());
        Assert.Equal("1", settings.SelectToken("defines.FROM_USER")?.Value<string>());
        Assert.Equal("1", settings.SelectToken("defines.FROM_PROJECT")?.Value<string>());
        Assert.Equal("project", settings.SelectToken("defines.WINNER")?.Value<string>());
        Assert.Equal(Path.Combine(userDirectory, "user-engine"), settings.SelectToken("pathRemapping./engine")?.Value<string>());
        Assert.Equal(Path.Combine(_root, "project-game"), settings.SelectToken("pathRemapping./game")?.Value<string>());
        Assert.Equal(Path.Combine(_root, "shaders", "preamble.glsl"), settings.SelectToken("glsl.preamble")?.Value<string>());
        Assert.Equal(Path.Combine(_root, "config", "compiler.json"), settings.Value<string>("configOverride"));
        Assert.Equal("V2018", settings.SelectToken("hlsl.version")?.Value<string>());
        Assert.Equal("warning", settings.Value<string>("severity"));
        Assert.Null(settings["serverPath"]);
        Assert.Null(settings.SelectToken("hlsl.enabled"));
    }

    [Fact]
    public void LoadExpandsSupportedPlaceholdersBeforeResolvingPaths()
    {
        const string environmentVariable = "SHADERTOOLS_TEST_INCLUDE";
        var oldValue = Environment.GetEnvironmentVariable(environmentVariable);
        var environmentPath = Path.Combine(_root, "environment-includes");
        Environment.SetEnvironmentVariable(environmentVariable, environmentPath);
        try
        {
            File.WriteAllText(Path.Combine(_root, ShaderConfiguration.FileName), """
                {
                  "includes": ["${workspaceFolder}/shared", "${env:SHADERTOOLS_TEST_INCLUDE}"],
                  "glsl": { "preamble": "${userHome}/shader-preamble.glsl" }
                }
                """);

            var settings = ShaderConfiguration.Load(_root).Settings;

            Assert.Equal(
                new[] { Path.Combine(_root, "shared"), environmentPath },
                settings["includes"]!.Values<string>());
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "shader-preamble.glsl"),
                settings.SelectToken("glsl.preamble")?.Value<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, oldValue);
        }
    }

    [Fact]
    public void LoadRejectsUnresolvedPlaceholderWithFileAndProperty()
    {
        var path = Path.Combine(_root, ShaderConfiguration.FileName);
        File.WriteAllText(path, """{ "includes": ["${env:SHADERTOOLS_MISSING_VALUE}/inc"] }""");
        Environment.SetEnvironmentVariable("SHADERTOOLS_MISSING_VALUE", null);

        var error = Assert.Throws<InvalidDataException>(() => ShaderConfiguration.Load(_root));

        Assert.Contains(path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("includes[0]", error.Message, StringComparison.Ordinal);
        Assert.Contains("SHADERTOOLS_MISSING_VALUE", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"includes\": \"inc\" }", "includes")]
    [InlineData("{ \"defines\": { \"COUNT\": 2 } }", "defines.COUNT")]
    [InlineData("{ \"glsl\": { \"targetClient\": \"DirectX\" } }", "glsl.targetClient")]
    [InlineData("{ \"hlsl\": { \"enable16bitTypes\": \"yes\" } }", "hlsl.enable16bitTypes")]
    [InlineData("{ \"trace\": { \"server\": \"everything\" } }", "trace.server")]
    public void LoadRejectsWrongTypesAndEnumValues(string json, string property)
    {
        var path = Path.Combine(_root, ShaderConfiguration.FileName);
        File.WriteAllText(path, json);

        var error = Assert.Throws<InvalidDataException>(() => ShaderConfiguration.Load(_root));

        Assert.Contains(Path.GetFileName(path), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(property, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsMalformedJsonWithFilename()
    {
        var path = Path.Combine(_root, ShaderConfiguration.FileName);
        File.WriteAllText(path, "{ nope");

        var error = Assert.Throws<InvalidDataException>(() => ShaderConfiguration.Load(_root));

        Assert.Contains(path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<Newtonsoft.Json.JsonReaderException>(error.InnerException);
    }

    [Fact]
    public void CreateDefaultSettingsReturnsAnIndependentObject()
    {
        var first = ShaderConfiguration.CreateDefaultSettings();
        first["validate"] = false;

        var second = ShaderConfiguration.CreateDefaultSettings();

        Assert.True(second.Value<bool>("validate"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
