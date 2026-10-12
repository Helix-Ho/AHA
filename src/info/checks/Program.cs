using Aha.Info;
using System.Text.Json.Nodes;

int assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new Exception(message);
}
void Reject<T>(Action action, string message) where T : Exception
{
    assertions++;
    try { action(); } catch (T) { return; }
    throw new Exception(message);
}
JsonObject Object(string json) => (JsonObject)JsonNode.Parse(json)!;

var input = Object("""{"parcels":[{"pnu":"9900000000100020000","address":"가상시 예시동 1"}],"notes":"keep","unknown":null}""");
var document = InfoData.Create("curve", true, input);
input["notes"] = "caller mutation";
Check(document["data"]!["notes"]!.GetValue<string>() == "keep", "Create must detach caller-owned JSON.");
Check(InfoData.Parse(document.ToJsonString())["data"]!["parcels"]![0]!["pnu"]!.GetValue<string>() == "9900000000100020000", "PNU remains a string.");
document["extension"] = Object("""{"nested":{"keep":7,"remove":8},"opaque":[1,2]}""");
var patch = Object("""{"site":false,"data":{"notes":null,"bcr_percent":60},"extension":{"nested":{"remove":null,"add":9}}}""");
var merged = InfoData.Merge(document, patch);
Check(!merged["site"]!.GetValue<bool>(), "Site may be turned off.");
Check(merged["data"]!["parcels"]![0]!["address"]!.GetValue<string>().Contains("예시동"), "Site off preserves parcel data.");
Check(((JsonObject)merged["data"]!).ContainsKey("unknown"), "Untouched explicit unknown null is preserved.");
Check(!((JsonObject)merged["data"]!).ContainsKey("notes"), "Patch null removes data fields.");
Check(merged["extension"]!["nested"]!["keep"]!.GetValue<int>() == 7, "Unknown nested fields survive.");
Check(merged["extension"]!["nested"]!["remove"] is null, "Nested null removes a field.");
Check(merged["extension"]!["opaque"]!.AsArray().Count == 2, "Unknown arrays survive.");
Check(document["site"]!.GetValue<bool>() && document["data"]!["notes"] is not null, "Merge must not mutate original.");
patch["data"]!["bcr_percent"] = 1;
Check(merged["data"]!["bcr_percent"]!.GetValue<int>() == 60, "Merge result must detach patch.");

var replacement = InfoData.Merge(document, Object("""{"data":{"parcels":[{"pnu":null,"address":"임의 대지"}]}}"""));
Check(replacement["data"]!["parcels"]!.AsArray().Count == 1 && replacement["data"]!["parcels"]![0]!["pnu"] is null, "Arrays replace whole, and null entries remain data.");
var changed = (JsonObject)document.DeepClone();
changed["site"] = false;
((JsonObject)changed["data"]!).Remove("notes");
changed["data"]!["new"] = Object("""{"value":3}""");
var difference = InfoData.Difference(document, changed);
Check(JsonNode.DeepEquals(InfoData.Merge(document, difference), changed), "Form difference must preserve unrelated metadata and deletions.");
Check(InfoData.Difference(document, document).Count == 0, "Unchanged editor emits no change.");

foreach (var json in new[]
{
    "[]", "null", "{}", "{", """{"version":"1","kind":"curve","site":false,"data":{}}""",
    """{"version":1,"kind":" ","site":false,"data":{}}""",
    """{"version":1,"kind":"curve","site":"true","data":{}}""",
    """{"version":1,"kind":"curve","site":false,"data":[]}"""
}) Reject<ArgumentException>(() => InfoData.Parse(json), "Invalid schema accepted: " + json);

foreach (var name in new[] { "version", "kind", "site", "data" })
    Reject<ArgumentException>(() => InfoData.Merge(document, new JsonObject { [name] = null }), "Required root field removed: " + name);
var future = Object("""{"version":2,"kind":"curve","site":true,"data":{},"future":99}""");
Check(InfoData.Parse(future.ToJsonString(), requireSupportedVersion: false)["future"]!.GetValue<int>() == 99, "Future format may be read.");
Reject<NotSupportedException>(() => InfoData.Parse(future.ToJsonString()), "Future format must reject editing.");
Reject<NotSupportedException>(() => InfoData.Merge(future, Object("""{"version":1}""")), "Editing must not downgrade a future document.");
Reject<NotSupportedException>(() => InfoData.Merge(document, Object("""{"version":2}""")), "Patch must not write an unsupported version.");

var fixedFields = new[] { "bcr_percent", "far_percent", "setback_m", "max_height_m", "max_floors" };
foreach (var key in fixedFields)
{
    foreach (var invalid in new[] { "\"60\"", "true", "[]", "{}", "-1", "1e309" })
    {
        var invalidData = Object("{\"" + key + "\":" + invalid + "}");
        Reject<ArgumentException>(() => InfoData.Create("curve", false, invalidData), "Factory accepted invalid " + key + ": " + invalid);
        Reject<ArgumentException>(() => InfoData.Merge(document, new JsonObject { ["data"] = invalidData.DeepClone() }), "MCP patch accepted invalid " + key + ": " + invalid);
    }
    var zeros = InfoData.Merge(document, new JsonObject { ["data"] = new JsonObject { [key] = 0 } });
    Check(zeros["data"]![key]!.GetValue<int>() == 0, key + " zero must remain an explicit value.");
    var absent = InfoData.Merge(zeros, new JsonObject { ["data"] = new JsonObject { [key] = null } });
    Check(!absent["data"]!.AsObject().ContainsKey(key), key + " merge null must remove the field.");
    var unknown = InfoData.Create("curve", false, new JsonObject { [key] = null });
    Check(unknown["data"]!.AsObject().ContainsKey(key) && unknown["data"]![key] is null, key + " factory unknown null must remain null.");
}
foreach (var invalid in new[] { "2.5", "9007199254740992.1", "90071992547409920000000000000.1", "1e-100", "-0.1" })
    Reject<ArgumentException>(() => InfoData.Merge(document, Object("{\"data\":{\"max_floors\":" + invalid + "}}")), "Fractional max_floors accepted.");
Check(InfoData.Create("curve", false, Object("""{"bcr_percent":125,"far_percent":12000,"setback_m":0.125,"max_height_m":10000,"max_floors":5.0}"""))["data"]!["far_percent"]!.GetValue<int>() == 12000,
    "Validation must not impose undocumented legal maxima or reject integer-valued JSON decimals.");
Reject<ArgumentException>(() => InfoData.Create("curve", false, new JsonObject { ["setback_m"] = double.NaN }), "NaN accepted.");
Reject<ArgumentException>(() => InfoData.Create("curve", false, new JsonObject { ["max_height_m"] = double.PositiveInfinity }), "Infinity accepted.");
Reject<ArgumentException>(() => InfoData.Create("curve", false, Object("""{"setback_m":1e-400}""")), "Tiny nonzero values must not be treated as zero.");
Reject<ArgumentException>(() => InfoData.Create("curve", false, Object("""{"setback_m":-1e-400}""")), "Tiny negative values must not pass as zero.");
Check(InfoData.Create("curve", false, Object("""{"max_floors":500e-2}"""))["data"]!["max_floors"]!.GetValue<double>() == 5,
    "Exact integer exponent notation is allowed.");

foreach (var invalid in new[]
{
    "{}", "\"parcel\"", "[null]", "[1]", "[[]]", "[{\"pnu\":9900000000100020000}]",
    "[{\"pnu\":\"123\"}]", "[{\"pnu\":\"111101010010001000x\"}]", "[{\"pnu\":\"１１１１０１０１００１０００１００００\"}]",
    "[{\"address\":false}]", "[{\"address\":{}}]"
}) Reject<ArgumentException>(() => InfoData.Merge(document, Object("{\"data\":{\"parcels\":" + invalid + "}}")), "Invalid parcel structure accepted: " + invalid);
var nullableParcels = InfoData.Create("curve", false, Object("""{"parcels":[{"pnu":null,"address":null,"unrecognized":{"value":-1}},{}]}"""));
Check(nullableParcels["data"]!["parcels"]![0]!["unrecognized"]!["value"]!.GetValue<int>() == -1, "Unknown parcel fields must remain unrestricted and preserved.");
Check(InfoData.Merge(nullableParcels, Object("""{"data":{"notes":"edit only"}}"""))["data"]!["parcels"]!.AsArray().Count == 2, "Missing parcel identity fields remain unknown, not inferred.");
var parcelFacts = InfoData.Create("object", false, Object("""{"parcels":[{"pnu":"9900000000100010000","land_category":"대","area_m2":1234.5,"land_use_zones":{"national_plan":"제2종일반주거지역, 가상도로(접합)","other_laws":"상대보호구역<교육환경 보호에 관한 법률>","future_zone":{"keep":true}}},{"address":"다른 원필지","land_category":null,"area_m2":0,"land_use_zones":null}]}"""));
Check(InfoData.Parse(parcelFacts.ToJsonString())["data"]!["parcels"]![0]!["area_m2"]!.GetValue<double>() == 1234.5,
    "Parcel area remains a parcel-local number on non-site objects.");
var factsAfterNoteEdit = InfoData.Merge(parcelFacts, Object("""{"data":{"notes":"unrelated edit"}}"""));
Check(JsonNode.DeepEquals(factsAfterNoteEdit["data"]!["parcels"], parcelFacts["data"]!["parcels"]), "All parcel facts and unknown nested zone metadata survive unrelated edits.");
Check(factsAfterNoteEdit["data"]!["parcels"]![1]!["area_m2"]!.GetValue<int>() == 0, "Explicit zero parcel area remains zero, not unknown.");
var editedFacts = (JsonObject)parcelFacts.DeepClone();
editedFacts["data"]!["parcels"]![0]!["land_category"] = "전";
editedFacts["data"]!["parcels"]![0]!["land_use_zones"]!.AsObject().Remove("other_laws");
Check(JsonNode.DeepEquals(InfoData.Merge(parcelFacts, InfoData.Difference(parcelFacts, editedFacts)), editedFacts),
    "Editor diff can edit/delete parcel facts without losing the other parcel or unknown nested groups.");
foreach (var invalid in new[]
{
    "\"land_category\":1", "\"land_category\":[]", "\"area_m2\":\"1234.5\"", "\"area_m2\":-1",
    "\"area_m2\":1e309", "\"area_m2\":1e-400", "\"area_m2\":true",
    "\"land_use_zones\":\"주거지역\"", "\"land_use_zones\":[]", "\"land_use_zones\":{\"national_plan\":1}",
    "\"land_use_zones\":{\"other_laws\":false}"
}) Reject<ArgumentException>(() => InfoData.Merge(document, Object("{\"data\":{\"parcels\":[{" + invalid + "}]}}")),
    "Invalid parcel fact type accepted: " + invalid);
Check(InfoData.Create("object", false, Object("""{"parcels":[{"land_category":null,"area_m2":null,"land_use_zones":{"national_plan":null,"other_laws":null}}]}"""))["data"]!["parcels"]![0]!["area_m2"] is null,
    "Missing parcel facts stay unknown; legal groups may individually be null.");
var unknownFields = InfoData.Merge(document, Object("""{"extraRoot":{"number":-7},"data":{"extraField":{"value":"not numeric","nested":false}}}"""));
Check(unknownFields["extraRoot"]!["number"]!.GetValue<int>() == -7 && unknownFields["data"]!["extraField"]!["nested"]!.GetValue<bool>() == false, "Fixed-field validation must not restrict extension fields.");
Reject<ArgumentException>(() => InfoData.Parse("""{"version":1,"kind":"curve","site":false,"data":{"max_height_m":"20"}}"""), "Read/parse accepted invalid fixed-field data.");
Reject<ArgumentException>(() => InfoData.Create("curve", false, Object("""{"notes":17}""")), "Notes must be text or null.");
var repairable = (JsonObject)document.DeepClone();
repairable["data"]!["max_floors"] = "bad value from an external editor";
Check(InfoData.Parse(repairable.ToJsonString(), validateFields: false)["data"]!["max_floors"] is not null, "Stored malformed fields can be inspected for repair.");
Check(InfoData.Merge(repairable, Object("""{"data":{"max_floors":5}}"""))["data"]!["max_floors"]!.GetValue<int>() == 5, "A corrective patch must be allowed.");
Check(!InfoData.Merge(repairable, Object("""{"data":{"max_floors":null}}"""))["data"]!.AsObject().ContainsKey("max_floors"), "Removing a malformed field must be allowed.");
Reject<ArgumentException>(() => InfoData.Merge(repairable, Object("""{"data":{"notes":"unrelated"}}""")), "An unrelated patch must not persist invalid fields.");
Console.WriteLine($"Info JSON checks passed: {assertions} assertions.");
