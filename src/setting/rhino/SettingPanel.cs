using System.Runtime.InteropServices;
using System.Security;
using Aha.Runtime;
using Aha.Topo;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace Aha.Setting;

[Guid("8221b057-e7a5-45b7-acfe-fb169d15e6cc")]
public sealed class SettingPanel : Panel, IPanel
{
    // Keychain calls can outlive a hidden panel. Reopening reads after a pending write.
    private static readonly SemaphoreSlim KeyStoreGate = new(1, 1);
    private readonly CredentialAccess keys;
    private readonly Func<string, CancellationToken, Task> validate;
    private readonly PasswordBox maskedKey = new();
    private readonly TextBox visibleKey = new();
    private readonly Panel keyInput = new();
    private readonly CheckBox showKey = new() { Text = "KEY 보기", Width = 82, Font = new Font(SystemFont.Default, 12) };
    private readonly Label keyStatus = Message("");
    private readonly Button saveKey = new() { Text = "KEY 저장", Width = 68, MinimumSize = new Size(68, 0), Font = new Font(SystemFont.Default, 12) };
    private readonly Button deleteKey = new() { Text = "KEY 삭제", Width = 68, MinimumSize = new Size(68, 0), Font = new Font(SystemFont.Default, 12) };
    private readonly Button validateKey = new() { Text = "KEY 검증", Width = 68, MinimumSize = new Size(68, 0), Font = new Font(SystemFont.Default, 12) };
    private readonly CheckBox enabled = new() { Text = "MCP 켜기" };
    private readonly Button probe = new() { Text = "연결 확인" };
    private readonly NumericStepper port = new() { MinValue = 1024, MaxValue = 65535, DecimalPlaces = 0, Increment = 1, Width = 100 };
    private readonly Label mcpStatus = Message("");
    private readonly Label lastCall = Message("");
    private readonly Label settingsWarning = Message("");
    private readonly TextBox url = new() { ReadOnly = true };
    private readonly Label connectionResult = Message("");
    private CancellationTokenSource? viewLifetime;
    private bool attached;
    private bool disposed;
    private bool keyBusy;
    private bool keyKnown;
    private bool keyStored;
    private bool syncingKey;
    private bool refreshingMcp;
    private int generation;
    private long observedKeyRevision = -1;
    private bool reloadKeyWhenIdle;

    public static void Open(RhinoDoc doc) => Aha.Runtime.ToolPanels.Open(doc, typeof(SettingPanel));

    public SettingPanel() : this(RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0U) { }

    public SettingPanel(uint documentSerialNumber) : this(documentSerialNumber, AhaSettings.Keys, ValidateRemoteAsync) { }

    internal SettingPanel(uint documentSerialNumber, CredentialAccess keys, Func<string, CancellationToken, Task> validate)
    {
        this.keys = keys;
        this.validate = validate;
        // These preferences belong to the user, not to a Rhino document.
        _ = documentSerialNumber;
        MinimumSize = new Size(360, 160);
        keyInput.Content = maskedKey;
        maskedKey.ToolTip = visibleKey.ToolTip = "KEY는 macOS Keychain에 저장합니다. 저장한 KEY를 바꾸려면 먼저 삭제하세요.";
        port.ToolTip = "한 Rhino 프로세스의 여러 문서는 같은 서버를 씁니다. 별도 프로세스에서 MCP를 함께 켤 때만 서로 다른 포트가 필요합니다.";
        var applyPort = new Button { Text = "포트 적용" };
        var copyUrl = new Button { Text = "주소 복사" };
        var layout = new DynamicLayout { Padding = new Padding(12), Spacing = new Size(8, 8) };
        var fitControls = new List<Control>();
        void Add(Control control)
        {
            layout.AddRow(control);
            fitControls.Add(control);
        }
        Add(Heading("VWorld KEY"));
        Add(keyInput);
        var keyActions = Row(saveKey, deleteKey, validateKey);
        keyActions.Spacing = 6;
        keyActions.Items.Add(new StackLayoutItem(null, true));
        keyActions.Items.Add(showKey);
        Add(keyActions);
        Add(keyStatus);
        Add(Heading("로컬 MCP"));
        layout.AddRow(enabled);
        layout.AddRow(Row(Message("포트"), port, applyPort));
        Add(mcpStatus);
        Add(url);
        layout.AddRow(Row(copyUrl, probe));
        Add(connectionResult);
        Add(lastCall);
        Add(settingsWarning);

        layout.Add(null);
        var scroll = new Scrollable { Content = layout, Border = BorderType.None };
        Content = scroll;
        void Fit()
        {
            int width = scroll.ClientSize.Width > 0 ? scroll.ClientSize.Width : 340;
            layout.Width = width;
            foreach (var control in fitControls) control.Width = Math.Max(1, width - 24);
            if (scroll.ScrollPosition.X != 0) scroll.ScrollPosition = new Point(0, scroll.ScrollPosition.Y);
        }
        scroll.SizeChanged += (_, _) => Fit();
        LoadComplete += (_, _) => Fit();
        maskedKey.TextChanged += (_, _) => KeyTextChanged(maskedKey.Text, visibleKey);
        visibleKey.TextChanged += (_, _) => KeyTextChanged(visibleKey.Text, maskedKey);
        showKey.CheckedChanged += (_, _) => keyInput.Content = showKey.Checked == true ? visibleKey : maskedKey;
        saveKey.Click += async (_, _) => await SaveKeyAsync();
        deleteKey.Click += async (_, _) => await DeleteKeyAsync();
        validateKey.Click += async (_, _) => await ValidateKeyAsync();
        enabled.CheckedChanged += (_, _) =>
        {
            if (refreshingMcp) return;
            ApplyMcpSettings(enabled.Checked == true, AhaSettings.McpPort, "MCP 사용 설정을 적용했습니다.");
        };
        applyPort.Click += (_, _) => ApplyMcpSettings(AhaSettings.McpEnabled, checked((int)port.Value), "포트를 적용했습니다. 현재 주소를 확인하세요.");
        probe.Click += async (_, _) =>
        {
            int view = generation;
            probe.Enabled = false;
            connectionResult.Text = "연결 확인 중…";
            try
            {
                string result = await McpControl.ProbeAsync();
                Ui(view, () => connectionResult.Text = result);
            }
            catch (Exception) { Ui(view, () => connectionResult.Text = "연결을 확인하지 못했습니다. MCP 상태와 포트를 확인하세요."); }
            finally { Ui(view, () => { probe.Enabled = true; RefreshMcp(); }); }
        };
        copyUrl.Click += (_, _) =>
        {
            try
            {
                string address = McpControl.Url;
                McpAddress.Validate(address);
                Clipboard.Instance.Text = address;
                connectionResult.Text = "MCP 주소를 복사했습니다." + StoppedCopyNotice();
            }
            catch (ArgumentException ex) { connectionResult.Text = ex.Message; }
            catch (Exception) { connectionResult.Text = "주소를 복사하지 못했습니다."; }
        };
        ApplyKeyState();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ActivateView();
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) => ActivateView();
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) => DeactivateView();
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) => DeactivateView();

    private void ActivateView()
    {
        if (attached || disposed) return;
        attached = true;
        generation++;
        viewLifetime = new CancellationTokenSource();
        keyBusy = keyKnown = keyStored = false;
        showKey.Checked = false;
        SetKeyText("");
        probe.Enabled = true;
        port.Value = AhaSettings.McpPort;
        McpControl.Changed += OnMcpChanged;
        keys.Changed += OnKeyChanged;
        RefreshMcp();
        _ = LoadKeyAsync();
    }

    protected override void OnUnLoad(EventArgs e)
    {
        DeactivateView();
        base.OnUnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            DeactivateView();
            disposed = true;
            // Only the current input is parented. Dispose both native input controls explicitly.
            SetKeyText("");
            keyInput.Content = null;
            maskedKey.Dispose();
            visibleKey.Dispose();
        }
        base.Dispose(disposing);
    }

    private void DeactivateView()
    {
        if (!attached) return;
        attached = false;
        generation++;
        McpControl.Changed -= OnMcpChanged;
        keys.Changed -= OnKeyChanged;
        viewLifetime?.Cancel();
        viewLifetime?.Dispose();
        viewLifetime = null;
        keyBusy = keyKnown = keyStored = false;
        observedKeyRevision = -1;
        reloadKeyWhenIdle = false;
        showKey.Checked = false;
        SetKeyText("");
        connectionResult.Text = "";
        ApplyKeyState();
    }

    private async Task LoadKeyAsync()
    {
        if (!BeginKeyAction("저장된 KEY를 읽는 중…", out int view, out var ct)) return;
        try
        {
            var saved = await InKeyStoreAsync(ReadKeySnapshot, ct);
            Ui(view, () =>
            {
                if (!ObserveKeySnapshot(saved.Revision)) return;
                keyKnown = true;
                keyStored = !string.IsNullOrEmpty(saved.Key);
                SetKeyText(saved.Key);
                keyStatus.Text = "";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Ui(view, () => keyStatus.Text = "KEY를 읽지 못했습니다. Keychain 접근을 확인한 뒤 패널을 다시 여세요."); }
        finally { EndKeyAction(view); }
    }

    private async Task SaveKeyAsync()
    {
        if (!keyKnown || keyStored || !BeginKeyAction("KEY 저장 중…", out int view, out var ct)) return;
        string candidate = maskedKey.Text;
        try
        {
            // Another panel may have saved a key since this view last read it. Never replace it silently.
            var saved = await InKeyStoreAsync(() =>
            {
                string existing = keys.Read();
                if (string.IsNullOrEmpty(existing)) keys.Save(candidate);
                return ReadKeySnapshot();
            }, ct);
            Ui(view, () =>
            {
                if (!ObserveKeySnapshot(saved.Revision)) return;
                keyStored = true;
                showKey.Checked = false;
                SetKeyText(saved.Key);
                keyStatus.Text = "";
            });
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException) { Ui(view, () => keyStatus.Text = "저장할 VWorld KEY를 입력하세요."); }
        catch (Exception ex) when (ex is SecurityException or NotSupportedException) { Ui(view, () => keyStatus.Text = ex.Message); }
        catch (Exception) { Ui(view, () => keyStatus.Text = "KEY를 저장하지 못했습니다. Keychain 접근 권한을 확인하세요."); }
        finally { EndKeyAction(view); }
    }

    private async Task DeleteKeyAsync()
    {
        if (!keyKnown || !keyStored || !BeginKeyAction("KEY 삭제 중…", out int view, out var ct)) return;
        try
        {
            var revision = await InKeyStoreAsync(() => { keys.Delete(); return keys.Revision; }, ct);
            Ui(view, () =>
            {
                if (!ObserveKeySnapshot(revision)) return;
                keyStored = false;
                showKey.Checked = false;
                SetKeyText("");
                keyStatus.Text = "";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SecurityException or NotSupportedException) { Ui(view, () => keyStatus.Text = ex.Message); }
        catch (Exception) { Ui(view, () => keyStatus.Text = "KEY를 삭제하지 못했습니다. Keychain 접근 권한을 확인하세요."); }
        finally { EndKeyAction(view); }
    }

    private async Task ValidateKeyAsync()
    {
        if (!keyKnown || !BeginKeyAction("KEY 검증 중…", out int view, out var ct)) return;
        string candidate = maskedKey.Text.Trim();
        long? savedRevision = null;
        long validationRevision = keys.Revision;
        try
        {
            if (keyStored)
            {
                var saved = await InKeyStoreAsync(ReadKeySnapshot, ct);
                candidate = saved.Key;
                savedRevision = validationRevision = saved.Revision;
                Ui(view, () => { if (ObserveKeySnapshot(saved.Revision)) { keyStored = !string.IsNullOrEmpty(candidate); SetKeyText(candidate); } });
            }
            if (string.IsNullOrWhiteSpace(candidate)) { Ui(view, () => keyStatus.Text = "검증할 KEY를 입력하세요."); return; }
            await validate(candidate, ct);
            ct.ThrowIfCancellationRequested();
            if (savedRevision.HasValue) keys.ReportValidation(savedRevision.Value, true);
            Ui(view, () => { if (ObserveKeySnapshot(validationRevision)) keyStatus.Text = "검증 성공 · VWorld 필지 조회에 사용할 수 있습니다."; });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (savedRevision.HasValue && !ct.IsCancellationRequested) keys.ReportValidation(savedRevision.Value, false);
            Ui(view, () => { if (ObserveKeySnapshot(validationRevision)) keyStatus.Text = "KEY 검증에 실패했습니다. 유효성·허용 서비스·연결을 확인하세요."; });
        }
        finally { EndKeyAction(view); }
    }

    private static async Task ValidateRemoteAsync(string candidate, CancellationToken ct)
    {
        using var client = new VWorldClient(candidate);
        await client.ParcelByPnuAsync("1114010300100310000", ct);
    }

    private static async Task<T> InKeyStoreAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await KeyStoreGate.WaitAsync(ct);
        try { return await Task.Run(operation, ct); }
        finally { KeyStoreGate.Release(); }
    }

    private bool BeginKeyAction(string message, out int view, out CancellationToken ct)
    {
        view = generation;
        ct = viewLifetime?.Token ?? new CancellationToken(canceled: true);
        if (keyBusy || !attached || disposed) return false;
        if (!keys.Supported)
        {
            keyStatus.Text = "KEY 보안 저장은 macOS Rhino 8에서 지원합니다.";
            ApplyKeyState();
            return false;
        }
        keyBusy = true;
        keyStatus.Text = message;
        ApplyKeyState();
        return true;
    }

    private (string Key, long Revision) ReadKeySnapshot()
    {
        string key = keys.Read();
        return (key, keys.Revision);
    }

    private bool ObserveKeySnapshot(long revision)
    {
        if (revision != keys.Revision) { reloadKeyWhenIdle = true; return false; }
        observedKeyRevision = revision;
        return true;
    }

    private void OnKeyChanged() => Ui(generation, () =>
    {
        if (observedKeyRevision == keys.Revision) return;
        if (keyBusy) reloadKeyWhenIdle = true;
        else _ = LoadKeyAsync();
    });

    private void EndKeyAction(int view) => Ui(view, () =>
    {
        keyBusy = false;
        ApplyKeyState();
        if (!reloadKeyWhenIdle) return;
        reloadKeyWhenIdle = false;
        if (observedKeyRevision != keys.Revision) _ = LoadKeyAsync();
    });

    private void SetKeyText(string value)
    {
        syncingKey = true;
        try { maskedKey.Text = visibleKey.Text = value; }
        finally { syncingKey = false; }
    }

    private void KeyTextChanged(string value, TextControl other)
    {
        if (syncingKey) return;
        syncingKey = true;
        try { other.Text = value; }
        finally { syncingKey = false; }
        ApplyKeyState();
    }

    private void ApplyKeyState()
    {
        var state = KeyControlState.From(keys.Supported, keyKnown, keyStored, keyBusy, !string.IsNullOrWhiteSpace(maskedKey.Text));
        maskedKey.Enabled = visibleKey.Enabled = state.InputEnabled;
        maskedKey.ReadOnly = visibleKey.ReadOnly = state.InputReadOnly;
        saveKey.Enabled = state.CanSave;
        deleteKey.Enabled = state.CanDelete;
        validateKey.Enabled = state.CanValidate;
        showKey.Enabled = state.CanReveal;
    }

    private void ApplyMcpSettings(bool useMcp, int mcpPort, string message)
    {
        try
        {
            AhaSettings.SaveMcpSettings(useMcp, mcpPort);
            McpControl.ApplySettings();
            connectionResult.Text = message;
        }
        catch (Exception) { connectionResult.Text = "설정을 적용하지 못했습니다. 포트와 설정 저장 권한을 확인하세요."; }
        RefreshMcp();
    }

    private void OnMcpChanged() => Ui(generation, RefreshMcp);
    private void RefreshMcp()
    {
        refreshingMcp = true;
        try
        {
            enabled.Checked = AhaSettings.McpEnabled;
            enabled.Text = enabled.Checked == true ? "MCP 켜짐" : "MCP 꺼짐";
        }
        finally { refreshingMcp = false; }
        mcpStatus.Text = McpControl.Status;
        url.Text = McpControl.Url;
        lastCall.Text = "마지막 호출: " + (string.IsNullOrWhiteSpace(McpControl.LastCall) ? "아직 없음" : McpControl.LastCall);
        settingsWarning.Text = AhaSettings.GeneralSettingsWarning;
    }

    private void Ui(int view, Action action)
    {
        if (disposed || !attached || view != generation) return;
        RhinoApp.InvokeOnUiThread(new Action(() => { if (!disposed && attached && view == generation) action(); }));
    }

    private static string StoppedCopyNotice() => McpControl.IsRunning ? "" : " MCP가 꺼져 있어 설정 주소만 복사했습니다.";
    private static Label Message(string text) => new() { Text = text, Wrap = WrapMode.Word, TextAlignment = TextAlignment.Left };
    private static Label Heading(string text) => new() { Text = text, Font = new Font(SystemFont.Bold, 12), TextAlignment = TextAlignment.Left };
    private static StackLayout Row(params Control[] controls)
    {
        var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var control in controls) row.Items.Add(control);
        return row;
    }
}
