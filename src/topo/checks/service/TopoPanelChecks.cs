using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Runtime;
using Eto.Forms;
using Rhino;
namespace Aha.Topo.Checks;
internal static class TopoPanelChecks
{
    internal static async Task<bool> RunAsync(RhinoDoc doc)
    {
        await MapNavigationChecks.RunAsync(doc);
        await CheckInputs(doc);
        foreach (string change in new[] { "generation", "request", "key", "dispose", "job-failure", "job-completion-failure", "current" })
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            int sent = 0;
            TopoPanel? panel = null;
            bool disposed = false;
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            T Field<T>(string n) => (T)typeof(TopoPanel).GetField(n, hidden)!.GetValue(panel)!;
            void Set(string n, object v) => typeof(TopoPanel).GetField(n, hidden)!.SetValue(panel, v);
            Task? work = null;
            try
            {
                work = await RhinoDispatch.InvokeAsync(() =>
                {
                    panel = new TopoPanel(doc.RuntimeSerialNumber, async (_, _, _, _, _) => { entered.SetResult(true); return await release.Task; }, _ => { sent++; return Task.CompletedTask; });
                    if (Field<CheckBox>("useNearRegion").Checked != new ModelOptions().UseNearRegion)
                        throw new Exception("Topo panel and service Near defaults differ");
                    if (Field<CheckBox>("contour").Checked != new ModelOptions().Contour
                        || Field<NumericStepper>("contourInterval").Value != new ModelOptions().ContourIntervalMetres
                        || Field<CheckBox>("separateFoundations").Checked != new ModelOptions().SeparateFoundations)
                        throw new Exception("Topo panel and service Contour/foundation defaults differ");
                    if (Field<CheckBox>("parapets").Checked != new ModelOptions().Parapets
                        || Field<NumericStepper>("parapetWidth").Value != new ModelOptions().ParapetWidthMetres * 1000
                        || Field<NumericStepper>("parapetHeight").Value != new ModelOptions().ParapetHeightMetres * 1000)
                        throw new Exception("Topo panel and service parapet defaults differ");
                    Set("keyAvailable", true); Set("mapReady", true);
                    return (Task)typeof(TopoPanel).GetMethod("LoadTile", hidden)!.Invoke(panel, new object[] { "fixture", 12, 1000, 1000, 0, 0, CancellationToken.None })!;
                });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await RhinoDispatch.InvokeAsync(() =>
                {
                    if (change == "generation") Set("mapGeneration", 1);
                    if (change == "request") Set("tileSequence", 1);
                    if (change == "key") Set("keyAvailable", false);
                    if (change == "dispose") { panel!.Dispose(); disposed = true; }
                    if (change == "job-failure")
                    {
                        var ready = new JsonObject { ["job_id"] = "fixed-ready-job", ["state"] = "ready",
                            ["progress"] = "준비 완료 · 생성 가능", ["summary"] = new JsonObject { ["prepared_object_count"] = 2 } };
                        typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] { ready });
                    }
                    if (change == "job-completion-failure")
                        typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] {
                            new JsonObject { ["state"] = "completed", ["result"] = JsonSerializer.SerializeToNode(new ModelResult { CreatedCount = 3 }) } });
                    return true;
                });
                if (change is "job-failure" or "job-completion-failure") release.SetException(new IOException("delayed map response"));
                else release.SetResult(new byte[] { 1, 2, 3 });
                await work;
                bool ok = await RhinoDispatch.InvokeAsync(() => change == "current"
                    ? sent == 1 && Field<Dictionary<string, string>>("tileCache").Count == 1
                    : sent == 0 && Field<Dictionary<string, string>>("tileCache").Count == 0
                        && (change != "job-failure" || Field<Label>("status").Text.Contains("객체 2개 준비")
                            && !Field<Label>("status").Text.Contains("배경지도"))
                        && (change != "job-completion-failure" || Field<Label>("status").Text.Contains("객체 3개")
                            && !Field<Label>("status").Text.Contains("배경지도")));
                if (!ok) throw new Exception("Topo map response state mismatch: " + change);
                if (change == "current")
                {
                    await RhinoDispatch.InvokeAsync(() =>
                    {
                        var result = new ModelResult { CreatedCount = 3, NearSolidCount = 2 };
                        string[] warnings = { "고정 입력: 도로 보정 미수렴", "고정 입력: 건물 fixture 제외 사유" };
                        result.Warnings.AddRange(warnings);
                        var report = typeof(TopoPanel).GetMethod("ReportGenerationResult", hidden)!;
                        report.Invoke(panel, new object[] { JsonSerializer.SerializeToNode(result)! });
                        string completion = Field<Label>("status").Text;
                        if (!completion.Contains("객체 3개") || !completion.Contains("근접 입체 2개")
                            || warnings.Any(warning => !completion.Contains(warning)))
                            throw new Exception("Topo panel omits completed-result counts or service warnings");
                        Set("closed", true);
                        try { report.Invoke(panel, new object[] { JsonSerializer.SerializeToNode(new ModelResult())! }); }
                        finally { Set("closed", false); }
                        if (Field<Label>("status").Text != completion)
                            throw new Exception("Late Topo result updates a closed panel");
                        report.Invoke(panel, new object[] { JsonSerializer.SerializeToNode(new ModelResult { AppliedSettings = new(UseNearRegion: false) })! });
                        if (warnings.Any(warning => Field<Label>("status").Text.Contains(warning))
                            || Field<Label>("status").Text.Contains("근접 입체"))
                            throw new Exception("Topo panel retains warnings or settings from an earlier result");
                        var near = Field<CheckBox>("useNearRegion");
                        var contour = Field<CheckBox>("contour");
                        var interval = Field<NumericStepper>("contourInterval");
                        contour.Checked = true;
                        near.Checked = false;
                        if (contour.Enabled || interval.Enabled) throw new Exception("Contour control is active without Near modelling");
                        var parapets = Field<CheckBox>("parapets");
                        var parapetWidth = Field<NumericStepper>("parapetWidth");
                        var parapetHeight = Field<NumericStepper>("parapetHeight");
                        if (parapets.Enabled || parapetWidth.Enabled || parapetHeight.Enabled)
                            throw new Exception("Parapet controls are active without Near");
                        near.Checked = true;
                        if (!contour.Enabled || !interval.Enabled || contour.Checked != true)
                            throw new Exception("Near toggle loses the user's Contour setting");
                        parapetWidth.Value = 250; parapetHeight.Value = 450;
                        parapets.Checked = false;
                        if (parapetWidth.Enabled || parapetHeight.Enabled) throw new Exception("Disabled parapets retain active dimensions");
                        parapets.Checked = true;
                        if (!parapetWidth.Enabled || !parapetHeight.Enabled || parapetWidth.Value != 250 || parapetHeight.Value != 450)
                            throw new Exception("Parapet toggle loses dimensions or activation state");
                        interval.Value = .5;
                        Field<CheckBox>("separateFoundations").Checked = true;
                        ModelOptions ReadOptions() => (ModelOptions)typeof(TopoPanel).GetMethod("ReadOptions", hidden)!.Invoke(panel, null)!;
                        var withContour = ReadOptions();
                        if (!withContour.Contour || withContour.ContourIntervalMetres != .5 || !withContour.SeparateFoundations)
                            throw new Exception("Topo panel does not pass its Contour/foundation settings to the common model options");
                        if (!withContour.Parapets || withContour.ParapetWidthMetres != .25 || withContour.ParapetHeightMetres != .45)
                            throw new Exception("Topo panel does not convert parapet millimetres to common metres");
                        near.Checked = false;
                        var withoutNear = ReadOptions();
                        if (withoutNear.Contour || !withoutNear.SeparateFoundations)
                            throw new Exception("Topo panel passes inactive Contour or drops independent foundation settings");
                        near.Checked = true;
                        contour.Checked = false;
                        if (interval.Enabled) throw new Exception("Unused Contour interval remains active");
                        return true;
                    });
                    bool sawInput = false;
                    var task = await RhinoDispatch.InvokeAsync(() =>
                    {
                        Field<TextBox>("address").Text = "input before action";
                        Field<NumericStepper>("nearDepth").Value = 7.5;
                        Func<CancellationToken, Task> action = _ => { sawInput = Field<TextBox>("address").Text == "input before action" && Field<NumericStepper>("nearDepth").Value == 7.5 && Field<bool>("busy") && !Field<CheckBox>("contour").Enabled && !Field<CheckBox>("separateFoundations").Enabled && !Field<CheckBox>("parapets").Enabled && !Field<NumericStepper>("parapetWidth").Enabled && !Field<NumericStepper>("parapetHeight").Enabled; return Task.CompletedTask; };
                        return (Task)typeof(TopoPanel).GetMethod("Run", hidden)!.Invoke(panel, new object[] { action })!;
                    });
                    await task;
                    if (!sawInput || await RhinoDispatch.InvokeAsync(() => Field<bool>("busy"))) throw new Exception("Topo action loses input or busy state");
                    await RhinoDispatch.InvokeAsync(() =>
                    {
                        var snapshot = new JsonObject
                        {
                            ["job_id"] = "fixed-panel-job", ["document_id"] = doc.RuntimeSerialNumber,
                            ["operation"] = "topo", ["state"] = "running", ["progress"] = "고정 입력 연산 중",
                            ["input"] = new JsonObject
                            {
                                ["mode"] = "model", ["pnus"] = new JsonArray("9900000000100030000"),
                                ["width_m"] = 500, ["depth_m"] = 600, ["terrain_level"] = 12,
                                ["options"] = JsonSerializer.SerializeToNode(new ModelOptions(Contour: true, NearDepthMetres: 45))
                            }
                        };
                        var apply = typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!;
                        void Apply() => apply.Invoke(panel, new object?[] { snapshot });
                        Apply();
                        if (Field<Button>("calculate").Enabled || Field<Button>("generate").Enabled
                            || !Field<Button>("cancel").Enabled || Field<ModelRangeInput>("width").Enabled
                            || Field<DynamicLayout>("modelControls").Visible || Field<WebView>("map").Visible)
                            throw new Exception("Running Topo job does not lock its input and separate creation");
                        string input = Field<Label>("jobSummary").Text;
                        if (!input.Contains("9900000000100030000") || !input.Contains("500 × 600")
                            || !input.Contains("45 m") || !input.Contains("Contour 1 m") || !input.Contains("파라펫 ON · 폭 300 mm · 높이 300 mm"))
                            throw new Exception("MCP-started Topo job omits its fixed input summary");
                        snapshot["state"] = "ready";
                        string[] readyNotices = { "평면 상세 기준 100mm", "생략 면적 12.34m²" };
                        snapshot["summary"] = new JsonObject
                        {
                            ["prepared_object_count"] = 7,
                            ["warnings"] = new JsonArray(null, "", readyNotices[0], "   ", readyNotices[1])
                        };
                        Set("keyAvailable", false);
                        Field<DynamicLayout>("modelControls").Enabled = false;
                        Apply();
                        if (!Field<Button>("generate").Enabled || Field<Button>("calculate").Enabled
                            || Field<Button>("cancel").Text != "준비 결과 폐기"
                            || !Field<Label>("status").Text.Contains("객체 7개 준비")
                            || !Field<Label>("status").Text.Contains("생성 버튼"))
                            throw new Exception("Prepared Topo cannot be created without a KEY or omits its summary");
                        CheckNotices(readyNotices);
                        for (Control? ancestor = Field<Button>("generate"); ancestor is not null; ancestor = ancestor.Parent)
                            if (!ancestor.Enabled) throw new Exception("Prepared creation is inside a disabled KEY-only form");
                        snapshot["state"] = "creating"; Apply();
                        if (Field<Button>("generate").Enabled || Field<Button>("cancel").Enabled)
                            throw new Exception("Creating Topo can be cancelled or submitted twice from the panel");
                        snapshot["state"] = "completed";
                        string[] completedNotices = { "도로 단차 200mm", "생략 면적 2.5m²" };
                        var completedResult = new ModelResult { CreatedCount = 3 };
                        completedResult.Warnings.AddRange(completedNotices);
                        snapshot["result"] = JsonSerializer.SerializeToNode(completedResult);
                        Apply();
                        if (Field<Button>("generate").Enabled || Field<Button>("cancel").Enabled
                            || !Field<DynamicLayout>("modelControls").Visible || Field<Label>("jobSummary").Visible
                            || !Field<Label>("status").Text.Contains("객체 3개")
                            || Field<Label>("status").Text.Contains("객체 7개 준비")
                            || readyNotices.Any(n => Field<Label>("status").Text.Contains(n)))
                            throw new Exception("Completed Topo does not restore panel inputs and retire creation");
                        CheckNotices(completedNotices);
                        foreach (var empty in new JsonArray?[] { null, new(), new(null, "", "   ") })
                        {
                            snapshot["state"] = "ready";
                            snapshot["summary"]!["warnings"] = empty?.DeepClone();
                            Apply(); CheckNotices(Array.Empty<string>());
                            if (!Field<Label>("status").Text.Contains("객체 7개 준비"))
                                throw new Exception("An empty notice list removes the preparation summary");
                            snapshot["state"] = "completed";
                            snapshot["result"]!["Warnings"] = empty?.DeepClone();
                            Apply(); CheckNotices(Array.Empty<string>());
                            if (!Field<Label>("status").Text.Contains("객체 3개"))
                                throw new Exception("An empty notice list removes the completion summary");
                        }
                        Set("keyAvailable", true);
                        apply.Invoke(panel, new object?[] { null });
                        return true;

                        void CheckNotices(string[] expected)
                        {
                            var lines = Field<Label>("status").Text.Split('\n');
                            if (lines.Count(line => line == "안내") != (expected.Length > 0 ? 1 : 0)
                                || !lines.Where(line => line.StartsWith("· ", StringComparison.Ordinal))
                                    .SequenceEqual(expected.Select(notice => "· " + notice)))
                                throw new Exception("Topo panel changes, duplicates or retains notice text/order");
                        }
                    });
                }
            }
            finally
            {
                release.TrySetResult(new byte[] { 1 }); if (work is not null) await work;
                await RhinoDispatch.InvokeAsync(() => { if (!disposed) panel?.Dispose(); return true; });
            }
        }
        await CheckSharedJobLifetime(doc);
        return true;
    }

    private static async Task CheckInputs(RhinoDoc doc)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        await RhinoDispatch.InvokeAsync(() =>
        {
            using var panel = new TopoPanel(doc.RuntimeSerialNumber, (_, _, _, _, _) => Task.FromResult(Array.Empty<byte>()), _ => Task.CompletedTask);
            T Field<T>(string name) => (T)typeof(TopoPanel).GetField(name, hidden)!.GetValue(panel)!;
            var input = Field<ModelRangeInput>("width");
            var text = (TextBox)typeof(ModelRangeInput).GetField("text", hidden)!.GetValue(input)!;
            foreach (var example in new[] { (421d, true, 500d), (421d, false, 400d), (400d, true, 500d),
                (400d, false, 300d), (100d, false, 50d), (50d, false, 50d), (3000d, true, 3000d), (50d, true, 100d) })
            {
                text.Text = example.Item1.ToString(System.Globalization.CultureInfo.CurrentCulture);
                input.Step(example.Item2);
                if (input.Value != example.Item3) throw new Exception("Directional extent snapping mismatch");
            }
            text.Text = 421.3.ToString(System.Globalization.CultureInfo.CurrentCulture); input.Commit();
            if (input.Value != 421.3) throw new Exception("Typed extent is snapped to a multiple");
            text.Text = "invalid"; input.Step(true);
            bool rejected = false;
            try { input.Commit(); } catch (ArgumentException) { rejected = true; }
            if (!rejected || text.Text != "invalid") throw new Exception("Invalid extent silently reuses a previous value");
            input.Value = 400;
            var advanced = Field<Expander>("advanced");
            if (advanced.Expanded) throw new Exception("Advanced settings are initially expanded");
            var floor = Field<NumericStepper>("floorHeight"); floor.Value = 4;
            advanced.Expanded = true; advanced.Expanded = false;
            if (floor.Value != 4) throw new Exception("Collapsing settings changes values");
            typeof(TopoPanel).GetField("keyAvailable", hidden)!.SetValue(panel, true);
            var origin = new GeoPoint(127, 37.5); var projection = new LocalProjection(origin);
            var parcel = new GeoFeature { Id = "synthetic", Polygons = new() { new() { new() {
                projection.ToGeo(-5,-5), projection.ToGeo(5,-5), projection.ToGeo(5,5), projection.ToGeo(-5,5), projection.ToGeo(-5,-5) } } } };
            typeof(TopoPanel).GetField("origin", hidden)!.SetValue(panel, origin);
            typeof(TopoPanel).GetField("target", hidden)!.SetValue(panel, SiteSelection.Create(new[] { parcel }, projection));
            void Refresh() => ((Task)typeof(TopoPanel).GetMethod("RefreshOverlay", hidden)!.Invoke(panel,null)!).GetAwaiter().GetResult();
            var near = Field<CheckBox>("useNearRegion"); near.Checked = false;
            input.Value = 2921; Field<ModelRangeInput>("depth").Value = 2000; Field<DropDown>("level").SelectedIndex = 3; Refresh();
            if (Field<Button>("calculate").Enabled || !Field<Label>("status").Text.Contains("타일"))
                throw new Exception("Terrain request limit does not disable calculation outside collapsed settings");
            Field<DropDown>("level").SelectedIndex = 0; Refresh();
            if (!Field<Button>("calculate").Enabled || Field<Label>("status").Text != "연산 가능")
                throw new Exception("Valid terrain request does not restore calculation");
            var apply = typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!;
            typeof(TopoPanel).GetField("mapReady", hidden)!.SetValue(panel, true);
            foreach (string terminal in new[] { "failed", "cancelled" })
            {
                apply.Invoke(panel, new object?[] { new JsonObject { ["state"] = "running" } });
                apply.Invoke(panel, new object?[] { new JsonObject { ["state"] = terminal, ["error"] = "고정 실패 사유", ["progress"] = "사용자 요청 취소" } });
                string reason = Field<Label>("status").Text;
                Refresh();
                if (Field<Label>("status").Text != reason || !reason.Contains(terminal == "failed" ? "고정 실패 사유" : "사용자 요청 취소"))
                    throw new Exception("Map restoration replaces the actual termination reason");
            }
            typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] {
                new JsonObject { ["state"] = "completed", ["result"] = JsonSerializer.SerializeToNode(new ModelResult { CreatedCount = 3 }) } });
            string completion = Field<Label>("status").Text;
            Field<DropDown>("level").SelectedIndex = 3; Refresh();
            if (Field<Button>("calculate").Enabled || Field<Label>("status").Text != completion
                || !Field<Label>("inputIssue").Visible || !Field<Label>("inputIssue").Text.Contains("타일"))
                throw new Exception("Input issue hides the completed summary or its own rejection reason");
            foreach (string terminal in new[] { "completed", "failed", "cancelled" })
            {
                var state = new JsonObject { ["state"] = terminal, ["error"] = "고정 실패 사유", ["progress"] = "사용자 요청 취소",
                    ["result"] = JsonSerializer.SerializeToNode(new ModelResult { CreatedCount = 3 }) };
                apply.Invoke(panel, new object?[] { state });
                apply.Invoke(panel, new object?[] { state });
                if (Field<Button>("calculate").Enabled || !Field<Label>("inputIssue").Visible
                    || !Field<Label>("inputIssue").Text.Contains("타일"))
                    throw new Exception("Repeated terminal state erases the current input error: " + terminal);
            }
            apply.Invoke(panel, new object?[] { new JsonObject { ["state"] = "completed", ["result"] = JsonSerializer.SerializeToNode(new ModelResult { CreatedCount = 3 }) } });
            Field<DropDown>("level").SelectedIndex = 0; Refresh();
            if (!Field<Button>("calculate").Enabled || Field<Label>("status").Text != completion || Field<Label>("inputIssue").Visible)
                throw new Exception("Resolved input issue replaces the completed summary or remains visible");
            typeof(TopoPanel).GetMethod("ApplyJobStatus", hidden)!.Invoke(panel, new object?[] { null });
            text.Text = "invalid"; Refresh(); Field<DropDown>("level").SelectedIndex = 1; Refresh();
            if (Field<Button>("calculate").Enabled) throw new Exception("Resolving a terrain limit clears an independent range error");
            input.Value = 400; near.Checked = true; Refresh();
            if (Field<Label>("terrainFormatTitle").Text != "원경 지형·도로 형식"
                || Field<Label>("buildingFormatTitle").Text != "원경 건물 형식") throw new Exception("Near format ownership labels are incorrect");
            return true;
        });
    }

    internal static Task<bool> CreatePrepared(RhinoDoc doc) => RhinoDispatch.InvokeAsync(() =>
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        using var panel = new TopoPanel(doc.RuntimeSerialNumber,
            (_, _, _, _, _) => Task.FromResult(Array.Empty<byte>()), _ => Task.CompletedTask);
        typeof(TopoPanel).GetField("keyAvailable", hidden)!.SetValue(panel, false);
        typeof(TopoPanel).GetMethod("ActivateJobStatus", hidden)!.Invoke(panel, null);
        var button = (Button)typeof(TopoPanel).GetField("generate", hidden)!.GetValue(panel)!;
        if (!button.Enabled) throw new Exception("Ready geometry cannot be created by the KEY-less panel");
        // Raise the actual button event; the service check verifies the resulting
        // geometry, one-time commit and Undo in its isolated document.
        typeof(Button).GetMethod("OnClick", hidden)!.Invoke(button, new object[] { EventArgs.Empty });
        return true;
    });

    private static async Task CheckSharedJobLifetime(RhinoDoc doc)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var release = new TaskCompletionSource<PreparedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new PanelPreparedResult();
        var request = Jobs.StartPrepared(doc.RuntimeSerialNumber, "topo", async (ct, _) => await release.Task.WaitAsync(ct),
            new JsonObject { ["mode"] = "xy", ["pnus"] = new JsonArray("9900000000100030000"), ["width_m"] = 100, ["depth_m"] = 100 });
        string id = request["job_id"]!.GetValue<string>();
        TopoPanel? panel = null;
        void Activate() => typeof(TopoPanel).GetMethod("ActivateJobStatus", hidden)!.Invoke(panel, null);
        Button Button(string name) => (Button)typeof(TopoPanel).GetField(name, hidden)!.GetValue(panel)!;
        try
        {
            await RhinoDispatch.InvokeAsync(() =>
            {
                panel = new TopoPanel(doc.RuntimeSerialNumber, (_, _, _, _, _) => Task.FromResult(Array.Empty<byte>()), _ => Task.CompletedTask);
                Activate();
                if (!Button("cancel").Enabled || Button("generate").Enabled)
                    throw new Exception("Panel does not discover an external running Topo job");
                typeof(TopoPanel).GetMethod("DeactivateJobStatus", hidden)!.Invoke(panel, null);
                panel.Dispose(); panel = null;
                return true;
            });
            if (Jobs.Status(id)["state"]!.GetValue<string>() != "running")
                throw new Exception("Panel hiding or disposal cancels the shared Topo job");
            release.SetResult(prepared);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Jobs.Status(id)["state"]!.GetValue<string>() == "running") await Task.Delay(10, timeout.Token);
            if (Jobs.Status(id)["state"]!.GetValue<string>() != "ready" || prepared.Disposed)
                throw new Exception("Hidden Topo preparation did not retain its prepared result");
            await RhinoDispatch.InvokeAsync(() =>
            {
                panel = new TopoPanel(doc.RuntimeSerialNumber, (_, _, _, _, _) => Task.FromResult(Array.Empty<byte>()), _ => Task.CompletedTask);
                Activate();
                if (!Button("generate").Enabled || Button("cancel").Text != "준비 결과 폐기" || Button("calculate").Enabled)
                    throw new Exception("Reopened KEY-less panel does not restore external ready work");
                typeof(TopoPanel).GetMethod("CancelCurrent", hidden)!.Invoke(panel, null);
                return true;
            });
            if (Jobs.Status(id)["state"]!.GetValue<string>() != "cancelled" || !prepared.Disposed)
                throw new Exception("Panel discard does not release the common prepared result");
            await RhinoDispatch.InvokeAsync(() =>
            {
                ((Task)typeof(TopoPanel).GetMethod("RefreshOverlay", hidden)!.Invoke(panel, null)!).GetAwaiter().GetResult();
                // Reattaching the panel and another tool's notification both
                // refresh the retained terminal Topo status.
                typeof(TopoPanel).GetMethod("DeactivateJobStatus", hidden)!.Invoke(panel, null);
                Activate();
                CheckInputIssue();
                return true;
            });
            string other = Jobs.Start(doc.RuntimeSerialNumber, "panel-notification-check",
                (_, _) => Task.FromResult<JsonNode?>(new JsonObject()))["job_id"]!.GetValue<string>();
            while (Jobs.Status(other)["state"]!.GetValue<string>() == "running") await Task.Delay(10, timeout.Token);
            await RhinoDispatch.InvokeAsync(() => { CheckInputIssue(); return true; });

            void CheckInputIssue()
            {
                var issue = (Label)typeof(TopoPanel).GetField("inputIssue", hidden)!.GetValue(panel)!;
                if (!issue.Visible || issue.Text != "대지 미선택" || Button("calculate").Enabled)
                    throw new Exception("Panel reattachment or another job notification erases the current input issue");
            }
        }
        finally
        {
            release.TrySetResult(prepared);
            Jobs.Cancel(id);
            await RhinoDispatch.InvokeAsync(() => { panel?.Dispose(); return true; });
        }
    }

    private sealed class PanelPreparedResult : PreparedJob
    {
        public bool Disposed { get; private set; }
        public override JsonNode Summary => new JsonObject { ["prepared_object_count"] = 2, ["mode"] = "xy" };
        public override Task<JsonNode?> CreateAsync() => throw new InvalidOperationException("This panel lifetime check must not create a model.");
        public override void Dispose() => Disposed = true;
    }
}
