using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Aha.Runtime;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace Aha.Topo;

// The panel observes the shared Topo job; prepared geometry is not panel-owned.
[Guid("3d7443ba-6647-4aef-8337-3aa0b5558d70")]
public sealed class TopoPanel : Panel, IPanel
{
    private readonly RhinoDoc doc;
    private readonly uint documentSerialNumber;
    private readonly Button settings = new() { Text = "Setting" };
    private readonly Label keyStatus = new() { Text = "VWorld KEY · 확인 중", Wrap = WrapMode.Word };
    private readonly Button issueKey = new() { Text = "V-WORLD 디지털트윈국토 · API 인증키 발급" };
    private readonly Label mapNotice = new() { Text = "VWorld KEY 등록 여부를 확인하고 있습니다.", Wrap = WrapMode.Word };
    private readonly DynamicLayout mapPlaceholder = new() { Spacing = new Size(8, 7) };
    private readonly DynamicLayout modelControls = new() { Spacing = new Size(8, 7), Enabled = false };
    private readonly TextBox address = new() { Text = "서울특별시 중구 세종대로 110" };
    private readonly ListBox candidates = new() { Height = 90, Visible = false };
    private readonly StackLayout selectedParcels = new() { Orientation = Orientation.Vertical, Spacing = 4, Visible = false };
    private readonly WebView map = new() { Height = 300, Visible = false, BrowserContextMenuEnabled = false };
    private readonly ModelRangeInput width = new(400);
    private readonly ModelRangeInput depth = new(400);
    private readonly CheckBox useNearRegion = new() { Checked = true };
    private readonly CheckBox contour = new() { Checked = false };
    private readonly NumericStepper contourInterval = Number(1, .001, 10000, .1, 3);
    private readonly CheckBox separateFoundations = new() { Checked = false };
    private readonly CheckBox parapets = new() { Checked = true };
    private readonly NumericStepper parapetWidth = Number(300, .001, double.MaxValue, 50, 3);
    private readonly NumericStepper parapetHeight = Number(300, .001, double.MaxValue, 50, 3);
    private readonly NumericStepper nearRatio = Number(2, 1.5, 100, .5);
    private readonly NumericStepper nearDepth = Number(30, .1, 3000, 5);
    private readonly DynamicLayout nearSettings = new() { Visible = false, Spacing = new Size(8, 7) };
    private readonly Label terrainFormatTitle = new();
    private readonly Label buildingFormatTitle = new();
    private readonly DynamicLayout advancedSettings = new() { Spacing = new Size(8, 7) };
    private readonly Expander advanced = new() { Header = "고급 설정", Expanded = false, Padding = new Padding(0) };
    private readonly NumericStepper unknownHeight = Number(6, 0.1, 500, 1);
    private readonly NumericStepper floorHeight = Number(3, 0.1, 20, 0.1);
    private readonly NumericStepper minArea = Number(10, 0, 10000, 5);
    private readonly DropDown terrainFormat = new() { DataStore = new[] { "Mesh", "NURBS" }, SelectedIndex = 0 };
    private readonly DropDown buildingFormat = new() { DataStore = new[] { "Mesh", "NURBS" }, SelectedIndex = 0 };
    private readonly CheckBox separateRoads = new() { Checked = true };
    private readonly CheckBox lowerRoads = new() { Checked = true };
    private readonly NumericStepper roadDrop = Number(200, 0, 10000, 50, 0);
    private readonly DropDown level = new() { DataStore = new[] { "12 · 약 15 m 표본", "13 · 약 8 m 표본", "14 · 약 4 m 표본", "15 · 약 2 m 표본" }, SelectedIndex = 0 };
    private readonly Label selection = new() { Text = "주소를 검색하거나 지도에서 필지를 클릭해 추가하세요. 다시 클릭하면 해제합니다.", Wrap = WrapMode.Word };
    private readonly Label status = new() { Text = "대지 미선택", Wrap = WrapMode.Word };
    private readonly Label inputIssue = new() { Wrap = WrapMode.Word, Visible = false };
    private readonly Label terrainSize = new() { Text = "대지 미선택", Wrap = WrapMode.Word, Font = new Font(SystemFont.Default, 10) };
    private readonly CheckBox showRoads = new() { Checked = true };
    private readonly Button search = new() { Text = "주소 검색" };
    private readonly Button clearParcels = new() { Text = "전체 해제", Enabled = false, Visible = false };
    private readonly Button calculate = new() { Text = "연산", Enabled = false };
    private readonly Button generate = new() { Text = "생성", Enabled = false };
    private readonly Button cancel = new() { Text = "취소", Enabled = false };
    private readonly Label jobSummary = new() { Wrap = WrapMode.Word, Visible = false };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim tileSlots = new(4);
    private readonly Dictionary<string, string> tileCache = new();
    private CancellationTokenSource? operation;
    private CancellationTokenSource? tileRequests;
    private List<AddressCandidate> found = new();
    private SiteSelection? target;
    private GeoPoint? origin;
    private bool closed;
    private bool mapReady;
    private bool busy;
    private bool validRange;
    private bool updatingRange;
    private int mapGeneration;
    private int tileSequence;
    private readonly Func<string, int, int, int, CancellationToken, Task<byte[]>> fetchTile;
    private readonly Func<string, Task>? sendMap;
    private readonly Func<string> mapKey;
    private bool keyStatusAttached;
    private int keyStatusGeneration;
    private bool keyAvailable;
    private long mapKeyRevision = -1;
    private bool jobStatusAttached;
    private string? observedJobId;
    private JsonObject? jobState;
    private bool JobLocked => jobState?["state"]?.GetValue<string>() is "running" or "ready" or "creating";

    public static void Open(RhinoDoc doc) => Aha.Runtime.ToolPanels.Open(doc, typeof(TopoPanel));

    public TopoPanel(uint documentSerialNumber) : this(documentSerialNumber, FetchTileAsync, null) { }

    internal TopoPanel(uint documentSerialNumber, Func<string, int, int, int, CancellationToken, Task<byte[]>> fetchTile,
        Func<string, Task>? sendMap, Func<string>? mapKey = null)
    {
        this.fetchTile = fetchTile; this.sendMap = sendMap;
        this.mapKey = mapKey ?? Aha.Setting.AhaSettings.GetVWorldKey;
        this.documentSerialNumber = documentSerialNumber;
        doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber)
            ?? throw new InvalidOperationException("대상 Rhino 문서가 없습니다.");
        var layout = new DynamicLayout { Padding = new Padding(14), Spacing = new Size(8, 7) };
        var section = layout;
        var fullWidthControls = new List<Control>();
        StackLayout Row(params Control[] controls)
        {
            var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var control in controls)
            {
                if (control is Label label) label.TextAlignment = TextAlignment.Left;
                row.Items.Add(new StackLayoutItem(control, control is TextBox || control is PasswordBox || control == calculate || control == generate || control == selection || control == keyStatus));
            }
            return row;
        }
        void Add(params Control[] controls)
        {
            foreach (var label in controls.OfType<Label>()) label.TextAlignment = TextAlignment.Left;
            if (controls.Length == 1)
            {
                if (controls[0] is Label label) label.Wrap = WrapMode.Word;
                fullWidthControls.Add(controls[0]);
                section.AddRow(controls[0]);
                return;
            }
            section.AddRow(Row(controls));
        }
        Add(new Label { Text = "AHA  /  TOPO", Font = new Font(SystemFont.Bold, 16) });
        Add(keyStatus, settings);
        settings.Click += (_, _) => Aha.Setting.SettingPanel.Open(doc);
        issueKey.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.vworld.kr/dev/v4apiRefer.do") { UseShellExecute = true }); }
            catch { status.Text = "기본 브라우저를 열지 못했습니다. V-WORLD 디지털트윈국토에서 API 인증키를 발급받으세요."; }
        };
        Add(address, search);
        Add(candidates);
        mapPlaceholder.AddRow(mapNotice);
        mapPlaceholder.AddRow(issueKey);
        fullWidthControls.Add(mapNotice);
        fullWidthControls.Add(issueKey);
        Add(mapPlaceholder);
        Add(map);
        Add(modelControls);
        section = modelControls;
        Add(new Label { Text = "지도: 드래그 이동 · 휠 또는 +/− 확대·축소 · 클릭 필지 추가/해제  |  출처: 국토교통부 · VWorld", Font = new Font(SystemFont.Default, 10) });
        Add(selection, clearParcels);
        Add(selectedParcels);
        TableLayout SettingRow(string title, Control input) => new()
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(new Label { Text = title, Width = 152,
                VerticalAlignment = VerticalAlignment.Center }, new TableCell(input, true)) }
        };
        // Keep the same title column in the basic and advanced sections.
        void Setting(string title, Control input) => section.AddRow(SettingRow(title, input));
        void NamedSetting(Label title, Control input)
        {
            title.Width = 152; title.VerticalAlignment = VerticalAlignment.Center;
            section.AddRow(new TableLayout { Spacing = new Size(8, 0),
                Rows = { new TableRow(title, new TableCell(input, true)) } });
        }
        Setting("가로 (m)", width);
        Setting("세로 (m)", depth);
        Setting("근접 영역", useNearRegion);
        Add(nearSettings);
        section = nearSettings;
        Setting("근접 반경 배수", nearRatio);
        Setting("입체 깊이 (m)", nearDepth);
        Setting("근접 건물 파라펫", parapets);
        Setting("파라펫 폭 (mm)", parapetWidth);
        Setting("파라펫 높이 (mm)", parapetHeight);
        Setting("Contour · 계단식 지형", contour);
        Setting("Contour 간격 (m)", contourInterval);
        section = modelControls;
        advanced.Content = advancedSettings;
        Add(advanced);
        section = advancedSettings;
        NamedSetting(terrainFormatTitle, terrainFormat);
        NamedSetting(buildingFormatTitle, buildingFormat);
        Setting("지형 표본 간격", level);
        Add(terrainSize);
        Add(new Label { Text = "도로", Font = new Font(SystemFont.Bold) });
        Setting("도로 표시", showRoads);
        Setting("땅과 도로 분리", separateRoads);
        Setting("도로 내리기", lowerRoads);
        Setting("내림값 (mm)", roadDrop);
        Add(new Label { Text = "건물", Font = new Font(SystemFont.Bold) });
        Setting("건물과 기초 분리", separateFoundations);
        Setting("층당 높이 (m)", floorHeight);
        Setting("기본 높이 (m)", unknownHeight);
        Setting("제외 면적 (m² 미만)", minArea);
        section = layout;
        Add(jobSummary);
        Add(status);
        Add(inputIssue);
        Add(calculate, generate, cancel);
        Add(new Label { Text = "\n주의\n아래 지형은 VWorld 고도 격자로 생성하며, 측량 자료가 아닙니다. 상세 레벨은 지역에 따라 제공되지 않을 수 있습니다.", Wrap = WrapMode.Word, Font = new Font(SystemFont.Default, 10) });
        var scroll = new Scrollable { Content = layout, Border = BorderType.None };
        Content = scroll;
        Size = new Size(440, 850);
        int previousContentWidth = -1;
        void FitContentWidth()
        {
            int viewportWidth = scroll.ClientSize.Width > 0 ? scroll.ClientSize.Width : ClientSize.Width;
            int contentWidth = Math.Max(1, viewportWidth - (layout.Padding?.Horizontal ?? 0));
            if (contentWidth == previousContentWidth) return;
            previousContentWidth = contentWidth;
            // A scrollable asks its content for an unconstrained preferred size.
            // Explicit viewport widths let long messages wrap instead of widening
            // the entire document when a validation error changes their text.
            layout.Width = viewportWidth;
            foreach (var control in fullWidthControls) control.Width = contentWidth;
            if (scroll.ScrollPosition.X != 0)
                scroll.ScrollPosition = new Point(0, scroll.ScrollPosition.Y);
        }
        scroll.SizeChanged += (_, _) => FitContentWidth();
        SizeChanged += (_, _) => FitContentWidth();
        FitContentWidth();
        search.Click += async (_, _) => await Run(Search);
        address.TextChanged += (_, _) => ClearSearchResults();
        address.KeyDown += async (_, e) => { if (e.Key == Keys.Enter && !busy) await Run(Search); };
        candidates.SelectedIndexChanged += async (_, _) =>
        {
            if (busy || candidates.SelectedIndex < 0 || candidates.SelectedIndex >= found.Count) return;
            var candidate = found[candidates.SelectedIndex];
            await Run(async ct =>
            {
                candidates.SelectedIndex = -1;
                using var client = Client();
                var parcel = await client.ParcelByPnuAsync(candidate.Pnu, ct);
                await Select(parcel, false, true, ct);
                ClearSearchResults();
            });
        };
        clearParcels.Click += async (_, _) => await Run(ct => SetSelection(Array.Empty<GeoFeature>(), false, ct));
        calculate.Click += (_, _) => StartPreparation();
        generate.Click += (_, _) => CreatePrepared();
        cancel.Click += (_, _) => CancelCurrent();
        width.ValueChanged += async (_, _) => { if (!updatingRange) await RefreshOverlay(); };
        depth.ValueChanged += async (_, _) => { if (!updatingRange) await RefreshOverlay(); };
        useNearRegion.CheckedChanged += async (_, _) => { RefreshNearControls(); await RefreshOverlay(); };
        contour.CheckedChanged += async (_, _) => { RefreshNearControls(); await RefreshOverlay(); };
        parapets.CheckedChanged += (_, _) => RefreshNearControls();
        contourInterval.ValueChanged += async (_, _) => await RefreshOverlay();
        nearRatio.ValueChanged += async (_, _) => await RefreshOverlay();
        nearDepth.ValueChanged += async (_, _) => await RefreshOverlay();
        showRoads.CheckedChanged += (_, _) => RefreshRoadControls();
        separateRoads.CheckedChanged += (_, _) => RefreshRoadControls();
        lowerRoads.CheckedChanged += (_, _) => RefreshRoadControls();
        level.SelectedIndexChanged += async (_, _) => await RefreshOverlay();
        foreach (var number in new[] { unknownHeight, floorHeight, minArea, roadDrop, parapetWidth, parapetHeight })
            number.ValueChanged += async (_, _) => await RefreshOverlay();
        GotFocus += (_, _) => { tileCache.Clear(); };
        map.MessageReceived += MapMessage;
        map.DocumentLoading += MapDocumentLoading;
        Load += (_, _) =>
        {
            ActivateKeyStatus();
            ActivateJobStatus();
            FitContentWidth();
        };
        SetBusy(false);
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { ActivateKeyStatus(); ActivateJobStatus(); _ = RefreshMapView(); }
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { DeactivateKeyStatus(); DeactivateJobStatus(); }
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { DeactivateKeyStatus(); DeactivateJobStatus(); }

    protected override void OnUnLoad(EventArgs e)
    {
        DeactivateKeyStatus();
        DeactivateJobStatus();
        base.OnUnLoad(e);
    }

    private async void ActivateKeyStatus()
    {
        if (closed || keyStatusAttached) return;
        keyStatusAttached = true;
        int generation = ++keyStatusGeneration;
        Aha.Setting.AhaSettings.VWorldKeyChanged += KeyStatusChanged;
        keyStatus.Text = "VWorld KEY · 확인 중";
        try
        {
            await Task.Run(() => Aha.Setting.AhaSettings.HasVWorldKey());
            if (!closed && keyStatusAttached && generation == keyStatusGeneration) KeyStatusChanged();
        }
        catch
        {
            if (!closed && keyStatusAttached && generation == keyStatusGeneration)
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    if (!closed && keyStatusAttached && generation == keyStatusGeneration)
                    {
                        keyStatus.Text = "VWorld KEY · 저장소 접근 실패";
                        ApplyKeyAvailability(false, "KEY 저장소에 접근하지 못했습니다. 상단 Setting에서 Keychain 접근과 KEY 등록 상태를 확인하세요.");
                    }
                }));
        }
    }

    private void DeactivateKeyStatus()
    {
        if (!keyStatusAttached) return;
        keyStatusAttached = false;
        keyStatusGeneration++;
        Aha.Setting.AhaSettings.VWorldKeyChanged -= KeyStatusChanged;
    }

    private void ActivateJobStatus()
    {
        if (closed) return;
        if (!jobStatusAttached) { Jobs.Changed += JobStatusChanged; jobStatusAttached = true; }
        RefreshJobStatus();
    }

    private void DeactivateJobStatus()
    {
        if (!jobStatusAttached) return;
        Jobs.Changed -= JobStatusChanged;
        jobStatusAttached = false;
    }

    private void JobStatusChanged(uint documentSerialNumber)
    {
        if (documentSerialNumber != this.documentSerialNumber) return;
        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            if (!closed && jobStatusAttached) RefreshJobStatus();
        }));
    }

    private void RefreshJobStatus()
    {
        if (closed) return;
        var current = TopoService.Current(doc.RuntimeSerialNumber);
        if (current is null && observedJobId is not null)
        {
            try { current = Jobs.Status(observedJobId); }
            catch (ArgumentException) { observedJobId = null; }
        }
        ApplyJobStatus(current);
    }

    private void ApplyJobStatus(JsonObject? snapshot)
    {
        if (closed) return;
        bool wasLocked = JobLocked;
        jobState = snapshot;
        if (JobLocked) inputIssue.Visible = false;
        if (snapshot is null)
        {
            jobSummary.Visible = false;
            SetBusy(busy);
            if (wasLocked) _ = RefreshMapView();
            else _ = RefreshOverlay();
            return;
        }
        observedJobId = snapshot["job_id"]?.GetValue<string>();
        string state = snapshot["state"]?.GetValue<string>() ?? "";
        if (JobLocked) operation?.Cancel();
        jobSummary.Text = DescribeInput(snapshot["input"] as JsonObject);
        jobSummary.Visible = JobLocked && jobSummary.Text.Length > 0;
        string progress = snapshot["progress"]?.GetValue<string>() ?? "";
        string? error = snapshot["error"]?.GetValue<string>();
        if (state == "completed" && snapshot["result"] is JsonObject result)
            ReportGenerationResult(result);
        else
        {
            status.Text = state switch
            {
                "running" => progress.Length > 0 ? progress : "자료 취득·형상 계산 중",
                "ready" => ReadyMessage(snapshot["summary"] as JsonObject, progress),
                "creating" => "Rhino 모델에 객체·레이어를 추가하고 있습니다.",
                "cancelled" => progress.Length > 0 ? progress : "연산이 취소되었습니다.",
                "failed" => error ?? "작업을 완료하지 못했습니다.",
                _ => progress
            };
            if (state == "ready" && !string.IsNullOrWhiteSpace(error)) status.Text += "\n" + error;
        }
        SetBusy(busy);
        if (wasLocked && !JobLocked) _ = RefreshMapView();
    }

    private static string DescribeInput(JsonObject? input)
    {
        if (input is null) return "";
        var parts = new List<string> { input["mode"]?.GetValue<string>() == "xy" ? "XY 연산 설정" : "Topo 연산 설정" };
        if (input["pnus"] is JsonArray pnus) parts.Add($"선택 필지 {pnus.Count}개 · " + string.Join(", ", pnus.Select(p => p?.GetValue<string>())));
        if (input["width_m"] is not null && input["depth_m"] is not null)
            parts.Add($"범위 {input["width_m"]} × {input["depth_m"]} m");
        if (input["terrain_level"] is not null) parts.Add($"지형 표본 레벨 {input["terrain_level"]}");
        if (input["options"] is JsonObject options)
        {
            string Flag(string key) => options[key]?.GetValue<bool>() == true ? "ON" : "OFF";
            var near = options["UseNearRegion"]?.GetValue<bool>();
            if (near is not null)
            {
                parts.Add($"지형 {(options["TerrainAsNurbs"]?.GetValue<bool>() == true ? "NURBS" : "Mesh")} · 건물 {(options["BuildingsAsNurbs"]?.GetValue<bool>() == true ? "NURBS" : "Mesh")}");
                parts.Add(near == true ? $"Near {options["NearRatio"]}R · 깊이 {options["NearDepthMetres"]} m" : "Near OFF");
                parts.Add(near == true && options["Contour"]?.GetValue<bool>() == true ? $"Contour {options["ContourIntervalMetres"]} m" : "Contour OFF");
                parts.Add($"건물과 기초 분리 {Flag("SeparateFoundations")}");
                parts.Add(near == true && options["Parapets"]?.GetValue<bool>() == true
                    ? $"파라펫 ON · 폭 {options["ParapetWidthMetres"]?.GetValue<double>() * 1000:g} mm · 높이 {options["ParapetHeightMetres"]?.GetValue<double>() * 1000:g} mm"
                    : "파라펫 OFF");
                parts.Add($"도로 {Flag("IncludeRoads")} · 분리 {Flag("SeparateRoads")} · 내림 {Flag("LowerRoads")} ({options["RoadDropMetres"]} m)");
                parts.Add($"기본 건물 높이 {options["UnknownHeight"]} m · 층당 높이 {options["FloorHeight"]} m · 최소 면적 {options["MinArea"]} m²");
            }
            else if (options["IncludeRoads"] is not null)
                parts.Add($"도로 {Flag("IncludeRoads")} · 최소 건물 면적 {options["MinArea"]} m²");
        }
        return string.Join("\n", parts);
    }

    private static string ReadyMessage(JsonObject? summary, string progress)
    {
        string count = summary?["prepared_object_count"]?.ToString() ?? "";
        string message = (progress.Length > 0 ? progress + "\n" : "")
            + (count.Length > 0 ? $"객체 {count}개 준비. " : "")
            + "생성 버튼을 누르면 Rhino 모델에 추가합니다.";
        return WithNotices(message, summary?["warnings"] as JsonArray);
    }

    private static string WithNotices(string message, JsonArray? warnings)
    {
        var notices = warnings?.Select(warning => warning?.GetValue<string>())
            .Where(warning => !string.IsNullOrWhiteSpace(warning)).ToArray();
        return notices is { Length: > 0 }
            ? message + "\n\n안내\n" + string.Join("\n", notices.Select(warning => "· " + warning))
            : message;
    }

    private void KeyStatusChanged() => RhinoApp.InvokeOnUiThread((Action)(() =>
    {
        if (closed || !keyStatusAttached) return;
        var state = Aha.Setting.AhaSettings.VWorldKeyState;
        keyStatus.Text = "VWorld KEY · " + (state switch
        {
            Aha.Setting.VWorldKeyStatus.Missing => "미등록",
            Aha.Setting.VWorldKeyStatus.Registered => "등록됨 · 미검증",
            Aha.Setting.VWorldKeyStatus.Validated => "검증 성공",
            Aha.Setting.VWorldKeyStatus.ValidationFailed => "검증 실패 · KEY/연결 확인",
            _ => "확인 중"
        });
        ApplyKeyAvailability(state is Aha.Setting.VWorldKeyStatus.Registered or Aha.Setting.VWorldKeyStatus.Validated or Aha.Setting.VWorldKeyStatus.ValidationFailed,
            state == Aha.Setting.VWorldKeyStatus.Unknown ? "VWorld KEY 등록 여부를 확인하고 있습니다."
                : "지도를 사용하려면 상단 Setting에서 VWorld KEY를 입력하고 저장하세요. KEY가 없다면 ‘V-WORLD 디지털트윈국토 · API 인증키 발급’을 여세요.");
    }));

    private void ApplyKeyAvailability(bool available, string notice)
    {
        long revision = Aha.Setting.AhaSettings.VWorldKeyRevision;
        bool reload = available && (!keyAvailable || mapKeyRevision != revision);
        bool wasAvailable = keyAvailable;
        keyAvailable = available;
        map.Visible = available;
        mapPlaceholder.Visible = !available;
        issueKey.Visible = !available;
        mapNotice.Text = notice;
        modelControls.Enabled = available;
        if (!available)
        {
            operation?.Cancel();
            if (wasAvailable) ResetMapRequests();
            ClearSearchResults();
        }
        SetBusy(busy);
        if (!reload) return;
        mapKeyRevision = revision;
        ResetMapRequests();
        tileRequests = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aha.Topo.TopoMap.html");
        if (stream == null)
        {
            map.Visible = false; mapPlaceholder.Visible = true;
            mapNotice.Text = "지도 화면 리소스를 찾지 못했습니다. AHA 설치를 확인하세요.";
            return;
        }
        using var reader = new StreamReader(stream);
        map.LoadHtml(reader.ReadToEnd().Replace("__AHA_MAP_GENERATION__", mapGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void ResetMapRequests()
    {
        mapReady = false;
        mapGeneration++;
        tileSequence = 0;
        tileRequests?.Cancel(); tileRequests?.Dispose(); tileRequests = null;
        tileCache.Clear();
    }

    private void MapDocumentLoading(object? sender, WebViewLoadingEventArgs e)
    {
        // This is an embedded viewer, not a navigable browser. Native Reload
        // reloads about:blank rather than the HTML supplied by LoadHtml on macOS.
        // Host-initiated loads reset mapReady first (including KEY changes).
        if (e.IsMainFrame && mapReady) e.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            DeactivateKeyStatus();
            DeactivateJobStatus();
            closed = true; operation?.Cancel(); lifetime.Cancel();
            ResetMapRequests();
            map.MessageReceived -= MapMessage;
            map.DocumentLoading -= MapDocumentLoading;
        }
        base.Dispose(disposing);
    }

    private static NumericStepper Number(double value, double min, double max, double increment, int decimalPlaces = 1) =>
        new() { DecimalPlaces = decimalPlaces, MinValue = min, MaxValue = max, Increment = increment, Value = value, Width = 100 };

    private VWorldClient Client()
    {
        return new VWorldClient(TopoService.RequireKey());
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy || JobLocked || closed || !keyAvailable) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        // End the native field-editor session before disabling its control.
        // On macOS disabling an active text/number editor discards its uncommitted
        // value, so reading the request afterwards would use the previous input.
        cancel.Enabled = true;
        cancel.Focus();
        SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!closed && !JobLocked) status.Text = "취소되었거나 요청 시간이 초과되었습니다."; }
        catch (Exception error)
        {
            // Provider exceptions deliberately contain no URL, key or response body.
            if (!closed && !JobLocked) status.Text = error is InvalidOperationException || error is ArgumentException || error is InvalidDataException
                ? error.Message : "작업을 완료하지 못했습니다: " + error.GetType().Name;
        }
        finally { operation.Dispose(); operation = null; if (!closed) SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        bool locked = value || JobLocked || !keyAvailable;
        modelControls.Visible = !JobLocked;
        map.Visible = keyAvailable && !JobLocked;
        address.Visible = search.Visible = !JobLocked;
        if (JobLocked) candidates.Visible = false;
        search.Enabled = !locked; address.Enabled = !locked; settings.Enabled = true;
        candidates.Enabled = !locked; calculate.Enabled = !locked && target != null && validRange;
        generate.Enabled = !value && jobState?["state"]?.GetValue<string>() == "ready";
        selectedParcels.Enabled = !locked;
        clearParcels.Enabled = !locked && target != null;
        width.Enabled = depth.Enabled = level.Enabled = unknownHeight.Enabled = floorHeight.Enabled = minArea.Enabled = terrainFormat.Enabled = buildingFormat.Enabled = !locked;
        showRoads.Enabled = separateFoundations.Enabled = !locked;
        RefreshRoadControls();
        RefreshNearControls();
        map.Enabled = !locked;
        string? jobStatus = jobState?["state"]?.GetValue<string>();
        cancel.Text = jobStatus == "ready" ? "준비 결과 폐기" : "취소";
        cancel.Enabled = value || jobStatus is "running" or "ready";
    }

    private void RefreshRoadControls()
    {
        separateRoads.Enabled = keyAvailable && !busy && !JobLocked && showRoads.Checked == true;
        lowerRoads.Enabled = keyAvailable && !busy && !JobLocked && showRoads.Checked == true && separateRoads.Checked == true;
        roadDrop.Enabled = lowerRoads.Enabled && lowerRoads.Checked == true;
    }

    private void RefreshNearControls()
    {
        useNearRegion.Enabled = keyAvailable && !busy && !JobLocked;
        bool enabled = useNearRegion.Checked == true;
        nearSettings.Visible = enabled;
        nearRatio.Enabled = nearDepth.Enabled = contour.Enabled = keyAvailable && !busy && !JobLocked && enabled;
        contourInterval.Enabled = contour.Enabled && contour.Checked == true;
        terrainFormatTitle.Text = enabled ? "원경 지형·도로 형식" : "지형·도로 형식";
        buildingFormatTitle.Text = enabled ? "원경 건물 형식" : "건물 형식";
        parapets.Enabled = contour.Enabled;
        parapetWidth.Enabled = parapetHeight.Enabled = parapets.Enabled && parapets.Checked == true;
    }

    private async Task Search(CancellationToken ct)
    {
        ClearSearchResults();
        status.Text = "주소 후보를 조회하고 있습니다…";
        using var client = Client();
        found = await client.SearchAsync(address.Text.Trim(), ct);
        ct.ThrowIfCancellationRequested();
        candidates.DataStore = found.Select(x => x.Label).ToList();
        candidates.SelectedIndex = -1;
        candidates.Visible = found.Count > 0;
        status.Text = found.Count == 0 ? "검색 결과가 없습니다. 도로명 또는 지번 주소를 입력하세요." : $"주소 후보 {found.Count}개. 목록에서 추가할 필지를 선택하세요. 기존 선택은 유지됩니다.";
    }

    private void ClearSearchResults()
    {
        candidates.Visible = false;
        candidates.SelectedIndex = -1;
        candidates.DataStore = Array.Empty<string>();
        found.Clear();
    }

    private void RefreshSelectedParcels(LocalProjection? projection = null)
    {
        selectedParcels.Items.Clear();
        selectedParcels.Visible = target != null;
        clearParcels.Visible = target != null;
        if (target == null || projection == null) return;
        foreach (var parcel in target.Parcels)
        {
            var parcelKey = ParcelKey(parcel);
            var label = new Label
            {
                Text = $"{(string.IsNullOrWhiteSpace(parcel.Pnu) ? parcel.Id : parcel.Pnu)}  ·  {Geo.Area(parcel, projection):N1} m²",
                VerticalAlignment = VerticalAlignment.Center
            };
            var remove = new Button { Text = "해제", ToolTip = "이 필지를 선택에서 해제합니다." };
            remove.Click += async (_, _) => await Run(ct => Remove(parcelKey, ct));
            selectedParcels.Items.Add(new StackLayout
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new StackLayoutItem(label, true), remove }
            });
        }
    }

    private static string ParcelKey(GeoFeature parcel) =>
        string.IsNullOrWhiteSpace(parcel.Pnu) ? "id:" + parcel.Id : "pnu:" + parcel.Pnu;

    private async Task Select(GeoFeature parcel, bool toggle, bool fitMap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parcels = target?.Parcels.ToList() ?? new List<GeoFeature>();
        int existing = parcels.FindIndex(p => ParcelKey(p) == ParcelKey(parcel));
        if (existing >= 0)
        {
            if (!toggle) { status.Text = "이미 선택한 필지입니다. 지도에서 다시 클릭하거나 목록에서 해제할 수 있습니다."; return; }
            parcels.RemoveAt(existing);
        }
        else parcels.Add(parcel);
        await SetSelection(parcels, fitMap, ct);
    }

    private Task Remove(string parcelKey, CancellationToken ct) =>
        SetSelection(target?.Parcels.Where(p => ParcelKey(p) != parcelKey).ToArray() ?? Array.Empty<GeoFeature>(), false, ct);

    private async Task SetSelection(IReadOnlyList<GeoFeature> parcels, bool fitMap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (parcels.Count == 0)
        {
            target = null; origin = null; validRange = false;
            RefreshSelectedParcels();
            selection.Text = "주소를 검색하거나 지도에서 필지를 클릭해 추가하세요. 다시 클릭하면 해제합니다.";
            terrainSize.Text = "대지 미선택";
            status.Text = "선택한 필지를 모두 해제했습니다.";
            await RefreshOverlay();
            return;
        }
        var points = parcels.SelectMany(p => p.Polygons).SelectMany(p => p).SelectMany(r => r).ToArray();
        if (points.Length == 0) throw new ArgumentException("선택 필지의 경계가 없습니다.");
        var centre = new GeoPoint((points.Min(p => p.Lon) + points.Max(p => p.Lon)) / 2,
            (points.Min(p => p.Lat) + points.Max(p => p.Lat)) / 2);
        var projection = new LocalProjection(centre);
        var combined = SiteSelection.Create(parcels, projection);
        target = combined; origin = centre;
        FitRangeToSelection(projection);
        RefreshSelectedParcels(projection);
        selection.Text = $"선택 필지 {combined.Parcels.Count}개 · 대지 구역 {combined.Components.Count}개 · 합친 면적 {Geo.Area(combined.Boundary, projection):N1} m²";
        await RefreshOverlay();
        if (fitMap)
        {
            var fitPoints = combined.Boundary.Polygons.SelectMany(p => p).SelectMany(r => r).ToArray();
            await Script("aha.fit(" + JsonSerializer.Serialize(new
            {
                bounds = new[] { fitPoints.Min(p => p.Lon), fitPoints.Min(p => p.Lat), fitPoints.Max(p => p.Lon), fitPoints.Max(p => p.Lat) }
            }) + ");");
        }
        if (validRange && !PreserveJobSummary) status.Text = "연산 가능";
    }

    private GeoBounds ModelBounds()
    {
        var projection = new LocalProjection(origin!);
        var sw = projection.ToGeo(-width.Value / 2, -depth.Value / 2);
        var ne = projection.ToGeo(width.Value / 2, depth.Value / 2);
        return new GeoBounds(sw.Lon, sw.Lat, ne.Lon, ne.Lat);
    }

    private bool PreserveJobSummary => jobState?["state"]?.GetValue<string>() is "running" or "ready" or "creating" or "completed" or "failed" or "cancelled";

    private async Task RefreshOverlay()
    {
        if (closed || JobLocked) return;
        inputIssue.Visible = false;
        if (target == null || origin == null)
        {
            validRange = false;
            calculate.Enabled = false;
            ShowInputIssue("대지 미선택");
            await Script("aha.overlay({rings:[],parcels:[],nearRings:[],bounds:null});");
            return;
        }
        IReadOnlyList<IReadOnlyList<GeoPoint>> nearRings = Array.Empty<IReadOnlyList<GeoPoint>>();
        GeoBounds? bounds = null;
        bool nearValid = true;
        try
        {
            var projection = new LocalProjection(origin);
            ValidateRange(projection);
            bounds = ModelBounds();
            BuildingRules.Validate(ReadOptions());
            if (useNearRegion.Checked == true)
            {
                nearRings = EditRegion.CreateUnbounded(target, projection, nearRatio.Value).Rings;
                EditRegion.Create(target, projection, bounds, nearRatio.Value);
            }
            var plan = TerrainClient.Plan(bounds, level.SelectedIndex + 12);
            terrainSize.Text = $"지형 {plan.TileCount}타일 · 예상 높이 표본 {plan.MeshVertices:N0}개";
            validRange = true;
            if (!PreserveJobSummary && !busy) status.Text = "연산 가능";
        }
        catch (ArgumentException error)
        {
            validRange = false; nearValid = false;
            ShowInputIssue(error.Message);
        }
        calculate.Enabled = keyAvailable && !busy && validRange;
        await Script("aha.overlay(" + JsonSerializer.Serialize(new
        {
            rings = target.Boundary.Polygons.SelectMany(p => p).Select(r => r.Select(p => new[] { p.Lon, p.Lat })),
            parcels = target.Parcels.Select(parcel => new
            {
                id = ParcelKey(parcel),
                polygons = parcel.Polygons.Select(p => p.Select(r => r.Select(point => new[] { point.Lon, point.Lat })))
            }),
            nearRings = nearRings.Select(r => r.Select(p => new[] { p.Lon, p.Lat })), nearValid,
            bounds = bounds is null ? null : new[] { bounds.West, bounds.South, bounds.East, bounds.North }
        }) + ");");
    }

    private void ShowInputIssue(string message)
    {
        if (!PreserveJobSummary) status.Text = message;
        else { inputIssue.Text = message; inputIssue.Visible = true; }
    }

    private (double Width, double Depth) RequiredRange(LocalProjection projection)
    {
        var points = target!.Boundary.Polygons.SelectMany(p => p).SelectMany(r => r).Select(projection.ToLocal).ToArray();
        double requiredWidth = Math.Ceiling(2 * points.Max(p => Math.Abs(p.X)) * 10) / 10;
        double requiredDepth = Math.Ceiling(2 * points.Max(p => Math.Abs(p.Y)) * 10) / 10;
        return (requiredWidth, requiredDepth);
    }

    private void FitRangeToSelection(LocalProjection projection)
    {
        var required = RequiredRange(projection);
        // Only selecting parcels may enlarge the extent. Writing Value during
        // a ValueChanged callback also replaces the native text being edited.
        updatingRange = true;
        try
        {
            if (width.Value < required.Width) width.Value = Math.Min(3000, required.Width);
            if (depth.Value < required.Depth) depth.Value = Math.Min(3000, required.Depth);
        }
        finally { updatingRange = false; }
    }

    private void ValidateRange(LocalProjection projection)
    {
        var required = RequiredRange(projection);
        if (required.Width > 3000 || required.Depth > 3000)
            throw new ArgumentException($"모델 범위 초과 · 최소 {required.Width:N1} × {required.Depth:N1} m / 최대 3,000 × 3,000 m");
        if (width.Value < required.Width || depth.Value < required.Depth)
            throw new ArgumentException($"모델 범위 부족 · 최소 {Math.Max(50, required.Width):N1} × {Math.Max(50, required.Depth):N1} m");
    }

    private ModelOptions ReadOptions() => new ModelOptions(UnknownHeight: unknownHeight.Value, FloorHeight: floorHeight.Value, MinArea: minArea.Value,
            RoadDropMetres: roadDrop.Value / 1000, TerrainAsNurbs: terrainFormat.SelectedIndex == 1,
            BuildingsAsNurbs: buildingFormat.SelectedIndex == 1, SeparateRoads: separateRoads.Checked == true,
            LowerRoads: lowerRoads.Checked == true, IncludeRoads: showRoads.Checked == true,
            UseNearRegion: useNearRegion.Checked == true, NearRatio: nearRatio.Value, NearDepthMetres: nearDepth.Value,
            Contour: useNearRegion.Checked == true && contour.Checked == true,
            ContourIntervalMetres: contourInterval.Value, SeparateFoundations: separateFoundations.Checked == true,
            Parapets: parapets.Checked == true, ParapetWidthMetres: parapetWidth.Value / 1000, ParapetHeightMetres: parapetHeight.Value / 1000);

    private void StartPreparation()
    {
        if (closed || busy || JobLocked || !keyAvailable || origin is null || target is null) return;
        // Commit the native numeric editor before reading and locking its value.
        cancel.Enabled = true;
        cancel.Focus();
        try
        {
            width.Commit(); depth.Commit();
            ValidateRange(new LocalProjection(origin));
            var request = TopoService.PrepareStart(doc, target.Parcels, origin, ModelBounds(), level.SelectedIndex + 12, ReadOptions());
            ApplyJobStatus(request);
            ActivateJobStatus();
        }
        catch (Exception error) { status.Text = UserError.Message(error); SetBusy(busy); }
    }

    private void CreatePrepared()
    {
        if (closed || busy || observedJobId is null || jobState?["state"]?.GetValue<string>() != "ready") return;
        try { ApplyJobStatus(TopoService.Create(observedJobId)); RefreshJobStatus(); }
        catch (Exception error) { status.Text = UserError.Message(error); SetBusy(busy); }
    }

    private void CancelCurrent()
    {
        if (closed) return;
        if (JobLocked && observedJobId is not null)
        {
            try { ApplyJobStatus(TopoService.Cancel(observedJobId)); RefreshJobStatus(); }
            catch (Exception error) { status.Text = UserError.Message(error); }
        }
        else operation?.Cancel();
    }

    private void ReportGenerationResult(JsonObject result)
    {
        if (closed) return;
        string Count(string key) => result[key]?.ToString() ?? "0";
        string nearSummary = result["AppliedSettings"]?["UseNearRegion"]?.GetValue<bool>() == true
            ? $" / 근접 입체 {Count("NearSolidCount")}개" : "";
        string message = $"생성 완료\n객체 {Count("CreatedCount")}개{nearSummary}";
        if (result["RoadParcels"] is not null)
            message += $" / 도로 필지 {Count("RoadParcels")}개 / 건물: 층수 추정 {Count("FloorEstimatedBuildings")}, 기본 높이 {Count("DefaultEstimatedBuildings")}, 지하 제외 {Count("UndergroundBuildings")}, 지상·지하 미확인 {Count("UnclassifiedBuildings")}, 면적 제외 {Count("FilteredBuildings")}, 범위 밖 {Count("OutsideBuildings")}, 형상 제외 {Count("InvalidBuildings")}.";
        status.Text = WithNotices(message, result["Warnings"] as JsonArray);
    }

    private async void MapMessage(object? sender, WebViewMessageEventArgs e)
    {
        try
        {
            if (closed || !keyAvailable) return;
            using var json = JsonDocument.Parse(e.Message);
            var root = json.RootElement;
            if (!root.TryGetProperty("generation", out var pageGeneration)
                || pageGeneration.GetString() != mapGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
            string? type = root.GetProperty("type").GetString();
            if (type != "ready" && !mapReady) return;
            switch (type)
            {
                case "ready":
                    if (mapReady) return;
                    mapReady = true;
                    await Script("aha.center(" + JsonSerializer.Serialize(new
                    {
                        lon = origin?.Lon ?? 126.9782, lat = origin?.Lat ?? 37.5666,
                        zoom = origin == null ? 16 : 17
                    }) + ");");
                    await RefreshOverlay();
                    break;
                case "tiles":
                    if (JobLocked) return;
                    var tiles = root.GetProperty("tiles").EnumerateArray().Select(x => (z: x.GetProperty("z").GetInt32(), x: x.GetProperty("x").GetInt32(), y: x.GetProperty("y").GetInt32())).ToArray();
                    if (tiles.Length > 30) return;
                    var generation = mapGeneration;
                    var sequence = ++tileSequence;
                    var apiKey = mapKey();
                    if (apiKey.Length == 0) { KeyStatusChanged(); return; }
                    if (tileRequests is null) return;
                    var tileToken = tileRequests.Token;
                    await Task.WhenAll(tiles.Select(t => LoadTile(apiKey, t.z, t.x, t.y, generation, sequence, tileToken)));
                    break;
                case "pick":
                    if (busy || JobLocked) return;
                    var point = new GeoPoint(root.GetProperty("lon").GetDouble(), root.GetProperty("lat").GetDouble());
                    if (point.Lon < 124 || point.Lon > 132 || point.Lat < 33 || point.Lat > 39) { status.Text = "한국 영역의 필지를 선택하세요."; return; }
                    await Run(async ct => { status.Text = "선택 지점의 필지를 조회하고 있습니다…"; using var client = Client(); await Select(await client.ParcelAtAsync(point, ct), true, false, ct); });
                    break;
                case "remove":
                    if (busy || JobLocked) return;
                    var id = root.GetProperty("id").GetString();
                    if (id != null) await Run(ct => Remove(id, ct));
                    break;

            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && !PreserveJobSummary) status.Text = "지도 처리 실패: " + error.GetType().Name; }
    }

    private async Task RefreshMapView()
    {
        if (closed || !keyAvailable || JobLocked) return;
        await RefreshOverlay();
        await Script("aha.refresh();");
    }

    private static async Task<byte[]> FetchTileAsync(string apiKey, int z, int x, int y, CancellationToken ct)
    {
        using var client = new VWorldClient(apiKey);
        return await client.TileAsync(z, x, y, ct);
    }

    private async Task LoadTile(string apiKey, int z, int x, int y, int generation, int sequence, CancellationToken ct)
    {
        if (z < 7 || z > 19 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return;
        await tileSlots.WaitAsync(ct);
        try
        {
            if (generation != mapGeneration || sequence != tileSequence || closed || !keyAvailable) return;
            var id = $"{z}/{x}/{y}";
            if (!tileCache.TryGetValue(id, out var image))
            {
                var bytes = await fetchTile(apiKey, z, x, y, ct);
                if (generation != mapGeneration || sequence != tileSequence || closed || !keyAvailable) return;
                image = "data:image/png;base64," + Convert.ToBase64String(bytes);
                if (tileCache.Count > 150) tileCache.Clear();
                tileCache[id] = image;
            }
            if (generation == mapGeneration && sequence == tileSequence && !closed) await Script("aha.tile(" + JsonSerializer.Serialize(id) + "," + JsonSerializer.Serialize(image) + ");");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed && !PreserveJobSummary && generation == mapGeneration && sequence == tileSequence) status.Text = "일부 배경지도를 받지 못했습니다. 키와 연결을 확인하세요."; }
        finally { tileSlots.Release(); }
    }

    private async Task Script(string body)
    {
        if (!closed && keyAvailable && mapReady)
        {
            if (sendMap is not null) await sendMap(body);
            else await map.ExecuteScriptAsync(body + " return 'ok';");
        }
    }
}
