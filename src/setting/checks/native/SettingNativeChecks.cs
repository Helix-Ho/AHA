using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Reflection;
using Eto.Forms;
using Aha.Runtime;
using Aha.Setting;
using Rhino;

namespace Aha.Setting.Checks;

/// <summary>Installed panel with isolated credentials and controlled delayed validation.</summary>
public static class SettingNativeChecks
{
    public static string Start(string outputDirectory)
    {
        if (RhinoApp.InvokeRequired) throw new InvalidOperationException("Run on the Rhino UI thread.");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "setting-panel.json");
        File.WriteAllText(path, "{\"status\":\"running\"}");
        _ = RunPanelChecksAsync(path);
        return path;
    }

    private static async Task RunPanelChecksAsync(string path)
    {
        var cases = new List<object>();
        var report = new Dictionary<string, object?> { ["status"] = "running", ["cases"] = cases };
        string original = "CHECK-ONLY-KEY";
        string stored = original;
        var keys = new CredentialAccess(() => stored, value => stored = value, () => stored = "", () => stored.Length > 0, true);
        var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SettingPanel? panel = null;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(SettingPanel).GetField(name, hidden)!.GetValue(panel)!;
        void Check(string name, bool value)
        { cases.Add(new { name, success = value }); if (!value) throw new InvalidOperationException(name); }
        async Task Ready()
        {
            for (int i = 0; i < 200; i++)
            {
                if (await RhinoDispatch.InvokeAsync(() => Field<bool>("keyKnown") && !Field<bool>("keyBusy"))) return;
                await Task.Delay(25);
            }
            throw new TimeoutException("Panel did not finish reading the credential store.");
        }
        try
        {
            panel = new SettingPanel(0, keys, async (_, _) => { requested.TrySetResult(true); await response.Task; });
            panel.PanelShown(0, default);
            await Ready();
            Check("panel_reads_existing_key_without_revealing_it", await RhinoDispatch.InvokeAsync(() =>
                Field<PasswordBox>("maskedKey").Text == original && Field<CheckBox>("showKey").Checked == false));
            Task? validation = null;
            await RhinoDispatch.InvokeAsync(() =>
            {
                validation = (Task)typeof(SettingPanel).GetMethod("ValidateKeyAsync", hidden)!.Invoke(panel, null)!;
                return true;
            });
            await requested.Task;
            keys.Delete();
            response.SetResult(true);
            await validation!;
            await Ready();
            Check("changed_key_cannot_be_overwritten_by_old_validation", await RhinoDispatch.InvokeAsync(() =>
                Field<PasswordBox>("maskedKey").Text == "" && Field<Button>("saveKey").Enabled && !Field<Button>("deleteKey").Enabled
                && !Field<Label>("keyStatus").Text.Contains("검증 성공")));
            await RhinoDispatch.InvokeAsync(() =>
            {
                keys.Save(original.Length == 0 ? "AHA-PANEL-INVALID-KEY" : original);
                return true;
            });
            await Ready();
            requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await RhinoDispatch.InvokeAsync(() =>
            {
                validation = (Task)typeof(SettingPanel).GetMethod("ValidateKeyAsync", hidden)!.Invoke(panel, null)!;
                panel.PanelHidden(0, default);
                Check("hidden_panel_clears_sensitive_inputs", Field<PasswordBox>("maskedKey").Text == "" && Field<TextBox>("visibleKey").Text == "");
                return true;
            });
            response.TrySetResult(true);
            await validation!;
            await RhinoDispatch.InvokeAsync(() => { panel.PanelShown(0, default); return true; });
            await Ready();
            Check("reopened_panel_reads_current_key_after_cancel", await RhinoDispatch.InvokeAsync(() =>
                Field<bool>("keyStored") && !Field<bool>("keyBusy") && Field<CheckBox>("showKey").Checked == false));
            report["status"] = "passed";
        }
        catch (Exception error) { report["status"] = "failed"; report["error_type"] = error.GetType().Name; }
        finally
        {
            await RhinoDispatch.InvokeAsync(() =>
            {
                panel?.Dispose();

                return true;
            });
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

}
