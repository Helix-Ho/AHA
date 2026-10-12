using System.Text.Json;
using System.Globalization;
using System.Text.Json.Nodes;
using Aha.Info;
using Aha.Runtime;
using Aha.Topo;
using Rhino;
using Rhino.DocObjects;

namespace Aha.Mcp;

internal static class McpTools
{
    private static JsonObject Property(string type, string description) => new() { ["type"] = type, ["description"] = description };
    private static JsonObject Setting(string type, string description, JsonNode fallback, double? minimum = null,
        double? maximum = null, bool exclusiveMinimum = false)
    {
        var rule = Property(type, description); rule["default"] = fallback;
        if (minimum is { } min) rule[exclusiveMinimum ? "exclusiveMinimum" : "minimum"] = min;
        if (maximum is { } max) rule["maximum"] = max;
        return rule;
    }
    private static JsonObject Doc() => Property("integer", "Live document_id from aha_context. Never substitute the newly active document.");
    private static JsonObject Obj() => Property("string", "Rhino object UUID from the intended document.");
    private static JsonObject Pnus() => new() { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 100,
        ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]{19}$" } };
    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required, bool readOnly) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = name is "aha_info_update" or "aha_info_delete" or "aha_eum_apply" or "aha_job_cancel",
            ["idempotentHint"] = readOnly || name == "aha_topo_create",
            ["openWorldHint"] = name.StartsWith("aha_topo") && name != "aha_topo_create" || name is "aha_eum_read" or "aha_eum_preview" }
    };

    public static JsonArray List() => new(
        Tool("aha_context", "Read open Rhino documents, selected object IDs and each document's current Topo job (if any). Match the user's intended objects/document before any mutation. Ask the user when ambiguous. Document IDs are runtime-only.", new(), [], true),
        Tool("aha_objects", "List objects in one document, optionally only site-designated objects. Returns at most 500 objects with paging. Does not alter selection.", new() { ["document_id"] = Doc(), ["site_only"] = Property("boolean", "Only objects whose aha.info.site is true; default false."), ["offset"] = Property("integer", "Zero-based page offset; default 0.") }, ["document_id"], true),
        Tool("aha_info_read", "Read object-local aha.info JSON and a compact opaque revision token. Missing metadata returns null and an empty revision. Pass the token unchanged for safe later edits; do not resend the JSON as revision.", new() { ["document_id"] = Doc(), ["object_id"] = Obj() }, ["document_id", "object_id"], true),
        Tool("aha_info_update", "Create or merge object-local aha.info (version=1, kind, site, data). RFC7396: null removes optional fields, arrays replace. data.parcels entries pair pnu/address with optional land_category, area_m2 (stored parcel area in m2, independent of Rhino geometry), and land_use_zones {national_plan,other_laws}. Missing/null values mean unknown. Arrays replace: preserve other parcels and extra fields when editing. site=true requires a valid closed Curve or solid Brep/Extrusion. Standard data fields: bcr_percent, far_percent, setback_m, max_height_m, max_floors, notes. Does not change geometry. Pass the opaque revision token from aha_info_read unchanged. Returns the updated Info and new revision.", new() { ["document_id"] = Doc(), ["object_id"] = Obj(), ["patch"] = Property("object", "Info root merge patch containing changed fields. Missing metadata starts from version:1, kind:object, site:false, data:{}; those root fields need not all be sent."), ["revision"] = Property("string", "Exact opaque token returned by aha_info_read/update; empty string for absent metadata. Never send the full Info JSON here.") }, ["document_id", "object_id", "patch", "revision"], false),
        Tool("aha_info_delete", "Remove only aha.info from the specified object, preserving geometry and other user text. Supports Rhino Undo. Pass the opaque revision token unchanged.", new() { ["document_id"] = Doc(), ["object_id"] = Obj(), ["revision"] = Property("string", "Exact opaque token from aha_info_read/update, not the Info JSON.") }, ["document_id", "object_id", "revision"], false),
        Tool("aha_topo_search", "Find a Korean site by parcel/road address using the VWorld key in AHA Setting. Returns Label, Pnu and Location {Lon,Lat} in WGS84; do not choose an ambiguous candidate without the user. The PNU can be used with aha_topo_xy_prepare_start for planar context, aha_topo_prepare_start for 3D context, or aha_eum_read/aha_eum_preview for planning evidence/candidates and an Eum page link.", new() { ["query"] = Property("string", "Address search text.") }, ["query"], true),
        Tool("aha_topo_parcel", "Read raw parcel geometry, PNU and full parcel address using a PNU or longitude/latitude. polygons is polygon -> ring -> {Lon,Lat} in WGS84 degrees; each first ring is an exterior and subsequent rings are holes. No Rhino objects are created. aha_topo_xy_prepare_start prepares site/context XY curves with coordinates, units and Info handled by Topo; aha_topo_create adds them. Eum planning evidence can be read with the PNU.", new() { ["pnu"] = Property("string", "19-digit parcel code, or omit for point selection."), ["longitude"] = Property("number", "WGS84 longitude."), ["latitude"] = Property("number", "WGS84 latitude.") }, [], true),
        Tool("aha_topo_xy_prepare_start", "Prepare Z=0 site boundaries and surrounding parcel, road and building footprint curves within a rectangular extent. No DEM, surfaces, solids or Rhino objects are added. Uses document units and acquired metadata. One pending Topo job per document; create or cancel it before preparing another. Returns job_id; poll aha_job_status until ready, then explicitly call aha_topo_create. Ready summary has settings, bounds, coordinates, counts and warnings; actual object IDs are returned only after creation.", XYProperties(), ["document_id", "pnus"], false),
        Tool("aha_topo_prepare_start", "Prepare existing terrain, roads and surrounding buildings without changing the Rhino model. Near is on by default: editable terrain solids and Extrusion buildings wholly inside Near. Optional contour prepares stepped plates, foundation cuts and smooth Site solids. For planar context use aha_topo_xy_prepare_start. Does not design buildings or determine legal limits. One pending Topo job per document. Poll aha_job_status until ready, then explicitly call aha_topo_create. Ready summary reports settings, bounds, coordinates, counts and warnings; completed creation reports object IDs.", TopoProperties(), ["document_id", "pnus"], false),
        Tool("aha_topo_create", "Create a ready Topo result in its original Rhino document, with one Undo. Does not fetch data or recalculate geometry and requires no VWorld key. Returns the same job_id; poll aha_job_status for completed/failed. A busy document retains ready for retry. Duplicate requests never create a second copy; completed requests return the existing result, even after Rhino Undo.", new() { ["job_id"] = Property("string", "Ready Topo job handle from prepare/status/context.") }, ["job_id"], false),
        Tool("aha_eum_read", "Read available Eum (토지이음) parcel information and planning-control evidence by PNU, preserving table/condition relationships. Does not choose percentage minima, resolve legal applicability or write Info. Returns job_id; poll aha_job_status for sections, source dates/statuses and the public land-use-plan URL. Missing/failed sections do not mean unrestricted. For heuristic numeric candidates and selected-field application use aha_eum_preview/aha_eum_apply.", new()
        {
            ["document_id"] = Doc(), ["pnu"] = Property("string", "19-digit PNU; use aha_topo_search if only an address is known."),
            ["sections"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["uniqueItems"] = true,
                ["description"] = "Optional section subset; omitted means all available sections.",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("parcel", "bcr_far", "height_floors", "building_line", "open_space") } }
        }, ["document_id", "pnu"], true),
        Tool("aha_eum_preview", "Look up preliminary site planning controls from Eum (토지이음) by PNU before early design or massing: recorded parcel area, land category, land-use zones, BCR/FAR and available other controls/notes. Useful without any Topo model or selected object. Successful result.parcels[0].url is the direct land-use-plan page: give this link to the user for checking the source. BCR/FAR are numeric MINIMA of percentages in each respective ordinance cell, including conditional/multiple-zone values; this heuristic does not resolve applicability, exceptions or area weighting. Explain the method/notice and do not present these as confirmed legal limits. Missing controls remain unknown. Optional object_id binds a preview to parcel_index (default 0); omitted pnu uses that slot's PNU. Without object_id, pnu is required and results cannot be applied. Address-only search is not supported; aha_topo_search can supply a PNU. Returns job_id; poll aha_job_status for the completed result. Read-only: no metadata is written; aha_eum_apply is a separate selected-field operation.", new() { ["document_id"] = Doc(), ["object_id"] = Obj(), ["pnu"] = Property("string", "19-digit PNU to search; may differ from the object identity."), ["parcel_index"] = Property("integer", "Target Info parcel slot, zero-based; default 0. Only slot 0 is valid if no parcels exist.") }, ["document_id"], true),
        Tool("aha_eum_apply", "Apply explicitly selected fields from a completed EumCrawl preview. BCR/FAR are minimum-percentage heuristics, not confirmed legal limits; disclose their method and Eum source link before applying. Send only its preview_id inside preview; values are read from the server cache. Include parcels.identity only to replace the address/PNU pair. Other values may deliberately come from a different PNU. Sources remain in the query preview; selected notes include the ratio method and source text. The preview must belong to this object/document and current metadata; stale previews fail. Unselected fields are not written.", new() { ["document_id"] = Doc(), ["object_id"] = Obj(), ["preview"] = Property("object", "An object containing only preview_id from the completed job result. Do not resend fields, parcels, sources, or current_data."), ["fields"] = new JsonObject { ["type"] = "array", ["items"] = Property("string", "Field name shown in preview.") } }, ["document_id", "object_id", "preview", "fields"], false),
        Tool("aha_job_status", "Read a job: running means acquisition/calculation, ready means Topo geometry is prepared but no objects exist, creating means Rhino insertion, completed includes actual results. A ready Topo needs aha_topo_create. failed/cancelled are not success. Wait a few seconds between polls.", new() { ["job_id"] = Property("string", "Execution handle from start.") }, ["job_id"], true),
        Tool("aha_job_cancel", "Cancel running work or discard a ready Topo result. Native calculations stop at safe points. creating is atomic and cannot be cancelled; a completed model can be undone in Rhino.", new() { ["job_id"] = Property("string", "Execution handle from start.") }, ["job_id"], false)
    );

    private static JsonObject TopoProperties() => new()
    {
        ["document_id"] = Doc(), ["pnus"] = Pnus(),
        ["width_m"] = Setting("number", "East-west extent in metres; must contain all selected parcels and the enabled Near region.", 400, 50, 3000),
        ["depth_m"] = Setting("number", "North-south extent in metres; must contain all selected parcels and the enabled Near region.", 400, 50, 3000),
        ["terrain_level"] = Setting("integer", "Fixed VWorld terrain level; availability depends on the source.", 12, 3, 15),
        ["options"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject
        {
            ["terrain_nurbs"] = Setting("boolean", "NURBS instead of Mesh for Far terrain, or all terrain when Near is off.", false),
            ["buildings_nurbs"] = Setting("boolean", "Editable extrusion/NURBS instead of Mesh for Far buildings, or all buildings when Near is off.", false),
            ["near"] = Setting("boolean", "Create editable land/separated-road solids and Extrusion buildings wholly inside the Near region. Boundary-touching buildings remain Far.", true),
            ["contour"] = Setting("boolean", "Make Near terrain as stepped Extrusion plates; excavate whole plates beneath surrounding Near buildings. Selected sites remain smooth solids; touching buildings are not excavated. Requires near=true. False keeps continuous uncut terrain.", false),
            ["contour_interval_m"] = Setting("number", "Contour plate interval in metres on the model Z grid. Used when contour=true; near_depth_m must exceed twice this value.", 1, 0, exclusiveMinimum:true),
            ["separate_foundations"] = Setting("boolean", "Create building bodies and foundations as separate objects/layers, divided at mean ground elevation. False creates each as one object. Roof is mean continuous-ground elevation plus building height; foundations extend below the whole-footprint minimum, or to an excavated contour plate bottom.", false),
            ["parapets"] = Setting("boolean", "Add separate capped Extrusions along outer and courtyard roof edges of Near buildings only, in Near::Buildings::Parapets. Modelled dimensions, not surveyed parapets. Existing building roof heights stay unchanged; parapets rise above them. Inactive when near=false.", true),
            ["parapet_width_m"] = Setting("number", "Parapet width inward from roof edges in metres. Narrow bands merge without overlaps; the building exterior and courtyards are preserved.", .3, 0, exclusiveMinimum:true),
            ["parapet_height_m"] = Setting("number", "Parapet height above the existing flat roof in metres.", .3, 0, exclusiveMinimum:true),
            ["near_ratio"] = Setting("number", "Near circle radius = ratio times the farthest boundary distance R from each site's area centroid. Overlapping circles share tangent envelopes. Used when near=true.", 2, 1.5),
            ["near_depth_m"] = Setting("number", "Near bottom depth in metres below the lowest terrain surface. With contour, measure from the lowest detail including cut floors; the depth includes two support plates and the thick base and must exceed twice contour_interval_m. Used when near=true.", 30, 0, exclusiveMinimum:true),
            ["roads"] = Setting("boolean", "Include cadastral road-parcel areas, references and road grading.", true),
            ["separate_roads"] = Setting("boolean", "Cut land and roads apart; false overlays road faces on continuous land.", true),
            ["lower_roads"] = Setting("boolean", "Apply the road drop only when roads and separate_roads are enabled.", true),
            ["road_drop_m"] = Setting("number", "Road lowering in metres; independent of 100mm planar cleanup. Used only for enabled, separated, lowered roads.", .2, 0),
            ["unknown_height_m"] = Setting("number", "Building height in metres when source height and floors are unknown.", 6, 0, exclusiveMinimum:true),
            ["floor_height_m"] = Setting("number", "Metres per above-ground storey when source height is unknown.", 3, 0, exclusiveMinimum:true),
            ["min_building_area_m2"] = Setting("number", "Minimum whole-footprint area in square metres, excluding holes; 0 keeps all sizes.", 10, 0)
        } }
    };

    private static JsonObject XYProperties() => new()
    {
        ["document_id"] = Doc(), ["pnus"] = Pnus(),
        ["width_m"] = Setting("number", "East-west extent in metres; must contain all selected parcels.", 400, 50, 3000),
        ["depth_m"] = Setting("number", "North-south extent in metres; must contain all selected parcels.", 400, 50, 3000),
        ["options"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject
        {
            ["roads"] = Setting("boolean", "Include original cadastral road-parcel boundary curves.", true),
            ["min_building_area_m2"] = Setting("number", "Minimum above-ground whole-building footprint area, excluding holes; 0 keeps all sizes. Confirmed underground references remain classified.", 10, 0)
        } }
    };

    internal static ModelOptions ReadTopoOptions(JsonObject o) => new(
        UnknownHeight:Number(o,"unknown_height_m",6), FloorHeight:Number(o,"floor_height_m",3), MinArea:Number(o,"min_building_area_m2",10),
        NearRatio:Number(o,"near_ratio",2),RoadDropMetres:Number(o,"road_drop_m",.2),UseNearRegion:Flag(o,"near",true),NearDepthMetres:Number(o,"near_depth_m",30),
        TerrainAsNurbs:Flag(o,"terrain_nurbs"),BuildingsAsNurbs:Flag(o,"buildings_nurbs"),SeparateRoads:Flag(o,"separate_roads",true),LowerRoads:Flag(o,"lower_roads",true),IncludeRoads:Flag(o,"roads",true),
        Contour:Flag(o,"contour"),ContourIntervalMetres:Number(o,"contour_interval_m",1),SeparateFoundations:Flag(o,"separate_foundations"),
        Parapets:Flag(o,"parapets",true),ParapetWidthMetres:Number(o,"parapet_width_m",.3),ParapetHeightMetres:Number(o,"parapet_height_m",.3));

    public static async Task<JsonNode?> CallAsync(string name, JsonObject args)
    {
        var descriptor = List().OfType<JsonObject>().FirstOrDefault(t => t["name"]!.GetValue<string>() == name)
            ?? throw new ArgumentException("공개되지 않은 AHA 도구입니다.");
        Validate(args, descriptor["inputSchema"]!.AsObject());
        switch (name)
        {
            case "aha_context": return await RhinoDispatch.InvokeAsync<JsonNode?>(() => Context());
            case "aha_objects": return await RhinoDispatch.InvokeAsync<JsonNode?>(() => Objects(DocumentAccess.Get(DocId(args)), Flag(args, "site_only"), Integer(args, "offset", 0)));
            case "aha_info_read": return await RhinoDispatch.InvokeAsync<JsonNode?>(() => ReadInfo(args));
            case "aha_info_update": return await RhinoDispatch.InvokeAsync<JsonNode?>(() =>
            {
                var doc = DocumentAccess.Get(DocId(args)); var id = ObjectId(args);
                // Token validation and the service's exact snapshot check share one UI-thread call.
                var expected = InfoRevision.RequireSnapshot(RawInfo(doc,id), Text(args,"revision"));
                var result = InfoService.Update(doc, id, args["patch"]!.AsObject(), expected);
                return new JsonObject { ["document_id"] = doc.RuntimeSerialNumber, ["object_id"] = id.ToString(), ["info"] = result, ["revision"] = InfoRevision.Token(RawInfo(doc,id)) };
            });
            case "aha_info_delete": return await RhinoDispatch.InvokeAsync<JsonNode?>(() =>
            {
                var doc = DocumentAccess.Get(DocId(args)); var id = ObjectId(args);
                var expected = InfoRevision.RequireSnapshot(RawInfo(doc,id), Text(args,"revision"));
                InfoService.Delete(doc, id, expected);
                return new JsonObject { ["document_id"] = doc.RuntimeSerialNumber, ["object_id"] = id.ToString(), ["deleted"] = true, ["revision"] = "" };
            });
            case "aha_topo_search":
                using (var client = new VWorldClient(TopoService.RequireKey()))
                { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); return JsonSerializer.SerializeToNode(await client.SearchAsync(Text(args, "query"), timeout.Token)); }
            case "aha_topo_parcel":
                using (var client = new VWorldClient(TopoService.RequireKey()))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    bool pnu = args.ContainsKey("pnu");
                    if (pnu && (args.ContainsKey("longitude") || args.ContainsKey("latitude"))) throw new ArgumentException("PNU 또는 경위도 중 하나만 지정하세요.");
                    if (!pnu && (!args.ContainsKey("longitude") || !args.ContainsKey("latitude"))) throw new ArgumentException("PNU 또는 경도·위도가 필요합니다.");
                    var parcel = pnu ? await client.ParcelByPnuAsync(Text(args, "pnu"), timeout.Token) : await client.ParcelAtAsync(new(Number(args,"longitude",0),Number(args,"latitude",0)), timeout.Token);
                    return new JsonObject { ["pnu"] = parcel.Pnu, ["address"] = parcel.Address, ["label"] = parcel.ParcelLabel, ["polygons"] = JsonSerializer.SerializeToNode(parcel.Polygons) };
                }
            case "aha_topo_xy_prepare_start":
            {
                var pnus = args["pnus"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                var o = args["options"] as JsonObject ?? new();
                return await RhinoDispatch.InvokeAsync<JsonNode?>(() => TopoService.PrepareXYByPnusStart(
                    DocumentAccess.Get(DocId(args)), pnus, Number(args,"width_m",400), Number(args,"depth_m",400),
                    Flag(o,"roads",true), Number(o,"min_building_area_m2",10)));
            }
            case "aha_topo_prepare_start":
            {
                var pnus = args["pnus"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                var o = args["options"] as JsonObject ?? new();
                var opts = ReadTopoOptions(o);
                BuildingRules.Validate(opts);
                return await RhinoDispatch.InvokeAsync<JsonNode?>(() => TopoService.PrepareByPnusStart(DocumentAccess.Get(DocId(args)),
                    pnus,Number(args,"width_m",400),Number(args,"depth_m",400),Integer(args,"terrain_level",12),opts));
            }
            case "aha_topo_create": return TopoService.Create(Text(args,"job_id"));
            case "aha_eum_read":
            {
                var doc = await RhinoDispatch.InvokeAsync(() => DocumentAccess.Get(DocId(args)));
                var sections = (args["sections"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray();
                return Jobs.Start(doc.RuntimeSerialNumber,"eum-crawl",async (ct, _) =>
                    await EumCrawl.EumService.ReadAsync(doc, Text(args,"pnu"), sections, ct));
            }
            case "aha_eum_preview":
            {
                var doc = await RhinoDispatch.InvokeAsync(() => DocumentAccess.Get(DocId(args)));
                Guid? id = args["object_id"] is null ? null : ObjectId(args);
                return Jobs.Start(doc.RuntimeSerialNumber, "eum-crawl", async (ct, _) => await EumCrawl.EumService.PreviewAsync(doc, id, args["pnu"]?.GetValue<string>(), Integer(args,"parcel_index",0), ct));
            }
            case "aha_eum_apply": return await RhinoDispatch.InvokeAsync<JsonNode?>(() => EumCrawl.EumService.Apply(DocumentAccess.Get(DocId(args)), ObjectId(args), args["preview"]!.AsObject(), args["fields"]!.AsArray().Select(n=>n!.GetValue<string>())));
            case "aha_job_status": return Jobs.Status(Text(args,"job_id"));
            case "aha_job_cancel": return Jobs.Cancel(Text(args,"job_id"));
            default: throw new ArgumentException("공개되지 않은 도구입니다.");
        }
    }

    private static JsonObject Context()
    {
        var docs = new JsonArray(); var i = 0;
        foreach (var doc in RhinoDoc.OpenDocuments()) docs.Add(new JsonObject
        {
            ["document_id"] = doc.RuntimeSerialNumber, ["display_name"] = $"{++i}. {(string.IsNullOrEmpty(doc.Name) ? "미저장 문서" : doc.Name)}",
            ["path"] = doc.Path, ["active"] = RhinoDoc.ActiveDoc?.RuntimeSerialNumber == doc.RuntimeSerialNumber,
            ["units"] = doc.ModelUnitSystem.ToString(), ["topo_job"] = Jobs.CurrentPrepared(doc.RuntimeSerialNumber), ["selected_objects"] = new JsonArray(doc.Objects.GetSelectedObjects(false,false).Select(o => (JsonNode?)Summary(doc,o)).ToArray())
        });
        return new JsonObject { ["documents"] = docs };
    }
    private static JsonObject Summary(RhinoDoc doc, RhinoObject obj) => new()
    { ["object_id"] = obj.Id.ToString(), ["name"] = obj.Name, ["type"] = obj.ObjectType.ToString(), ["layer"] = doc.Layers[obj.Attributes.LayerIndex].FullPath, ["has_info"] = obj.Attributes.GetUserString(InfoData.Key) != null };
    private static JsonObject Objects(RhinoDoc doc, bool siteOnly, int offset)
    {
        if (offset < 0) throw new ArgumentException("offset은 0 이상이어야 합니다.");
        var objects = doc.Objects.GetObjectList(new ObjectEnumeratorSettings { ActiveObjects = true, HiddenObjects = true, LockedObjects = true, DeletedObjects = false });
        // Discovery follows Info's read contract so a malformed business value
        // does not hide a site that must still be discoverable for repair.
        bool IsSite(RhinoObject obj) { try { return InfoService.Read(doc, obj.Id)?["site"]?.GetValue<bool>() == true; } catch { return false; } }
        var matches = objects.Where(o => !siteOnly || IsSite(o)).ToArray();
        return new JsonObject { ["document_id"] = doc.RuntimeSerialNumber, ["total"] = matches.Length, ["offset"] = offset,
            ["objects"] = new JsonArray(matches.Skip(offset).Take(500).Select(o => (JsonNode?)Summary(doc,o)).ToArray()) };
    }
    private static JsonObject ReadInfo(JsonObject a)
    {
        var doc = DocumentAccess.Get(DocId(a)); var id = ObjectId(a);
        return new JsonObject { ["document_id"] = doc.RuntimeSerialNumber, ["object_id"] = id.ToString(), ["info"] = InfoService.Read(doc,id), ["revision"] = InfoRevision.Token(RawInfo(doc,id)) };
    }
    private static string? RawInfo(RhinoDoc doc, Guid id)
    {
        var obj = doc.Objects.FindId(id);
        if (obj is null || obj.IsDeleted) throw new InvalidOperationException("대상 객체가 삭제되었거나 이 문서에 없습니다.");
        return obj.Attributes.GetUserString(InfoData.Key);
    }
    private static uint DocId(JsonObject a) => (uint)WholeNumber(a, "document_id", 0, uint.MaxValue);
    private static Guid ObjectId(JsonObject a) => Guid.TryParse(Text(a,"object_id"),out var id) ? id : throw new ArgumentException("객체 UUID가 올바르지 않습니다.");
    private static string Text(JsonObject a,string key) => a[key]?.GetValue<string>() ?? throw new ArgumentException(key+" 값이 필요합니다.");
    private static double Number(JsonObject a, string key, double fallback)
        => a[key] is null ? fallback : TryNumber(a[key], out var number) ? number : throw new ArgumentException("유한한 숫자가 필요합니다: " + key);
    private static int Integer(JsonObject a, string key, int fallback)
        => a[key] is null ? fallback : (int)WholeNumber(a, key, int.MinValue, int.MaxValue);
    private static double WholeNumber(JsonObject a, string key, double minimum, double maximum)
    {
        if (!TryNumber(a[key], out var number) || number != Math.Truncate(number) || number < minimum || number > maximum)
            throw new ArgumentException("허용 범위의 정수가 필요합니다: " + key);
        return number;
    }
    private static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        // JsonElement-backed wire values and CLR-backed values must have the same semantics.
        if (value.TryGetValue<double>(out number)) return double.IsFinite(number);
        if (value.TryGetValue<float>(out var single)) { number = single; return double.IsFinite(number); }
        return value.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
    private static bool Flag(JsonObject a,string key,bool fallback=false) => a[key]?.GetValue<bool>() ?? fallback;
    private static void Validate(JsonObject args, JsonObject schema)
    {
        var properties = schema["properties"]!.AsObject();
        foreach (var key in schema["required"]?.AsArray() ?? new JsonArray())
            if (!args.ContainsKey(key!.GetValue<string>())) throw new ArgumentException(key + " 값이 필요합니다.");
        foreach (var (key, value) in args)
        {
            if (!properties.TryGetPropertyValue(key, out var rule)) throw new ArgumentException("지원하지 않는 입력: " + key);
            ValidateValue(value, rule!.AsObject(), key);
        }
    }

    private static void ValidateValue(JsonNode? value, JsonObject rule, string path)
    {
        var kind = rule["type"]!.GetValue<string>();
        bool valid = kind switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue s && s.TryGetValue<string>(out _),
            "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
            "integer" => TryNumber(value, out var integer) && integer == Math.Truncate(integer),
            "number" => TryNumber(value, out _),
            _ => false
        };
        if (!valid) throw new ArgumentException("입력 형식이 올바르지 않습니다: " + path);
        if (rule["enum"] is JsonArray choices && !choices.Any(choice => choice?.ToJsonString() == value?.ToJsonString()))
            throw new ArgumentException("지원하지 않는 값: " + path);
        if (kind is "number" or "integer")
        {
            TryNumber(value, out double number);
            if (rule["minimum"] is JsonNode minimum && TryNumber(minimum, out var min) && number < min ||
                rule["maximum"] is JsonNode maximum && TryNumber(maximum, out var max) && number > max ||
                rule["exclusiveMinimum"] is JsonNode exclusive && TryNumber(exclusive, out var exclusiveMin) && number <= exclusiveMin)
                throw new ArgumentException("허용 범위를 벗어난 값: " + path);
        }
        if (value is JsonArray array)
        {
            if (rule["minItems"] is JsonNode minimum && array.Count < minimum.GetValue<int>() ||
                rule["maxItems"] is JsonNode maximum && array.Count > maximum.GetValue<int>())
                throw new ArgumentException("항목 수가 허용 범위를 벗어났습니다: " + path);
            if (rule["uniqueItems"]?.GetValue<bool>() == true &&
                array.Select(item => item?.ToJsonString()).Distinct(StringComparer.Ordinal).Count() != array.Count)
                throw new ArgumentException("중복된 항목: " + path);
            if (rule["items"] is JsonObject itemRule)
                for (int i = 0; i < array.Count; i++) ValidateValue(array[i], itemRule, path + "[" + i + "]");
        }
        if (value is JsonObject child && rule["properties"] is JsonObject) Validate(child, rule);
    }
}
