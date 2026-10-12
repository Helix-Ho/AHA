using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Aha.Mcp;

/// <summary>Stateless request/response subset of MCP Streamable HTTP; GET/SSE is optional.</summary>
public sealed class McpProtocol(Func<JsonArray> tools, Func<string, JsonObject, Task<JsonNode?>> call)
{
    public static string ApplicationVersion => typeof(McpProtocol).Assembly.GetName().Version!.ToString(3);
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26"];
    // MCP sends JSON text, not HTML. Preserve Korean characters in text content and on the wire.
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public async Task<JsonObject?> DispatchAsync(JsonNode? message)
    {
        if (message is not JsonObject request || request["jsonrpc"] is not JsonValue rpc ||
            !rpc.TryGetValue<string>(out var rpcVersion) || rpcVersion != "2.0" ||
            request["method"] is not JsonValue methodNode || !methodNode.TryGetValue<string>(out var method))
            return Error(null, -32600, "Invalid JSON-RPC request.");
        var id = request["id"]?.DeepClone();
        if (id is not null && id.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
            return Error(null, -32600, "Invalid JSON-RPC id.");
        if (request.ContainsKey("params") && request["params"] is not JsonObject)
            return Error(id, -32602, "Parameters must be an object.");
        if (id == null) return null;
        var p = request["params"] as JsonObject ?? new JsonObject();
        JsonNode? result;
        switch (method)
        {
            case "initialize":
                if (p.ContainsKey("protocolVersion") &&
                    (p["protocolVersion"] is not JsonValue v || !v.TryGetValue<string>(out _)))
                    return Error(id, -32602, "Protocol version must be a string.");
                var requested = p["protocolVersion"]?.GetValue<string>();
                result = new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(requested) ? requested : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "aha", ["version"] = ApplicationVersion },
                    ["instructions"] = "AHA tools operate independently in the open local Rhino. Read aha_context and use the intended document_id explicitly. aha_topo_xy_prepare_start prepares planar site/context curves; aha_topo_prepare_start prepares terrain and buildings with editable Near regions enabled by default. Both return a job that reaches ready without adding Rhino objects; aha_topo_create explicitly adds that prepared result. aha_eum_read returns planning evidence without choosing values; aha_eum_preview returns heuristic candidates for selected-field application. Both provide the Eum source link; unknown controls are not unrestricted. Info is object-local. Poll aha_job_status; ready is prepared, completed is actually created. EumCrawl jobs complete after their lookup."
                }; break;
            case "ping": result = new JsonObject(); break;
            case "tools/list": result = new JsonObject { ["tools"] = tools() }; break;
            case "resources/list": result = new JsonObject { ["resources"] = new JsonArray() }; break;
            case "resources/templates/list": result = new JsonObject { ["resourceTemplates"] = new JsonArray() }; break;
            case "prompts/list": result = new JsonObject { ["prompts"] = new JsonArray() }; break;
            case "tools/call":
                if (p["name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) return Error(id, -32602, "Tool name is required.");
                if (p.ContainsKey("arguments") && p["arguments"] is not JsonObject)
                    return Error(id, -32602, "Tool arguments must be an object.");
                var args = p["arguments"] as JsonObject ?? new JsonObject();
                try
                {
                    var data = await call(name, args).ConfigureAwait(false);
                    result = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = data?.ToJsonString(JsonOptions) ?? "null" }),
                        ["isError"] = false
                    };
                    if (data is JsonObject) result["structuredContent"] = data;
                }
                catch (Exception error)
                {
                    var text = error is ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException
                        ? error.Message : "AHA tool failed: " + error.GetType().Name;
                    result = new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
                }
                break;
            default: return Error(id, -32601, "Unknown method.");
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }
    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
