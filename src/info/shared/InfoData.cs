using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace Aha.Info;

/// <summary>The object-local aha.info document. No Rhino or UI dependency.</summary>
public static class InfoData
{
    public const string Key = "aha.info";
    public const int CurrentVersion = 1;

    public static JsonObject Create(string kind, bool site, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new JsonObject
        {
            ["version"] = CurrentVersion, ["kind"] = kind, ["site"] = site,
            ["data"] = data.DeepClone()
        };
        Validate(result);
        return result;
    }

    public static JsonObject Parse(string json, bool requireSupportedVersion = true, bool validateFields = true)
    {
        JsonObject value;
        try
        {
            value = JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("Info JSON의 최상위 값은 객체여야 합니다.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Info JSON 형식이 올바르지 않습니다.", error);
        }
        Validate(value, requireSupportedVersion, validateFields);
        return value;
    }

    public static void Validate(JsonObject value, bool requireSupportedVersion = true, bool validateFields = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number < 1)
            throw new ArgumentException("Info version은 양의 정수여야 합니다.");
        if (requireSupportedVersion && number != CurrentVersion)
            throw new NotSupportedException($"Info version {number}은 읽기만 가능합니다. 이 버전을 지원하는 AHA로 편집하세요.");
        if (value["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Info kind는 비어 있지 않은 문자여야 합니다.");
        if (value["site"] is not JsonValue site || !site.TryGetValue<bool>(out _))
            throw new ArgumentException("Info site는 true 또는 false여야 합니다.");
        if (value["data"] is not JsonObject data)
            throw new ArgumentException("Info data는 객체여야 합니다.");
        if (validateFields && number == CurrentVersion) ValidateFields(data);
    }

    private static void ValidateFields(JsonObject data)
    {
        foreach (var key in new[] { "bcr_percent", "far_percent", "setback_m", "max_height_m", "max_floors" })
            ValidateNumber(data[key], key, integer: key == "max_floors");
        ValidateText(data["notes"], "notes");
        if (data["parcels"] is null) return;
        if (data["parcels"] is not JsonArray parcels)
            throw new ArgumentException("Info data.parcels는 원필지 객체의 목록 또는 null이어야 합니다.");
        for (var index = 0; index < parcels.Count; index++)
        {
            if (parcels[index] is not JsonObject parcel)
                throw new ArgumentException($"Info data.parcels[{index}]는 원필지 객체여야 합니다.");
            var prefix = $"parcels[{index}]";
            ValidateText(parcel["address"], prefix + ".address");
            ValidateText(parcel["pnu"], prefix + ".pnu");
            ValidateText(parcel["land_category"], prefix + ".land_category");
            ValidateNumber(parcel["area_m2"], prefix + ".area_m2", integer: false);
            if (parcel["land_use_zones"] is not null)
            {
                if (parcel["land_use_zones"] is not JsonObject zones)
                    throw new ArgumentException($"Info data.{prefix}.land_use_zones는 객체 또는 null이어야 합니다.");
                ValidateText(zones["national_plan"], prefix + ".land_use_zones.national_plan");
                ValidateText(zones["other_laws"], prefix + ".land_use_zones.other_laws");
            }
            if (parcel["pnu"] is JsonValue pnu)
            {
                var text = pnu.GetValue<string>();
                if (text.Length != 19 || text.Any(c => c < '0' || c > '9'))
                    throw new ArgumentException($"Info data.{prefix}.pnu는 19자리 숫자 문자열이어야 합니다.");
            }
        }
    }

    private static void ValidateText(JsonNode? value, string key)
    {
        if (value is null) return;
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out _))
            throw new ArgumentException($"Info data.{key}는 문자 또는 null이어야 합니다.");
    }

    private static void ValidateNumber(JsonNode? value, string key, bool integer)
    {
        if (value is null) return;
        // Serialize a scalar to handle both parsed JSON numbers and CLR numeric values
        // without accepting numeric strings, booleans or non-finite doubles.
        string raw;
        try
        {
            if (value is not JsonValue || value.GetValueKind() != JsonValueKind.Number)
                throw new ArgumentException();
            raw = value.ToJsonString();
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Info data.{key}는 0 이상의 유한한 숫자 또는 null이어야 합니다.");
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < 0)
            throw new ArgumentException($"Info data.{key}는 0 이상의 유한한 숫자 또는 null이어야 합니다.");
        var exponentAt = raw.IndexOfAny(new[] { 'e', 'E' });
        var coefficient = exponentAt < 0 ? raw : raw[..exponentAt];
        var nonzero = coefficient.Any(c => c >= '1' && c <= '9');
        if (number == 0 && nonzero)
            throw new ArgumentException($"Info data.{key}의 숫자는 표현 가능한 범위보다 작습니다. 0으로 바꾸지 말고 값을 확인하세요.");
        if (integer)
        {
            // Inspect decimal digits so rounding a large double never hides a fraction.
            var pointAt = coefficient.IndexOf('.');
            var fractionalDigits = pointAt < 0 ? 0 : coefficient.Length - pointAt - 1;
            var trailingZeros = 0;
            for (var index = coefficient.Length - 1; index >= 0; index--)
            {
                if (coefficient[index] == '.') continue;
                if (coefficient[index] != '0') break;
                trailingZeros++;
            }
            var exponent = 0L;
            if (exponentAt >= 0 && !long.TryParse(raw[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                exponent = raw[exponentAt + 1] == '-' ? long.MinValue : long.MaxValue;
            if (nonzero && exponent < (long)fractionalDigits - trailingZeros)
                throw new ArgumentException("Info data.max_floors는 0 이상의 정수 또는 null이어야 합니다.");
        }
    }

    /// <summary>RFC 7396 merge patch. Null removes a property; arrays replace as a whole.</summary>
    public static JsonObject Merge(JsonObject current, JsonObject patch)
    {
        // Existing supported data can be repaired by a patch. Validate the complete
        // result before callers are allowed to persist it.
        Validate(current, validateFields: false);
        ArgumentNullException.ThrowIfNull(patch);
        var result = (JsonObject)current.DeepClone();
        MergeInto(result, patch);
        Validate(result);
        return result;
    }

    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var item in patch)
        {
            if (item.Value is null) { target.Remove(item.Key); continue; }
            if (item.Value is JsonObject children)
            {
                if (target[item.Key] is not JsonObject destination)
                {
                    destination = new JsonObject();
                    target[item.Key] = destination;
                }
                MergeInto(destination, children);
            }
            else target[item.Key] = item.Value.DeepClone();
        }
    }

    /// <summary>Changes made by the form, including explicitly removed fields.</summary>
    public static JsonObject Difference(JsonObject before, JsonObject after)
    {
        var patch = new JsonObject();
        foreach (var item in before)
            if (!after.ContainsKey(item.Key)) patch[item.Key] = null;
        foreach (var item in after)
        {
            if (before.TryGetPropertyValue(item.Key, out var old) && JsonNode.DeepEquals(old, item.Value)) continue;
            patch[item.Key] = old is JsonObject oldObject && item.Value is JsonObject newObject
                ? Difference(oldObject, newObject) : item.Value?.DeepClone();
        }
        return patch;
    }
}
