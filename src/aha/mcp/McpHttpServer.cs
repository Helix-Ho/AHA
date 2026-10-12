using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aha.Mcp;

public sealed class McpHttpServer : IDisposable
{
    private const int MaxBytes = 1024 * 1024;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly McpProtocol protocol;
    public string Url { get; }

    public McpHttpServer(int port, Func<JsonArray> tools, Func<string,JsonObject,Task<JsonNode?>> call)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("MCP 포트는 1024~65535 범위여야 합니다.");
        Url = $"http://127.0.0.1:{port}/mcp/";
        protocol = new(tools,call);
        listener.Prefixes.Add(Url);
    }
    public void Start() { listener.Start(); _ = AcceptLoopAsync(); }
    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(lifetime.Token).ConfigureAwait(false);
                _ = HandleAsync(context);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var endpoint = new Uri(Url);
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Url?.Authority != endpoint.Authority || request.Url.AbsolutePath != endpoint.AbsolutePath)
            { response.StatusCode = 403; return; }
            var origin = request.Headers["Origin"];
            if (origin != null && (!Uri.TryCreate(origin,UriKind.Absolute,out var o) || o.Scheme != endpoint.Scheme || o.Authority != endpoint.Authority))
            { response.StatusCode = 403; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; response.Headers["Allow"] = "POST"; return; }
            var version = request.Headers["MCP-Protocol-Version"];
            if (version != null && !McpProtocol.Versions.Contains(version)) { response.StatusCode = 400; return; }
            if (request.ContentType?.Split(';')[0].Trim() != "application/json") { response.StatusCode = 415; return; }
            if (request.ContentLength64 > MaxBytes) { response.StatusCode = 413; return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var body = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await request.InputStream.ReadAsync(buffer,timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > MaxBytes) { response.StatusCode = 413; return; }
                body.Write(buffer,0,count);
            }
            JsonObject? output;
            try
            {
                var requestBytes = body.ToArray();
                // JsonNode materializes dictionaries lazily. Reject ambiguous names
                // throughout the received document before any tool can be dispatched.
                using var document = JsonDocument.Parse(requestBytes);
                output = HasDuplicateProperties(document.RootElement)
                    ? McpProtocol.Error(null, -32600, "Duplicate JSON properties are not allowed.")
                    : await protocol.DispatchAsync(JsonNode.Parse(requestBytes)).ConfigureAwait(false);
            }
            catch (JsonException) { output = McpProtocol.Error(null,-32700,"Invalid JSON."); }
            catch (InvalidOperationException) { output = McpProtocol.Error(null,-32600,"Invalid JSON-RPC request."); }
            if (output == null) { response.StatusCode = 202; return; }
            var bytes = Encoding.UTF8.GetBytes(output.ToJsonString(McpProtocol.JsonOptions));
            response.ContentType = "application/json"; response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "no-store";
            await response.OutputStream.WriteAsync(bytes,lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { response.StatusCode = 408; }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException) { }
        finally { response.Close(); }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (HasDuplicateProperties(item)) return true;
        return false;
    }

    public void Dispose() { lifetime.Cancel(); listener.Close(); lifetime.Dispose(); }
}
