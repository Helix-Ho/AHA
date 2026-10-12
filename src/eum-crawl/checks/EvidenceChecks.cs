using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aha.EumCrawl;
using Aha.EumCrawl.Checks;

internal static class EvidenceChecks
{
    private static string Pnu => FixtureData.Facts.GetProperty("pnu").GetString()!;
    private static double CalculatedBcr => FixtureData.Facts.GetProperty("calculated_bcr_percent").GetDouble();
    private static double CalculatedFar => FixtureData.Facts.GetProperty("calculated_far_percent").GetDouble();
    internal static async Task RunAsync(string fixtures)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        const string html = """
            <h3>겹친 지역·조건별 규제</h3><p>각 조건을 확인하세요. <a href="/web/example.jsp?x=1&amp;y=2">원문</a>
            <a href="javascript:alert(1)">접근 불가</a><a href="data:text/html,bad">실행 금지</a></p>
            <table><caption>건폐율·용적률</caption><thead><tr><th rowspan=2 scope=row>지역</th><th colspan='2'>비율</th></tr>
            <tr><th>건폐율</th><th>용적률</th></tr></thead><tbody><tr><th>가상 지역</th><td>57%</td><td>일반 170%<br>특례 205%</td></tr>
            <tr><th>다른 지구</th><td>35%</td><td>80~160% (예외 제외)</td></tr></tbody></table>
            <p>규제 적용에는 별도 조건이 있습니다.</p><ul><li>조건 A<li>조건 B</ul>
            <p hidden>숨긴 999%</p><p style="display: none">숨긴 888%</p><script>bad(777)</script><input value='666'>
            """;
        var blocks = EumEvidence.Extract(html, EumClient.ParcelUrl(Pnu));
        var table = blocks.OfType<JsonObject>().Single(b => b["type"]!.GetValue<string>() == "table");
        Check(table["caption"]!.GetValue<string>() == "건폐율·용적률" && table["rows"]!.AsArray().Count == 4, "Preserve table caption, header and data row order");
        Check(table["rows"]![0]!["cells"]![0]!["rowspan"]!.GetValue<int>() == 2 && table["rows"]![0]!["cells"]![1]!["colspan"]!.GetValue<int>() == 2
            && table["rows"]![0]!["cells"]![0]!["header"]!.GetValue<bool>(), "Preserve merged table cells and header semantics");
        Check(table["rows"]![2]!["cells"]![2]!["blocks"]![0]!["text"]!.GetValue<string>() == "일반 170%\n특례 205%", "Keep both percentages and condition labels; no minimum selection");
        var json = blocks.ToJsonString();
        Check(!json.Contains("minimum_percentage") && !json.Contains("candidate") && !json.Contains("999") && !json.Contains("888") && !json.Contains("777") && !json.Contains("666"), "No heuristic or hidden input/script values in evidence");
        var links = blocks.OfType<JsonObject>().Where(b => b["links"] is JsonArray).SelectMany(b => b["links"]!.AsArray()).ToArray();
        Check(links.Length == 1 && links[0]!["url"]!.GetValue<string>() == "https://www.eum.go.kr/web/example.jsp?x=1&y=2", "Resolve relative HTTP links and reject executable schemes");
        Check(blocks.OfType<JsonObject>().Count(b => b["type"]!.GetValue<string>() == "list_item") == 2, "Preserve omitted closing li tags");
        foreach (var hiddenParent in new[] { "hidden", "style='display: none'", "aria-hidden='true'", "class='layer_pop'" })
        {
            var concealed = "<div " + hiddenParent + "><table><tr><td id='present_area'>999 ㎡</td></tr></table></div>";
            Check(EumEvidence.Extract(concealed, EumClient.ParcelUrl(Pnu), parcel: true).Count == 0,
                "Parcel facts inside a hidden ancestor are not source evidence: " + hiddenParent);
            var visible = EumEvidence.Extract(concealed + "<table><tr><td id='present_area'>123.4 ㎡</td></tr></table>",
                EumClient.ParcelUrl(Pnu), parcel: true);
            Check(visible[0]!["rows"]![0]!["cells"]![1]!["blocks"]![0]!["text"]!.GetValue<string>() == "123.4 ㎡",
                "A hidden duplicate cannot shadow the visible parcel fact: " + hiddenParent);
        }
        var nested = EumEvidence.Extract("<table><tr><td>outer<table><tr><th>nested</th><td>3m</td></tr></table></td></tr></table>", EumClient.ParcelUrl(Pnu));
        Check(nested[0]!["rows"]!.AsArray().Count == 1 && nested[0]!["rows"]![0]!["cells"]![0]!["blocks"]![1]!["type"]!.GetValue<string>() == "table", "Nested tables remain inside their cell, not duplicated as outer rows");
        var wrapped = EumEvidence.Extract("<center><table><tr><td>60%</td></tr><p>단, 특정 조건에서만 적용</p></table></center>", EumClient.ParcelUrl(Pnu));
        Check(wrapped[0]!["rows"]!.AsArray().Count == 1 && wrapped[0]!["notes"]![0]!["text"]!.GetValue<string>() == "단, 특정 조건에서만 적용", "Unknown layout wrapper and table footnote retain structure and conditions");
        var omitted = EumEvidence.Extract("<table><tr><th>A<th>B<tr><td>1<td>2</table>", EumClient.ParcelUrl(Pnu));
        Check(omitted[0]!["rows"]!.AsArray().Count == 2 && omitted[0]!["rows"]![1]!["cells"]!.AsArray().Count == 2, "Tolerate ordinary omitted row/cell closing tags");
        var calculatedUi = File.ReadAllText(FixtureData.PathFor("ratios-calculated-ui.html"));
        var separated = EumEvidence.Extract(calculatedUi, EumClient.ParcelUrl(Pnu));
        Check(separated[1]!["status"]!.GetValue<string>() == "not_computed" &&
            separated[2]!["rows"]![0]!["cells"]![1]!["status"]!.GetValue<string>() == "not_computed" &&
            separated[2]!["rows"]![0]!["cells"]![3]!["status"]!.GetValue<string>() == "not_computed" &&
            separated[3]!["rows"]![1]!["cells"]![1]!["status"]!.GetValue<string>() == "not_computed",
            "Eum client-calculated diagram and area cells are explicit unknowns; table relationships remain intact");
        Check(separated[4]!["rows"]![1]!["cells"]![1]!["blocks"]![0]!["text"]!.GetValue<string>() == CalculatedBcr + "%" &&
            separated[4]!["rows"]![1]!["cells"]![2]!["blocks"]![0]!["text"]!.GetValue<string>() == CalculatedFar + "%",
            "Regulation table numbers remain evidence independently of uncomputed chart placeholders");
        var actualZero = EumEvidence.Extract(calculatedUi.Replace(CalculatedBcr + "%", "0%"), EumClient.ParcelUrl(Pnu));
        Check(actualZero[4]!["rows"]![1]!["cells"]![1]!["blocks"]![0]!["text"]!.GetValue<string>() == "0%",
            "A genuine regulation cell 0% is preserved; no content-based zero filtering");
        Check(!separated[1]!["text"]!.GetValue<string>().Contains("0%") &&
            !separated[2]!["rows"]![0]!["cells"]![1]!["blocks"]![0]!["text"]!.GetValue<string>().Contains("0㎡"),
            "Client-side UI initial numbers are not emitted as evidence");
        var changedPlaceholder = EumEvidence.Extract(calculatedUi.Replace("0%", "99%"), EumClient.ParcelUrl(Pnu));
        Check(changedPlaceholder[1]!["status"]!.GetValue<string>() == "not_computed" && !changedPlaceholder[1]!["text"]!.GetValue<string>().Contains("99%"),
            "Identify derived UI by source role, independently of placeholder value");
        foreach (var invalid in new[] { Array.Empty<string>(), new[] { "bcr_far", "bcr_far" }, new[] { "unknown" } })
        { try { EumEvidence.ValidateSections(invalid); throw new Exception("Invalid section selection accepted"); } catch (ArgumentException) { } }
        try { EumEvidence.Extract("<p>" + new string('가', 100001) + "</p>", EumClient.ParcelUrl(Pnu)); throw new Exception("Overlong source silently returned"); }
        catch (InvalidDataException) { }
        var handler = new FixtureHandler(fixtures);
        using (var client = new EumClient(handler))
        {
            var read = await client.ReadEvidenceAsync(Pnu);
            Check(read["status"]!.GetValue<string>() == "ok" && read["sections"] is JsonArray { Count: 5 } && handler.Requests.Count == 5, "Shared acquisition obtains main and four requested regulation sections");
            Check(read["address"]!.GetValue<string>() == FixtureData.Facts.GetProperty("address").GetString() && read["url"]!.GetValue<string>() == EumClient.ParcelUrl(Pnu), "CP949 identity and public link preserved");
            Check(read["fields"] is null && read["preview_id"] is null && !read.ToJsonString().Contains("minimum_percentage"), "Read evidence has no preview token or heuristic candidate schema");
            Check(read["sections"]![1]!["reference_date"]!.GetValue<string>() == FixtureData.Facts.GetProperty("ratio_date").GetString(), "Legal reference date retained outside hidden HTML");
        }
        var subset = new FixtureHandler(fixtures);
        using (var client = new EumClient(subset))
        {
            var read = await client.ReadEvidenceAsync(Pnu, ["bcr_far"]);
            Check(subset.Requests.Count == 2 && read["sections"] is JsonArray { Count: 1 }, "Only requested sections returned/fetched after required parcel identity request");
        }
        using (var client = new EumClient(new FixtureHandler(fixtures, failTab: true)))
        {
            var read = await client.ReadEvidenceAsync(Pnu);
            Check(read["status"]!.GetValue<string>() == "partial" && read["sections"]![1]!["status"]!.GetValue<string>() == "failed"
                && read["sections"]![2]!["status"]!.GetValue<string>() == "ok" && read["url"]!.GetValue<string>() == EumClient.ParcelUrl(Pnu), "Explicit partial failure preserves other source sections and public URL");
        }
        var main = File.ReadAllText(FixtureData.PathFor("parcel-recorded.html"));
        using (var client = new EumClient(new EvidenceHandler(main, calculatedUi)))
        {
            var read = await client.ReadEvidenceAsync(Pnu, ["bcr_far"]);
            Check(read["status"]!.GetValue<string>() == "ok" && read["warnings"] is JsonArray { Count: 1 } &&
                read["sections"]![0]!["blocks"]![1]!["status"]!.GetValue<string>() == "not_computed",
                "Read response exposes uncomputed visualization warning while retaining valid regulation evidence");
        }
        foreach (var bad in new[] { "<p>규제 " + new string('가', 30001) + "</p>", "<p>규제</p><input id=pnu value='9900000000100020000'>", "<p>규제</p>" + new string('x', 2 * 1024 * 1024) })
        {
            using var client = new EumClient(new EvidenceHandler(main, bad));
            var read = await client.ReadEvidenceAsync(Pnu, ["bcr_far"]);
            Check(read["sections"]![0]!["status"]!.GetValue<string>() == "failed" && read["sections"]![0]!["blocks"] is JsonArray { Count: 0 }, "Oversize/mismatched section fails explicitly, never truncated or attached to another parcel");
        }
        using (var client = new EumClient(new EvidenceHandler(main.Replace(Pnu, "9900000000100020000"), html)))
        { try { await client.ReadEvidenceAsync(Pnu); throw new Exception("Wrong parcel accepted"); } catch (InvalidDataException) { } }
        using (var client = new EumClient(new FixtureHandler(fixtures, stalledPath: "GYAjax"), TimeSpan.FromSeconds(2)))
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        { try { await client.ReadEvidenceAsync(Pnu, ["bcr_far"], cancel.Token); throw new Exception("Read cancellation ignored"); } catch (OperationCanceledException) { } }
        Console.WriteLine("Eum evidence checks passed: source structure/conditions/spans/links, no heuristic, selected acquisition, identity/encoding, partial failure, limits and cancellation.");
    }
    private sealed class EvidenceHandler(string main, string tab) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("luLandDet.jsp") ? main : tab, Encoding.UTF8, "text/html") });
        }
    }
}
