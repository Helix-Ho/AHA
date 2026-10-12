using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Topo;

namespace Aha.Topo.Checks;

internal static class BuildingSourceChecks
{
    public static async Task<int> Run()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        async Task<Exception> Fails(Func<Task> action, string name)
        {
            Exception? failure = null;
            try { await action(); } catch (Exception ex) { failure = ex; }
            Check(failure != null, name);
            return failure!;
        }
        const string buildingId = "9900000000100310000012345";
        const string dummyKey = "building-source-offline-key";
        var projection = new LocalProjection(new GeoPoint(126.9782, 37.5666));
        var small = projection.Bounds(100);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(Feature("spbd.1", "4", buildingId))))
        {
            var building = VWorldClient.ParseBuilding(document.RootElement);
            Check(building.SourceLayer == "LT_C_SPBD" && building.Id == "spbd.1" && building.BuildingId == buildingId,
                "building source feature ID and management number remain separate");
            Check(building.Pnu == "" && building.Address == "", "building management number never becomes a fabricated cadastral PNU or address");
            Check(building.Height == null && building.Floors == 4,
                "address building uses gro_flo_co and never consumes legacy height or floor fields");
            var sourcePolygons = document.RootElement.GetProperty("geometry").GetProperty("coordinates");
            Check(building.Polygons.Count == 2 && building.Polygons[0].Count == 2,
                "address building multipart geometry and courtyard hole are preserved");
            var same = true;
            for (var p = 0; p < building.Polygons.Count; p++)
            for (var r = 0; r < building.Polygons[p].Count; r++)
            for (var v = 0; v < building.Polygons[p][r].Count; v++)
                same &= building.Polygons[p][r][v] == new GeoPoint(sourcePolygons[p][r][v][0].GetDouble(), sourcePolygons[p][r][v][1].GetDouble());
            Check(same, "every source building vertex retains its exact longitude and latitude");
        }
        foreach (var floor in new string?[] { null, "", "NaN", "Infinity", "unknown" })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(Feature("spbd.unknown", floor, null)));
            var building = VWorldClient.ParseBuilding(document.RootElement);
            Check(building.Floors == null && building.Height == null && building.BuildingId == "" && building.Polygons.Count == 2,
                "missing management number and invalid floors retain source geometry: " + (floor ?? "null"));
        }
        foreach (var floor in new object[] { 0, -1, 12, "3.5" })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(Feature("spbd.floor", floor, buildingId)));
            Check(VWorldClient.ParseBuilding(document.RootElement).Floors == Convert.ToDouble(floor, System.Globalization.CultureInfo.InvariantCulture),
                "finite source floor values remain raw for the height policy: " + floor);
        }
        var explicitPnu = JsonNode.Parse(JsonSerializer.Serialize(Feature("spbd.pnu", "2", buildingId)))!;
        explicitPnu["properties"]!["pnu"] = "9900000000100310000";
        explicitPnu["properties"]!["addr"] = "가상시 예시구 예시동 1-1";
        using (var document = JsonDocument.Parse(explicitPnu.ToJsonString()))
        {
            var building = VWorldClient.ParseBuilding(document.RootElement);
            Check(building.Pnu == "9900000000100310000", "only an explicit valid PNU may be retained");
            Check(building.Address == "가상시 예시구 예시동 1-1", "building acquisition retains an explicitly supplied addr");
        }
        explicitPnu["properties"]!["buld_nm"] = " 건물 이름 ";
        explicitPnu["properties"]!["buld_nm_dc"] = "A동";
        explicitPnu["properties"]!["bul_eng_nm"] = "Building A";
        explicitPnu["properties"]!["sido"] = "가상시";
        explicitPnu["properties"]!["sigungu"] = "예시구";
        explicitPnu["properties"]!["gu"] = "예시면";
        explicitPnu["properties"]!["rd_nm"] = "창작로";
        explicitPnu["properties"]!["buld_no"] = "23-1";
        using (var document = JsonDocument.Parse(explicitPnu.ToJsonString()))
        {
            var building = VWorldClient.ParseBuilding(document.RootElement);
            Check(building.BuildingDetails == new BuildingDetails("건물 이름", "A동", "Building A", "가상시", "예시구", "예시면", "창작로", "23-1"),
                "building response retains descriptive fields and gu as township, not district");
            Check(building.BuildingDetails!.RoadAddress == "가상시 예시구 예시면 창작로 23-1" && building.Address == "가상시 예시구 예시동 1-1",
                "road address composition does not overwrite an explicitly supplied parcel address");
            Check((building.BuildingDetails with { CityDistrict = "" }).RoadAddress == "" &&
                  (building.BuildingDetails with { BuildingNumber = "" }).RoadAddress == "",
                "incomplete address components never masquerade as a full road address");
        }

        var paging = new FakeHandler(uri =>
        {
            var q = Query(uri);
            if (uri.AbsolutePath != "/req/data" || q["data"] != VWorldClient.BuildingSourceLayer ||
                q["crs"] != "EPSG:4326" || q["size"] != "1000" || q["key"] != dummyKey ||
                !q["geomFilter"].StartsWith("BOX(126.", StringComparison.Ordinal) || q.ContainsKey("bbox"))
                throw new Exception("Unexpected building source request");
            var page = int.Parse(q["page"]);
            return Json(Page(new[] { Feature("spbd." + page, page == 1 ? "3" : null, buildingId) }, 2, page, 1));
        });
        using (var client = new VWorldClient(dummyKey, paging))
        {
            var result = await client.BuildingsAsync(small);
            Check(result.Count == 2 && paging.Calls == 2 && client.TotalRequests == 2,
                "building acquisition uses Data API lon/lat BOX and retrieves all declared pages");
            Check(result.Select(f => f.Id).Distinct().Count() == 2 && result.Select(f => f.BuildingId).Distinct().Count() == 1,
                "distinct source geometries sharing an administrative building number are not merged");
            var report = client.CollectionReports.Single();
            Check(report.Layer == VWorldClient.BuildingSourceLayer && report.ApiRecords == 2 && report.UniqueFeatures == 2,
                "building completeness report identifies the actual source layer");
        }
        var large = projection.Bounds(1500);
        var tiles = VWorldClient.QueryDataTiles(large);
        Check(tiles.Count == 9 && tiles.All(tile =>
        {
            var a = projection.ToLocal(new GeoPoint(tile.West, tile.South));
            var b = projection.ToLocal(new GeoPoint(tile.East, tile.North));
            return (b.X - a.X) * (b.Y - a.Y) < 2_000_000;
        }), "shared Data API tiling keeps every 3 km query tile below two square kilometres");
        Check(tiles[0].West == large.West && tiles[^1].East == large.East &&
            tiles[0].South == large.South && tiles[^1].North == large.North &&
            tiles[0].East == tiles[1].West && tiles[0].North == tiles[3].South,
            "shared Data API tiling has no spatial gaps");
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Page(new[] { Feature("spbd.shared", "3", null) }, 1)))))
        {
            var result = await client.BuildingsAsync(large);
            var report = client.CollectionReports.Single();
            Check(result.Count == 1 && report.Requests == 9 && report.CollectedRecords == 9 && report.RepeatedRecords == 8,
                "identical building records across complete tiles deduplicate with overlap accounting");
        }
        var changes = 0;
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Page(
            new[] { Feature("spbd.conflict", (++changes).ToString(), buildingId) }, 1)))))
            await Fails(async () => { await client.BuildingsAsync(large); }, "same building feature ID with changed tile attributes fails");
        changes = 0;
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ =>
        {
            var value = JsonNode.Parse(Page(new[] { Feature("spbd.conflict", "3", buildingId) }, 1))!;
            value["response"]!["result"]!["featureCollection"]!["features"]![0]!["geometry"]!["coordinates"]![0]![0]![0]![0] = 126.978 + .001 * changes++;
            return Json(value.ToJsonString());
        })))
            await Fails(async () => { await client.BuildingsAsync(large); }, "same building feature ID with changed tile geometry fails");

        foreach (var field in new[] { "record.total", "record.current", "page.total", "page.current", "page.size" })
        {
            var value = JsonNode.Parse(Page(new[] { Feature("spbd.1", "3", null) }, 1))!;
            var parts = field.Split('.');
            value["response"]![parts[0]]!.AsObject().Remove(parts[1]);
            using var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(value.ToJsonString())));
            await Fails(async () => { await client.BuildingsAsync(small); }, "building completeness rejects missing " + field);
        }
        foreach (var (field, replacement) in new[]
        {
            ("record.total", "-1"), ("record.current", "0"), ("page.current", "2"),
            ("page.total", "2"), ("page.size", "0"), ("page.size", "1001")
        })
        {
            var value = JsonNode.Parse(Page(new[] { Feature("spbd.1", "3", null) }, 1))!;
            var parts = field.Split('.'); value["response"]![parts[0]]![parts[1]] = replacement;
            using var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(value.ToJsonString())));
            await Fails(async () => { await client.BuildingsAsync(small); }, "building completeness rejects inconsistent " + field + "=" + replacement);
        }
        using (var client = new VWorldClient(dummyKey, new FakeHandler(uri => Json(Page(
                   new[] { Feature("spbd.duplicate", "3", null) }, 2, int.Parse(Query(uri)["page"]), 1)))))
            await Fails(async () => { await client.BuildingsAsync(small); }, "identical feature repeated across building pages fails");
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Page(
                   new[] { Feature("spbd.duplicate", "3", null), Feature("spbd.duplicate", "3", null) }, 2)))))
            await Fails(async () => { await client.BuildingsAsync(small); }, "duplicate source ID within one building page fails");

        const string empty = "{\"response\":{\"status\":\"NOT_FOUND\",\"record\":{\"total\":\"0\",\"current\":\"0\"},\"page\":{\"total\":\"1\",\"current\":\"1\",\"size\":\"1000\"}}}";
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(empty))))
            Check((await client.BuildingsAsync(small)).Count == 0, "actual NOT_FOUND schema succeeds without a result object");
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json("{\"response\":{\"status\":\"NOT_FOUND\"}}"))))
            await Fails(async () => { await client.BuildingsAsync(small); }, "bare NOT_FOUND does not silently prove an empty building area");
        using (var cancellation = new CancellationTokenSource())
        {
            var handler = new FakeHandler(_ => { cancellation.Cancel(); return Json(Page(new[] { Feature("spbd.1", "3", null) }, 2, 1, 1)); });
            using var client = new VWorldClient(dummyKey, handler);
            var failure = await Fails(async () => { await client.BuildingsAsync(small, cancellation.Token); }, "building pagination cancellation stops immediately");
            Check(failure is OperationCanceledException && handler.Calls == 1, "building caller cancellation remains cancellation and sends no next page");
        }
        using (var client = new VWorldClient(dummyKey, new FakeHandler(uri =>
        {
            var page = int.Parse(Query(uri)["page"]);
            return Json(Page(new[] { Feature("changing." + page, "3", null) }, page == 1 ? 2 : 3, page, 1));
        })))
            await Fails(async () => { await client.BuildingsAsync(small); }, "shared collector rejects changing total during pagination");
        var paged = new FakeHandler(uri =>
        {
            var page = int.Parse(Query(uri)["page"]);
            return Json(Page(new[] { Feature("budget." + page, "3", null) }, 1000, page, 1));
        });
        using (var client = new VWorldClient(dummyKey, paged))
            Check((await client.BuildingsAsync(small)).Count==1000,
                "valid small pages complete beyond the former request count budget");
        Check(paged.Calls == 1000, "pagination stops at the verified source total");
        return passed + await BuildingClassificationChecks.Run();
    }

    private static object Feature(string id, object? floors, string? management) => new
    {
        type = "Feature", id,
        properties = new { bd_mgt_sn = management, gro_flo_co = floors, hg = 999, ground_floor_co = 99 },
        geometry = new { type = "MultiPolygon", coordinates = new[]
        {
            new[]
            {
                new[] { new[] {126.978,37.566}, new[] {126.979,37.566}, new[] {126.979,37.567}, new[] {126.978,37.567}, new[] {126.978,37.566} },
                new[] { new[] {126.9782,37.5662}, new[] {126.9784,37.5662}, new[] {126.9784,37.5664}, new[] {126.9782,37.5664}, new[] {126.9782,37.5662} }
            },
            new[] { new[] { new[] {126.98,37.566}, new[] {126.981,37.566}, new[] {126.981,37.567}, new[] {126.98,37.566} } }
        } }
    };
    private static string Page(IEnumerable<object> features, int total, int current = 1, int size = 1000) => JsonSerializer.Serialize(new
    {
        response = new
        {
            status = "OK", record = new { total = total.ToString(), current = features.Count().ToString() },
            page = new { total = Math.Max(1, (total + size - 1) / size).ToString(), current = current.ToString(), size = size.ToString() },
            result = new { featureCollection = new { type = "FeatureCollection", features } }
        }
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(response(request.RequestUri!));
        }
    }
}
