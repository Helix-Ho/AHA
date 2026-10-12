using System.Security.Cryptography;
using System.Text;

namespace Aha.Mcp;

/// <summary>Compact MCP concurrency tokens; the stored Info JSON remains unchanged.</summary>
internal static class InfoRevision
{
    public static string Token(string? raw) => raw is null ? ""
        : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    /// <summary>Call on the document UI thread immediately before the service mutation.</summary>
    public static string RequireSnapshot(string? raw, string revision)
    {
        if (!string.Equals(Token(raw), revision, StringComparison.Ordinal))
            throw new InvalidOperationException("편집 중 객체 정보가 변경되었습니다. aha_info_read로 다시 읽은 뒤 새 revision으로 편집하세요.");
        // An empty expected snapshot makes InfoService verify that metadata is still absent.
        return raw ?? "";
    }
}
