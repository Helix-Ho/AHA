using System.Text.Json;

namespace Aha.Setting;

internal sealed record GeneralSettings(bool McpEnabled = false, int McpPort = 18940);

/// <summary>The on-disk format deliberately contains no credential fields.</summary>
internal static class SettingsCodec
{
    internal const int DefaultPort = 18940;

    internal static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "MCP 포트는 1024~65535여야 합니다.");
    }

    internal static string Encode(GeneralSettings value)
    {
        ValidatePort(value.McpPort);
        return JsonSerializer.Serialize(new { mcpEnabled = value.McpEnabled, mcpPort = value.McpPort },
            new JsonSerializerOptions { WriteIndented = true });
    }

    internal static GeneralSettings Decode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException();
            bool enabled = root.TryGetProperty("mcpEnabled", out var e) ? e.GetBoolean() : false;
            int port = root.TryGetProperty("mcpPort", out var p) ? p.GetInt32() : DefaultPort;
            ValidatePort(port);
            return new GeneralSettings(enabled, port);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("저장된 AHA 설정을 읽지 못했습니다. Setting에서 MCP 설정을 다시 저장하세요.");
        }
    }
}

internal sealed class SettingsStore(string path)
{
    internal GeneralSettings Read() => File.Exists(path)
        ? SettingsCodec.Decode(File.ReadAllText(path)) : new GeneralSettings();

    internal void Write(GeneralSettings value)
    {
        string json = SettingsCodec.Encode(value);
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("설정 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
