using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aha.EumCrawl;
using Aha.EumCrawl.Checks;

var fixtures = FixtureData.DirectoryPath;
string Fixture(string name) => File.ReadAllText(FixtureData.PathFor(name));
var facts = FixtureData.Facts;
string sourcePnu = facts.GetProperty("pnu").GetString()!;
string sourceOtherPnu = facts.GetProperty("other_pnu").GetString()!;
string sourceAddress = facts.GetProperty("address").GetString()!;
double sourceArea = facts.GetProperty("area_m2").GetDouble();
string sourceAreaText = facts.GetProperty("area_text").GetString()!;
string sourceMarkCodes = facts.GetProperty("mark_codes").GetString()!;
string sourceOrdinance = facts.GetProperty("ordinance").GetString()!;
double sourceBcr = facts.GetProperty("bcr_percent").GetDouble();
double sourceFar = facts.GetProperty("far_percent").GetDouble();
double sourceFarConditional = facts.GetProperty("far_conditional_percent").GetDouble();
string sourceFarCondition = facts.GetProperty("far_condition_text").GetString()!;
string sourceRatioDate = facts.GetProperty("ratio_date").GetString()!;
string sourceSetbackDate = facts.GetProperty("setback_date").GetString()!;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var main = Fixture("parcel-recorded.html");
EumParser.ValidateParcel(main, sourcePnu);
Check(EumParser.ParcelAddress(main) == sourceAddress, "Parcel identity/address");
var reference = EumParser.ParcelReferenceText(main);
Check(reference.Contains("지목: 대") && reference.Contains("면적: " + sourceAreaText), "Read exact main-page category and area");
Check(reference.Contains("제2종일반주거지역") && reference.Contains("소로2류(폭 8m~10m)(접합)")
    && reference.Contains("상대보호구역<교육환경 보호에 관한 법률>") && reference.Contains("허가대상:외국인"),
    "Preserve parcel designations, adjacent-road qualifiers, other laws and specific conditions");
Check(!reference.Contains("닫기") && !reference.Contains("자세히") && !reference.Contains("중층 중심의 주거용")
    && reference.Split("제2종일반주거지역").Length == 2, "Exclude nested popup explanations and duplicate titles");
Check(EumParser.ParcelReferenceText("<td id='present_area'>자료 없음</td><li>제3종일반주거지역 범례</li>") == "",
    "Unknown area and map legend never become parcel facts");
var parcelFields = EumParser.ParseParcelFields(main);
Check(parcelFields["land_category"]?["value"]?.GetValue<string>() == "대"
    && parcelFields["area_m2"]?["value"]?.GetValue<double>() == sourceArea, "Parcel facts have named text/number candidates");
Check(parcelFields["land_use_zones"]?["value"]?["national_plan"]?.GetValue<string>().Contains("소로2류(폭 8m~10m)(접합)") == true
    && parcelFields["land_use_zones"]?["value"]?["other_laws"]?.GetValue<string>().Contains("상대보호구역") == true,
    "Preserve original legal groups and qualifiers, not a union of zones");
var missingFacts = EumParser.ParseParcelFields("<td id='present_area'>자료 없음</td><td id='present_mark1'>-</td>");
Check(EumParser.ParcelFields.All(f => missingFacts[f]?["status"]?.GetValue<string>() == "unknown" && missingFacts[f]?["value"] is null),
    "Absent parcel facts remain unknown, never zero or no restriction");
Check(EumParser.ParseParcelFields("<td id='present_area'>0 ㎡</td>")["area_m2"]?["value"]?.GetValue<double>() == 0,
    "An explicit zero area is not an omitted value");
Check(EumParser.MarkCodes(main) == sourceMarkCodes, "Use actual marked parcel zones for the height request");
Check(EumClient.ParcelUrl(sourcePnu).StartsWith("https://www.eum.go.kr/web/ar/lu/luLandDet.jsp?pnu="),
    "Official parcel URL is available without a lookup");
try { EumClient.ParcelUrl("https://example.com/"); throw new Exception("Arbitrary parcel URL accepted"); }
catch (ArgumentException) { }
var fields = EumParser.ParseFields(Fixture("ratios-recorded.html"), Fixture("height-recorded.html"), "", Fixture("setback-recorded.html"));
Check(fields["bcr_percent"]?["value"]?.GetValue<double>() == sourceBcr, "Use the ordinance cell, never 0% diagram");
Check(fields["far_percent"]?["status"]?.GetValue<string>() == "candidate"
    && fields["far_percent"]?["value"]?.GetValue<double>() == sourceFar, "Conditional FAR uses the observed minimum as a numerical reference");
Check(fields["far_percent"]?["method"]?.GetValue<string>() == "minimum_percentage"
    && fields["far_percent"]?["notice"]?.GetValue<string>() == EumParser.RatioPolicyNotice
    && fields["far_percent"]?["observed_percentages"] is JsonArray { Count: 2 }
    && fields["far_percent"]!["observed_percentages"]!.AsArray().Any(v => v?.GetValue<double>() == sourceFarConditional)
    && fields["far_percent"]?["requires_confirmation"]?.GetValue<bool>() == true,
    "Minimum is an explicitly identified heuristic with original observed numbers and confirmation, not a legal determination");
Check(fields["far_percent"]?["text"]?.GetValue<string>().Contains(sourceFarCondition) == true, "Preserve condition text");
Check(fields["max_height_m"]?["value"] is null && fields["setback_m"]?["value"] is null, "Daylight diagram is not a sitewide height/setback");
Check(EumParser.ReferenceDate(Fixture("setback-recorded.html"), "K2") == sourceSetbackDate, "Preserve hidden legal reference date");
Check(EumParser.ReferenceDate(Fixture("ratios-recorded.html"), "GY") == sourceRatioDate, "Preserve displayed legal reference date");
var empty = EumParser.ParseFields(Fixture("empty.html"), "", "", "");
Check(EumParser.NumericFields.All(f => empty[f]?["status"]?.GetValue<string>() == "unknown" && empty[f]?["value"] is null), "Empty means unknown, never unlimited or zero");
var explicitHeight = EumParser.ParseFields("", "<table><tr><th>최고높이</th><td>25m 이하</td></tr><tr><th>최고층수</th><td>7층</td></tr></table>", "", "");
Check(explicitHeight["max_height_m"]?["value"]?.GetValue<double>() == 25 && explicitHeight["max_floors"]?["value"]?.GetValue<double>() == 7, "Strict labelled table grammar");
var multi = EumParser.ParseFields(Fixture("ratios-recorded.html") + Fixture("ratios-recorded.html"), "", "", "");
Check(multi["bcr_percent"]?["value"]?.GetValue<double>() == sourceBcr
    && multi["bcr_percent"]?["observed_percentages"] is JsonArray { Count: 1 }, "Duplicate zone values do not change the minimum");
string RatioCell(string name, string visible, string hidden = "999") => $"<td><input name='{name}' value='{hidden}'>{visible}</td>";
var overlap = EumParser.ParseFields("<td>5% 법률 일반 설명</td>" +
    RatioCell("gun_basic_A", "60%") + RatioCell("gun_basic_B", "40% (특정 용도)") +
    RatioCell("yong_basic_A", "230% · 재건축 250%") + RatioCell("yong_basic_B", "180%"), "", "", "");
Check(overlap["bcr_percent"]?["value"]?.GetValue<double>() == 40
    && overlap["far_percent"]?["value"]?.GetValue<double>() == 180,
    "Overlapping zones and conditional values use their own column minima; unrelated cells are excluded");
foreach (var range in new[] { "100~250%", "100 % ~ 250 %", "100～250％", "100–250퍼센트", "100부터 250%", "250~100%" })
{
    var parsed = EumParser.ParseFields(RatioCell("yong_basic_A", range), "", "", "");
    Check(parsed["far_percent"]?["value"]?.GetValue<double>() == 100
        && parsed["far_percent"]?["observed_percentages"] is JsonArray { Count: 2 },
        "A shared range unit covers both endpoints: " + range);
}
var decimals = EumParser.ParseFields(RatioCell("gun_basic_A", "60.5% 또는 45.25퍼센트") +
    RatioCell("yong_basic_A", "1,200.5% / 999.75%"), "", "", "");
Check(decimals["bcr_percent"]?["value"]?.GetValue<double>() == 45.25
    && decimals["far_percent"]?["value"]?.GetValue<double>() == 999.75, "Decimal percentages and grouped thousands preserve numeric precision");
foreach (var unsupported in new[] { "60", "자료 없음", "면적 60㎡ · 2026.10.05 · 20m · 7층", "101%", "-10%", "−10%", "+10%", "- 20%", "− 20%", "+ 20%", "1,00%", "1,50%", "1.2.3%" })
{
    var parsed = EumParser.ParseFields(RatioCell("gun_basic_A", unsupported, "60"), "", "", "");
    Check(parsed["bcr_percent"]?["status"]?.GetValue<string>() == "unknown"
        && parsed["bcr_percent"]?["value"] is null, "Missing/invalid BCR remains unknown despite a hidden value: " + unsupported);
}
var explicitRange = EumParser.ParseFields(RatioCell("yong_basic_A", "100 % 이상 250 % 이하"), "", "", "");
Check(explicitRange["far_percent"]?["value"]?.GetValue<double>() == 100
    && explicitRange["far_percent"]?["observed_percentages"] is JsonArray { Count: 2 }, "Two explicit units with prose keep both percentages");
var mixedUnits = EumParser.ParseFields(RatioCell("yong_basic_A", "3~10m 이격, 200%"), "", "", "");
Check(mixedUnits["far_percent"]?["value"]?.GetValue<double>() == 200
    && mixedUnits["far_percent"]?["observed_percentages"] is JsonArray { Count: 1 }, "A metre range does not borrow a later unrelated percent unit");
var invalidMaximum = EumParser.ParseFields(RatioCell("yong_basic_A", "10001% / -200%") +
    RatioCell("gun_basic_A", "105% 또는 75%"), "", "", "");
Check(invalidMaximum["far_percent"]?["value"] is null
    && invalidMaximum["bcr_percent"]?["value"]?.GetValue<double>() == 75, "Out-of-domain tokens do not become ratio references");
var explicitZero = EumParser.ParseFields(RatioCell("gun_basic_A", "0% 또는 60%"), "", "", "");
Check(explicitZero["bcr_percent"]?["value"]?.GetValue<double>() == 0,
    "Explicit ordinance-cell zero is a value, distinct from excluded diagram placeholders or missing data");
var conditionalHeight = EumParser.ParseFields("", "<tr><th>최고높이</th><td>25m 또는 30m</td></tr>", "", "");
Check(conditionalHeight["max_height_m"]?["value"] is null,
    "Minimum policy does not leak into heights, floors or boundary-dependent setbacks");
Check(!EumParser.IsPnu("9900000000300010000"), "Invalid parcel discriminator rejected");
Check(EumParser.Text("<script>bad()</script><p>조건 &amp; 예외</p>") == "조건 & 예외", "Discard scripts, preserve decoded terms");
try { EumParser.ValidateParcel(Fixture("failure.html"), sourcePnu); throw new Exception("Failure page accepted"); }
catch (InvalidDataException) { }

JsonObject recordedResult;
var handler = new FixtureHandler(fixtures);
using (var client = new EumClient(handler))
{
    var result = await client.LookupAsync(sourcePnu);
    recordedResult = (JsonObject)result.DeepClone();
    Check(result["url"]?.GetValue<string>() ==
        EumClient.ParcelUrl(sourcePnu),
        "Successful lookup returns the queried parcel's public land-use-plan page, not an AJAX endpoint");
    Check(result["sources"] is JsonArray { Count: 5 }, "Main-page reference plus four regulation responses");
    Check(result["fields"]?["bcr_percent"]?["value"]?.GetValue<double>() == sourceBcr, "HTTP codepage decoding and extraction");
    Check(result["warnings"]!.AsArray().Any(w => w?.GetValue<string>() == EumParser.RatioPolicyNotice),
        "Every lookup includes the minimum extraction policy for callers");
    Check(handler.Requests.Count == 5 && handler.Requests.Single(u => u.AbsolutePath.Contains("CAjax")).Query.Contains("markUcodes=" + Uri.EscapeDataString(sourceMarkCodes)),
        "Main facts add no requests and height parameters match the official screen");
    var notes = EumParser.Notes(new[] { result });
    Check(!notes.Contains("면적:") && !notes.Contains("지목:") && notes.Contains($"제2종일반주거지역 · 용적률(조례): {sourceFar}%")
        && notes.Contains(sourceFarCondition) && notes.Contains("최고높이: 명확한 단일 수치를 확인하지 못했습니다.")
        && notes.Contains(EumParser.RatioPolicyNotice) && notes.Contains($"용적률(조례) 최솟값 참고: {sourceFar}%"),
        "Parcel facts are separate from notes; regulatory evidence and unknown limits stay explicit");
    Check(notes.Contains("건축선: ") && notes.Contains("대지 안의 공지: ")
        && notes.Contains(sourceOrdinance) && notes.Contains("별표4") && notes.Contains("대지 안의 공지기준"),
        "Notes retain fetched building-line and open-space source text without a numeric setback");
    Check(result["sources"]![1]?["text"]?.GetValue<string>().Contains("0%") == true,
        "Original regulation evidence stays in the lookup result for review");
}
// A single lookup is explicitly applied to one selected slot, regardless of its current identity.
var originalData = JsonNode.Parse("""{"notes":"keep memo","area_m2":99,"sources":[{"url":"legacy","custom":true}],"parcels":[{"pnu":"__SOURCE_PNU__","address":"first address","area_m2":12},{"pnu":"__OTHER_PNU__","address":"different target address","land_category":"전","area_m2":7,"custom":{"keep":1},"land_use_zones":{"national_plan":"old plan","other_laws":"keep this unknown group","extension":true}},{"pnu":"__SOURCE_PNU__","address":"repeated PNU","area_m2":8}]}""".Replace("__SOURCE_PNU__", sourcePnu).Replace("__OTHER_PNU__", sourceOtherPnu))!.AsObject();
var firstParcel = (JsonObject)recordedResult.DeepClone();
firstParcel["parcel_fields"]!["land_use_zones"]!["value"]!.AsObject().Remove("other_laws");
var boundPreview = EumApply.CreatePreview(firstParcel, originalData, 1);
Check(boundPreview["parcel_index"]!.GetValue<int>() == 1 && boundPreview["parcels"] is JsonArray { Count: 1 }
    && boundPreview["fields"]!["parcels.area_m2"]!["value"]!.GetValue<double>() == sourceArea,
    "Single-parcel preview uses simple field values and one explicit destination slot");
var parcelPatch = EumApply.DataPatch(boundPreview, originalData,
    new[] { "parcels.land_category", "parcels.area_m2", "parcels.land_use_zones" });
var changedParcels = parcelPatch["parcels"]!.AsArray();
Check(changedParcels.Count == 3 && changedParcels[1]!["land_category"]!.GetValue<string>() == "대"
    && changedParcels[1]!["area_m2"]!.GetValue<double>() == sourceArea
    && JsonNode.DeepEquals(changedParcels[0], originalData["parcels"]![0])
    && JsonNode.DeepEquals(changedParcels[2], originalData["parcels"]![2]),
    "Only the selected slot changes; another slot with the same lookup PNU is untouched");
Check(changedParcels[1]!["pnu"]!.GetValue<string>() == sourceOtherPnu
    && changedParcels[1]!["address"]!.GetValue<string>() == "different target address"
    && changedParcels[1]!["custom"]!["keep"]!.GetValue<int>() == 1,
    "Different lookup identity is allowed without replacing an unchecked identity or extension data");
Check(changedParcels[1]!["land_use_zones"]!["other_laws"]!.GetValue<string>() == "keep this unknown group"
    && changedParcels[1]!["land_use_zones"]!["extension"]!.GetValue<bool>()
    && changedParcels[1]!["land_use_zones"]!["national_plan"]!.GetValue<string>().Contains("제2종일반주거지역"),
    "Only confirmed legal groups change; unknown groups and nested metadata survive");
Check(parcelPatch.Count == 1 && originalData["parcels"]![1]!["area_m2"]!.GetValue<int>() == 7
    && originalData["area_m2"]!.GetValue<int>() == 99 && parcelPatch["sources"] is null,
    "Patches preserve source JSON and do not add evidence, notes or a combined parcel area");
var identityPatch = EumApply.DataPatch(boundPreview, originalData, new[] { "parcels.identity" });
Check(identityPatch["parcels"]![1]!["pnu"]!.GetValue<string>() == sourcePnu
    && identityPatch["parcels"]![1]!["address"]!.GetValue<string>() == sourceAddress
    && identityPatch["parcels"]![1]!["area_m2"]!.GetValue<int>() == 7
    && identityPatch["parcels"]![1]!["custom"]!["keep"]!.GetValue<int>() == 1,
    "Identity has its own pair replacement and preserves unselected attributes");
var categoryOnly = EumApply.DataPatch(boundPreview, originalData, new[] { "parcels.land_category" });
Check(categoryOnly["parcels"]![1]!["area_m2"]!.GetValue<int>() == 7, "Unchecked area is preserved");
var knownFields = boundPreview["fields"]!.AsObject().Where(f => f.Key != "parcels.identity"
    && f.Value?["status"]?.GetValue<string>() == "candidate").Select(f => f.Key).ToArray();
var knownPatch = EumApply.DataPatch(boundPreview, originalData, knownFields);
Check(knownPatch["bcr_percent"]!.GetValue<double>() == sourceBcr && knownPatch["far_percent"]!.GetValue<double>() == sourceFar
    && knownPatch["notes"]!.GetValue<string>().Contains(sourceFarCondition)
    && knownPatch["notes"]!.GetValue<string>().Contains("대지 안의 공지기준") && knownPatch["sources"] is null,
    "All known fields can be selected together; heuristic ratios are numbers while conditions stay in optional notes");
var farOnly = EumApply.DataPatch(boundPreview, originalData, new[] { "far_percent" });
Check(farOnly.Count == 1 && farOnly["far_percent"]!.GetValue<double>() == sourceFar,
    "Selecting the FAR minimum does not overwrite unchecked notes or unrelated fields");
try { EumApply.DataPatch(boundPreview, originalData, new[] { "setback_m" }); throw new Exception("Unresolved setback applied"); }
catch (InvalidOperationException) { }
try { EumApply.DataPatch(boundPreview, originalData, new[] { "parcels.unknown" }); throw new Exception("Unknown field accepted"); }
catch (ArgumentException) { }
try { EumApply.DataPatch(boundPreview, originalData, Array.Empty<string>()); throw new Exception("Empty selection accepted"); }
catch (ArgumentException) { }
var changedData = (JsonObject)originalData.DeepClone();
changedData["notes"] = "changed while lookup was running";
try { EumApply.DataPatch(boundPreview, changedData, new[] { "bcr_percent" }); throw new Exception("Changed non-parcel data accepted"); }
catch (InvalidOperationException) { }
var reordered = (JsonObject)originalData.DeepClone();
reordered["parcels"]!.AsArray().RemoveAt(0);
try { EumApply.DataPatch(boundPreview, reordered, new[] { "parcels.land_category" }); throw new Exception("Reordered parcels accepted"); }
catch (InvalidOperationException) { }
var noAreaParcel = (JsonObject)recordedResult.DeepClone();
noAreaParcel["parcel_fields"] = EumParser.ParseParcelFields("<input name='present_class_val' value='임야'><td id='present_mark1'>자연녹지지역(일부)</td>");
var noAreaPreview = EumApply.CreatePreview(noAreaParcel, originalData, 1);
Check(noAreaPreview["fields"]!["parcels.area_m2"]!["status"]!.GetValue<string>() == "unknown",
    "Missing area stays unknown in a single-parcel preview");
try { EumApply.DataPatch(noAreaPreview, originalData, new[] { "parcels.area_m2" }); throw new Exception("Unknown area applied"); }
catch (InvalidOperationException) { }
var emptyData = new JsonObject();
var standalone = EumApply.CreatePreview(recordedResult, emptyData, 0);
Check(standalone["current_data"] is JsonObject { Count: 0 } && standalone["parcel_index"]!.GetValue<int>() == 0
    && standalone["fields"]!["parcels.identity"]!["status"]!.GetValue<string>() == "candidate" && emptyData.Count == 0,
    "No object data is needed to create a standalone lookup preview");
Check(standalone["parcels"]![0]!["url"]?.GetValue<string>() == recordedResult["url"]?.GetValue<string>()
    && boundPreview["parcels"]![0]!["url"]?.GetValue<string>() == recordedResult["url"]?.GetValue<string>(),
    "Standalone and object-bound previews preserve the public URL for clients to share with the user");
var newParcelPatch = EumApply.DataPatch(standalone, emptyData, new[] { "parcels.identity", "parcels.land_category", "parcels.area_m2" });
Check(newParcelPatch["parcels"] is JsonArray { Count: 1 }
    && newParcelPatch["parcels"]![0]!["pnu"]!.GetValue<string>() == sourcePnu
    && newParcelPatch["parcels"]![0]!["area_m2"]!.GetValue<double>() == sourceArea,
    "An explicitly bound empty Info data can append its first parcel at slot zero");
var emptyListPreview = EumApply.CreatePreview(recordedResult, new JsonObject { ["parcels"] = new JsonArray() }, 0);
Check(emptyListPreview["parcel_index"]!.GetValue<int>() == 0, "An empty parcel list also accepts its first slot");
foreach (var invalidIndex in new[] { -1, 3 })
{
    try { EumApply.CreatePreview(recordedResult, originalData, invalidIndex); throw new Exception("Out-of-range slot accepted"); }
    catch (InvalidOperationException) { }
}
try { EumApply.CreatePreview(recordedResult, emptyData, 1); throw new Exception("Sparse empty slot accepted"); }
catch (InvalidOperationException) { }
var previewCopy = (JsonObject)boundPreview.DeepClone();
previewCopy["current_data"]!["notes"] = "changed clone";
previewCopy["parcels"]![0]!["address"] = "changed clone";
Check(originalData["notes"]!.GetValue<string>() == "keep memo"
    && firstParcel["address"]!.GetValue<string>() == sourceAddress, "Preview snapshots do not alias source JSON");
using (var client = new EumClient(new FixtureHandler(fixtures, failTab: true)))
{
    var result = await client.LookupAsync(sourcePnu);
    Check(result["warnings"] is JsonArray { Count: 2 }, "A failed tab remains visible alongside the ratio policy notice");
    Check(result["fields"]?["bcr_percent"]?["value"] is null, "Failed ratio tab never yields default values");
    var preview = EumApply.CreatePreview(result, emptyData, 0);
    Check(preview["parcels"]![0]!["url"]?.GetValue<string>() == recordedResult["url"]?.GetValue<string>(),
        "Partial regulation failure still provides the successfully verified parcel's public review link");
}
using (var client = new EumClient(new FixtureHandler(fixtures, stalledPath: "luLandDet.jsp"), TimeSpan.FromMilliseconds(40)))
using (var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    try { await client.LookupAsync(sourcePnu, safety.Token); throw new Exception("Stalled body escaped the request deadline"); }
    catch (HttpRequestException) { Check(!safety.IsCancellationRequested, "The request deadline cancels a body after headers arrive"); }
}
using (var client = new EumClient(new FixtureHandler(fixtures, stalledPath: "GYAjax"), TimeSpan.FromMilliseconds(40)))
using (var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var result = await client.LookupAsync(sourcePnu, safety.Token);
    Check(result["warnings"] is JsonArray { Count: 2 } && result["sources"] is JsonArray { Count: 4 }
        && result["fields"]?["bcr_percent"]?["value"] is null, "A stalled tab becomes unknown while remaining tabs continue");
}
using (var client = new EumClient(new FixtureHandler(fixtures, stalledPath: "luLandDet.jsp"), TimeSpan.FromSeconds(2)))
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
{
    try { await client.LookupAsync(sourcePnu, cancel.Token); throw new Exception("Caller cancellation was ignored"); }
    catch (OperationCanceledException) { Check(cancel.IsCancellationRequested, "Caller cancellation stays cancellation, not a recoverable tab failure"); }
}
await EvidenceChecks.RunAsync(fixtures);
Console.WriteLine("EumCrawl offline checks passed: scoped ratio minima, conditions/multiple zones/ranges/decimals, method notices, missing and invalid percentages, strict heights, fixture parcel facts/zones, standalone preview and explicit slot application, separate identity replacement, unknown/unchecked/extension preservation, snapshot conflicts, request parameters, identity/encoding, empty/failure, tab failure, body deadlines and caller cancellation.");
