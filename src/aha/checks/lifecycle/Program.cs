using System.Net;
using System.Net.Sockets;
using Aha.Runtime;
using Aha.Setting;

int passed = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    passed++;
}
TcpListener HoldPort(int port = 0)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    return listener;
}
int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
void CheckReleased(int port)
{
    using var listener = HoldPort(port);
    Check(listener.Server.IsBound, "previous listener port can be rebound");
}
void Apply(bool enabled, int port)
{
    AhaSettings.McpEnabled = enabled;
    AhaSettings.McpPort = port;
    McpControl.ApplySettings();
}

int first;
int second;
using (var a = HoldPort())
using (var b = HoldPort()) { first = Port(a); second = Port(b); }
int? running = null;
try
{
    Apply(false, first);
    Check(!McpControl.IsRunning, "disabled settings leave no server");
    foreach (int port in Enumerable.Range(0, 11).Select(i => i % 2 == 0 ? first : second))
    {
        var previous = running;
        Apply(true, port);
        Check(McpControl.IsRunning && McpControl.Url == $"http://127.0.0.1:{port}/mcp/", "one server uses the newly applied port");
        Check((await McpControl.ProbeAsync()).StartsWith("AHA MCP 서버 응답 정상", StringComparison.Ordinal), "new server responds to initialize");
        if (previous.HasValue) CheckReleased(previous.Value);
        int cancels = Jobs.CancelCount;
        Apply(true, port);
        Check(Jobs.CancelCount == cancels, "reapplying the active port does not stop or restart the server");
        running = port;
    }
    Apply(false, second);
    Check(!McpControl.IsRunning, "turning MCP off clears the active server");
    CheckReleased(first);
    CheckReleased(second);
    Check((await McpControl.ProbeAsync()).Contains("실행 중이지 않습니다", StringComparison.Ordinal), "probe rejects the stopped server");

    Apply(true, first);
    Check(McpControl.IsRunning, "server can restart after repeated port changes and off");
    using (var occupied = HoldPort(second))
    {
        Apply(true, second);
        Check(!McpControl.IsRunning && McpControl.Status.StartsWith("시작 실패", StringComparison.Ordinal), "occupied new port reports failure and retains no active server");
        CheckReleased(first);
    }
    CheckReleased(second);
    Apply(true, second);
    Check(McpControl.IsRunning && (await McpControl.ProbeAsync()).StartsWith("AHA MCP 서버 응답 정상", StringComparison.Ordinal), "server recovers after a failed port change");
}
finally { McpControl.Stop(); }
CheckReleased(first);
CheckReleased(second);
Console.WriteLine($"{passed} MCP lifecycle checks passed; only temporary loopback ports and in-memory settings were used.");
