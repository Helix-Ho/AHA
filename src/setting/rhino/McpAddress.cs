namespace Aha.Setting;

internal static class McpAddress
{
    internal static void Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("올바른 AHA 로컬 MCP 주소가 필요합니다. MCP 상태와 포트를 확인하세요.");
    }
}
