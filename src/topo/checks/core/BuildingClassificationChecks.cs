using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Topo;

namespace Aha.Topo.Checks;

internal static class BuildingClassificationChecks
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        async Task Fails(Func<Task> action, string name)
        {
            Exception? failure = null;
            try { await action(); } catch (Exception error) { failure = error; }
            Check(failure != null && !failure.Message.Contains("classification-secret", StringComparison.Ordinal), name);
        }
        var source = new[] { Feature(1110140248, 0), Feature(1110138961, 0), Feature(1110138946, 0) };
        var sourceJson = JsonSerializer.Serialize(source);
        using (var client = Client(uri =>
        {
            var query = Query(uri);
            Check(uri.AbsolutePath == "/req/wfs" && query["typename"] == "lt_c_spbd" &&
                query["propertyName"] == "bd_mgt_sn,buld_se_cd,gro_flo_co,und_flo_co" && !query.ContainsKey("bbox"),
                "classification fetch requests only attributes of explicit same-layer feature IDs");
            Check(query["featureID"].Split(',').SequenceEqual(source.Select(f => f.Id.ToLowerInvariant())),
                "classification uses exact feature identity rather than PNU or location");
            return Response(new[] { Row(source[2], "0", 0), Row(source[0], "1", 0), Row(source[1], "1", 1) });
        }))
        {
            var result = await client.ClassifyBuildingsAsync(source);
            Check(result.Select(f => f.Id).SequenceEqual(source.Select(f => f.Id)), "reordered classification response preserves source order and IDs");
            Check(result.Take(2).All(BuildingRules.IsUnderground), "explicit underground code excludes both station and zero-basement-count shopping footprint");
            Check(!BuildingRules.IsUnderground(result[2]) && BuildingRules.HasKnownPosition(result[2]), "zero-floor Daehanmun counterexample remains above ground");
            Check(result[1].UndergroundFloors == 1 && result[0].UndergroundFloors == 0, "underground floor metadata remains raw and independent of position classification");
            Check(result.Zip(source).All(p => JsonSerializer.Serialize(p.First.Polygons) == JsonSerializer.Serialize(p.Second.Polygons)) && JsonSerializer.Serialize(source) == sourceJson,
                "classification preserves every multipart source footprint or courtyard coordinate");
            Check(result.Zip(source).All(p => p.First.Pnu == p.Second.Pnu && p.First.Address == p.Second.Address && p.First.Floors == p.Second.Floors && p.First.Height == p.Second.Height),
                "classification cannot replace the original PNU address height or ground-floor attributes");
            Check(result.Zip(source).All(p => JsonSerializer.Serialize(p.First.BuildingDetails) == JsonSerializer.Serialize(p.Second.BuildingDetails)),
                "building position classification preserves acquired building details");
            Check(client.BuildingClassificationSummary is { Requests: 1, SourceFeatures: 3, Underground: 2, AboveGround: 1 },
                "classification reports confirmed above and below ground counts");
        }
        using (var client = Client(_ => Response(new[] { Row(source[0], "2"), Row(source[1], null), Row(source[2], "99") })))
        {
            var result = await client.ClassifyBuildingsAsync(source);
            Check(result.All(f => f.BuildingClassification == BuildingClassificationStatus.UnknownCode && !BuildingRules.IsUnderground(f)),
                "unsupported or missing position codes remain explicit unknowns and are not excluded");
            Check(result[0].BuildingPositionCode == "2" && result[2].BuildingPositionCode == "99", "unsupported raw position codes are retained");
            Check(client.BuildingClassificationSummary?.UnknownCode == 3, "classification reports unknown codes independently");
        }
        using (var client = Client(_ => Response(new[] { Row(source[0], "1") })))
        {
            var result = await client.ClassifyBuildingsAsync(source);
            Check(result.Skip(1).All(f => f.BuildingClassification == BuildingClassificationStatus.NotFound), "complete exact-ID response explicitly marks absent identities as not found");
            Check(client.BuildingClassificationSummary?.NotFound == 2 && result.Count == 3, "missing enrichment does not discard original building geometry");
        }
        var noIdentity = new[] { new GeoFeature { Id = source[0].Id, SourceLayer = "LT_C_SPBD", Polygons = source[0].Polygons },
            new GeoFeature { Id = "LT_C_SPBD.bad,other", SourceLayer = "LT_C_SPBD", BuildingId = "administrative-id", Polygons = source[0].Polygons } };
        using (var client = Client(_ => throw new Exception("Unexpected network request")))
        {
            var result = await client.ClassifyBuildingsAsync(noIdentity);
            Check(result.All(f => f.BuildingClassification == BuildingClassificationStatus.MissingIdentity) && client.TotalRequests == 0,
                "missing management number or unsafe source identity remains unknown without guessed joins");
            Check(!BuildingRules.IsUnderground(new GeoFeature { BuildingPositionCode = "1" }), "unverified underground code cannot exclude a building");
        }
        foreach (var issue in new[] { "different_management", "missing_management", "unexpected_id", "duplicate", "changed_floors", "wrong_count", "truncated", "missing_count", "error_envelope" })
        {
            using var client = Client(_ =>
            {
                var row = JsonNode.Parse(JsonSerializer.Serialize(Row(source[0], "1")))!;
                switch (issue)
                {
                    case "different_management": row["properties"]!["bd_mgt_sn"] = "another-management-id"; break;
                    case "missing_management": row["properties"]!.AsObject().Remove("bd_mgt_sn"); break;
                    case "unexpected_id": row["id"] = "lt_c_spbd.99999"; break;
                    case "changed_floors": row["properties"]!["gro_flo_co"] = 8; break;
                }
                var payload = JsonNode.Parse(Collection(issue == "duplicate" ? new[] { row, row.DeepClone() } : new[] { row }))!;
                if (issue == "wrong_count") payload["numberReturned"] = 5;
                if (issue == "truncated") payload["numberMatched"] = 5;
                if (issue == "missing_count") payload.AsObject().Remove("numberMatched");
                return Json(issue == "error_envelope" ? "{\"response\":{\"status\":\"ERROR\"}}" : payload.ToJsonString());
            });
            await Fails(async () => await client.ClassifyBuildingsAsync(new[] { source[0] }), "classification rejects " + issue);
        }
        using (var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))
            await Fails(async () => await client.ClassifyBuildingsAsync(source), "classification transport failure is not silently converted to unknown");
        using (var client = Client(_ => Json("<ServiceException>classification-secret</ServiceException>")))
            await Fails(async () => await client.ClassifyBuildingsAsync(source), "classification XML service error fails without exposing credentials or response text");
        using (var client = Client(_ => throw new Exception("Unexpected request")))
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Fails(async () => await client.ClassifyBuildingsAsync(source, cts.Token), "classification honors cancellation before any request");
            Check(client.TotalRequests == 0, "cancelled classification sends no requests");
            await Fails(async () => await client.ClassifyBuildingsAsync(new[] { source[0], source[0] }), "duplicate source identities cannot be silently merged during classification");
        }
        var many = Enumerable.Range(1, 5 * VWorldClient.BuildingClassificationBatchSize + 1).Select(i => Feature(i, 0, "shared-number")).ToArray();
        var handler = new AsyncHandler(async (uri, token) =>
        {
            await Task.Delay(10, token);
            var query = Query(uri); var ids = query["featureID"].Split(',');
            if (ids.Length > VWorldClient.BuildingClassificationBatchSize || query["maxFeatures"] != ids.Length.ToString()) throw new Exception("Invalid batch size");
            return Response(ids.Select(id => Row(many.Single(f => f.Id.ToLowerInvariant() == id), "0")));
        });
        using (var client = new VWorldClient("classification-secret", handler))
        {
            var result = await client.ClassifyBuildingsAsync(many);
            Check(result.Count == many.Length && result.All(BuildingRules.HasKnownPosition) && handler.Calls == 6,
                "classification batches every feature exactly once even when administrative IDs are shared");
            Check(handler.MaximumConcurrency is > 1 and <= 4, "classification limits parallel requests to four");
        }
        var overBudget = Enumerable.Range(1, VWorldClient.BuildingClassificationBatchSize * 256 + 1).Select(i => Feature(i, 0)).ToArray();
        var byId=overBudget.ToDictionary(f=>f.Id.ToLowerInvariant());
        using (var client = Client(uri => Response(Query(uri)["featureID"].Split(',').Select(id=>Row(byId[id.ToLowerInvariant()],"0")))))
        {
            var classified=await client.ClassifyBuildingsAsync(overBudget);
            Check(classified.Count==overBudget.Length&&classified.All(BuildingRules.HasKnownPosition),
                "classification preserves complete valid output beyond the former batch count budget");
            Check(client.TotalRequests == 257, "classification requests exactly the required batches");
        }
        // Full public acquisition path: geometry still comes from Data API, position from WFS.
        using (var client = Client(uri => uri.AbsolutePath == "/req/data"
            ? Json(JsonSerializer.Serialize(new { response = new { status = "OK", record = new { total = "1", current = "1" },
                page = new { total = "1", current = "1", size = "1000" }, result = new { featureCollection = new { type = "FeatureCollection", features = new[] {
                    new { type = "Feature", id = source[0].Id, properties = new { bd_mgt_sn = source[0].BuildingId, gro_flo_co = 0, pnu = source[0].Pnu, addr = source[0].Address },
                        geometry = new { type = "MultiPolygon", coordinates = source[0].Polygons.Select(p=>p.Select(r=>r.Select(v=>new[]{v.Lon,v.Lat}))) } } } } } } }))
            : Response(new[] { Row(source[0], "1") })))
        {
            var result = await client.BuildingsAsync(new GeoBounds(126.978, 37.566, 126.979, 37.567));
            Check(result.Count == 1 && BuildingRules.IsUnderground(result[0]) && client.TotalRequests == 2,
                "public building acquisition enriches classifications after complete original geometry collection");
            var parcelInfo = JsonNode.Parse(TopoObjectInfo.Building(result[0], detailed: true))!["data"]!["parcels"]!.AsArray().Single()!;
            Check(parcelInfo["pnu"]!.GetValue<string>() == source[0].Pnu && parcelInfo["address"]!.GetValue<string>() == source[0].Address && client.TotalRequests == 2,
                "acquired building PNU and address survive classification and object info without additional requests");
        }
        return passed;
    }

    private static GeoFeature Feature(long id, double floors, string? management = null) => new()
    {
        Id = "LT_C_SPBD." + id, SourceLayer = "LT_C_SPBD", BuildingId = management ?? "management-" + id,
        BuildingDetails = new("건물 " + id, "A동", "", "가상시", "예시구", "", "모형로", "110"),
        Floors = floors, Pnu = "9900000000100310000", Address = "가상시 예시구 예시동 1-1", Polygons = new() { new() {
            new() { new(126.978,37.566),new(126.979,37.566),new(126.979,37.567),new(126.978,37.566) },
            new() { new(126.9782,37.5662),new(126.9784,37.5662),new(126.9784,37.5664),new(126.9782,37.5662) } } }
    };
    private static object Row(GeoFeature source, string? code, double below = 0) => new
    {
        type = "Feature", id = source.Id.ToLowerInvariant(), geometry = (object?)null,
        properties = new { bd_mgt_sn = source.BuildingId, buld_se_cd = code, gro_flo_co = source.Floors, und_flo_co = below }
    };
    private static string Collection(IEnumerable<object> rows) => JsonSerializer.Serialize(new
        { type = "FeatureCollection", features = rows, numberMatched = rows.Count(), numberReturned = rows.Count(), totalFeatures = rows.Count() });
    private static HttpResponseMessage Response(IEnumerable<object> rows) => Json(Collection(rows));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static VWorldClient Client(Func<Uri, HttpResponseMessage> handler) => new("classification-secret", new AsyncHandler((uri, _) => Task.FromResult(handler(uri))));
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2))
        .ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1]));
    private sealed class AsyncHandler(Func<Uri,CancellationToken,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls;
        private int active;
        public int MaximumConcurrency;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); var current = Interlocked.Increment(ref active);
            lock (this) MaximumConcurrency = Math.Max(MaximumConcurrency, current);
            try { return await handler(request.RequestUri!,ct); } finally { Interlocked.Decrement(ref active); }
        }
    }
}
