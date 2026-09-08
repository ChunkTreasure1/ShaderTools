using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace ShaderTools.VisualStudio.LanguageClient
{
    // FileExtensionAttribute is single-use. Separate exports let VS order each mapping.
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .glsl"), FileExtension(".glsl"), Order(Before = "default")]
    internal sealed class GlslFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .vert"), FileExtension(".vert"), Order(Before = "default")]
    internal sealed class VertFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .frag"), FileExtension(".frag"), Order(Before = "default")]
    internal sealed class FragFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .geom"), FileExtension(".geom"), Order(Before = "default")]
    internal sealed class GeomFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .comp"), FileExtension(".comp"), Order(Before = "default")]
    internal sealed class CompFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .tesc"), FileExtension(".tesc"), Order(Before = "default")]
    internal sealed class TescFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .tese"), FileExtension(".tese"), Order(Before = "default")]
    internal sealed class TeseFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .gs"), FileExtension(".gs"), Order(Before = "default")]
    internal sealed class GsFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .vsh"), FileExtension(".vsh"), Order(Before = "default")]
    internal sealed class VshFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .fsh"), FileExtension(".fsh"), Order(Before = "default")]
    internal sealed class FshFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .gsh"), FileExtension(".gsh"), Order(Before = "default")]
    internal sealed class GshFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .mesh"), FileExtension(".mesh"), Order(Before = "default")]
    internal sealed class MeshFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .task"), FileExtension(".task"), Order(Before = "default")]
    internal sealed class TaskFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rgen"), FileExtension(".rgen"), Order(Before = "default")]
    internal sealed class RgenFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rint"), FileExtension(".rint"), Order(Before = "default")]
    internal sealed class RintFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rahit"), FileExtension(".rahit"), Order(Before = "default")]
    internal sealed class RahitFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rchit"), FileExtension(".rchit"), Order(Before = "default")]
    internal sealed class RchitFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rmiss"), FileExtension(".rmiss"), Order(Before = "default")]
    internal sealed class RmissFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .rcall"), FileExtension(".rcall"), Order(Before = "default")]
    internal sealed class RcallFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .hlsl"), FileExtension(".hlsl"), Order(Before = "default")]
    internal sealed class HlslFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .hlsli"), FileExtension(".hlsli"), Order(Before = "default")]
    internal sealed class HlsliFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .fx"), FileExtension(".fx"), Order(Before = "default")]
    internal sealed class FxFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .fxh"), FileExtension(".fxh"), Order(Before = "default")]
    internal sealed class FxhFilePathProvider : ShaderFilePathProvider { }
    [Export(typeof(IFilePathToContentTypeProvider)), Name("Shader Tools .wgsl"), FileExtension(".wgsl"), Order(Before = "default")]
    internal sealed class WgslFilePathProvider : ShaderFilePathProvider { }
}
