using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;
using Font = Eto.Drawing.Font;

namespace Aha.Info;

/// <summary>A document-bound editor hosted by Rhino docking containers.</summary>
[Guid("f49a5a98-1283-4ba8-835e-8b40ccf5ec3a")]
public sealed class InfoPanel : Panel, IPanel
{
    private readonly RhinoDoc doc;
    private readonly Func<uint?> activeDocument;
    private readonly UITimer timer = new() { Interval = 0.4 };
    private readonly Label selection = new() { Text = "객체 하나를 선택하세요.", Wrap = WrapMode.Word };
    private readonly Label status = new() { Text = "선택한 객체가 없거나 여러 개입니다.", Wrap = WrapMode.Word };
    private readonly TextBox kind = new();
    private readonly CheckBox site = new() { Text = "대지로 지정" };
    private readonly DynamicLayout siteFields = new() { Spacing = new Size(8, 7) };
    private readonly ListBox parcels = new() { Height = 100 };
    private readonly TextBox parcelPnu = new() { PlaceholderText = "19자리 PNU" };
    private readonly TextBox parcelAddress = new() { PlaceholderText = "원필지 주소" };
    private readonly TextBox parcelCategory = new() { PlaceholderText = "미확인" };
    private readonly TextBox parcelArea = new() { PlaceholderText = "미확인 · ㎡" };
    private readonly TextArea parcelPlanZones = new() { Height = 55, Wrap = true };
    private readonly TextArea parcelOtherZones = new() { Height = 55, Wrap = true };
    private readonly Dictionary<string, TextBox> numbers = new();
    private readonly TextArea notes = new() { Height = 70, Wrap = true };
    private readonly ListBox fields = new() { Height = 140 };
    private readonly TextBox fieldName = new() { PlaceholderText = "항목 이름" };
    private readonly DropDown fieldType = new() { DataStore = new[] { "문자", "숫자", "참 / 거짓", "목록 / 객체 (JSON)" }, SelectedIndex = 0 };
    private readonly TextArea fieldValue = new() { Height = 75, Wrap = true };
    private readonly CheckBox fieldBoolean = new() { Text = "참 (선택 해제: 거짓)", Visible = false };
    private readonly Button save = new() { Text = "저장" };
    private readonly Button reload = new() { Text = "편집 취소 / 새로 읽기" };
    private readonly Button delete = new() { Text = "정보 삭제" };
    private readonly DynamicLayout editor = new() { Spacing = new Size(8, 8) };
    private readonly Button advanced = new() { Text = "고급 JSON 편집" };
    private Guid objectId;
    private JsonObject original = InfoData.Create("object", false, new JsonObject());
    private JsonObject working = InfoData.Create("object", false, new JsonObject());
    private string expectedJson = "";
    private string[] fieldKeys = Array.Empty<string>();
    private int parcelEditingIndex = -1;
    private bool dirty, parcelDirty, fieldDirty, refreshing, closed, readable;
    private static readonly string[] StandardKeys = { "parcels", "bcr_percent", "far_percent", "setback_m", "max_height_m", "max_floors", "notes" };
    private JsonObject Data => (JsonObject)working["data"]!;

    public static void Open(RhinoDoc doc) => Aha.Runtime.ToolPanels.Open(doc, typeof(InfoPanel));

    public InfoPanel(uint documentSerialNumber) : this(documentSerialNumber, () => RhinoDoc.ActiveDoc?.RuntimeSerialNumber) { }

    internal InfoPanel(uint documentSerialNumber, Func<uint?> activeDocument)
    {
        this.activeDocument = activeDocument;
        doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber)
            ?? throw new InvalidOperationException("대상 Rhino 문서가 없습니다.");
        var layout = new DynamicLayout { Padding = 14, Spacing = new Size(8, 8) };
        layout.AddRow(new Label { Text = "AHA  /  INFO", Font = new Font(SystemFont.Bold, 16) });
        layout.AddRow(selection);
        layout.AddRow(new Label { Text = "Rhino에서 객체 하나를 선택하세요. 저장한 정보는 해당 객체와 함께 3dm에 남습니다.", Wrap = WrapMode.Word });
        editor.AddRow(Row(new Label { Text = "종류" }, kind));
        editor.AddRow(site);
        editor.AddRow(new Label { Text = "대지 지정 해제는 입력 정보를 보존합니다. 빈 항목은 미입력으로 처리합니다.", Wrap = WrapMode.Word });
        editor.AddRow(new Label { Text = "원필지 · 주소 / PNU · 필지별 속성" });
        editor.AddRow(parcels);
        editor.AddRow(parcelAddress);
        editor.AddRow(parcelPnu);
        editor.AddRow(Row(new Label { Text = "지목", Width = 110 }, parcelCategory));
        editor.AddRow(Row(new Label { Text = "면적 (㎡)", Width = 130 }, parcelArea));
        editor.AddRow(new Label { Text = "필지별 입력 면적입니다. Rhino 형상 면적과 별개이며 합산하지 않습니다. 빈칸은 미확인입니다.", Wrap = WrapMode.Word });
        editor.AddRow(new Label { Text = "국토계획법에 따른 용도지역·지구 (미확인 시 빈칸)" });
        editor.AddRow(parcelPlanZones);
        editor.AddRow(new Label { Text = "다른 법령에 따른 지역·지구 (미확인 시 빈칸)" });
        editor.AddRow(parcelOtherZones);
        var newParcel = new Button { Text = "새 필지" };
        var applyParcel = new Button { Text = "필지 반영" };
        var removeParcel = new Button { Text = "필지 삭제" };
        editor.AddRow(Row(newParcel, applyParcel, removeParcel));
        AddNumber("건폐율 (%)", "bcr_percent");
        AddNumber("용적률 (%)", "far_percent");
        AddNumber("이격거리 (m)", "setback_m");
        AddNumber("최고 높이 (m)", "max_height_m");
        AddNumber("최고 층수", "max_floors");
        siteFields.AddRow(new Label { Text = "값은 직접 입력하거나 수집한 자료입니다. 이 패널에서 법규를 판정하지 않습니다.", Wrap = WrapMode.Word });
        editor.AddRow(siteFields);
        editor.AddRow(new Label { Text = "메모" });
        editor.AddRow(notes);
        editor.AddRow(new Label { Text = "추가 항목" });
        editor.AddRow(fields);
        editor.AddRow(Row(fieldName, fieldType));
        editor.AddRow(fieldValue);
        editor.AddRow(fieldBoolean);
        var newField = new Button { Text = "새 항목" };
        var applyField = new Button { Text = "항목 추가 / 수정" };
        var removeField = new Button { Text = "항목 삭제" };
        editor.AddRow(Row(newField, applyField, removeField));
        editor.AddRow(advanced);
        layout.AddRow(editor);
        layout.AddRow(Row(save, reload, delete));
        layout.AddRow(status);
        var scroll = new Scrollable { Content = layout, Border = BorderType.None };
        Content = scroll;
        Size = new Size(400, 760);
        void Fit() => layout.Width = Math.Max(340, scroll.ClientSize.Width);
        scroll.SizeChanged += (_, _) => Fit();
        Load += (_, _) => { Fit(); Poll(); timer.Start(); };
        UnLoad += (_, _) => timer.Stop();
        kind.TextChanged += (_, _) => Changed();
        site.CheckedChanged += (_, _) => { siteFields.Visible = site.Checked == true; Changed(); };
        notes.TextChanged += (_, _) => Changed();
        parcelPnu.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcelAddress.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcelCategory.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcelArea.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcelPlanZones.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcelOtherZones.TextChanged += (_, _) => { if (!refreshing) { parcelDirty = true; Changed(); } };
        parcels.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing) return;
            var next = parcels.SelectedIndex;
            Run(() =>
            {
                try { CommitParcel(); }
                catch
                {
                    refreshing = true;
                    try { parcels.SelectedIndex = parcelEditingIndex; }
                    finally { refreshing = false; }
                    throw;
                }
                RefreshParcels(); LoadParcel(next);
            });
        };
        newParcel.Click += (_, _) => Run(() => { CommitParcel(); RefreshParcels(); LoadParcel(-1); });
        applyParcel.Click += (_, _) => Run(() => { CommitParcel(); RefreshParcels(); LoadParcel(parcelEditingIndex); status.Text = "필지 정보가 편집에 반영되었습니다. 저장을 눌러 완료하세요."; });
        removeParcel.Click += (_, _) => Run(() =>
        {
            if (Data["parcels"] is not JsonArray list || parcelEditingIndex < 0 || parcelEditingIndex >= list.Count) return;
            list.RemoveAt(parcelEditingIndex);
            if (list.Count == 0) Data.Remove("parcels");
            parcelDirty = false; LoadParcel(-1); RefreshParcels(); Changed();
        });
        fields.SelectedIndexChanged += (_, _) => Run(LoadField);
        void FieldChanged() { if (!refreshing) { fieldDirty = true; Changed(); } }
        fieldName.TextChanged += (_, _) => FieldChanged();
        fieldValue.TextChanged += (_, _) => FieldChanged();
        fieldBoolean.CheckedChanged += (_, _) => FieldChanged();
        fieldType.SelectedIndexChanged += (_, _) =>
        {
            fieldBoolean.Visible = fieldType.SelectedIndex == 2;
            fieldValue.Visible = fieldType.SelectedIndex != 2;
            FieldChanged();
        };
        newField.Click += (_, _) => Run(() =>
        {
            if (fieldDirty) ApplyField();
            refreshing = true;
            try { fieldName.Text = ""; fieldValue.Text = ""; fieldType.SelectedIndex = 0; fields.SelectedIndex = -1; }
            finally { refreshing = false; }
        });
        applyField.Click += (_, _) => Run(ApplyField);
        removeField.Click += (_, _) => Run(() =>
        {
            var key = fieldName.Text.Trim();
            if (StandardKeys.Contains(key)) throw new ArgumentException("기본 항목은 위 입력란에서 편집하세요.");
            if (Data.Remove(key))
            {
                fieldDirty = false;
                refreshing = true;
                try { fieldName.Text = ""; fieldValue.Text = ""; }
                finally { refreshing = false; }
                RefreshFields(); Changed();
            }
        });
        advanced.Click += (_, _) => Run(OpenAdvanced);
        save.Click += (_, _) => Run(Save);
        reload.Click += (_, _) => Run(() =>
        {
            if (dirty && MessageBox.Show(this, "현재 편집을 버리고 선택한 객체의 정보를 새로 읽을까요?", "Info", MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
            dirty = false; objectId = Guid.Empty; Poll();
        });
        delete.Click += (_, _) => Run(() =>
        {
            if (MessageBox.Show(this, "이 객체의 AHA 정보를 모두 삭제할까요? 객체 형상은 유지됩니다.", "Info 정보 삭제", MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
            InfoService.Delete(doc, objectId, expectedJson);
            dirty = false; LoadInfo(objectId); status.Text = "정보를 삭제했습니다. Rhino Undo로 되돌릴 수 있습니다.";
        });
        timer.Elapsed += (_, _) => Poll();
        Poll();

        void AddNumber(string label, string key)
        {
            var box = new TextBox { PlaceholderText = "미입력" };
            numbers.Add(key, box); box.TextChanged += (_, _) => Changed();
            siteFields.AddRow(Row(new Label { Text = label, Width = 110 }, box));
        }
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { Poll(); timer.Start(); }
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) => timer.Stop();
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) => timer.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            closed = true;
            timer.Stop(); timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static StackLayout Row(params Control[] controls)
    {
        var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var control in controls) row.Items.Add(new StackLayoutItem(control, control is TextBox));
        return row;
    }

    private void Changed()
    {
        if (refreshing) return;
        dirty = true; save.Text = "변경 저장"; status.Text = "저장하지 않은 편집이 있습니다.";
    }

    private void Poll()
    {
        if (closed) return;
        try
        {
            if (RhinoDoc.FromRuntimeSerialNumber(doc.RuntimeSerialNumber) != doc)
            {
                editor.Enabled = save.Enabled = delete.Enabled = reload.Enabled = false;
                status.Text = "대상 문서가 닫혔습니다."; return;
            }
            if (activeDocument() != doc.RuntimeSerialNumber)
            {
                editor.Enabled = save.Enabled = delete.Enabled = false;
                status.Text = "Info를 연 Rhino 문서로 돌아가서 편집하세요."; return;
            }
            var selected = doc.Objects.GetSelectedObjects(false, false).Take(2).ToArray();
            var next = selected.Length == 1 ? selected[0].Id : Guid.Empty;
            if (next != objectId)
            {
                if (dirty)
                    status.Text = "선택이 바뀌었습니다. 현재 편집을 저장하거나 ‘편집 취소 / 새로 읽기’를 누르면 새 선택으로 이동합니다.";
                else LoadInfo(next);
            }
            var obj = objectId == Guid.Empty ? null : doc.Objects.FindId(objectId);
            if (obj is not null && !obj.IsDeleted)
            {
                var raw = obj.Attributes.GetUserString(InfoData.Key) ?? "";
                if (!string.Equals(raw, expectedJson, StringComparison.Ordinal))
                {
                    if (!dirty) LoadInfo(objectId);
                    else status.Text = "객체 정보가 외부에서 변경되었습니다. 현재 편집은 보존되며 저장은 충돌 검사로 보호됩니다.";
                }
            }
            var writable = readable && obj is not null && InfoService.CanEdit(doc, obj);
            editor.Enabled = save.Enabled = writable;
            delete.Enabled = writable && expectedJson.Length != 0;
            reload.Enabled = true;
            if (obj is not null) site.Enabled = InfoService.CanBeSite(obj) || site.Checked == true;
            if (obj is not null && !writable && readable) status.Text = "잠긴 객체·참조 객체·읽기 전용 문서는 조회만 가능합니다.";
            if (objectId != Guid.Empty && (obj is null || obj.IsDeleted)) status.Text = "편집하던 객체가 삭제되었습니다. 새 객체를 선택하고 새로 읽으세요.";
        }
        catch (Exception error) { editor.Enabled = save.Enabled = delete.Enabled = false; status.Text = error.Message; }
    }

    private void LoadInfo(Guid id)
    {
        objectId = id; readable = false; dirty = parcelDirty = fieldDirty = false;
        var obj = id == Guid.Empty ? null : doc.Objects.FindId(id);
        if (obj is null || obj.IsDeleted)
        {
            expectedJson = ""; selection.Text = "객체 하나를 선택하세요.";
            working = InfoData.Create("object", false, new JsonObject()); Populate();
            editor.Enabled = save.Enabled = delete.Enabled = false; status.Text = "선택한 객체가 없거나 여러 개입니다.";
            return;
        }
        selection.Text = $"선택 객체: {(string.IsNullOrWhiteSpace(obj.Name) ? obj.ObjectType.ToString() : obj.Name)}";
        expectedJson = obj.Attributes.GetUserString(InfoData.Key) ?? "";
        working = InfoData.Create("object", false, new JsonObject());
        try
        {
            working = InfoService.Read(doc, id) ?? InfoData.Create("object", false, new JsonObject());
            original = (JsonObject)working.DeepClone();
            Populate();
            InfoData.Validate(working, validateFields: false);
            readable = true;
            status.Text = expectedJson.Length == 0 ? "아직 정보가 없습니다. 입력 후 저장하면 생성됩니다." : "객체 정보를 읽었습니다.";
        }
        catch (Exception error) { Populate(); status.Text = error.Message; editor.Enabled = save.Enabled = delete.Enabled = false; }
    }

    private void Populate()
    {
        refreshing = true;
        try
        {
            kind.Text = working["kind"]!.GetValue<string>();
            site.Checked = working["site"]!.GetValue<bool>();
            siteFields.Visible = site.Checked == true;
            notes.Text = StringValue(Data["notes"]);
            foreach (var item in numbers) item.Value.Text = Data[item.Key]?.ToString() ?? "";
            RefreshParcels(); LoadParcel(Data["parcels"] is JsonArray list && list.Count > 0 ? 0 : -1); RefreshFields();
            fieldName.Text = ""; fieldValue.Text = ""; fieldType.SelectedIndex = 0;
            save.Text = "저장";
        }
        finally { refreshing = false; }
    }

    private void CommitInputs(bool validateFields = true)
    {
        working["kind"] = kind.Text.Trim(); working["site"] = site.Checked == true;
        SetText("notes", notes.Text);
        foreach (var item in numbers)
        {
            var text = item.Value.Text.Trim();
            // Do not rewrite an untouched value (including an explicit null). Keep
            // entered JSON digits until the same validation used by MCP has run.
            if (text == (Data[item.Key]?.ToString() ?? "")) continue;
            if (text.Length == 0) { Data.Remove(item.Key); continue; }
            Data[item.Key] = ParseNumber(text);
        }
        CommitParcel();
        if (fieldDirty) ApplyField();
        InfoData.Validate(working, validateFields: validateFields);
    }

    private void SetText(string key, string text)
    {
        if (text == StringValue(Data[key])) return;
        if (string.IsNullOrWhiteSpace(text)) Data.Remove(key);
        else Data[key] = text;
    }

    private static JsonNode ParseNumber(string text)
    {
        JsonNode? value;
        try { value = JsonNode.Parse(text); }
        catch (JsonException) { throw new ArgumentException("숫자로 입력하세요. 단위와 쉼표 없이 소수점은 점(.)을 사용합니다."); }
        if (value is not JsonValue || value.GetValueKind() != JsonValueKind.Number)
            throw new ArgumentException("숫자로 입력하거나 미입력이면 비워두세요.");
        return value;
    }

    private void CommitParcel()
    {
        if (!parcelDirty) return;
        var pnu = parcelPnu.Text.Trim(); var address = parcelAddress.Text.Trim();
        if (pnu.Length > 0 && (pnu.Length != 19 || pnu.Any(c => c < '0' || c > '9')))
            throw new ArgumentException("PNU는 19자리 숫자여야 합니다.");
        if (pnu.Length == 0 && address.Length == 0 && parcelEditingIndex < 0 &&
            new[] { parcelCategory.Text, parcelArea.Text, parcelPlanZones.Text, parcelOtherZones.Text }.All(string.IsNullOrWhiteSpace))
        { parcelDirty = false; return; }
        if (Data["parcels"] is not null && Data["parcels"] is not JsonArray)
            throw new ArgumentException("기존 parcels가 목록 형식이 아닙니다. 고급 JSON에서 확인하세요.");
        var list = Data["parcels"] as JsonArray;
        if (list is null) { list = new JsonArray(); Data["parcels"] = list; }
        var entry = parcelEditingIndex >= 0 && parcelEditingIndex < list.Count && list[parcelEditingIndex] is JsonObject previous
            ? (JsonObject)previous.DeepClone() : new JsonObject();
        entry["pnu"] = pnu.Length == 0 ? null : pnu;
        entry["address"] = address.Length == 0 ? null : address;
        if (string.IsNullOrWhiteSpace(parcelCategory.Text)) entry.Remove("land_category");
        else entry["land_category"] = parcelCategory.Text.Trim();
        var areaText = parcelArea.Text.Trim();
        if (areaText.Length == 0) entry.Remove("area_m2");
        else
        {
            // Keep the numeric token so InfoData can also reject underflow instead of silently storing zero.
            entry["area_m2"] = ParseNumber(areaText);
        }
        var zones = entry["land_use_zones"] is JsonObject existingZones ? (JsonObject)existingZones.DeepClone() : new JsonObject();
        if (string.IsNullOrWhiteSpace(parcelPlanZones.Text)) zones.Remove("national_plan");
        else zones["national_plan"] = parcelPlanZones.Text.Trim();
        if (string.IsNullOrWhiteSpace(parcelOtherZones.Text)) zones.Remove("other_laws");
        else zones["other_laws"] = parcelOtherZones.Text.Trim();
        if (zones.Count == 0) entry.Remove("land_use_zones");
        else entry["land_use_zones"] = zones;
        _ = InfoData.Create("parcel", false, new JsonObject { ["parcels"] = new JsonArray(entry.DeepClone()) });
        if (parcelEditingIndex >= 0 && parcelEditingIndex < list.Count) list[parcelEditingIndex] = entry;
        else { list.Add(entry); parcelEditingIndex = list.Count - 1; }
        parcelDirty = false; Changed();
    }

    private void RefreshParcels()
    {
        var was = refreshing; refreshing = true;
        try
        {
            parcels.DataStore = Data["parcels"] is JsonArray list
                ? list.Select((p, i) => p is JsonObject parcel ? $"{i + 1}. {StringValue(parcel["address"], "주소 미입력")}  ·  {StringValue(parcel["pnu"], "PNU 미입력")}" : $"{i + 1}. {p?.ToJsonString() ?? "null"}").ToArray()
                : Array.Empty<string>();
        }
        finally { refreshing = was; }
    }

    private void LoadParcel(int index)
    {
        var was = refreshing; refreshing = true;
        try
        {
            parcelEditingIndex = index; parcelDirty = false;
            var entry = Data["parcels"] is JsonArray list && index >= 0 && index < list.Count ? list[index] as JsonObject : null;
            parcelPnu.Text = StringValue(entry?["pnu"]); parcelAddress.Text = StringValue(entry?["address"]);
            parcelCategory.Text = StringValue(entry?["land_category"]);
            parcelArea.Text = entry?["area_m2"]?.ToString() ?? "";
            var zones = entry?["land_use_zones"] as JsonObject;
            parcelPlanZones.Text = StringValue(zones?["national_plan"]);
            parcelOtherZones.Text = StringValue(zones?["other_laws"]);
            parcels.SelectedIndex = index;
        }
        finally { refreshing = was; }
    }

    private void RefreshFields()
    {
        var was = refreshing; refreshing = true;
        try
        {
            fieldKeys = Data.Select(p => p.Key).Where(key => !StandardKeys.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            fields.DataStore = fieldKeys.Select(key => $"{key}: {Preview(Data[key])}").ToArray();
        }
        finally { refreshing = was; }
    }

    private void LoadField()
    {
        if (refreshing || fields.SelectedIndex < 0 || fields.SelectedIndex >= fieldKeys.Length) return;
        var key = fieldKeys[fields.SelectedIndex];
        if (fieldDirty) ApplyField();
        var value = Data[key];
        refreshing = true;
        try
        {
            fieldName.Text = key;
            fieldType.SelectedIndex = value switch
            {
                JsonObject or JsonArray => 3,
                JsonValue scalar when scalar.TryGetValue<bool>(out _) => 2,
                JsonValue scalar when scalar.TryGetValue<string>(out _) => 0,
                _ => 1
            };
            fieldBoolean.Checked = value is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) && flag;
            fieldValue.Text = value is JsonValue text && text.TryGetValue<string>(out var content) ? content : value?.ToJsonString() ?? "";
            fields.SelectedIndex = Array.IndexOf(fieldKeys, key);
            fieldDirty = false;
        }
        finally { refreshing = false; }
    }

    private void ApplyField()
    {
        var key = fieldName.Text.Trim();
        if (key.Length == 0) throw new ArgumentException("추가 항목 이름을 입력하세요.");
        if (StandardKeys.Contains(key)) throw new ArgumentException("기본 항목은 위 입력란에서 편집하세요.");
        var text = fieldValue.Text;
        JsonNode? value;
        switch (fieldType.SelectedIndex)
        {
            case 0: value = JsonValue.Create(text); break;
            case 1:
                value = ParseNumber(text.Trim());
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new ArgumentException("숫자 값을 입력하세요.");
                break;
            case 2:
                value = JsonValue.Create(fieldBoolean.Checked == true); break;
            default:
                value = JsonNode.Parse(text);
                if (value is not JsonObject && value is not JsonArray) throw new ArgumentException("목록 또는 객체 JSON을 입력하세요.");
                break;
        }
        Data[key] = value; fieldDirty = false; RefreshFields(); Changed(); status.Text = "항목이 편집에 반영되었습니다. 저장을 눌러 완료하세요.";
    }

    private void OpenAdvanced()
    {
        // A root-valid stored document may need its malformed fields repaired here.
        // Commit current inputs, then validate the complete result after applying the patch.
        CommitInputs(validateFields: false);
        var patchBox = new TextArea { Text = "{}", Wrap = false, Height = 220 };
        var preview = new TextArea { Text = working.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ReadOnly = true, Height = 220, Wrap = false };
        var error = new Label { Wrap = WrapMode.Word };
        var apply = new Button { Text = "변경사항 반영" };
        var cancel = new Button { Text = "취소" };
        var content = new DynamicLayout { Padding = 12, Spacing = new Size(8, 8) };
        content.AddRow(new Label { Text = "현재 정보 (읽기 전용)" }); content.AddRow(preview);
        content.AddRow(new Label { Text = "JSON 변경사항 · null은 항목 삭제, 배열은 전체 교체입니다.", Wrap = WrapMode.Word });
        content.AddRow(patchBox); content.AddRow(error); content.AddRow(Row(apply, cancel));
        var dialog = new Dialog<bool> { Title = "Info · 고급 JSON", Content = content, ClientSize = new Size(640, 620), Resizable = true };
        apply.Click += (_, _) =>
        {
            try
            {
                var patch = JsonNode.Parse(patchBox.Text) as JsonObject ?? throw new ArgumentException("JSON 객체를 입력하세요.");
                ApplyAdvanced(patch); dialog.Close(true);
            }
            catch (Exception problem) { error.Text = problem.Message; }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.ShowModal(this); dialog.Dispose();
    }

    private void ApplyAdvanced(JsonObject patch)
    {
        var next = InfoData.Merge(working, patch);
        var obj = doc.Objects.FindId(objectId);
        if (next["site"]!.GetValue<bool>() && (obj is null || !InfoService.CanBeSite(obj)))
            throw new ArgumentException("대지는 유효한 닫힌 Curve, Brep 또는 Extrusion으로 지정할 수 있습니다.");
        working = next; Populate(); Changed();
    }

    private void Save()
    {
        CommitInputs();
        var patch = expectedJson.Length == 0 ? (JsonObject)working.DeepClone() : InfoData.Difference(original, working);
        InfoService.Update(doc, objectId, patch, expectedJson);
        dirty = false; LoadInfo(objectId); status.Text = "저장했습니다. Rhino Undo / Redo로 되돌리거나 복원할 수 있습니다.";
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { status.Text = error is JsonException ? "JSON 형식이 올바르지 않습니다." : error.Message; }
    }

    private static string StringValue(JsonNode? value, string fallback = "") =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : fallback;

    private static string Preview(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "미입력";
        return text.Length > 110 ? text[..107] + "…" : text;
    }
}
