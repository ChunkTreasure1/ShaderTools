using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public class AdapterTests
{
    [Fact]
    public void SuppliesWorkspaceFoldersWhenVsOnlyHasASolutionDirectory()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        var adapter = new ProtocolAdapter(root, _ => new JObject());
        var message = JObject.Parse("{method:'initialize',params:{capabilities:{textDocument:{completion:{}}}}}");
        var result = adapter.AdaptClientMessage(message);
        Assert.Equal(new Uri(root).AbsoluteUri, (string?)result.SelectToken("params.rootUri"));
        Assert.Equal(new Uri(root).AbsoluteUri, (string?)result.SelectToken("params.workspaceFolders[0].uri"));
        Assert.True((bool)result.SelectToken("params.capabilities.workspace.configuration")!);
        Assert.NotNull(result.SelectToken("params.capabilities.textDocument.completion"));
    }

    [Theory]
    [InlineData("file:///C:/shaders/basic.frag", "shader-tools-glsl", "glsl")]
    [InlineData("file:///C:/shaders/basic.hlsl", "shader-tools-hlsl", "hlsl")]
    [InlineData("file:///C:/shaders/compute.wgsl", "shader-tools-wgsl", "wgsl")]
    public void NormalizesVisualStudioContentTypeToServerLanguage(string uri, string contentType, string language)
    {
        var adapter = new ProtocolAdapter(null, _ => new JObject());
        var message = new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["languageId"] = contentType, ["text"] = "void main() {}" } } };
        Assert.Equal(language, (string?)adapter.AdaptClientMessage(message).SelectToken("params.textDocument.languageId"));
    }

    [Fact]
    public void ConfigurationResponsesPreserveRequestIdsAndItemOrder()
    {
        var adapter = new ProtocolAdapter(Path.GetTempPath(), _ => JObject.Parse("{includes:['C:/project/include'],glsl:{targetClient:'Vulkan1_3'}}"));
        var response = adapter.GetServerRequestResponse(JObject.Parse("{id:'server-1',method:'workspace/configuration',params:{items:[{section:'shader-validator'},{section:'shader-validator.glsl'},{section:'unrelated'}]}}"));
        Assert.Equal("server-1", (string?)response!["id"]);
        Assert.Equal("C:/project/include", (string?)response.SelectToken("result[0].includes[0]"));
        Assert.Equal("Vulkan1_3", (string?)response.SelectToken("result[1].targetClient"));
        Assert.Equal(JTokenType.Null, response["result"]![2]!.Type);
    }

    [Fact]
    public void ActiveVariantSurvivesServerRequestsAndCanBeCleared()
    {
        var adapter = new ProtocolAdapter(null, _ => new JObject());
        var selected = JObject.Parse("{method:'textDocument/didChangeShaderVariant',params:{shaderVariant:{url:'file:///C:/shader.frag',entryPoint:'main'}}}");
        adapter.AdaptClientMessage(selected);
        var request = JObject.Parse("{id:42,method:'textDocument/shaderVariant',params:{uri:'file:///C:/shader.frag'}}");
        Assert.Equal("main", (string?)adapter.GetServerRequestResponse(request)!.SelectToken("result.shaderVariant.entryPoint"));
        adapter.AdaptClientMessage(JObject.Parse("{method:'textDocument/didChangeShaderVariant',params:{shaderVariant:null}}"));
        Assert.Equal(JTokenType.Null, adapter.GetServerRequestResponse(request)!["result"]!["shaderVariant"]!.Type);
    }

    [Fact]
    public void UnrecognizedServerRequestsAreForwardedAndNotificationsDoNotGetResponses()
    {
        var adapter = new ProtocolAdapter(null, _ => new JObject());
        Assert.Null(adapter.GetServerRequestResponse(JObject.Parse("{id:1,method:'window/showMessageRequest'}")));
        Assert.Null(adapter.GetServerRequestResponse(JObject.Parse("{method:'window/workDoneProgress/create'}")));
    }
}
