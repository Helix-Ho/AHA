using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aha.EumCrawl;

internal sealed record EumSourcePage(string Section, string Title, string Code, string Url, string Html, string RetrievedAt, string? Error = null);

/// <summary>Source reading, not a legal parser: preserve text and table relationships without choosing numerical values.</summary>
public static class EumEvidence
{
    public static readonly string[] SectionNames = ["parcel", "bcr_far", "height_floors", "building_line", "open_space"];
    public const string Notice = "토지이음에서 받은 항목 원문을 구조화한 자료입니다. 수치 선택·적용성·예외 판정을 하지 않습니다. 표의 조건과 원문 링크를 함께 확인하세요. 브라우저·JavaScript·도면 및 외부 링크의 내용은 읽지 않습니다.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex TagPattern = new("\\G<(?<close>/)?(?<tag>[a-zA-Z][a-zA-Z0-9:-]*)(?<attrs>(?:[^>\"']|\"[^\"]*\"|'[^']*')*)>", RegexOptions.Singleline, Timeout);
    private const int MaxNodes = 50000;
    private const int MaxText = 100000;

    public static string[] ValidateSections(string[]? sections)
    {
        var selected = sections ?? SectionNames;
        if (selected.Length == 0 || selected.Length > SectionNames.Length ||
            selected.Any(s => s is null || !SectionNames.Contains(s, StringComparer.Ordinal)) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("sections는 parcel, bcr_far, height_floors, building_line, open_space 중 중복 없는 항목이어야 합니다.");
        return selected.ToArray();
    }

    internal static JsonObject Render(string pnu, EumSourcePage main, IReadOnlyList<EumSourcePage> pages)
    {
        var sections = new JsonArray(); var warnings = new JsonArray();
        foreach (var page in pages)
        {
            var section = new JsonObject { ["name"] = page.Section, ["title"] = page.Title, ["source_url"] = page.Url,
                ["retrieved_at"] = page.RetrievedAt, ["reference_date"] = EumParser.ReferenceDate(page.Html, page.Code) };
            var error = page.Error;
            JsonArray blocks = new();
            if (error is null)
                try { blocks = Extract(page.Html, page.Url, page.Section == "parcel"); }
                catch (InvalidDataException ex) { error = ex.Message; }
            section["status"] = error is not null ? "failed" : blocks.Count == 0 ? "no_content" : "ok";
            section["blocks"] = blocks;
            if (error is not null) { section["error"] = error; warnings.Add(page.Section + ": " + error); }
            if (error is null && blocks.Count == 0) warnings.Add(page.Section + ": 읽을 수 있는 본문을 확인하지 못했습니다. 규제 없음으로 해석하지 마세요.");
            if (HasUncomputed(blocks)) warnings.Add(page.Section + ": 브라우저에서 계산·갱신하는 도식과 면적 표시의 초기값은 제외했습니다. 해당 표시는 미확인이며 원문 열람 페이지에서 확인하세요.");
            sections.Add(section);
        }
        return new JsonObject { ["pnu"] = pnu, ["address"] = EumParser.ParcelAddress(main.Html), ["url"] = main.Url,
            ["retrieved_at"] = main.RetrievedAt, ["status"] = sections.OfType<JsonObject>().Any(s => s["status"]!.GetValue<string>() != "ok") ? "partial" : "ok",
            ["sections"] = sections, ["warnings"] = warnings, ["notice"] = Notice };
    }

    /// <summary>Deterministic, bounded Eum HTML fragment reader. No CSS engine, script execution or linked-page traversal.</summary>
    public static JsonArray Extract(string html, string sourceUrl, bool parcel = false)
    {
        Node root;
        try { root = Parse(html); }
        catch (RegexMatchTimeoutException ex) { throw new InvalidDataException("항목 HTML을 제한 시간 안에 읽지 못했습니다.", ex); }
        JsonArray result;
        if (parcel)
        {
            // Select only parcel fact cells, not navigation, legends or repeated popup explanations.
            var rows = new JsonArray();
            foreach (var (id, label) in new[] { ("present_class", "지목"), ("present_area", "면적"),
                ("present_mark1", "국토계획법에 따른 지역·지구"), ("present_mark2", "다른 법령에 따른 지역·지구"), ("present_mark3", "별도 확인사항") })
            {
                var cell = Visible(root).FirstOrDefault(n => n.Attributes.GetValueOrDefault("id") == id);
                if (cell is null) continue;
                rows.Add(new JsonObject { ["cells"] = new JsonArray(
                    new JsonObject { ["header"] = true, ["rowspan"] = 1, ["colspan"] = 1, ["blocks"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["text"] = label }) },
                    Cell(cell, sourceUrl)) });
            }
            result = rows.Count == 0 ? new() : new(new JsonObject { ["type"] = "table", ["rows"] = rows });
        }
        else result = Blocks(root, sourceUrl);
        if (TextSize(result) > MaxText) throw new InvalidDataException("항목 원문이 처리 한도를 초과했습니다. 일부를 잘라 반환하지 않습니다.");
        return result;
    }

    private static int TextSize(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => text.Length,
        JsonObject o => o.Sum(p => TextSize(p.Value)), JsonArray a => a.Sum(TextSize), _ => 0
    };
    private static bool HasUncomputed(JsonNode? node) => node switch
    {
        JsonObject o => o["status"] is JsonValue v && v.TryGetValue<string>(out var status) && status == "not_computed" || o.Any(p => HasUncomputed(p.Value)),
        JsonArray a => a.Any(HasUncomputed), _ => false
    };
    private sealed class Node(string tag, string text = "")
    {
        public string Tag { get; } = tag;
        public string Text { get; } = text;
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Node> Children { get; } = new();
    }
    private static IEnumerable<Node> Descendants(Node node)
    { foreach (var child in node.Children) { yield return child; foreach (var item in Descendants(child)) yield return item; } }
    private static bool Hidden(Node node)
    {
        if (node.Tag is "script" or "style" or "input" or "button" or "select" or "textarea" or "noscript" or "svg" or "canvas") return true;
        var style = node.Attributes.GetValueOrDefault("style", "");
        return node.Attributes.ContainsKey("hidden") || node.Attributes.GetValueOrDefault("aria-hidden") == "true" ||
            Regex.IsMatch(style, @"(?:display\s*:\s*none|visibility\s*:\s*hidden)", RegexOptions.IgnoreCase, Timeout) ||
            node.Attributes.GetValueOrDefault("class", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(c => c is "layer_pop" or "ico_q");
    }
    private static string? Uncomputed(Node node)
    {
        // Eum's luLandDetUse.js replaces these UI placeholders after client-side calculation.
        // Match their roles, not their displayed number: a genuine 0% in a regulation cell is evidence.
        if (node.Attributes.GetValueOrDefault("class", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("coverage_wrap"))
            return "산출 도식은 브라우저 계산 전이므로 미확인입니다. 초기 비율·조건 문구를 규제값으로 제공하지 않습니다.";
        var id = node.Attributes.GetValueOrDefault("id", "");
        if (id is "areaTotal" or "areaTotalM" || Regex.IsMatch(id, @"^areaM?U[A-Z0-9]{5}$", RegexOptions.None, Timeout))
            return "산출면적은 브라우저 계산 전이므로 미확인입니다. 초기 표시값을 면적으로 제공하지 않습니다.";
        return id == "gy_addr" ? "소재지 표시를 브라우저가 채우기 전입니다. 조회 결과의 address를 확인하세요." : null;
    }
    private static JsonObject UncomputedBlock(string reason) => new() { ["type"] = "paragraph", ["status"] = "not_computed", ["text"] = reason };
    private static JsonArray Blocks(Node node, string sourceUrl)
    {
        var blocks = new JsonArray(); var pending = new List<Node>();
        void Flush()
        {
            if (pending.Count == 0) return;
            var wrapper = new Node("span"); wrapper.Children.AddRange(pending); pending.Clear();
            var text = Plain(wrapper); if (text.Length == 0) return;
            blocks.Add(Paragraph(wrapper, "paragraph", sourceUrl));
        }
        foreach (var child in node.Children)
        {
            if (Hidden(child)) continue;
            if (Uncomputed(child) is { } reason) { Flush(); blocks.Add(UncomputedBlock(reason)); }
            else if (child.Tag == "table") { Flush(); var table = Table(child, sourceUrl); if (table["rows"]!.AsArray().Count > 0 || table["notes"] is JsonArray { Count: > 0 }) blocks.Add(table); }
            else if (child.Tag is "tr" or "td" or "th")
            {
                Flush(); var table = new Node("table"); table.Children.Add(child);
                blocks.Add(Table(table, sourceUrl));
            }
            else if (child.Tag is "p" or "li" or "dt" or "dd" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Flush();
                if (Descendants(child).Any(n => n.Tag == "table")) { foreach (var b in Blocks(child, sourceUrl)) blocks.Add(b!.DeepClone()); }
                else if (Plain(child).Length > 0) blocks.Add(Paragraph(child, child.Tag.StartsWith('h') ? "heading" : child.Tag is "li" or "dt" or "dd" ? "list_item" : "paragraph", sourceUrl));
            }
            else if (child.Tag is "div" or "section" or "article" or "main" or "body" or "html" or "ul" or "ol" or "dl" or "form" or "header" or "footer" or "nav")
            { Flush(); foreach (var block in Blocks(child, sourceUrl)) blocks.Add(block!.DeepClone()); }
            else if (Descendants(child).Any(n => n.Tag is "table" or "p" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"))
            { Flush(); foreach (var block in Blocks(child, sourceUrl)) blocks.Add(block!.DeepClone()); }
            else pending.Add(child);
        }
        Flush(); return blocks;
    }
    private static JsonObject Paragraph(Node node, string type, string sourceUrl)
    {
        var result = new JsonObject { ["type"] = type, ["text"] = Plain(node) };
        if (type == "heading") result["level"] = int.Parse(node.Tag[1..]);
        var links = new JsonArray();
        foreach (var link in Visible(node).Where(n => n.Tag == "a"))
        {
            var href = link.Attributes.GetValueOrDefault("href", "").Trim();
            if (href.Length == 0 || !Uri.TryCreate(new Uri(sourceUrl), href, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) continue;
            links.Add(new JsonObject { ["text"] = Plain(link), ["url"] = uri.AbsoluteUri });
        }
        if (links.Count > 0) result["links"] = links;
        return result;
    }
    private static IEnumerable<Node> Visible(Node node)
    { if (Hidden(node) || Uncomputed(node) is not null) yield break; yield return node; foreach (var child in node.Children) foreach (var value in Visible(child)) yield return value; }
    private static string Plain(Node node)
    {
        var text = new StringBuilder();
        void Add(Node n)
        {
            if (Hidden(n) || Uncomputed(n) is not null) return;
            if (n.Tag == "#text") text.Append(n.Text);
            else { if (n.Tag == "br") text.Append('\n'); foreach (var child in n.Children) Add(child); if (n.Tag is "p" or "li" or "div") text.Append('\n'); }
        }
        Add(node);
        return string.Join("\n", text.ToString().Split('\n').Select(line => Regex.Replace(line, @"\s+", " ", RegexOptions.None, Timeout).Trim()).Where(line => line.Length > 0));
    }
    private static JsonObject Table(Node table, string sourceUrl)
    {
        var rows = new JsonArray(); var notes = new JsonArray();
        void Visit(Node n)
        {
            if (Hidden(n)) return;
            if (n.Tag == "tr")
            {
                var cells = new JsonArray(); foreach (var cell in n.Children.Where(c => c.Tag is "td" or "th" && !Hidden(c))) cells.Add(Cell(cell, sourceUrl));
                if (cells.Count > 0) rows.Add(new JsonObject { ["cells"] = cells }); return;
            }
            if (n.Tag is "td" or "th") { rows.Add(new JsonObject { ["cells"] = new JsonArray(Cell(n, sourceUrl)) }); return; }
            if (n.Tag is "table" or "thead" or "tbody" or "tfoot")
            { foreach (var child in n.Children) Visit(child); return; }
            if (n.Tag is "caption" or "colgroup" or "col") return;
            var wrapper = new Node("div"); wrapper.Children.Add(n);
            foreach (var block in Blocks(wrapper, sourceUrl)) notes.Add(block!.DeepClone());
        }
        Visit(table);
        var result = new JsonObject { ["type"] = "table", ["rows"] = rows };
        if (notes.Count > 0) result["notes"] = notes;
        var caption = table.Children.FirstOrDefault(n => n.Tag == "caption"); if (caption is not null && Plain(caption).Length > 0) result["caption"] = Plain(caption);
        return result;
    }
    private static JsonObject Cell(Node node, string sourceUrl)
    {
        int Span(string name)
        {
            if (!node.Attributes.TryGetValue(name, out var value)) return 1;
            if (!int.TryParse(value, out var number) || number < (name == "rowspan" ? 0 : 1) || number > 1000) throw new InvalidDataException("표 셀 병합 범위를 읽을 수 없습니다.");
            return number;
        }
        var reason = Uncomputed(node);
        var cell = new JsonObject { ["header"] = node.Tag == "th", ["rowspan"] = Span("rowspan"), ["colspan"] = Span("colspan"), ["blocks"] = reason is null ? Blocks(node, sourceUrl) : new JsonArray(UncomputedBlock(reason)) };
        if (reason is not null) cell["status"] = "not_computed";
        foreach (var attr in new[] { "scope", "headers", "id" }) if (node.Attributes.TryGetValue(attr, out var value)) cell[attr] = value;
        return cell;
    }

    private static Node Parse(string html)
    {
        var root = new Node("root"); var stack = new List<Node> { root }; int index = 0, nodes = 0;
        void Add(Node node) { if (++nodes > MaxNodes) throw new InvalidDataException("항목 HTML 구조가 처리 한도를 초과했습니다."); stack[^1].Children.Add(node); }
        while (index < html.Length)
        {
            if (html[index] != '<')
            {
                var end = html.IndexOf('<', index); if (end < 0) end = html.Length;
                Add(new Node("#text", WebUtility.HtmlDecode(html[index..end]))); index = end; continue;
            }
            if (html.AsSpan(index).StartsWith("<!--")) { var end = html.IndexOf("-->", index + 4, StringComparison.Ordinal); index = end < 0 ? html.Length : end + 3; continue; }
            var match = TagPattern.Match(html, index);
            if (!match.Success) { if (index + 1 < html.Length && html[index + 1] is '!' or '?') { var end = html.IndexOf('>', index); index = end < 0 ? html.Length : end + 1; } else { Add(new Node("#text", "<")); index++; } continue; }
            index += match.Length; var tag = match.Groups["tag"].Value.ToLowerInvariant();
            if (match.Groups["close"].Success)
            { var at = stack.FindLastIndex(n => n.Tag == tag); if (at > 0) stack.RemoveRange(at, stack.Count - at); continue; }
            if (tag is "script" or "style")
            { var end = html.IndexOf("</" + tag, index, StringComparison.OrdinalIgnoreCase); var close = end < 0 ? -1 : html.IndexOf('>', end); index = close < 0 ? html.Length : close + 1; continue; }
            if (tag is "td" or "th" or "tr")
            {
                var boundary = stack.FindLastIndex(n => n.Tag == "table");
                var cell = stack.FindLastIndex(n => n.Tag is "td" or "th"); if (cell > boundary) stack.RemoveRange(cell, stack.Count - cell);
                if (tag == "tr") { var row = stack.FindLastIndex(n => n.Tag == "tr"); if (row > boundary) stack.RemoveRange(row, stack.Count - row); }
            }
            if (tag is "p" or "div" or "table" or "h1" or "h2" or "h3" or "h4") { var p = stack.FindLastIndex(n => n.Tag == "p"); if (p > 0) stack.RemoveRange(p, stack.Count - p); }
            if (tag == "li" && stack[^1].Tag == "li") stack.RemoveAt(stack.Count - 1);
            var node = new Node(tag);
            foreach (Match a in Regex.Matches(match.Groups["attrs"].Value, "(?<name>[a-zA-Z_][a-zA-Z0-9_:-]*)(?:\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<uq>[^\\s/>]+)))?", RegexOptions.Singleline, Timeout))
                node.Attributes[a.Groups["name"].Value] = WebUtility.HtmlDecode(a.Groups["dq"].Success ? a.Groups["dq"].Value : a.Groups["sq"].Success ? a.Groups["sq"].Value : a.Groups["uq"].Value);
            Add(node);
            if (tag is not ("input" or "br" or "hr" or "img" or "meta" or "link" or "area" or "base" or "embed" or "source" or "wbr") && !match.Groups["attrs"].Value.TrimEnd().EndsWith('/'))
            { if (stack.Count >= 100) throw new InvalidDataException("항목 HTML 중첩이 처리 한도를 초과했습니다."); stack.Add(node); }
        }
        return root;
    }
}
