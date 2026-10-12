using System.Text.Json.Nodes;

// Only host boundaries are substituted. The server/controller/protocol are production source.
namespace Aha.Setting
{
    public static class AhaSettings
    {
        public static bool McpEnabled { get; set; }
        public static int McpPort { get; set; }
    }
}
namespace Rhino
{
    public static class RhinoApp
    {
        public static void InvokeOnUiThread(Action action) => action();
        public static void WriteLine(string text) { }
    }
}
namespace Aha.Runtime
{
    public static class Jobs
    {
        public static int CancelCount { get; private set; }
        public static void CancelTransient() => CancelCount++;
    }
}
namespace Aha.Mcp
{
    public static class McpTools
    {
        public static JsonArray List() => new();
        public static Task<JsonNode?> CallAsync(string name, JsonObject arguments) => Task.FromResult<JsonNode?>(new JsonObject());
    }
}
