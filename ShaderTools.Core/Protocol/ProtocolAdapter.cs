using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace ShaderTools.Core.Protocol
{
    public sealed class ProtocolAdapter
    {
        private readonly Func<string, JObject> configuration;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private JObject? activeVariant;
        private string workspaceRoot;

        public ProtocolAdapter(string? workspaceRoot, Func<string, JObject> configuration, Action<string>? log = null)
        {
            this.workspaceRoot = workspaceRoot ?? "";
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.log = log ?? (_ => { });
        }
        public string WorkspaceRoot { get { lock (gate) { return workspaceRoot; } } }

        public JObject AdaptClientMessage(JObject message)
        {
            lock (gate)
            {
                JObject result = (JObject)message.DeepClone();
                switch ((string?)result["method"])
                {
                    case "initialize":
                        JObject parameters = Object(result, "params");
                        if (string.IsNullOrEmpty(workspaceRoot))
                        {
                            string? root = (string?)parameters["rootUri"] ?? (string?)parameters.SelectToken("workspaceFolders[0].uri");
                            if (Uri.TryCreate(root, UriKind.Absolute, out Uri uri) && uri.IsFile)
                            {
                                workspaceRoot = uri.LocalPath;
                            }
                            else
                            {
                                workspaceRoot = (string?)parameters["rootPath"] ?? "";
                            }
                        }
                        if (!string.IsNullOrEmpty(workspaceRoot))
                        {
                            string uri = new Uri(Path.GetFullPath(workspaceRoot)).AbsoluteUri;
                            parameters["rootUri"] = uri;
                            parameters["rootPath"] = workspaceRoot;
                            parameters["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)) });
                        }
                        JObject capabilities = Object(parameters, "capabilities");
                        Object(capabilities, "workspace")["configuration"] = true;
                        Object(capabilities, "window")["workDoneProgress"] = false;
                        Object(Object(Object(capabilities, "textDocument"), "publishDiagnostics"), "tagSupport")["valueSet"] = new JArray(1, 2);
                        break;
                    case "textDocument/didOpen":
                        if (result.SelectToken("params.textDocument") is JObject document)
                        {
                            string language = (string?)document["languageId"] ?? "";
                            if (language.StartsWith("shader-tools-", StringComparison.OrdinalIgnoreCase))
                            {
                                document["languageId"] = language.Substring("shader-tools-".Length).ToLowerInvariant();
                            }

                            if (string.IsNullOrEmpty(workspaceRoot) && Uri.TryCreate((string?)document["uri"], UriKind.Absolute, out Uri file) && file.IsFile)
                            {
                                workspaceRoot = FindWorkspaceRoot(file.LocalPath);
                            }
                        }
                        break;
                    case "textDocument/didChangeShaderVariant":
                        activeVariant = result.SelectToken("params.shaderVariant") as JObject;
                        break;
                }
                return result;
            }
        }

        public JObject? GetServerRequestResponse(JObject message)
        {
            if (message["id"] == null || message["method"] == null)
            {
                return null;
            }

            JToken value;
            switch ((string?)message["method"])
            {
                case "workspace/configuration":
                    JObject settings = configuration(WorkspaceRoot);
                    JArray values = [];
                    foreach (JToken item in message.SelectToken("params.items") as JArray ?? [])
                    {
                        string? section = (string?)item["section"];
                        JToken? selected = section is null or "shader-validator" ? settings : null;
                        if (section != null && section.StartsWith("shader-validator.", StringComparison.Ordinal))
                        {
                            selected = settings;
                            foreach (string part in section.Substring("shader-validator.".Length).Split('.'))
                            {
                                selected = (selected as JObject)?[part];
                            }
                        }
                        values.Add(selected?.DeepClone() ?? JValue.CreateNull());
                    }
                    value = values;
                    log("Applied shader configuration for " + WorkspaceRoot);
                    break;
                case "textDocument/shaderVariant":
                    lock (gate)
                    {
                        value = new JObject { ["shaderVariant"] = activeVariant?.DeepClone() ?? JValue.CreateNull() };
                    }

                    break;
                case "window/workDoneProgress/create":
                    value = JValue.CreateNull();
                    break;
                default: return null;
            }
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = value };
        }

        private static JObject Object(JObject parent, string name)
        {
            if (parent[name] is JObject existing)
            {
                return existing;
            }

            JObject created = [];
            parent[name] = created;
            return created;
        }

        public static string FindWorkspaceRoot(string filePath)
        {
            DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
            for (DirectoryInfo current = directory; current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, ".shadertools.json")))
                {
                    return current.FullName;
                }
            }

            return directory.FullName;
        }
    }
}
