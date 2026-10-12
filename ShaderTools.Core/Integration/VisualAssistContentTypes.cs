using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderTools.Core.Integration;

// Visual Assist only enables itself for editor content types it knows by name,
// plus a semicolon-separated CustomContentTypes list it reads from the registry.
// It lowercases that list and compares exact names, so entries are matched
// case-insensitively and entries owned by other tools are kept as written.
public static class VisualAssistContentTypes
{
    public const string ValueName = "CustomContentTypes";

    public static string GetKeyPath(int visualStudioMajorVersion)
    {
        return $@"Software\Whole Tomato\Visual Assist X\VaNet{visualStudioMajorVersion}";
    }

    public static string Update(string? current, IEnumerable<string> contentTypes, bool include)
    {
        if (contentTypes == null)
        {
            throw new ArgumentNullException(nameof(contentTypes));
        }

        HashSet<string> owned = new HashSet<string>(contentTypes, StringComparer.OrdinalIgnoreCase);
        List<string> entries = (current ?? "")
            .Split(';')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0 && (include || !owned.Contains(entry)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (include)
        {
            entries.AddRange(contentTypes.Where(type => !entries.Contains(type, StringComparer.OrdinalIgnoreCase)));
        }

        return string.Join(";", entries);
    }
}
