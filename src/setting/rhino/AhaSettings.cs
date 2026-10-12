namespace Aha.Setting;

/// <summary>User preferences and a separate OS credential store. Never reads environment keys or .env files.</summary>
public static class AhaSettings
{
    private static readonly object Gate = new();
    internal static readonly CredentialAccess Keys = new(MacKeychain.Read, MacKeychain.Write,
        MacKeychain.Delete, MacKeychain.Contains, OperatingSystem.IsMacOS());
    private static readonly SettingsStore Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AHA", "settings.json"));
    public static bool SupportsSecureKeyStorage => Keys.Supported;
    public static VWorldKeyStatus VWorldKeyState => Keys.State;
    public static event Action? VWorldKeyChanged { add => Keys.Changed += value; remove => Keys.Changed -= value; }
    public static string GetVWorldKey() => Keys.Read();
    public static void SaveVWorldKey(string key) => Keys.Save(key);
    public static void DeleteVWorldKey() => Keys.Delete();
    public static bool HasVWorldKey() => Keys.Contains();
    internal static long VWorldKeyRevision => Keys.Revision;

    public static bool McpEnabled
    {
        get { lock (Gate) return ReadOrDefault().McpEnabled; }
        set { lock (Gate) Store.Write(ReadOrDefault() with { McpEnabled = value }); }
    }

    public static int McpPort
    {
        get { lock (Gate) return ReadOrDefault().McpPort; }
        set
        {
            SettingsCodec.ValidatePort(value);
            lock (Gate) Store.Write(ReadOrDefault() with { McpPort = value });
        }
    }

    // One write prevents a half-applied enabled/port pair in the settings panel.
    public static void SaveMcpSettings(bool enabled, int port)
    {
        SettingsCodec.ValidatePort(port);
        lock (Gate) Store.Write(new GeneralSettings(enabled, port));
    }

    public static string GeneralSettingsWarning
    {
        get
        {
            lock (Gate)
            {
                try { Store.Read(); return ""; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return "저장된 일반 설정을 읽지 못해 MCP를 기본 OFF로 표시합니다. 설정을 저장하면 복구합니다."; }
            }
        }
    }

    private static GeneralSettings ReadOrDefault()
    {
        try { return Store.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new GeneralSettings(); }
    }
}
