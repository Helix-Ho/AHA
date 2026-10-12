using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Runtime;
using Eto.Forms;
using Rhino;

namespace Aha.Topo.Checks;

internal static class MapNavigationChecks
{
    internal static async Task RunAsync(RhinoDoc doc)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        TopoPanel? panel = null;
        WebView? map = null;
        var ready = NewSignal();
        var scripts = new List<string>();
        int fetched = 0;
        var applyKey = typeof(TopoPanel).GetMethod("ApplyKeyAvailability", hidden)!;
        T Field<T>(string name) => (T)typeof(TopoPanel).GetField(name, hidden)!.GetValue(panel)!;
        try
        {
            await RhinoDispatch.InvokeAsync(() =>
            {
                panel = new TopoPanel(doc.RuntimeSerialNumber,
                    (_, _, _, _, _) => { fetched++; return Task.FromResult(Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoaGj4DwAFhAKAjM1mJgAAAABJRU5ErkJggg==")); },
                    script =>
                    {
                        scripts.Add(script);
                        return script.StartsWith("aha.tile(", StringComparison.Ordinal)
                            ? map!.ExecuteScriptAsync(script) : Task.CompletedTask;
                    }, () => "fixed-map-key");
                map = Field<WebView>("map");
                if (map.BrowserContextMenuEnabled)
                    throw new Exception("Embedded map exposes browser navigation commands");
                // Deliver stale and pre-initialization messages before the real
                // page's ready. All messages use the ordinary host handler.
                var handler = (EventHandler<WebViewMessageEventArgs>)Delegate.CreateDelegate(
                    typeof(EventHandler<WebViewMessageEventArgs>), panel,
                    typeof(TopoPanel).GetMethod("MapMessage", hidden)!);
                map.MessageReceived -= handler;
                map.MessageReceived += (sender, e) =>
                {
                    using var message = JsonDocument.Parse(e.Message);
                    bool initializing = message.RootElement.GetProperty("type").GetString() == "ready";
                    if (initializing)
                    {
                        string current = Field<int>("mapGeneration").ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string previous = (Field<int>("mapGeneration") - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string status = Field<Label>("status").Text;
                        foreach (string generation in new[] { previous, current })
                        {
                            Deliver(new { type = "tiles", generation, tiles = new[] { new { z = 12, x = 1000, y = 1000 } } });
                            Deliver(new { type = "pick", generation, lon = 0, lat = 0 });
                            Deliver(new { type = "remove", generation, id = "fixed-selection" });
                        }
                        if (fetched != 0 || Field<bool>("mapReady") || Field<Label>("status").Text != status)
                            throw new Exception("Stale or pre-initialization map message changes panel state");
                    }
                    handler(sender, e);
                    if (initializing) ready.TrySetResult(true);
                };
                void Deliver(object value) => handler(map, new WebViewMessageEventArgs(JsonSerializer.Serialize(value)));
                typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] {
                    new JsonObject { ["state"] = "running", ["progress"] = "fixed running work" } });
                applyKey.Invoke(panel, new object[] { true, "fixed KEY availability" });
                return true;
            });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RhinoDispatch.InvokeAsync(() =>
            {
                if (!Field<bool>("mapReady") || map!.Visible) throw new Exception("Locked job loses map initialization");
                typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] { null });
                if (!map.Visible || !scripts.Any(script => script.Contains("aha.refresh")))
                    throw new Exception("Returning from work does not refresh the current map view");
                var handler = typeof(TopoPanel).GetMethod("MapMessage", hidden)!;
                void Deliver(object value) => handler.Invoke(panel, new object?[] { map, new WebViewMessageEventArgs(JsonSerializer.Serialize(value)) });
                int current = Field<int>("mapGeneration");
                string status = Field<Label>("status").Text;
                Deliver(new { type = "pick", generation = (current - 1).ToString(), lon = 0, lat = 0 });
                Deliver(new { type = "remove", generation = (current - 1).ToString(), id = "fixed-selection" });
                Deliver(new { type = "tiles", generation = (current - 1).ToString(), tiles = new[] { new { z = 12, x = 1000, y = 1000 } } });
                if (fetched != 0 || Field<Label>("status").Text != status)
                    throw new Exception("Previous page changes an initialized map");
                Deliver(new { type = "tiles", generation = current.ToString(), tiles = new[] { new { z = 12, x = 1000, y = 1000 } } });
                if (fetched != 1 || !scripts.Any(script => script.Contains("aha.tile(")) || Field<int>("mapGeneration") != current)
                    throw new Exception("Current tile response is missing or changes the page identity");
                return true;
            });
            await Script("aha.center({lon:127,lat:37,zoom:16});");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (await Script("return [...document.querySelectorAll('#tiles img')].length>0 && [...document.querySelectorAll('#tiles img')].every(i=>i.complete&&i.naturalWidth===1)?'loaded':'pending';") != "loaded")
                    await Task.Delay(20, timeout.Token);
            string saved = await Script("aha.overlay({rings:[[[127,37],[127.01,37],[127.01,37.01]]],bounds:[127,37,127.01,37.01]});return document.getElementById('overlay').innerHTML;");
            if (saved.Length == 0 || !saved.Contains("parcel")) throw new Exception("Map check has no selection overlay");
            int initializationCount = scripts.Count;
            for (int i = 0; i < 2; i++)
            {
                var loading = NewSignal();
                EventHandler<WebViewLoadingEventArgs> onLoading = (_, e) =>
                {
                    if (e.IsMainFrame) loading.TrySetResult(e.Cancel);
                };
                await RhinoDispatch.InvokeAsync(() => { map!.DocumentLoading += onLoading; map.Reload(); return true; });
                try
                {
                    if (!await loading.Task.WaitAsync(TimeSpan.FromSeconds(10)))
                        throw new Exception("Native Reload can replace the embedded map");
                    if (await Script("return document.getElementById('overlay').innerHTML;") != saved
                        || scripts.Count != initializationCount)
                        throw new Exception("Reload changes map selection or reinitializes its state");
                }
                finally { await RhinoDispatch.InvokeAsync(() => { map!.DocumentLoading -= onLoading; return true; }); }
            }
            await RhinoDispatch.InvokeAsync(() =>
            {
                applyKey.Invoke(panel, new object[] { false, "fixed KEY removal" });
                if (Field<bool>("mapReady") || map!.Visible || Field<CancellationTokenSource?>("tileRequests") is not null)
                    throw new Exception("KEY removal retains an active map");
                ready = NewSignal();
                fetched = 0;
                applyKey.Invoke(panel, new object[] { true, "fixed KEY re-registration" });
                return true;
            });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (await Script("return typeof aha.center;") != "function" || scripts.Count <= initializationCount)
                throw new Exception("Browser navigation guard prevents host-initiated map reload");
        }
        finally { await RhinoDispatch.InvokeAsync(() => { panel?.Dispose(); return true; }); }

        async Task<string> Script(string source)
        {
            var task = await RhinoDispatch.InvokeAsync(() => map!.ExecuteScriptAsync(source));
            return await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
