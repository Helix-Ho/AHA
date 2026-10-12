using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aha.EumCrawl;

/// <summary>Parses only observed Eum table cells. Diagrams and general law text never become numeric limits.</summary>
public static class EumParser
{
    public const string RatioPolicyNotice = "건폐율·용적률은 토지이음 각 조례 항목에서 읽은 %의 최솟값입니다. 조건·특례·중첩 규정을 판정하지 않아 실제 허용값과 다를 수 있습니다. 토지이음 원문을 확인하세요.";
    public static readonly string[] NumericFields = { "bcr_percent", "far_percent", "setback_m", "max_height_m", "max_floors" };
    public static readonly string[] ParcelFields = { "land_category", "area_m2", "land_use_zones" };
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private static MatchCollection Matches(string input, string pattern) => Regex.Matches(input, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, MatchTimeout);

    public static bool IsPnu(string value) => value.Length == 19 && value.All(c => c is >= '0' and <= '9')
        && value[10] is '1' or '2';

    public static string Input(string html, string name)
    {
        var found = "";
        foreach (Match input in Matches(html, "<input\\b[^>]*>"))
        {
            var attributes = Attributes(input.Value);
            if ((attributes.GetValueOrDefault("name") == name || attributes.GetValueOrDefault("id") == name)
                && attributes.TryGetValue("value", out var value))
            {
                if (attributes.GetValueOrDefault("id") == name) return value.Trim();
                if (!string.IsNullOrWhiteSpace(value)) found = value.Trim();
            }
        }
        return found;
    }

    public static string ParcelAddress(string html)
    {
        foreach (Match row in Matches(html, "<tr\\b[^>]*>(.*?)</tr>"))
        {
            var cells = Matches(row.Groups[1].Value, "<t[dh]\\b[^>]*>(.*?)</t[dh]>");
            if (cells.Count >= 2 && Text(cells[0].Groups[1].Value) == "소재지") return Text(cells[1].Groups[1].Value);
        }
        return "";
    }

    public static string MarkCodes(string html) => string.Join(";", Matches(html, "<input\\b[^>]*>").Cast<Match>()
        .Select(m => Attributes(m.Value)).Where(a => a.GetValueOrDefault("name") == "mark_ucode")
        .Select(a => a.GetValueOrDefault("value", "").Trim()).Where(v => v.Length > 0));

    /// <summary>Observed main-page cells only; map legends and popup explanations are not parcel designations.</summary>
    public static string ParcelReferenceText(string html)
    {
        var fields = ParseParcelFields(html);
        var lines = ParcelFields.Where(f => fields[f]?["status"]?.GetValue<string>() == "candidate")
            .Select(f => fields[f]!["text"]!.GetValue<string>()).ToList();
        var additional = AdditionalReferenceText(html);
        if (additional.Length > 0) lines.Add(additional);
        return string.Join("\n", lines);
    }

    public static JsonObject ParseParcelFields(string html)
    {
        var result = new JsonObject();
        foreach (var field in ParcelFields) result[field] = Unknown("미확인 · 기존값 유지");
        var landClass = Input(html, "present_class_val");
        if (Regex.IsMatch(landClass, "^[가-힣]{1,20}$", RegexOptions.None, MatchTimeout) && IsKnownText(landClass))
            result["land_category"] = Candidate(JsonValue.Create(landClass)!, "지목: " + landClass);
        var area = Text(Cell(html, "present_area"));
        var number = Regex.Match(area, "^([0-9]+(?:,[0-9]{3})*(?:\\.[0-9]+)?)\\s*(?:㎡|m²)$", RegexOptions.None, MatchTimeout);
        if (number.Success && double.TryParse(number.Groups[1].Value.Replace(",", ""), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var squareMetres) && double.IsFinite(squareMetres))
            result["area_m2"] = Candidate(JsonValue.Create(squareMetres)!, "토지이음 면적: " + area);
        var zones = new JsonObject();
        var texts = new List<string>();
        foreach (var (id, key, label) in new[] { ("present_mark1", "national_plan", "국토계획법에 따른 지역·지구"),
            ("present_mark2", "other_laws", "다른 법령에 따른 지역·지구") })
        {
            var value = Text(WithoutPopupDivs(Cell(html, id)));
            if (!IsKnownText(value)) continue;
            zones[key] = value;
            texts.Add(label + ": " + value);
        }
        if (zones.Count > 0) result["land_use_zones"] = Candidate(zones, string.Join("\n", texts));
        return result;
    }

    private static bool IsKnownText(string value) => value.Length > 0 && value is not ("-" or "미확인" or "자료 없음" or "정보 없음");
    public static string AdditionalReferenceText(string html)
    {
        var value = Text(WithoutPopupDivs(Cell(html, "present_mark3")));
        return IsKnownText(value) ? "별도 확인사항: " + value : "";
    }

    public static JsonObject Candidate(JsonNode value, string text) => new()
        { ["status"] = "candidate", ["value"] = value.DeepClone(), ["text"] = text, ["requires_confirmation"] = true };

    private static string Cell(string html, string id)
    {
        foreach (Match cell in Matches(html, "<td\\b([^>]*)>(.*?)</td>"))
            if (Attributes(cell.Groups[1].Value).GetValueOrDefault("id") == id) return cell.Groups[2].Value;
        return "";
    }

    private static string WithoutPopupDivs(string html)
    {
        var result = new StringBuilder();
        var end = 0;
        var depth = 0;
        foreach (Match tag in Matches(html, "</?div\\b[^>]*>"))
        {
            if (depth == 0) result.Append(html, end, tag.Index - end);
            var closing = tag.Value.StartsWith("</", StringComparison.Ordinal);
            if (!closing && (depth > 0 || Attributes(tag.Value).GetValueOrDefault("class", "").Split(' ').Contains("layer_pop"))) depth++;
            else if (closing && depth > 0) depth--;
            end = tag.Index + tag.Length;
        }
        if (depth == 0) result.Append(html, end, html.Length - end);
        return result.ToString();
    }

    public static void ValidateParcel(string html, string pnu)
    {
        if (Input(html, "pnu") != pnu || string.IsNullOrWhiteSpace(ParcelAddress(html)) ||
            !html.Contains("present_class", StringComparison.Ordinal))
            throw new InvalidDataException("토지이음에서 해당 필지를 확인하지 못했습니다. PNU 또는 서비스 응답을 확인하세요.");
    }

    public static JsonObject ParseFields(string gy, string height, string line, string space)
    {
        var result = new JsonObject();
        foreach (var field in NumericFields) result[field] = Unknown("명확한 단일 수치를 확인하지 못했습니다.");
        ParseRatio(gy, "gun_basic_", "bcr_percent", 100, result);
        ParseRatio(gy, "yong_basic_", "far_percent", 10000, result);
        // These require an explicit labelled table row, not the north-daylight diagram or law paragraphs.
        ParseLabelled(height, new[] { "최고높이", "최고 높이", "높이제한", "높이 제한" }, "max_height_m", "m|미터", 10000, result);
        ParseLabelled(height, new[] { "최고층수", "최고 층수", "층수제한", "층수 제한" }, "max_floors", "층", 1000, result);
        // Boundary-specific and use-specific setback rules cannot be reduced to one setback_m.
        var setbackTexts = new[] { (Label: "건축선", Text: Text(line)), (Label: "대지 안의 공지", Text: Text(space)) }
            .Where(item => item.Text.Length > 0).Select(item => item.Label + ": " + item.Text).ToArray();
        if (setbackTexts.Length > 0)
        {
            result["setback_m"] = Unknown("건축선·대지 안의 공지는 경계와 건축물 조건을 확인해야 합니다.", "conditional");
            result["setback_m"]!["source_text"] = string.Join("\n", setbackTexts);
        }
        return result;
    }

    private static void ParseRatio(string html, string prefix, string field, double maximum, JsonObject result)
    {
        var texts = new List<string>();
        foreach (Match cell in Matches(html, "<td\\b[^>]*>(.*?)</td>"))
        {
            var content = cell.Groups[1].Value;
            if (!Matches(content, "<input\\b[^>]*>").Cast<Match>().Any(m =>
                    Attributes(m.Value).GetValueOrDefault("name", "").StartsWith(prefix, StringComparison.Ordinal))) continue;
            texts.Add(Text(content));
        }
        var numbers = texts.SelectMany(Percentages).Where(value => value >= 0 && value <= maximum)
            .Distinct().OrderBy(value => value).ToArray();
        result[field] = numbers.Length > 0
            ? Candidate(JsonValue.Create(numbers[0])!, string.Join(" / ", texts))
            : Unknown(texts.Count > 0 ? string.Join(" / ", texts) : "백분율 수치를 확인하지 못했습니다.");
        result[field]!["method"] = "minimum_percentage";
        result[field]!["notice"] = RatioPolicyNotice;
        result[field]!["observed_percentages"] = new JsonArray(numbers.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (texts.Count == 0) return;
        var evidence = new List<string>();
        foreach (Match row in Matches(html, "<tr\\b[^>]*>(.*?)</tr>"))
        {
            var cells = Matches(row.Groups[1].Value, "<t[dh]\\b[^>]*>(.*?)</t[dh]>");
            foreach (Match cell in cells)
                if (Matches(cell.Groups[1].Value, "<input\\b[^>]*>").Cast<Match>().Any(m =>
                    Attributes(m.Value).GetValueOrDefault("name", "").StartsWith(prefix, StringComparison.Ordinal)))
                    evidence.Add((cells.Count > 1 ? Text(cells[0].Groups[1].Value) + " · " : "") +
                        FieldLabel(field) + ": " + Text(cell.Groups[1].Value));
        }
        result[field]!["source_text"] = evidence.Count > 0 ? string.Join("\n", evidence) : FieldLabel(field) + ": " + string.Join(" / ", texts);
    }

    private static IEnumerable<double> Percentages(string text)
    {
        // Units must be visible. A shared trailing unit also covers range endpoints (100~250%).
        // Negative/malformed tokens, dates, areas, hidden values and page summary diagrams are not inputs.
        const string number = @"[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?";
        const string unit = @"%|％|퍼센트";
        var pattern = $@"(?<![0-9.,+\-−–—])(?<number>{number})(?:\s*(?:{unit})?\s*(?:~|～|〜|∼|–|—|−|-|부터|에서)\s*(?<number>{number}))*\s*(?:{unit})";
        foreach (Match expression in Matches(text, pattern))
        {
            // A separated sign must not turn a negative/malformed value into a positive substring.
            var preceding = expression.Index - 1;
            while (preceding >= 0 && char.IsWhiteSpace(text[preceding])) preceding--;
            if (preceding >= 0 && "+-−–—".Contains(text[preceding])) continue;
            foreach (Capture capture in expression.Groups["number"].Captures)
                if (double.TryParse(capture.Value.Replace(",", ""), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) yield return value;
        }
    }

    private static void ParseLabelled(string html, string[] labels, string field, string unit, double maximum, JsonObject result)
    {
        var texts = new List<string>();
        foreach (Match row in Matches(html, "<tr\\b[^>]*>(.*?)</tr>"))
        {
            var cells = Matches(row.Groups[1].Value, "<t[dh]\\b[^>]*>(.*?)</t[dh]>");
            if (cells.Count == 2 && labels.Contains(Text(cells[0].Groups[1].Value))) texts.Add(Text(cells[1].Groups[1].Value));
        }
        SetFromTexts(texts, field, unit, maximum, result);
    }

    private static void SetFromTexts(List<string> texts, string field, string unit, double maximum, JsonObject result)
    {
        if (texts.Count == 0) return;
        var combined = string.Join(" / ", texts);
        // Heights/floors still need one explicit, unconditional number; the ratio minimum policy is separate.
        var number = Regex.Match(texts[0], $"^([0-9]+(?:\\.[0-9]+)?)\\s*(?:{unit})\\s*(?:이하)?$",
            RegexOptions.CultureInvariant, MatchTimeout);
        if (texts.Count != 1 || !number.Success ||
            !double.TryParse(number.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0 || value > maximum || field == "max_floors" && value != Math.Truncate(value))
        {
            result[field] = Unknown(combined, "conditional");
            return;
        }
        result[field] = new JsonObject { ["status"] = "candidate", ["value"] = value,
            ["text"] = combined, ["requires_confirmation"] = true };
    }

    public static JsonObject Unknown(string text, string status = "unknown") => new()
        { ["status"] = status, ["text"] = text, ["requires_confirmation"] = true };

    public static string FieldLabel(string field) => field switch
    {
        "bcr_percent" => "건폐율(조례)", "far_percent" => "용적률(조례)", "setback_m" => "이격",
        "max_height_m" => "최고높이", "max_floors" => "최고층수",
        "land_category" or "parcels.land_category" => "지목",
        "area_m2" or "parcels.area_m2" => "토지이음 면적 (㎡)",
        "land_use_zones" or "parcels.land_use_zones" => "용도지역·지구",
        _ => "토지이용계획 참고"
    };

    public static string Notes(IEnumerable<JsonObject> parcels) => string.Join("\n\n", parcels.Select(p =>
        $"토지이음 참고조회 · {p["address"]} · PNU {p["pnu"]}\n" +
        RatioPolicyNotice + "\n" +
        (string.IsNullOrEmpty(p["additional_reference_text"]?.GetValue<string>()) ? "" : p["additional_reference_text"] + "\n") +
        string.Join("\n", NumericFields.Select(f =>
            (p["fields"]?[f]?["method"]?.GetValue<string>() == "minimum_percentage" &&
             p["fields"]?[f]?["value"] is JsonValue value && value.TryGetValue<double>(out var ratio)
                ? $"{FieldLabel(f)} 최솟값 참고: {ratio.ToString(CultureInfo.InvariantCulture)}%\n" : "") +
            (p["fields"]?[f]?["source_text"]?.GetValue<string>() ?? $"{FieldLabel(f)}: {p["fields"]?[f]?["text"]}")))));

    public static string ReferenceDate(string html, string category)
    {
        var hidden = Input(html, "base_date" + category);
        if (Regex.IsMatch(hidden, "^[0-9]{4}[.-][0-9]{2}[.-][0-9]{2}$", RegexOptions.None, MatchTimeout)) return hidden;
        var match = Regex.Match(Text(html), "(?:규제\\s*법령|작성)\\s*기준일\\s*:\\s*([0-9]{4}[.-][0-9]{2}[.-][0-9]{2})",
            RegexOptions.None, MatchTimeout);
        return match.Success ? match.Groups[1].Value : "";
    }

    public static string Text(string html)
    {
        var clean = Regex.Replace(html, "<(script|style)\\b[^>]*>.*?</\\1>|<!--.*?-->", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, MatchTimeout);
        clean = Regex.Replace(clean, "<[^>]*>", " ", RegexOptions.Singleline, MatchTimeout);
        return Regex.Replace(WebUtility.HtmlDecode(clean), "\\s+", " ", RegexOptions.None, MatchTimeout).Trim();
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Matches(tag, "([a-zA-Z_][a-zA-Z0-9_-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s/>]+))"))
            result[attribute.Groups[1].Value] = WebUtility.HtmlDecode(attribute.Groups[2].Success
                ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
        return result;
    }
}
