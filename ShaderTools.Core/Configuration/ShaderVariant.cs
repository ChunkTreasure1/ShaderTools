using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ShaderTools.Core.Configuration;

public sealed class ShaderVariant
{
    private readonly string _shadingLanguage;

    internal ShaderVariant(
        string name,
        string filePath,
        string entryPoint,
        string? stage,
        IReadOnlyDictionary<string, string> defines,
        IReadOnlyList<string> includes,
        string shadingLanguage)
    {
        Name = name;
        FilePath = filePath;
        EntryPoint = entryPoint;
        Stage = stage;
        Defines = defines;
        Includes = includes;
        _shadingLanguage = shadingLanguage;
    }

    public string Name { get; }

    public string FilePath { get; }

    public string EntryPoint { get; }

    public string? Stage { get; }

    public IReadOnlyDictionary<string, string> Defines { get; }

    public IReadOnlyList<string> Includes { get; }

    public JObject ToProtocol()
    {
        var defines = new JObject();
        foreach (var pair in Defines)
        {
            defines[pair.Key] = pair.Value;
        }

        return new JObject
        {
            ["url"] = new System.Uri(FilePath).AbsoluteUri,
            ["shadingLanguage"] = _shadingLanguage,
            ["entryPoint"] = EntryPoint,
            ["stage"] = Stage == null ? JValue.CreateNull() : new JValue(Stage),
            ["defines"] = defines,
            ["includes"] = new JArray(Includes),
        };
    }
}
