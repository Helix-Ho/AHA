using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Aha.Mcp;
using Aha.Setting;
using Rhino;

namespace Aha.Runtime;

public static class McpControl
{
    private static McpHttpServer? server;
    private const string StoppedStatus = "꺼짐";
    public static string Status { get; private set; } = StoppedStatus;
    public static bool IsRunning => server != null;
    public static string Url => server?.Url ?? ConfiguredUrl(AhaSettings.McpPort);
    public static string LastCall { get; private set; } = "외부 도구 호출 기록 없음";
    public static event Action? Changed;
    public static void ApplySettings()
    {
        bool enabled = AhaSettings.McpEnabled;
        int port = AhaSettings.McpPort;
        if (server != null && enabled && server.Url == ConfiguredUrl(port)) return;
        Stop();
        if (!enabled) return;
        try
        {
            server = new(port,McpTools.List,async (name,args) =>
            {
                try
                {
                    var result = await McpTools.CallAsync(name,args).ConfigureAwait(false);
                    Record(name,args,"응답 완료"); return result;
                }
                catch { Record(name,args,"실패"); throw; }
            });
            server.Start(); Status = "실행 중";
        }
        catch (Exception error)
        {
            server?.Dispose(); server = null;
            Status = "시작 실패 · 이 Rhino의 서버가 실행 중이지 않습니다. 표시 주소는 설정값입니다. 포트 사용 여부를 확인하세요 (" + error.GetType().Name + ")";
        }
        Notify();
    }
    public static void Stop()
    {
        server?.Dispose(); server = null; Jobs.CancelTransient(); Status = StoppedStatus; Notify();
    }
    private static string ConfiguredUrl(int port) => $"http://127.0.0.1:{port}/mcp/";
    private static void Record(string name,JsonObject args,string outcome)
    {
        var document = args["document_id"]?.ToJsonString();
        var objectId = args["object_id"]?.ToString();
        LastCall = $"{DateTime.Now:HH:mm:ss} · {name} · {outcome}" + (document == null ? "" : $" · 문서 {document}") + (objectId == null ? "" : $" · 객체 {objectId}");
        RhinoApp.InvokeOnUiThread((Action)(() => RhinoApp.WriteLine("AHA MCP: " + LastCall)));
        Notify();
    }
    private static void Notify() => RhinoApp.InvokeOnUiThread((Action)(() => Changed?.Invoke()));

    public static async Task<string> ProbeAsync()
    {
        var activeServer = server;
        if (activeServer == null) return "이 Rhino의 MCP가 실행 중이지 않습니다. MCP를 켜고 시작 상태를 확인하세요.";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "aha-setting-check", ["version"] = "1" } } };
        using var request = new HttpRequestMessage(HttpMethod.Post,activeServer.Url) { Content = new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json") };
        request.Headers.TryAddWithoutValidation("Accept","application/json, text/event-stream");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!ReferenceEquals(server,activeServer)) return "확인 중 MCP 설정이 변경되었습니다. 다시 확인하세요.";
        return response.IsSuccessStatusCode && data?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "aha"
            ? "AHA MCP 서버 응답 정상. 클라이언트 등록·실제 호출은 별도로 확인하세요."
            : "AHA MCP 응답을 확인하지 못했습니다.";
    }
}
