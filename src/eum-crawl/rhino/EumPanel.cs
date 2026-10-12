using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Aha.Info;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace Aha.EumCrawl;

/// <summary>Standalone parcel lookup, or a read-only current/new comparison for one selected object.</summary>
[Guid("c3915adf-4f44-4af4-8d97-e5c194e92ef0")]
public sealed class EumPanel : Panel, IPanel
{
    private readonly RhinoDoc doc;
    private readonly Func<uint?> activeDocument;
    private readonly EumWorkflow workflow;
    private readonly UITimer timer = new() { Interval = 0.4 };
    private readonly Label target = Label();
    private readonly Label status = Label();
    private readonly TextBox pnu = new() { PlaceholderText = "19자리 PNU" };
    private readonly Label queryAddress = Label();
    private readonly Label currentAddress = Label();
    private readonly Label currentPnu = Label();
    private readonly DropDown parcelSlot = new() { Visible = false };
    private readonly Button search = new() { Text = "검색" };
    private readonly Button original = new() { Text = "토지이음 열람", Enabled = false };
    private readonly Button apply = new() { Text = "선택 항목 반영", Enabled = false };
    private readonly DynamicLayout workspace = new() { Spacing = new Size(10, 9) };
    private readonly TableLayout identityTable = Grid();
    private readonly TableLayout resultTable = Grid();
    private readonly List<Control> valueCells = new();
    private readonly List<Label> fullWidthLabels = new();
    private readonly Dictionary<string, Label> currentValues = new();
    private readonly Dictionary<string, Label> resultValues = new();
    private readonly Dictionary<string, CheckBox> choices = new();
    private readonly TextArea details = new() { ReadOnly = true, Wrap = true, Height = 170 };
    private readonly Expander reference;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? pending;
    private JsonObject currentData = new();
    private JsonObject? preview;
    private Guid objectId;
    private string observedRaw = "";
    private int selectionCount = -1, parcelIndex, generation, fittedClientWidth = -1;
    private bool refreshing, closed, busy, contextAvailable, writable;
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["parcels.land_category"] = "지목", ["parcels.area_m2"] = "면적 (㎡)",
        ["parcels.land_use_zones"] = "용도지역·지구", ["bcr_percent"] = "건폐율 (%)", ["far_percent"] = "용적률 (%)",
        ["setback_m"] = "이격거리 (m)", ["max_height_m"] = "최고높이 (m)", ["max_floors"] = "최고층수", ["notes"] = "메모"
    };

    public static void Open(RhinoDoc doc) => Aha.Runtime.ToolPanels.Open(doc, typeof(EumPanel));

    public EumPanel(uint documentSerialNumber) : this(documentSerialNumber, EumService.Workflow) { }

    internal EumPanel(uint documentSerialNumber, EumWorkflow workflow, Func<uint?>? activeDocument = null)
    {
        this.workflow = workflow;
        this.activeDocument = activeDocument ?? (() => RhinoDoc.ActiveDoc?.RuntimeSerialNumber);
        doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber)
            ?? throw new InvalidOperationException("대상 Rhino 문서가 없습니다.");
        // Rhino's global Label style right-aligns labels; a named local style runs after it.
        Styles.Add<Label>("aha-eum-left", label =>
        {
            label.TextAlignment = TextAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;
        });
        var layout = new DynamicLayout { Padding = 12, Spacing = new Size(6, 8) };
        var heading = Label("EumCrawl"); heading.Font = new Font(SystemFont.Bold, 15);
        fullWidthLabels.Add(heading); fullWidthLabels.Add(target); fullWidthLabels.Add(status);
        layout.AddRow(heading);
        layout.AddRow(target);
        workspace.AddRow(parcelSlot);
        var identityChoice = Choice("parcels.identity", "주소와 PNU를 함께 반영");
        AddGridRow(identityTable, "항목", Label("기존"), Label("신규"), Label("반영"), header: true);
        AddGridRow(identityTable, "주소", currentAddress, queryAddress, identityChoice);
        currentPnu.Wrap = WrapMode.Character;
        AddGridRow(identityTable, "PNU", currentPnu, pnu, new Panel());
        workspace.AddRow(identityTable);

        var actions = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center };
        actions.Items.Add(search); actions.Items.Add(original);
        var hint = Label("PNU로 검색 · 주소는 자동 조회");
        fullWidthLabels.Add(hint);
        var actionBand = new DynamicLayout { Padding = new Padding(0, 6), Spacing = new Size(6, 4) };
        actionBand.AddRow(actions); actionBand.AddRow(hint);
        var ratioNotice = Label(EumParser.RatioPolicyNotice);
        fullWidthLabels.Add(ratioNotice);
        actionBand.AddRow(ratioNotice);
        workspace.AddRow(actionBand);

        foreach (var field in Labels)
        {
            var before = Label(); var after = Label("미조회");
            currentValues.Add(field.Key, before); resultValues.Add(field.Key, after);
            AddGridRow(resultTable, field.Value, before, after, Choice(field.Key, field.Value + " 반영"));
        }
        workspace.AddRow(resultTable);
        workspace.AddRow(apply);
        reference = new Expander { Header = "조회 참고", Content = details, Expanded = false, Visible = false };
        workspace.AddRow(reference);
        layout.AddRow(workspace);
        layout.AddRow(status);
        var attribution = new LinkButton { Text = "출처: 국토교통부 · 토지이음", ToolTip = "https://www.eum.go.kr/" };
        attribution.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.eum.go.kr/") { UseShellExecute = true }); }
            catch { status.Text = "기본 브라우저를 열지 못했습니다. https://www.eum.go.kr/에서 토지이음을 확인하세요."; }
        };
        layout.AddRow(attribution);
        layout.AddRow(null);
        var scroll = new Scrollable { Content = layout, Border = BorderType.None };
        Content = scroll; Size = new Size(520, 740);
        scroll.SizeChanged += (_, _) => Fit(scroll, layout);
        Load += (_, _) => { Poll(); Fit(scroll, layout); timer.Start(); };
        UnLoad += (_, _) => { timer.Stop(); CancelPending(); };
        timer.Elapsed += (_, _) => Poll();
        pnu.TextChanged += (_, _) =>
        {
            if (refreshing) return;
            ClearPreview(); queryAddress.Text = "검색 전";
            status.Text = "검색할 PNU를 입력한 뒤 검색을 누르세요.";
        };
        parcelSlot.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing || parcelSlot.SelectedIndex < 0) return;
            parcelIndex = parcelSlot.SelectedIndex; LoadSlot();
        };
        search.Click += async (_, _) => await Search();
        original.Click += (_, _) => Run(() =>
        {
            Poll();
            if (preview is null) return;
            var resultPnu = preview["parcels"]![0]!["pnu"]!.GetValue<string>();
            Process.Start(new ProcessStartInfo(EumClient.ParcelUrl(resultPnu)) { UseShellExecute = true });
        });
        apply.Click += (_, _) => Run(Apply);
        Poll();
    }

    private static Label Label(string text = "") => new()
    {
        Text = text, Wrap = WrapMode.Word, TextAlignment = TextAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, Style = "aha-eum-left"
    };
    private static TableLayout Grid() => new() { Spacing = new Size(1, 1), BackgroundColor = SystemColors.DisabledText };
    private CheckBox Choice(string key, string tooltip)
    {
        var choice = new CheckBox { ToolTip = tooltip, Enabled = false };
        choice.CheckedChanged += (_, _) => UpdateControls();
        choices.Add(key, choice);
        return choice;
    }
    private static Panel Cell(Control content, int width = -1) => new()
    {
        Content = content, Padding = new Padding(6, 7), BackgroundColor = SystemColors.ControlBackground,
        Width = width
    };
    private void AddGridRow(TableLayout grid, string field, Control before, Control after, Control check, bool header = false)
    {
        var label = Label(field); label.Width = 64;
        if (header)
        {
            label.Font = new Font(SystemFont.Bold, 12);
            foreach (var text in new[] { before, after, check }.OfType<Label>()) text.Font = label.Font;
        }
        var left = Cell(before); var right = Cell(after);
        valueCells.Add(left); valueCells.Add(right);
        valueCells.Add(before); valueCells.Add(after);
        var mark = new StackLayout { Orientation = Orientation.Horizontal, HorizontalContentAlignment = HorizontalAlignment.Center };
        mark.Items.Add(check);
        grid.Rows.Add(new TableRow(new TableCell(Cell(label, 76)), new TableCell(left, true),
            new TableCell(right, true), new TableCell(Cell(mark, 38))));
    }
    private void Fit(Scrollable scroll, DynamicLayout layout)
    {
        fittedClientWidth = scroll.ClientSize.Width;
        var width = Math.Max(320, scroll.ClientSize.Width - 4);
        var inner = width - 24;
        var valueWidth = Math.Max(84, (inner - 76 - 38 - 3) / 2);
        layout.Width = width;
        foreach (var control in valueCells) control.Width = control is Panel ? valueWidth : valueWidth - 12;
        foreach (var label in fullWidthLabels) label.Width = inner;
        details.Width = inner;
    }
    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { Poll(); timer.Start(); }
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { timer.Stop(); CancelPending(); }
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { timer.Stop(); CancelPending(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            closed = true; timer.Stop(); timer.Dispose(); lifetime.Cancel(); ClearPreview(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Poll()
    {
        if (closed) return;
        // A vertical scrollbar can change the viewport without resizing the panel.
        if (Content is Scrollable viewport && viewport.Content is DynamicLayout content &&
            viewport.ClientSize.Width > 0 && viewport.ClientSize.Width != fittedClientWidth)
            Fit(viewport, content);
        try
        {
            var available = RhinoDoc.FromRuntimeSerialNumber(doc.RuntimeSerialNumber) == doc &&
                activeDocument() == doc.RuntimeSerialNumber;
            if (!available)
            {
                if (contextAvailable) ClearPreview();
                contextAvailable = false; selectionCount = -1; UpdateControls();
                status.Text = "EumCrawl을 연 문서로 돌아가세요. 대상 문서가 닫혔다면 새 문서에서 다시 여세요.";
                return;
            }
            contextAvailable = true;
            var selected = doc.Objects.GetSelectedObjects(false, false).Take(2).ToArray();
            var id = selected.Length == 1 ? selected[0].Id : Guid.Empty;
            var raw = id == Guid.Empty ? "" : selected[0].Attributes.GetUserString(InfoData.Key) ?? "";
            writable = selected.Length == 1 && InfoService.CanEdit(doc, selected[0]);
            if (selected.Length != selectionCount || id != objectId || raw != observedRaw)
            {
                ClearPreview(); selectionCount = selected.Length; objectId = id; observedRaw = raw; parcelIndex = 0;
                currentData = new JsonObject();
                if (id != Guid.Empty)
                {
                    var info = InfoService.Read(doc, id);
                    if (info is not null) { InfoData.Validate(info); currentData = (JsonObject)info["data"]!.DeepClone(); }
                }
                var parcels = currentData["parcels"] as JsonArray;
                refreshing = true;
                try
                {
                    parcelSlot.DataStore = parcels?.Select((p, i) => $"원필지 {i + 1} · {p?["address"] ?? p?["pnu"]}").ToArray() ?? Array.Empty<string>();
                    parcelSlot.SelectedIndex = parcels?.Count > 0 ? 0 : -1;
                }
                finally { refreshing = false; }
                parcelSlot.Visible = selectionCount == 1 && parcels?.Count > 1;
                target.Text = selectionCount switch
                {
                    0 => "단순 조회 · 객체를 선택하면 현재값과 비교하여 반영할 수 있습니다.",
                    1 => "선택 객체: " + (string.IsNullOrWhiteSpace(selected[0].Name) ? selected[0].ObjectType.ToString() : selected[0].Name),
                    _ => "객체가 여러 개 선택되어 있습니다. 선택을 해제하거나 한 개만 선택하세요."
                };
                LoadSlot();
                if (Content is Scrollable scroll && scroll.Content is DynamicLayout layout) Fit(scroll, layout);
            }
            UpdateControls();
        }
        catch (Exception error)
        {
            ClearPreview(); contextAvailable = false; workspace.Enabled = false; status.Text = error.Message;
            // Retry reading a corrected attribute instead of retaining a partially loaded target.
            observedRaw = "\0";
        }
    }

    private JsonNode? Parcel => currentData["parcels"] is JsonArray items && parcelIndex < items.Count ? items[parcelIndex] : null;
    private void LoadSlot()
    {
        ClearPreview();
        refreshing = true;
        try { pnu.Text = Parcel?["pnu"]?.GetValue<string>() ?? ""; }
        finally { refreshing = false; }
        queryAddress.Text = Parcel?["address"]?.GetValue<string>() ?? "주소 미확인";
        currentAddress.Text = Parcel?["address"]?.ToString() ?? "—";
        currentPnu.Text = Parcel?["pnu"]?.ToString() ?? "—";
        foreach (var field in currentValues)
            field.Value.Text = selectionCount == 1 ? Format(field.Key.StartsWith("parcels.", StringComparison.Ordinal) ? Parcel?[field.Key[8..]] : currentData[field.Key]) : "—";
        status.Text = "검색은 저장된 정보를 바꾸지 않습니다.";
        UpdateControls();
    }

    private async Task Search()
    {
        Poll();
        if (closed || busy || !contextAvailable || selectionCount > 1) return;
        ClearPreview();
        queryAddress.Text = "검색 중…";
        var version = generation;
        var targetId = objectId == Guid.Empty ? (Guid?)null : objectId;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pending = request; busy = true; UpdateControls(); status.Text = "토지이음 검색 중…";
        try
        {
            var result = await workflow.PreviewAsync(doc, targetId, pnu.Text, parcelIndex, request.Token);
            Poll();
            if (closed || version != generation) { workflow.Discard(result); return; }
            preview = result;
            queryAddress.Text = result["parcels"]![0]!["address"]!.GetValue<string>();
            foreach (var field in choices)
            {
                var value = result["fields"]?[field.Key];
                var known = value?["status"]?.GetValue<string>() == "candidate";
                field.Value.Checked = known;
                if (resultValues.TryGetValue(field.Key, out var display))
                    display.Text = known ? Format(value?["value"]) :
                        value?["status"]?.GetValue<string>() == "conditional" ? "조건부\n" + value?["text"] : "미확인";
            }
            details.Text = string.Join("\n", ((JsonArray)result["warnings"]!).Select(w => w?.ToString())) +
                "\n\n" + string.Join("\n\n", ((JsonArray)result["sources"]!).Select(s =>
                    $"{s?["title"]}\n기준일: {EmptyUnknown(s?["reference_date"])} · 조회: {s?["retrieved_at"]}\n{s?["text"]}"));
            reference.Visible = true;
            status.Text = targetId is null ? "검색 완료. 열람을 누르면 토지이음 페이지가 열립니다." :
                "검색 완료. 추출된 항목은 모두 체크되어 있습니다. 반영할 항목만 남겨두세요.";
        }
        catch (OperationCanceledException) { if (!closed && version == generation) { queryAddress.Text = "검색 취소"; status.Text = "검색이 취소되었습니다."; } }
        catch (Exception error) { if (!closed && version == generation) { queryAddress.Text = "검색 실패"; status.Text = "검색 실패 · " + error.Message; } }
        finally
        {
            if (ReferenceEquals(pending, request)) { pending = null; busy = false; }
            request.Dispose();
            if (!closed) UpdateControls();
        }
    }

    private void Apply()
    {
        Poll();
        if (preview is null || selectionCount != 1 || !contextAvailable || !writable)
            throw new InvalidOperationException("반영할 객체 한 개를 선택하고 다시 검색하세요.");
        workflow.Apply(doc, objectId, preview, choices.Where(c => c.Value.Checked == true).Select(c => c.Key));
        ClearPreview(); observedRaw = "\0"; Poll();
        status.Text = "선택한 항목을 반영했습니다. Rhino Undo로 되돌릴 수 있습니다.";
    }
    private void CancelPending()
    {
        if (pending is null) return;
        ClearPreview();
        status.Text = "검색이 취소되었습니다. 다시 검색하세요.";
    }
    private void ClearPreview()
    {
        generation++; pending?.Cancel(); pending = null; busy = false;
        workflow.Discard(preview); preview = null;
        foreach (var choice in choices.Values) { choice.Checked = false; choice.Enabled = false; }
        foreach (var value in resultValues.Values) value.Text = "미조회";
        details.Text = ""; reference.Visible = false; original.Enabled = apply.Enabled = false;
        UpdateControls();
    }
    private void UpdateControls()
    {
        workspace.Enabled = contextAvailable && selectionCount <= 1;
        search.Enabled = !busy; original.Enabled = !busy && preview is not null;
        foreach (var entry in choices)
        {
            entry.Value.Visible = true;
            entry.Value.Enabled = !busy && writable && preview?["fields"]?[entry.Key]?["status"]?.GetValue<string>() == "candidate";
        }
        apply.Visible = selectionCount == 1;
        apply.Enabled = !busy && writable && preview is not null && choices.Any(c => c.Value.Checked == true && c.Value.Enabled);
    }
    private static string EmptyUnknown(JsonNode? node) => string.IsNullOrWhiteSpace(node?.ToString()) ? "미확인" : node.ToString();
    private static string Format(JsonNode? node)
    {
        if (node is JsonObject value)
            return string.Join("\n", value.Select(p => (p.Key == "national_plan" ? "국토계획법" : p.Key == "other_laws" ? "다른 법령" : p.Key) + ": " + Format(p.Value)));
        return EmptyUnknown(node);
    }
    private void Run(Action action)
    {
        if (busy || closed) return;
        try { action(); } catch (Exception error) { status.Text = error.Message; }
    }
}
