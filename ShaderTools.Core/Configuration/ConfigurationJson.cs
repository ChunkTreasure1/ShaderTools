using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ShaderTools.Core.Configuration;

internal static class ConfigurationJson
{
    private static readonly Regex PlaceholderPattern = new Regex(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static JToken ReadJson(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            using StreamReader textReader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            using JsonTextReader jsonReader = new JsonTextReader(textReader);
            JToken result = JToken.Load(
                jsonReader,
                new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Load,
                });

            if (jsonReader.Read())
            {
                throw new JsonReaderException("Additional JSON content follows the root value.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid JSON in '{filePath}': {exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"Could not read configuration file '{filePath}': {exception.Message}", exception);
        }
    }

    public static string RequireString(JObject value, string propertyName, string filePath, string path, bool allowEmpty = false)
    {
        JToken? token = value[propertyName];
        string propertyPath = JoinPath(path, propertyName);
        if (token == null || token.Type != JTokenType.String)
        {
            throw Invalid(filePath, propertyPath, "must be a string");
        }

        string result = token.Value<string>()!;
        if (!allowEmpty && string.IsNullOrWhiteSpace(result))
        {
            throw Invalid(filePath, propertyPath, "must not be empty");
        }

        return result;
    }

    public static JObject OptionalStringMap(JObject value, string propertyName, string filePath, string path)
    {
        JToken? token = value[propertyName];
        string propertyPath = JoinPath(path, propertyName);
        if (token == null)
        {
            return [];
        }

        if (token is not JObject result)
        {
            throw Invalid(filePath, propertyPath, "must be an object whose values are strings");
        }

        ValidateStringMap(result, filePath, propertyPath);
        return result;
    }

    public static JArray OptionalStringArray(JObject value, string propertyName, string filePath, string path)
    {
        JToken? token = value[propertyName];
        string propertyPath = JoinPath(path, propertyName);
        if (token == null)
        {
            return [];
        }

        if (token is not JArray result)
        {
            throw Invalid(filePath, propertyPath, "must be an array of strings");
        }

        for (int index = 0; index < result.Count; index++)
        {
            if (result[index].Type != JTokenType.String)
            {
                throw Invalid(filePath, $"{propertyPath}[{index}]", "must be a string");
            }
        }

        return result;
    }

    public static void ValidateStringMap(JObject map, string filePath, string path)
    {
        foreach (JProperty property in map.Properties())
        {
            if (property.Value.Type != JTokenType.String)
            {
                throw Invalid(filePath, JoinPath(path, property.Name), "must be a string");
            }
        }
    }

    public static string ResolveText(string value, string workspaceRoot, string filePath, string propertyPath)
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string result = PlaceholderPattern.Replace(value, match =>
        {
            string placeholder = match.Groups[1].Value;
            if (string.Equals(placeholder, "workspaceFolder", StringComparison.Ordinal))
            {
                return workspaceRoot;
            }

            if (string.Equals(placeholder, "userHome", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(userHome))
                {
                    throw Invalid(filePath, propertyPath, "cannot resolve ${userHome}");
                }

                return userHome;
            }

            const string environmentPrefix = "env:";
            if (placeholder.StartsWith(environmentPrefix, StringComparison.Ordinal))
            {
                string variableName = placeholder.Substring(environmentPrefix.Length);
                if (variableName.Length == 0)
                {
                    throw Invalid(filePath, propertyPath, "contains an empty environment variable placeholder");
                }

                string environmentValue = Environment.GetEnvironmentVariable(variableName);
                if (environmentValue == null)
                {
                    throw Invalid(filePath, propertyPath, $"cannot resolve environment variable '{variableName}'");
                }

                return environmentValue;
            }

            throw Invalid(filePath, propertyPath, $"contains unsupported placeholder '${{{placeholder}}}'");
        });

        if (result.IndexOf("${", StringComparison.Ordinal) >= 0)
        {
            throw Invalid(filePath, propertyPath, "contains an unresolved placeholder");
        }

        return result;
    }

    public static string ResolvePath(string value, string baseDirectory, string workspaceRoot, string filePath, string propertyPath)
    {
        string expanded = ResolveText(value, workspaceRoot, filePath, propertyPath);
        if (expanded.Length == 0)
        {
            return expanded;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Invalid configuration in '{filePath}' at '{propertyPath}': '{value}' is not a valid path.",
                exception);
        }
    }

    public static InvalidDataException Invalid(string filePath, string propertyPath, string detail)
    {
        return new InvalidDataException($"Invalid configuration in '{filePath}' at '{propertyPath}': {detail}.");
    }

    public static string JoinPath(string parent, string child)
    {
        return string.IsNullOrEmpty(parent) ? child : parent + "." + child;
    }

    public static void RejectUnknownProperties(JObject value, ISet<string> allowed, string filePath, string path)
    {
        foreach (JProperty property in value.Properties())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid(filePath, JoinPath(path, property.Name), "is not a supported property");
            }
        }
    }
}
