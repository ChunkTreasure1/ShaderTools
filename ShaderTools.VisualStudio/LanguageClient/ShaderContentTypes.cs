using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;
using ShaderTools.VisualStudio.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;

namespace ShaderTools.VisualStudio.LanguageClient
{
    public static class ShaderContentTypes
    {
        public const string Base = "shader-tools";
        public const string Glsl = "shader-tools-glsl";
        public const string Hlsl = "shader-tools-hlsl";
        public const string Wgsl = "shader-tools-wgsl";
        [Export, Name(Base), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        public static ContentTypeDefinition ShaderDefinition;
        [Export, Name(Glsl), BaseDefinition(Base)] public static ContentTypeDefinition GlslDefinition;
        [Export, Name(Hlsl), BaseDefinition(Base)] public static ContentTypeDefinition HlslDefinition;
        [Export, Name(Wgsl), BaseDefinition(Base)] public static ContentTypeDefinition WgslDefinition;
        private static readonly HashSet<string> GlslExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".glsl", ".vert", ".frag", ".geom", ".comp", ".tesc", ".tese", ".gs", ".vsh", ".fsh", ".gsh", ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall" };
        public static string ForPath(string path)
        {
            string extension = Path.GetExtension(path ?? "");
            if (GlslExtensions.Contains(extension))
            {
                return Glsl;
            }

            if (ShaderOptions.Current.EnableHlsl && (extension.Equals(".hlsl", StringComparison.OrdinalIgnoreCase) || extension.Equals(".hlsli", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fxh", StringComparison.OrdinalIgnoreCase)))
            {
                return Hlsl;
            }

            if (ShaderOptions.Current.EnableWgsl && extension.Equals(".wgsl", StringComparison.OrdinalIgnoreCase))
            {
                return Wgsl;
            }

            return null;
        }
    }

    // File-path providers take precedence over legacy extension mappings, including native HLSL.
    internal abstract class ShaderFilePathProvider : IFilePathToContentTypeProvider
    {
        [Import] internal IContentTypeRegistryService Registry { get; set; }
        public bool TryGetContentTypeForFilePath(string filePath, out IContentType contentType)
        {
            string name = ShaderContentTypes.ForPath(filePath);
            contentType = name == null ? null : Registry.GetContentType(name);
            return contentType != null;
        }
    }
}
