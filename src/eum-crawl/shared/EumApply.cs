using System.Text.Json.Nodes;

namespace Aha.EumCrawl;

/// <summary>Maps one parcel lookup to an explicitly selected Info parcel slot.</summary>
public static class EumApply
{
    public static JsonObject CreatePreview(JsonObject parcelResult, JsonObject currentData, int parcelIndex)
    {
        ValidateSlot(currentData, parcelIndex);
        var fields = new JsonObject();
        foreach (var name in EumParser.NumericFields)
            fields[name] = parcelResult["fields"]?[name]?.DeepClone() ?? EumParser.Unknown("미확인 · 기존값 유지");
        foreach (var name in EumParser.ParcelFields)
            fields["parcels." + name] = parcelResult["parcel_fields"]?[name]?.DeepClone() ?? EumParser.Unknown("미확인 · 기존값 유지");
        var pnu = parcelResult["pnu"]?.GetValue<string>() ?? "";
        if (!EumParser.IsPnu(pnu)) throw new ArgumentException("조회 결과의 PNU가 올바르지 않습니다.");
        var address = parcelResult["address"]?.GetValue<string>() ?? "";
        fields["parcels.identity"] = EumParser.Candidate(new JsonObject { ["pnu"] = pnu, ["address"] = address },
            address + " · PNU " + pnu);
        fields["notes"] = EumParser.Candidate(JsonValue.Create(EumParser.Notes(new[] { parcelResult }))!, "조회한 필지의 법규 후보·조건 참고 메모");
        return new JsonObject
        {
            ["fields"] = fields, ["parcels"] = new JsonArray(parcelResult.DeepClone()),
            ["current_data"] = currentData.DeepClone(), ["parcel_index"] = parcelIndex,
            ["sources"] = parcelResult["sources"]?.DeepClone() ?? new JsonArray(),
            ["warnings"] = parcelResult["warnings"]?.DeepClone() ?? new JsonArray()
        };
    }

    public static JsonObject DataPatch(JsonObject preview, JsonObject currentData, IEnumerable<string> fields)
    {
        if (preview["current_data"] is not JsonObject snapshot || !JsonNode.DeepEquals(currentData, snapshot))
            throw new InvalidOperationException("조회 후 객체 정보가 변경되었습니다. 다시 조회하세요.");
        var parcelIndex = preview["parcel_index"]?.GetValue<int>() ?? throw new InvalidOperationException("적용할 필지 위치가 없습니다.");
        ValidateSlot(currentData, parcelIndex);
        var patch = new JsonObject();
        JsonArray? changedParcels = null;
        var selected = fields.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new ArgumentException("적용할 항목을 선택하세요.");
        foreach (var field in selected)
        {
            var parcelName = field.StartsWith("parcels.", StringComparison.Ordinal) ? field[8..] : null;
            if (parcelName is null ? !EumParser.NumericFields.Contains(field) && field != "notes"
                : parcelName != "identity" && !EumParser.ParcelFields.Contains(parcelName))
                throw new ArgumentException("지원하지 않는 적용 항목입니다: " + field);
            var candidate = preview["fields"]?[field];
            if (candidate?["status"]?.GetValue<string>() != "candidate" || candidate["value"] is null)
                throw new InvalidOperationException("확인되지 않은 항목은 자동 적용할 수 없습니다: " + field);
            if (parcelName is null) { patch[field] = candidate["value"]!.DeepClone(); continue; }

            changedParcels ??= currentData["parcels"] is JsonArray current ? (JsonArray)current.DeepClone() : new JsonArray();
            if (changedParcels.Count == 0) changedParcels.Add(new JsonObject());
            var entry = changedParcels[parcelIndex]!.AsObject();
            if (parcelName == "identity")
            {
                var identity = candidate["value"]!.AsObject();
                entry["pnu"] = identity["pnu"]?.DeepClone();
                entry["address"] = identity["address"]?.DeepClone();
            }
            else if (parcelName == "land_use_zones")
            {
                var zones = entry[parcelName] is JsonObject prior ? (JsonObject)prior.DeepClone() : new JsonObject();
                var known = candidate["value"]!.AsObject();
                foreach (var name in new[] { "national_plan", "other_laws" })
                    if (known[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        zones[name] = value.DeepClone();
                entry[parcelName] = zones;
            }
            else entry[parcelName] = candidate["value"]!.DeepClone();
        }
        if (changedParcels is not null) patch["parcels"] = changedParcels;
        return patch;
    }

    private static void ValidateSlot(JsonObject data, int index)
    {
        if (data["parcels"] is not null && data["parcels"] is not JsonArray)
            throw new InvalidOperationException("원필지 목록 형식을 확인할 수 없습니다.");
        var parcels = data["parcels"] as JsonArray;
        var count = parcels?.Count ?? 0;
        if (index < 0 || (count == 0 ? index != 0 : index >= count || parcels![index] is not JsonObject))
            throw new InvalidOperationException("적용할 원필지 위치를 확인할 수 없습니다.");
    }
}
