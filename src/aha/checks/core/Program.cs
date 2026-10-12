using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Aha.Mcp;

int assertions = 0;
void Check(bool value,string message) { if (!value) throw new Exception(message); assertions++; }
InvalidOperationException CheckStale(string? raw,string token,string message)
{
    try { InfoRevision.RequireSnapshot(raw,token); }
    catch (InvalidOperationException error) { assertions++; return error; }
    throw new Exception(message);
}

// Fixed digest independently checked with SHA-256 over these exact UTF-8 bytes.
const string revisionSource = """{"주소":"가상시 예시구 모형로 110"}""";
const string revisionToken = "sha256:bc7490916eec979fb8499dfa606744db9b5ab4de204bfa9f35ffd602b56f1f88";
Check(InfoRevision.Token(null) == "","absent Info revision is empty");
Check(InfoRevision.Token("") == "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    "stored empty string has the SHA-256 empty-byte digest");
Check(InfoRevision.Token(null) != InfoRevision.Token(""),"absence differs from stored empty text");
Check(InfoRevision.Token(revisionSource) == revisionToken,"known Korean UTF-8 revision digest");
Check(InfoRevision.Token(revisionSource) == InfoRevision.Token(revisionSource),"revision digest is deterministic");
Check(InfoRevision.RequireSnapshot(revisionSource,revisionToken) == revisionSource,"matching revision returns exact raw snapshot");
Check(InfoRevision.RequireSnapshot(null,"") == "","absent snapshot maps to empty expected JSON");
Check(InfoRevision.RequireSnapshot("",InfoRevision.Token("")) == "","stored empty snapshot is accepted with its own token");
CheckStale("""{"주소":"가상시 예시구 모형로 111"}""",revisionToken,"changed data rejects stale revision");
CheckStale("""{ "주소": "가상시 예시구 모형로 110" }""",revisionToken,"format changes reject stale revision");
CheckStale(null,InfoRevision.Token(""),"stored empty revision cannot match absence");
CheckStale("","","absent revision cannot match stored empty text");
CheckStale(revisionSource,revisionToken.ToUpperInvariant(),"revision comparison is ordinal and case-sensitive");
CheckStale(revisionSource,revisionSource,"raw JSON cannot substitute for an opaque revision");
const string invalidToken = "invalid-token-민감한-호출자-입력";
var staleError = CheckStale(revisionSource,invalidToken,"invalid revision rejects update");
Check(!staleError.Message.Contains(revisionSource,StringComparison.Ordinal) &&
      !staleError.Message.Contains("가상시",StringComparison.Ordinal) &&
      !staleError.Message.Contains(invalidToken,StringComparison.Ordinal),"stale errors reveal neither source text nor supplied token");
var largeSource = new string('가',1024*1024);
var largeToken = InfoRevision.Token(largeSource);
Check(largeToken.Length == 71 && largeToken.StartsWith("sha256:",StringComparison.Ordinal) &&
      largeToken[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),"large source produces a bounded lowercase opaque revision");
Check(InfoRevision.RequireSnapshot(largeSource,largeToken) == largeSource,"large source can be checked without changing the snapshot");

var protocol = new McpProtocol(() => new JsonArray(new JsonObject { ["name"]="read" }),
    (name,args) => name == "read" ? Task.FromResult<JsonNode?>(new JsonObject { ["value"] = args["value"]?.DeepClone() }) : throw new ArgumentException("Unknown tool"));
async Task<JsonObject?> Send(string json) => await protocol.DispatchAsync(JsonNode.Parse(json));
var init = await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
Check(init?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-03-26","version negotiation");
Check(await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""") == null,"notification has no response");
Check((await Send("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}"""))?["result"]?["tools"]?.AsArray().Count == 1,"discovery");
var result = await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read","arguments":{"value":12}}}""");
Check(result?["result"]?["structuredContent"]?["value"]?.GetValue<int>() == 12,"structured result");
Check((await Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"missing"}}"""))?["result"]?["isError"]?.GetValue<bool>() == true,"tool errors");
Check((await Send("""{"jsonrpc":"2.0","id":5,"method":"missing"}"""))?["error"]?["code"]?.GetValue<int>() == -32601,"method errors");
Check((await Send("[]"))?["error"]?["code"]?.GetValue<int>() == -32600,"batch is not a valid message");
foreach (var malformed in new[]
{
    """{"jsonrpc":2,"id":1,"method":"ping"}""",
    """{"jsonrpc":"2.0","id":{},"method":"ping"}""",
    """{"jsonrpc":"2.0","id":true,"method":"ping"}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[]}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":[]}}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":null}}""",
    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":42}}"""
}) Check((await Send(malformed))?["error"] is JsonObject, "malformed envelope/arguments must be rejected before dispatch");
Check(init?["result"]?["serverInfo"]?["version"]?.GetValue<string>() == McpProtocol.ApplicationVersion,
    "MCP reports the built assembly version");

const string koreanValue = "가상시 예시구 · 대지 검토\n인용 \"필지\"와 역슬래시 \\ 유지";
var koreanRequest = new JsonObject
{
    ["jsonrpc"]="2.0", ["id"]=6, ["method"]="tools/call",
    ["params"]=new JsonObject { ["name"]="read", ["arguments"]=new JsonObject { ["value"]=koreanValue } }
}.ToJsonString();
var koreanResult = await Send(koreanRequest);
var koreanText = koreanResult?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
Check(koreanText?.Contains("가상시 예시구",StringComparison.Ordinal) == true,"tool text content contains readable Korean characters");
Check(koreanText?.Contains("\\uC11C",StringComparison.OrdinalIgnoreCase) == false,"tool text content does not escape Korean as Unicode sequences");
Check(JsonNode.Parse(koreanText!)?["value"]?.GetValue<string>() == koreanValue,"tool text content round-trips Korean and JSON-special characters");
Check(JsonNode.DeepEquals(JsonNode.Parse(koreanText!),koreanResult?["result"]?["structuredContent"]),
    "tool text content and structured content contain identical values");
Console.WriteLine($"MCP protocol and revision passed: {assertions} assertions.");
await JobChecks.RunAsync();
if (!args.Contains("--http")) return;
var probe = new TcpListener(IPAddress.Loopback,0); probe.Start(); int port=((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
int dispatchedCalls = 0;
using var server = new McpHttpServer(port,()=>new JsonArray(),(_,arguments)=>
{
    Interlocked.Increment(ref dispatchedCalls);
    return Task.FromResult<JsonNode?>(new JsonObject { ["value"]=arguments["value"]?.DeepClone() });
}); server.Start();
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
async Task<HttpResponseMessage> Post(string body,string? origin=null,string? version=null)
{
    var request = new HttpRequestMessage(HttpMethod.Post,server.Url) { Content = new StringContent(body,Encoding.UTF8,"application/json") };
    request.Headers.TryAddWithoutValidation("Accept","application/json, text/event-stream");
    if (body.Length > 1024*1024) request.Headers.ExpectContinue = true;
    if(origin!=null)request.Headers.TryAddWithoutValidation("Origin",origin);
    if(version!=null)request.Headers.TryAddWithoutValidation("MCP-Protocol-Version",version);
    return await client.SendAsync(request);
}
Check((await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")).StatusCode==HttpStatusCode.OK,"POST");
Check((await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")).StatusCode==HttpStatusCode.Accepted,"notification 202");
Check((await client.GetAsync(server.Url)).StatusCode==HttpStatusCode.MethodNotAllowed,"GET 405");
Check((await Post("{}","https://example.com")).StatusCode==HttpStatusCode.Forbidden,"foreign Origin rejected");
Check((await Post("{}",version:"1900-01-01")).StatusCode==HttpStatusCode.BadRequest,"unsupported version");
using (var tcp = new TcpClient())
{
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var stream = tcp.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /mcp/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: 1048577\r\nConnection: close\r\n\r\n"));
    using var reader = new StreamReader(stream);
    var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(status?.Contains("413") == true, "oversized request rejected before reading body");
}
Check(JsonNode.Parse(await (await Post("{" )).Content.ReadAsStringAsync())?["error"]?["code"]?.GetValue<int>()==-32700,"malformed JSON");
foreach (var duplicate in new[]
{
    """{"jsonrpc":"2.0","jsonrpc":"2.0","id":1,"method":"ping"}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","name":"other","arguments":{}}}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"value":{"x":1,"x":2}}}}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"value":[{"x":1,"x":2}]}}}""",
    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","\u006eame":"other","arguments":{}}}"""
})
{
    int beforeDispatch = dispatchedCalls;
    using var rejected = await Post(duplicate);
    var error = JsonNode.Parse(await rejected.Content.ReadAsStringAsync());
    Check(rejected.StatusCode == HttpStatusCode.OK && error?["error"]?["code"]?.GetValue<int>() == -32600,
        "duplicate JSON names produce an explicit JSON-RPC rejection at every nesting level");
    Check(dispatchedCalls == beforeDispatch, "duplicate JSON names are rejected before tool dispatch");
}
using (var distinct = await Post("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"value":{"x":1,"X":2}}}}"""))
{
    var data = JsonNode.Parse(await distinct.Content.ReadAsStringAsync())?["result"]?["structuredContent"]?["value"];
    Check(data?["x"]?.GetValue<int>() == 1 && data?["X"]?.GetValue<int>() == 2,
        "JSON names remain ordinal and valid requests work after duplicate rejection");
}
using var koreanResponse = await Post(koreanRequest);
Check(koreanResponse.StatusCode == HttpStatusCode.OK,"Korean tool HTTP call succeeds");
var koreanWire = await koreanResponse.Content.ReadAsStringAsync();
Check(koreanWire.Contains("가상시 예시구",StringComparison.Ordinal),"HTTP response wire retains literal Korean characters");
Check(!koreanWire.Contains("\\uC11C",StringComparison.OrdinalIgnoreCase),"HTTP response wire does not re-escape Korean content");
var koreanHttpResult = JsonNode.Parse(koreanWire)?["result"];
Check(koreanHttpResult?["structuredContent"]?["value"]?.GetValue<string>() == koreanValue,
    "HTTP structured content round-trips Korean and JSON-special characters");
var koreanHttpText = koreanHttpResult?["content"]?[0]?["text"]?.GetValue<string>();
Check(koreanHttpText?.Contains("가상시 예시구",StringComparison.Ordinal) == true,
    "HTTP text content retains readable Korean after decoding its outer JSON");
Check(JsonNode.Parse(koreanHttpText!)?["value"]?.GetValue<string>() == koreanValue,
    "HTTP text content round-trips to the original value");
Console.WriteLine($"MCP including HTTP passed: {assertions} assertions.");
