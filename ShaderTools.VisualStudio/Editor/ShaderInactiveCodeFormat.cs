using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace ShaderTools.VisualStudio.Editor
{
    internal static class ShaderInactiveCodeFormat
    {
        internal const string ClassificationName = "ShaderTools.InactiveCode";

#pragma warning disable 649
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(ClassificationName)]
        internal static ClassificationTypeDefinition ClassificationType;
#pragma warning restore 649
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = ShaderInactiveCodeFormat.ClassificationName)]
    [Name(ShaderInactiveCodeFormat.ClassificationName)]
    [UserVisible(true)]
    internal sealed class ShaderInactiveCodeFormatDefinition : ClassificationFormatDefinition
    {
        public ShaderInactiveCodeFormatDefinition()
        {
            DisplayName = "Shader Tools Inactive Code";
            ForegroundOpacity = 0.55;
        }
    }
}
