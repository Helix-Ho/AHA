using System.Reflection;
using System.Text.Json.Nodes;
using Aha.Runtime;
using Eto.Forms;
using Rhino;
namespace Aha.EumCrawl.Checks;
internal static class EumPanelChecks
{
    internal static async Task<bool> RunAsync(RhinoDoc doc, Guid first, Guid second, EumClient client)
    {
        var parcel = await client.LookupAsync(FixtureData.Facts.GetProperty("pnu").GetString()!);
        foreach (string change in new[] { "input", "selection", "hidden", "multiple" })
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflow = new EumWorkflow(async (_, _) => { entered.SetResult(true); await release.Task; return (JsonObject)parcel.DeepClone(); }, () => DateTimeOffset.UtcNow);
            EumPanel? panel = null;
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            T Field<T>(string n) => (T)typeof(EumPanel).GetField(n, hidden)!.GetValue(panel)!;
            object? Call(string n) => typeof(EumPanel).GetMethod(n, hidden)!.Invoke(panel, null);
            Task? search = null;
            try
            {
                search = await RhinoDispatch.InvokeAsync(() =>
                {
                    doc.Objects.UnselectAll(); doc.Objects.Select(first);
                    panel = new EumPanel(doc.RuntimeSerialNumber, workflow, () => doc.RuntimeSerialNumber);
                    Call("Poll"); Field<TextBox>("pnu").Text = FixtureData.Facts.GetProperty("pnu").GetString()!;
                    return (Task)Call("Search")!;
                });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await RhinoDispatch.InvokeAsync(() =>
                {
                    if (change == "input") Field<TextBox>("pnu").Text = "9900000000100020000";
                    else if (change == "hidden") panel!.PanelHidden(0, default);
                    else { if (change == "selection") doc.Objects.UnselectAll(); doc.Objects.Select(second); Call("Poll"); }
                    return true;
                });
                release.SetResult(true); await search;
                bool valid = await RhinoDispatch.InvokeAsync(() => Field<JsonObject?>("preview") is null
                    && !Field<Button>("apply").Enabled && !Field<Button>("original").Enabled);
                if (!valid) throw new Exception("Eum stale response survived " + change);
            }
            finally
            {
                release.TrySetResult(true);
                if (search is not null) await search;
                await RhinoDispatch.InvokeAsync(() => { panel?.Dispose(); doc.Objects.UnselectAll(); return true; });
            }
        }
        return true;
    }
}
