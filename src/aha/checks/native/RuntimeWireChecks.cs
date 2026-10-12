using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Mcp;
using Aha.Info;
using Aha.Runtime;
using Aha.Topo;
using Rhino;
using Rhino.Geometry;
namespace Aha.Checks;
public static class RuntimeWireChecks
{
    public static string Start(string output)
    {
        var result = JsonNode.Parse(RuntimeNativeChecks.RunSafe())!.AsObject();
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "runtime.json");
        File.WriteAllText(path, "{\"status\":\"running\"}");
        _ = Task.Run(async () =>
        {
            RhinoDoc? doc = null;
            try
            {
                if (result["success"]?.GetValue<bool>() != true) throw new Exception("Runtime service checks failed");
                var definitions = McpTools.List();
                if (definitions.Count == 0 || definitions.Select(x=>x!["name"]!.GetValue<string>()).Distinct().Count() != definitions.Count) throw new Exception("Duplicate or empty MCP list");
                if (definitions.Any(tool => tool?["name"]?.GetValue<string>() is "aha_topo_start" or "aha_topo_xy_start"))
                    throw new Exception("Automatic creation aliases must not bypass explicit prepare/create");
                foreach (var tool in definitions)
                {
                    if (string.IsNullOrWhiteSpace(tool!["description"]?.GetValue<string>()) || tool["annotations"]?["readOnlyHint"] is null) throw new Exception("Missing MCP contract");
                    var schema = tool["inputSchema"]!;
                    foreach (var key in schema["required"]!.AsArray()) if (schema["properties"]?[key!.GetValue<string>()] is null) throw new Exception("Invalid required input");
                }
                var product = typeof(InfoService).Assembly;
                var panels = product.GetTypes().Where(t=>t.IsSubclassOf(typeof(Eto.Forms.Panel)) && t.IsPublic).ToArray();
                if (panels.Length != 5 || panels.Select(t=>t.GUID).Distinct().Count() != 5 || panels.Any(t=>t.GetConstructor(new[]{typeof(uint)}) is null)) throw new Exception("Panel registration contract");
                Guid id = await RhinoDispatch.InvokeAsync(() => { doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Meters; return doc.Objects.AddCircle(new Circle(Plane.WorldXY, 10)); });
                int port;
                using (var held = new TcpListener(IPAddress.Loopback, 0)) { held.Start(); port = ((IPEndPoint)held.LocalEndpoint).Port; }
                using var server = new McpHttpServer(port, McpTools.List, McpTools.CallAsync); server.Start();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                async Task<JsonObject> Call(string name, JsonObject args)
                {
                    var request = new JsonObject { ["jsonrpc"]="2.0", ["id"]=1, ["method"]="tools/call", ["params"]=new JsonObject { ["name"]=name, ["arguments"]=args } };
                    using var response = await client.PostAsync(server.Url, new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode(); return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsObject();
                }
                JsonObject Target() => new() { ["document_id"]=doc!.RuntimeSerialNumber, ["object_id"]=id.ToString() };
                JsonObject Definition(string name) => definitions.OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == name);
                JsonObject Properties(string name) => Definition(name)["inputSchema"]!["properties"]!.AsObject();
                double Numeric(JsonNode? value) => value is null ? double.NaN : JsonSerializer.Deserialize<double>(value.ToJsonString());
                void Range(JsonObject property, double fallback, double? minimum = null, double? maximum = null, bool exclusive = false)
                {
                    if (Numeric(property["default"]) != fallback || minimum is { } min && Numeric(property[exclusive ? "exclusiveMinimum" : "minimum"]) != min
                        || maximum is { } max && Numeric(property["maximum"]) != max)
                        throw new Exception("MCP numeric settings differ from their declared defaults/ranges");
                }
                foreach (string name in new[] { "aha_topo_prepare_start", "aha_topo_xy_prepare_start" })
                {
                    var tool = Definition(name);
                    if (tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() != false
                        || tool["annotations"]?["destructiveHint"]?.GetValue<bool>() != false
                        || tool["annotations"]?["idempotentHint"]?.GetValue<bool>() != false
                        || tool["inputSchema"]?["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet()
                            .SetEquals(new[] { "document_id", "pnus" }) != true)
                        throw new Exception("Topo additions must declare their document/PNU contract without destructive or idempotent hints");
                    Range(Properties(name)["width_m"]!.AsObject(), 400, 50, 3000);
                    Range(Properties(name)["depth_m"]!.AsObject(), 400, 50, 3000);
                    var options = Properties(name)["options"]!["properties"]!.AsObject();
                    if (options["roads"]?["default"]?.GetValue<bool>() != true) throw new Exception("Default road setting differs");
                    Range(options["min_building_area_m2"]!.AsObject(), 10, 0);
                }
                var createDefinition = Definition("aha_topo_create");
                if (createDefinition["annotations"]?["readOnlyHint"]?.GetValue<bool>() != false
                    || createDefinition["annotations"]?["idempotentHint"]?.GetValue<bool>() != true
                    || createDefinition["annotations"]?["openWorldHint"]?.GetValue<bool>() != false
                    || Properties("aha_topo_create").Select(pair => pair.Key).ToHashSet().SetEquals(new[] { "job_id" }) != true)
                    throw new Exception("Topo create must be an explicit idempotent local operation by job ID");
                var topoOptions = Properties("aha_topo_prepare_start")["options"]!["properties"]!.AsObject();
                var defaults = new Aha.Topo.ModelOptions();
                if (!defaults.UseNearRegion || topoOptions["near"]?["default"]?.GetValue<bool>() != defaults.UseNearRegion
                    || defaults.Contour || topoOptions["contour"]?["default"]?.GetValue<bool>() != defaults.Contour
                    || topoOptions["contour"]?["type"]?.GetValue<string>() != "boolean"
                    || defaults.SeparateFoundations || topoOptions["separate_foundations"]?["default"]?.GetValue<bool>() != defaults.SeparateFoundations
                    || !defaults.Parapets || topoOptions["parapets"]?["default"]?.GetValue<bool>() != defaults.Parapets
                    || topoOptions["terrain_nurbs"]?["default"]?.GetValue<bool>() != defaults.TerrainAsNurbs
                    || topoOptions["buildings_nurbs"]?["default"]?.GetValue<bool>() != defaults.BuildingsAsNurbs)
                    throw new Exception("MCP defaults and common model defaults differ");
                Range(Properties("aha_topo_prepare_start")["terrain_level"]!.AsObject(), 12, 3, 15);
                Range(topoOptions["near_ratio"]!.AsObject(), defaults.NearRatio, 1.5);
                Range(topoOptions["contour_interval_m"]!.AsObject(), defaults.ContourIntervalMetres, 0, exclusive: true);
                Range(topoOptions["near_depth_m"]!.AsObject(), defaults.NearDepthMetres, 0, exclusive: true);
                Range(topoOptions["parapet_width_m"]!.AsObject(), defaults.ParapetWidthMetres, 0, exclusive: true);
                Range(topoOptions["parapet_height_m"]!.AsObject(), defaults.ParapetHeightMetres, 0, exclusive: true);
                Range(topoOptions["road_drop_m"]!.AsObject(), defaults.RoadDropMetres, 0);
                Range(topoOptions["unknown_height_m"]!.AsObject(), defaults.UnknownHeight, 0, exclusive: true);
                Range(topoOptions["floor_height_m"]!.AsObject(), defaults.FloorHeight, 0, exclusive: true);
                var xyOptions = Properties("aha_topo_xy_prepare_start")["options"]!["properties"]!.AsObject();
                if (!xyOptions.Select(pair => pair.Key).ToHashSet().SetEquals(new[] { "roads", "min_building_area_m2" }))
                    throw new Exception("XY accepts settings unrelated to planar context");
                var evidence = Definition("aha_eum_read");
                if (evidence["annotations"]?["readOnlyHint"]?.GetValue<bool>() != true
                    || evidence["annotations"]?["destructiveHint"]?.GetValue<bool>() != false
                    || Properties("aha_eum_read")["sections"]?["items"]?["enum"]?.AsArray()
                        .Select(value => value!.GetValue<string>()).ToHashSet().SetEquals(new[] { "parcel", "bcr_far", "height_floors", "building_line", "open_space" }) != true
                    || evidence["inputSchema"]?["required"]?.AsArray().Select(value => value!.GetValue<string>()).ToHashSet()
                        .SetEquals(new[] { "document_id", "pnu" }) != true)
                    throw new Exception("Eum evidence read contract differs from supported section names");
                var objectBefore = await RhinoDispatch.InvokeAsync(() => new
                {
                    Geometry = doc!.Objects.FindId(id)!.Geometry.DataCRC(0),
                    Attributes = doc.Objects.FindId(id)!.Attributes.ToJSON(new Rhino.FileIO.SerializationOptions()),
                    Layers = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).ToHashSet()
                });
                foreach (JsonNode documentNumber in new JsonNode[] { JsonValue.Create(doc!.RuntimeSerialNumber)!,
                    JsonValue.Create((int)doc.RuntimeSerialNumber)!, JsonValue.Create((long)doc.RuntimeSerialNumber)!,
                    JsonNode.Parse(doc.RuntimeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))! })
                {
                    var direct = await McpTools.CallAsync("aha_objects", new JsonObject { ["document_id"] = documentNumber, ["offset"] = 0 });
                    if (direct?["total"]?.GetValue<int>() != 1) throw new Exception("Constructed and parsed MCP integer inputs differ");
                }
                foreach (JsonNode widthNumber in new JsonNode[] { JsonValue.Create(400)!, JsonValue.Create(400L)!, JsonValue.Create(400.25m)!, JsonValue.Create(400.25f)!, JsonNode.Parse("400.25")! })
                {
                    string? validationError = null;
                    try
                    {
                        var startedNumeric = await McpTools.CallAsync("aha_topo_prepare_start", new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber,
                            ["pnus"] = new JsonArray("invalid-pnu"), ["width_m"] = widthNumber, ["terrain_level"] = 12 });
                        string numericJob = startedNumeric!["job_id"]!.GetValue<string>();
                        for (int attempt = 0; attempt < 100; attempt++)
                        {
                            var numericState = await McpTools.CallAsync("aha_job_status", new JsonObject { ["job_id"] = numericJob });
                            if (numericState!["state"]!.GetValue<string>() != "running")
                            {
                                if (numericState["state"]!.GetValue<string>() == "failed") validationError = numericState["error"]?.GetValue<string>();
                                break;
                            }
                            await Task.Delay(20);
                        }
                    }
                    catch (ArgumentException error) { validationError = error.Message; }
                    if (validationError?.Contains("19자리", StringComparison.Ordinal) != true)
                        throw new Exception("Constructed/parsed numbers did not reach the same PNU validation before external access");
                }
                foreach (JsonNode invalidNumber in new JsonNode[] { JsonValue.Create(-1)!, JsonValue.Create(.5)!, JsonValue.Create((ulong)uint.MaxValue + 1)!, JsonValue.Create("1")! })
                {
                    bool rejectedNumber = false;
                    try { await McpTools.CallAsync("aha_objects", new JsonObject { ["document_id"] = invalidNumber }); }
                    catch (ArgumentException) { rejectedNumber = true; }
                    if (!rejectedNumber) throw new Exception("MCP document numeric range/type not enforced");
                }
                JsonObject TopoArgs() => new() { ["document_id"] = doc!.RuntimeSerialNumber, ["pnus"] = new JsonArray("9900000000100010000") };
                async Task Rejected(string name, JsonObject args)
                {
                    var response = await Call(name, args);
                    if (response["isError"]?.GetValue<bool>() == true) return;
                    string job = response["structuredContent"]?["job_id"]?.GetValue<string>()
                        ?? throw new Exception(name + " accepted invalid input without an error or job");
                    for (int attempt = 0; attempt < 100; attempt++)
                    {
                        var state = (await Call("aha_job_status", new JsonObject { ["job_id"] = job }))["structuredContent"];
                        if (state?["state"]?.GetValue<string>() == "failed" && !string.IsNullOrWhiteSpace(state["error"]?.GetValue<string>())) return;
                        if (state?["state"]?.GetValue<string>() != "running") throw new Exception(name + " invalid input did not fail");
                        await Task.Delay(20);
                    }
                    throw new Exception(name + " invalid input did not finish validation");
                }
                var unknown = await Call("aha_unregistered_tool", new JsonObject());
                if (unknown["isError"]?.GetValue<bool>() != true)
                    throw new Exception("Unregistered MCP tool must fail before starting a job");
                foreach (string name in new[] { "aha_topo_prepare_start", "aha_topo_xy_prepare_start" })
                {
                    var empty = TopoArgs(); empty["pnus"] = new JsonArray(); await Rejected(name, empty);
                    var duplicate = TopoArgs(); duplicate["pnus"] = new JsonArray("9900000000100010000", "9900000000100010000"); await Rejected(name, duplicate);
                    var malformed = TopoArgs(); malformed["pnus"] = new JsonArray("bad-pnu"); await Rejected(name, malformed);
                    var extent = TopoArgs(); extent["width_m"] = 49; await Rejected(name, extent);
                    var oversized = TopoArgs(); oversized["depth_m"] = 3001; await Rejected(name, oversized);
                    var area = TopoArgs(); area["options"] = new JsonObject { ["min_building_area_m2"] = -1 }; await Rejected(name, area);
                    var typed = TopoArgs(); typed["options"] = new JsonObject { ["roads"] = "true" }; await Rejected(name, typed);
                }
                var unused = TopoArgs(); unused["options"] = new JsonObject { ["near"] = true }; await Rejected("aha_topo_xy_prepare_start", unused);
                var unusedContour = TopoArgs(); unusedContour["options"] = new JsonObject { ["contour"] = false }; await Rejected("aha_topo_xy_prepare_start", unusedContour);
                var retired = TopoArgs(); retired["options"] = new JsonObject { ["excavate_buildings"] = false }; await Rejected("aha_topo_prepare_start", retired);
                var noNear = TopoArgs(); noNear["options"] = new JsonObject { ["contour"] = true, ["near"] = false }; await Rejected("aha_topo_prepare_start", noNear);
                var noBase = TopoArgs(); noBase["options"] = new JsonObject { ["contour"] = true, ["near_depth_m"] = 2 }; await Rejected("aha_topo_prepare_start", noBase);
                var noInterval = TopoArgs(); noInterval["options"] = new JsonObject { ["contour"] = true, ["contour_interval_m"] = 0 }; await Rejected("aha_topo_prepare_start", noInterval);
                foreach (var value in new JsonNode?[] { JsonValue.Create("false"), JsonValue.Create(0), null })
                {
                    foreach (string option in new[] { "contour", "separate_foundations", "parapets" })
                    {
                        var badFlag = TopoArgs(); badFlag["options"] = new JsonObject { [option] = value?.DeepClone() };
                        await Rejected("aha_topo_prepare_start", badFlag);
                    }
                }
                var badRatio = TopoArgs(); badRatio["options"] = new JsonObject { ["near_ratio"] = 1.49 }; await Rejected("aha_topo_prepare_start", badRatio);
                foreach (string name in new[] { "parapet_width_m", "parapet_height_m" })
                foreach (JsonNode? value in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create(-.3), JsonValue.Create("0.3"), null })
                {
                    var bad = TopoArgs(); bad["options"] = new JsonObject { ["parapets"] = false, [name] = value?.DeepClone() };
                    await Rejected("aha_topo_prepare_start", bad);
                }
                await Rejected("aha_eum_read", new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber, ["pnu"] = "bad-pnu" });
                foreach (var sections in new[] { new JsonArray(), new JsonArray("not-supported"), new JsonArray("parcel", "parcel") })
                    await Rejected("aha_eum_read", new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber, ["pnu"] = "9900000000100010000", ["sections"] = sections });
                if (!await RhinoDispatch.InvokeAsync(() => doc!.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject).Count() == 1
                    && doc.Objects.FindId(id)?.Geometry.DataCRC(0) == objectBefore.Geometry
                    && doc.Objects.FindId(id)?.Attributes.ToJSON(new Rhino.FileIO.SerializationOptions()) == objectBefore.Attributes
                    && objectBefore.Layers.SetEquals(doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id))))
                    throw new Exception("Rejected MCP input changed geometry, attributes or layers");
                await Rejected("aha_topo_create", new JsonObject { ["job_id"] = "missing-prepared-job" });
                {
                    var projection = new LocalProjection(new GeoPoint(127, 37.5));
                    var parcel = new GeoFeature { Id = "wire-parcel", Pnu = "9900000000100010000", Address = "Fixed wire parcel", Polygons = new()
                    { new() { new() { projection.ToGeo(0, 0), projection.ToGeo(10, 0), projection.ToGeo(10, 10), projection.ToGeo(0, 10), projection.ToGeo(0, 0) } } } };
                    var workflow = new TopoWorkflow(() => new WireTopoSource(parcel));
                    string preparedId = (await RhinoDispatch.InvokeAsync(() => workflow.StartXY(doc!, new[] { parcel.Pnu }, 100, 100, true, 10)))["job_id"]!.GetValue<string>();
                    async Task<JsonNode> StateAfterWork()
                    {
                        for (int attempt = 0; attempt < 200; attempt++)
                        {
                            var state = (await Call("aha_job_status", new JsonObject { ["job_id"] = preparedId }))["structuredContent"]!;
                            if (state["state"]!.GetValue<string>() is not ("running" or "creating")) return state;
                            await Task.Delay(20);
                        }
                        throw new Exception("Prepared wire job did not reach its expected state");
                    }
                    var preparedState = await StateAfterWork();
                    if (preparedState["state"]?.GetValue<string>() != "ready" || preparedState["result"] is not null
                        || preparedState["summary"]?["prepared_object_count"]?.GetValue<int>() != 2
                        || !await RhinoDispatch.InvokeAsync(() => doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject).Count() == 1))
                        throw new Exception("Ready wire job must not change the document or advertise created IDs");
                    var createArguments = new JsonObject { ["job_id"] = preparedId };
                    var firstCreate = await Call("aha_topo_create", createArguments);
                    if (firstCreate["isError"]?.GetValue<bool>() == true) throw new Exception(firstCreate.ToJsonString());
                    var created = await StateAfterWork();
                    if (created["state"]?.GetValue<string>() != "completed") throw new Exception(created.ToJsonString());
                    var repeat = await Call("aha_topo_create", (JsonObject)createArguments.DeepClone());
                    if (!JsonNode.DeepEquals(created["result"], repeat["structuredContent"]?["result"]))
                        throw new Exception("Repeated wire create returned a different result or inserted twice");
                    var createdIds = created["result"]!["ObjectIds"]!.AsArray().Select(value => Guid.Parse(value!.GetValue<string>())).ToArray();
                    await RhinoDispatch.InvokeAsync(() =>
                    {
                        if (createdIds.Length != 2 || doc!.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject).Count() != 3
                            || createdIds.Any(objectId => doc.Objects.FindId(objectId)?.Geometry is not Curve
                                || InfoService.Read(doc, objectId)?["data"]?["parcels"]?[0]?["pnu"]?.GetValue<string>() != parcel.Pnu))
                            throw new Exception("Wire Topo creation result does not match actual XY geometry and metadata");
                        foreach (var objectId in createdIds) doc.Objects.Delete(objectId, true);
                        return true;
                    });
                }
                result["topo_and_eum_contract_checks"] = "passed";
                var read = await Call("aha_info_read", Target());
                string revision = read["structuredContent"]!["revision"]!.GetValue<string>();
                var args = Target(); args["revision"] = revision; args["patch"] = new JsonObject { ["site"]=true, ["data"]=new JsonObject { ["notes"]="wire fixture", ["bcr_percent"]=0 } };
                var updated = await Call("aha_info_update", args);
                if (updated["isError"]?.GetValue<bool>() == true) throw new Exception(updated.ToJsonString());
                var stale = await Call("aha_info_update", (JsonObject)args.DeepClone());
                if (stale["isError"]?.GetValue<bool>() != true) throw new Exception("Stale wire write accepted");
                var invalid = Target(); invalid["revision"] = updated["structuredContent"]!["revision"]!.DeepClone(); invalid["patch"] = new JsonObject { ["data"] = new JsonObject { ["max_floors"]=2.5 } };
                if ((await Call("aha_info_update",invalid))["isError"]?.GetValue<bool>() != true) throw new Exception("Invalid numeric write accepted");
                var actual = await RhinoDispatch.InvokeAsync(() => InfoService.Read(doc!, id));
                if (actual?["data"]?["notes"]?.GetValue<string>() != "wire fixture" || actual["data"]?["bcr_percent"]?.GetValue<int>() != 0) throw new Exception("Wire and installed service differ");
                var queried = await Call("aha_info_read", Target());
                if (!JsonNode.DeepEquals(actual,queried["structuredContent"]?["info"])) throw new Exception("Wire read differs from object");
                await RhinoDispatch.InvokeAsync(() =>
                {
                    var repairable = (JsonObject)actual!.DeepClone();
                    repairable["data"]!["max_floors"] = "invalid external edit";
                    using var attributes = doc!.Objects.FindId(id)!.Attributes.Duplicate();
                    attributes.SetUserString(InfoData.Key, repairable.ToJsonString());
                    if (!doc.Objects.ModifyAttributes(id, attributes, true)) throw new Exception("Cannot prepare repairable Info fixture");
                    return true;
                });
                var sites = await Call("aha_objects", new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber, ["site_only"] = true });
                var listed = sites["structuredContent"]?["objects"]?.AsArray();
                if (listed?.Any(o => o?["object_id"]?.GetValue<string>() == id.ToString()) != true)
                    throw new Exception("Site discovery hides an object with repairable business fields");
                var repairRead = await Call("aha_info_read", Target());
                if (repairRead["structuredContent"]?["info"]?["data"]?["max_floors"]?.GetValue<string>() != "invalid external edit")
                    throw new Exception("Discovered Info must remain readable for correction");
                var repairArgs = Target();
                repairArgs["revision"] = repairRead["structuredContent"]!["revision"]!.DeepClone();
                repairArgs["patch"] = new JsonObject { ["data"] = new JsonObject { ["max_floors"] = 5 } };
                var repaired = await Call("aha_info_update", repairArgs);
                if (repaired["structuredContent"]?["info"]?["data"]?["max_floors"]?.GetValue<int>() != 5)
                    throw new Exception("A discovered site cannot be corrected through the common Info write service");
                var solidId = await RhinoDispatch.InvokeAsync(() => doc!.Objects.AddBrep(
                    new BoundingBox(new Point3d(20, 0, 0), new Point3d(30, 10, 5)).ToBrep()));
                var solidTarget = new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber, ["object_id"] = solidId.ToString() };
                var solidRead = await Call("aha_info_read", solidTarget);
                var solidWrite = (JsonObject)solidTarget.DeepClone();
                solidWrite["revision"] = solidRead["structuredContent"]!["revision"]!.DeepClone();
                solidWrite["patch"] = new JsonObject { ["site"] = true, ["kind"] = "parcel" };
                if ((await Call("aha_info_update", solidWrite))["isError"]?.GetValue<bool>() == true)
                    throw new Exception("MCP Info rejects a closed Site solid");
                var solidSites = (await Call("aha_objects", new JsonObject { ["document_id"] = doc!.RuntimeSerialNumber, ["site_only"] = true }))["structuredContent"]?["objects"]?.AsArray();
                if (solidSites?.Any(value => value?["object_id"]?.GetValue<string>() == solidId.ToString()
                    && value["type"]?.GetValue<string>() == "Brep") != true)
                    throw new Exception("MCP site discovery omits a designated Site solid");
                result["wire_checks"] = "passed"; result["status"]="passed";
            }
            catch (Exception error) { result["status"]="failed"; result["error"]=error.ToString(); }
            finally
            {
                await RhinoDispatch.InvokeAsync(() => { if (doc is not null) { Jobs.CancelDocument(doc.RuntimeSerialNumber); doc.Dispose(); } return true; });
                File.WriteAllText(path, result.ToJsonString(new JsonSerializerOptions { WriteIndented=true }));
            }
        });
        return path;
    }
    private sealed class WireTopoSource(GeoFeature parcel) : ITopoSource
    {
        public Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (pnu != parcel.Pnu) throw new ArgumentException("Unexpected fixture PNU");
            return Task.FromResult(parcel);
        }
        public Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new List<GeoFeature> { parcel }); }
        public Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new List<GeoFeature>()); }
        public Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress)
            => throw new InvalidOperationException("XY wire preparation must not acquire terrain");
        public void Dispose() { }
    }
}
