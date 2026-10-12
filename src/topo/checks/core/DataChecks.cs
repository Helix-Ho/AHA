using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aha.Topo;

namespace Aha.Topo.Checks;

internal static class DataChecks
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
            try { await action(); }
            catch (Exception ex) { failure = ex; }
            Check(failure != null, name);
            return failure!;
        }

        var origin = new GeoPoint(126.9782, 37.5666);
        var projection = new LocalProjection(origin);
        var atOrigin = projection.ToLocal(origin);
        Check(atOrigin == (0d, 0d), "projection origin is zero metres");
        var roundTrip = projection.ToLocal(projection.ToGeo(500, -500));
        Check(Math.Abs(roundTrip.X - 500) < 1e-6 && Math.Abs(roundTrip.Y + 500) < 1e-6,
            "500 metre projection round-trip");
        var degree = projection.ToLocal(new GeoPoint(origin.Lon + .001, origin.Lat + .001));
        Check(degree.X > 88 && degree.X < 89 && degree.Y > 110 && degree.Y < 112,
            "WGS84 latitude-dependent metre scale");
        var bounds = projection.Bounds(100);
        Check(Math.Abs(projection.ToLocal(new GeoPoint(bounds.East, bounds.North)).X - 100) < 1e-6,
            "query bounds use metres");
        var children = bounds.Split();
        Check(children.Length == 4 && children[0].West == bounds.West && children[3].North == bounds.North &&
              children[0].East == children[1].West && children[0].North == children[2].South,
            "quadrants share edges without gaps");
        List<GeoPoint> Ring(params (double X, double Y)[] local) =>
            local.Select(p => projection.ToGeo(p.X, p.Y)).ToList();
        var polygon = new List<List<GeoPoint>>
        {
            Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)),
            Ring((2, 2), (4, 2), (4, 4), (2, 4), (2, 2))
        };
        Check(Math.Abs(Geo.Area(polygon, projection) - 96) < 1e-5, "footprint subtracts holes");
        polygon[0].Reverse();
        polygon[1].Reverse();
        Check(Math.Abs(Geo.Area(polygon, projection) - 96) < 1e-5, "area is independent of ring direction");
        Check(Geo.SignedArea(new[] { (1e9, 1e9), (1e9 + 10, 1e9), (1e9 + 10, 1e9 + 10), (1e9, 1e9 + 10) }) == 100,
            "shoelace avoids cancellation at large coordinates");

        using (var document = JsonDocument.Parse(Collection(new[] { Feature("dt_d002.fid-1", 42) }, 1)))
        {
            var parsed = VWorldClient.ParseParcelCollection(document.RootElement).Single();
            Check(parsed.Feature.SourceLayer == "dt_d002" && parsed.Feature.Pnu == "9900000000100310000", "parcel source layer and PNU are preserved");
            Check(parsed.Feature.Polygons[0][0][0] == new GeoPoint(126.978, 37.566), "GeoJSON coordinates are longitude then latitude");
            Check(parsed.Feature.Polygons[0][0].First() == parsed.Feature.Polygons[0][0].Last(), "rings are closed");
            Check(parsed.Feature.Id == "dt_d002.fid-1" && parsed.Identity == "source:99000:42", "raw ID and stable source identity remain distinct");
            Check(parsed.Feature.Address == "", "WFS parcel without addr preserves an empty address rather than deriving one from PNU");
        }
        using (var document = JsonDocument.Parse(Collection(new[] { Feature("normal.1", 1) }, 1)))
        {
            var parsed = VWorldClient.ParseParcelCollection(document.RootElement).Single().Feature;
            Check(parsed.Height == null && parsed.Floors == null, "parcel geometry does not acquire building heights");
        }

        const string dummyKey = "offline-dummy-key";
        const string symbols = "전답과목임광염대장학차주창도철제천구유양수공체원종사묘잡";
        for (int i = 0; i < symbols.Length; i++)
            Check(ParcelLandCategory.ParseLabel("123-4" + symbols[i]) == i + 1,
                "registered cadastral symbol maps to its official category: " + symbols[i]);
        Check(ParcelLandCategory.ParseLabel("  산 123 - 4 도  ") == 14,
            "mountain parcel prefix and formatting whitespace do not change land category");
        foreach (var label in new string?[] { null, "", "123", "도로", "123도로", "서울대로", "123-도", "123도 extra", "123도전", "NaN도" })
            Check(ParcelLandCategory.ParseLabel(label) == null, "unknown/malformed cadastral label remains unclassified: " + label);
        foreach (var layer in new[] { "dt_d002", "LP_PA_CBND_BUBUN" })
        {
            string LabelCollection(string label) => JsonSerializer.Serialize(new
            {
                type = "FeatureCollection", features = new[] { new {
                    id = "stable.1", properties = new Dictionary<string,string> {
                        ["pnu"] = "9900000000100310000", [layer == "dt_d002" ? "lnm_lndcgr_smbol" : "jibun"] = label },
                    geometry = new { type = "Polygon", coordinates = new[] { new[] {
                        new[] {126.978,37.566},new[] {126.979,37.566},new[] {126.979,37.567},new[] {126.978,37.566} } } }
                } }
            });
            using var roadDoc = JsonDocument.Parse(LabelCollection(" 123-4도 "));
            using var landDoc = JsonDocument.Parse(LabelCollection("123-4대"));
            using var unknownDoc = JsonDocument.Parse(LabelCollection("123-4"));
            var parsedRoad = VWorldClient.ParseParcelCollection(roadDoc.RootElement,layer).Single();
            var parsedLand = VWorldClient.ParseParcelCollection(landDoc.RootElement,layer).Single();
            var parsedUnknown = VWorldClient.ParseParcelCollection(unknownDoc.RootElement,layer).Single();
            Check(parsedRoad.Feature.ParcelLabel == " 123-4도 " && parsedRoad.Feature.LandCategoryCode == 14,
                layer + " preserves raw parcel label and normalized road category");
            Check(parsedLand.Feature.LandCategoryCode == 8 && parsedUnknown.Feature.LandCategoryCode == null,
                layer + " distinguishes non-road and unknown without reading PNU digits");
            Check(parsedRoad.Identity == parsedLand.Identity && parsedRoad.ContentHash != parsedLand.ContentHash,
                layer + " category change remains part of stable collection content identity");
            Check(parsedRoad.Feature.Address == "" && parsedLand.Feature.Address == "",
                layer + " cadastral labels do not masquerade as full addresses when addr is absent");
        }
        var searchHandler = new FakeHandler(uri =>
        {
            var query = Query(uri);
            if (uri.AbsolutePath != "/req/search" || query["key"] != dummyKey || query["query"] != "가상시 모형관 110")
                throw new Exception("Unexpected search request");
            var ids = query["category"] == "ROAD"
                ? new[] { "9900000000100310000", "9900000000100310000" }
                : new[] { "9900000000100310000", "9900000000100320000" };
            return Json(JsonSerializer.Serialize(new
            {
                response = new
                {
                    status = "OK", result = new
                    {
                        items = ids.Select(id => new
                        {
                            id, address = new { road = "가상시 예시구 모형로 110", parcel = "예시동 1-1", bldnm = "모형관" },
                            point = new { x = "126.9782", y = "37.5666" }
                        })
                    }
                }
            }));
        });
        using (var client = new VWorldClient(dummyKey, searchHandler))
        {
            var found = await client.SearchAsync(" 가상시 모형관 110 ");
            Check(found.Count == 2 && searchHandler.Calls == 2 && found[0].ToString().Contains("모형로"),
                "road/parcel search encodes input and deduplicates address candidates by PNU");
        }

        var pointHandler = new FakeHandler(uri =>
        {
            var query = Query(uri);
            if (query["geomFilter"] != "POINT(126.9782 37.5666)" || query["data"] != "LP_PA_CBND_BUBUN")
                throw new Exception("Unexpected point query");
            return Json("{\"response\":{\"status\":\"OK\",\"record\":{\"total\":\"1\"},\"result\":{\"featureCollection\":" +
                        Collection(new[] { SelectedParcelFeature() }, 1) + "}}}");
        });
        using (var client = new VWorldClient(dummyKey, pointHandler))
        {
            var selected = await client.ParcelAtAsync(origin);
            Check(selected.Pnu == "9900000000100310000" && selected.Polygons.Count == 1, "point query returns selected parcel geometry");
            Check(selected.Address == "가상시 예시구 예시동 1-1" && selected.ParcelLabel == "31대" &&
                  selected.SourceLayer == "LP_PA_CBND_BUBUN" && pointHandler.Calls == 1,
                "selected Data API parcel preserves addr separately from jibun without an additional address request");
        }

        using (var client = new VWorldClient(dummyKey, new FakeHandler(uri =>
        {
            var query = Query(uri);
            if (!query["attrFilter"].StartsWith("pnu:=:", StringComparison.Ordinal))
                throw new Exception("Missing PNU attribute filter");
            return Json("{\"response\":{\"status\":\"OK\",\"record\":{\"total\":\"1\"},\"result\":{\"featureCollection\":" +
                        Collection(new[] { SelectedParcelFeature() }, 1) + "}}}");
        })))
        {
            var selected = await client.ParcelByPnuAsync("9900000000100310000");
            Check(selected.Pnu == "9900000000100310000", "PNU query returns the requested parcel");
            Check(selected.Address == "가상시 예시구 예시동 1-1" && client.TotalRequests == 1,
                "PNU selection retains the supplied addr in its single Data API response");
            await Fails(async () => { await client.ParcelByPnuAsync("9900000000100320000"); },
                "mismatched PNU response cannot select the wrong parcel");
        }

        var splitCalls = 0;
        var splitHandler = new FakeHandler(uri =>
        {
            var query = Query(uri);
            var call = splitCalls++;
            if (call == 0 && query["bbox"] != "37,126,38,127,EPSG:4326") throw new Exception("Wrong WFS axis order");
            if (query["maxFeatures"] != "1000" || query["typename"] != "dt_d002") throw new Exception("Wrong WFS parameters");
            if (call == 0) return Json(Collection(new[] { Feature("dt_d002.fid-root", 42) }, 2));
            // The same parcel row has different generated IDs in overlapping tiles.
            // Distinct source rows must not merge on PNU alone.
            var sourceId = call <= 2 ? 42 : 43;
            return Json(Collection(new[] { Feature("dt_d002.fid-child" + call, sourceId) }, 1));
        });
        using (var client = new VWorldClient(dummyKey, splitHandler))
        {
            var parcels = await client.ParcelsAsync(new GeoBounds(126, 37, 127, 38));
            Check(splitCalls == 5 && parcels.Count == 2 && parcels.Select(b => b.Pnu).Distinct().Count() == 1,
                "recursive WFS uses lat/lon bbox and merges overlapping feature IDs, never PNU");
            Check(parcels.All(p => p.Address == "") && splitHandler.Calls == 5 && client.TotalRequests == 5,
                "surrounding WFS parcels without addr remain empty without per-parcel address lookup");
        }

        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Collection(new[]
               { Feature("dt_d002.fid-a", 42), Feature("dt_d002.fid-b", 42) }, 2)))))
        {
            var same = await client.ParcelsAsync(bounds);
            var report = client.CollectionReports.Single();
            Check(same.Count == 1 && report.ApiRecords == 2 && report.CollectedRecords == 2 && report.RepeatedRecords == 1,
                "identical source rows are accounted for once in geometry without being mistaken for missing records");
        }
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Collection(new[]
               { Feature("dt_d002.fid-a", 42, marker: "a"), Feature("dt_d002.fid-b", 42, marker: "b") }, 2)))))
        {
            var distinct = await client.ParcelsAsync(bounds);
            Check(distinct.Count == 2,
                "shared source identity with differing attributes is never silently merged");
        }
        int incompleteCalls = 0;
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ =>
               Json(Collection(new[] { Feature("dt_d002.fid-a", 42), Feature("dt_d002.fid-b", 42) }, incompleteCalls++ == 0 ? 3 : 2)))))
            await Fails(async () => { await client.ParcelsAsync(bounds); }, "truly missing global records still fail after duplicate accounting");

        var endlessHandler = new FakeHandler(_ => Json(Collection(new[] { Feature("dt_d002.fid-a", 42) }, 1001)));
        using (var client = new VWorldClient(dummyKey, endlessHandler))
            await Fails(async () => { await client.ParcelsAsync(bounds); }, "source records that cannot be spatially separated fail completeness");
        Check(endlessHandler.Calls > 8 && endlessHandler.Calls < 64,
            "indistinguishable query coordinates stop subdivision without claiming missing records were collected");

        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json("{\"type\":\"FeatureCollection\",\"features\":[]}"))))
            await Fails(async () => { await client.ParcelsAsync(bounds); }, "missing total count fails rather than claiming completeness");

        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ =>
               new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent(dummyKey) })))
        {
            var error = await Fails(async () => { await client.TileAsync(18, 223528, 101433); }, "redirect response is rejected");
            Check(!error.ToString().Contains(dummyKey), "HTTP failure does not expose key or body");
        }
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => throw new HttpRequestException("url?key=" + dummyKey))))
        {
            var error = await Fails(async () => { await client.ParcelsAsync(bounds); }, "transport errors are sanitized");
            Check(!error.ToString().Contains(dummyKey), "transport exception chain does not expose key");
        }
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json("{\"error\":\"invalid key\"}"))))
            await Fails(async () => { await client.TileAsync(18, 223528, 101433); }, "map API error cannot masquerade as a PNG");

        using (var cancellation = new CancellationTokenSource())
        using (var client = new VWorldClient(dummyKey, new FakeHandler(_ => Json(Collection(Array.Empty<object>(), 0)))))
        {
            cancellation.Cancel();
            var error = await Fails(async () => { await client.ParcelsAsync(bounds, cancellation.Token); }, "cancel stops collection");
            Check(error is OperationCanceledException, "caller cancellation is preserved");
        }
        return passed;
    }

    private static object Feature(string id, int sourceId, string marker = "same") => new
    {
        type = "Feature", id,
        geometry = new
        {
            type = "MultiPolygon",
            coordinates = new[] { new[] { new[]
            {
                new[] { 126.978, 37.566 }, new[] { 126.979, 37.566 },
                new[] { 126.979, 37.567 }, new[] { 126.978, 37.566 }
            } } }
        },
        properties = new
        {
            pnu = "9900000000100310000", src_objectid = sourceId, src_signgu_code = "99000",
            marker
        }
    };

    // Data API source fields from the selected Seoul City Hall parcel; geometry uses the existing offline fixture.
    private static object SelectedParcelFeature()
    {
        var feature = JsonSerializer.SerializeToNode(Feature("parcel.1", 1))!.AsObject();
        var properties = feature["properties"]!.AsObject();
        properties["addr"] = "가상시 예시구 예시동 1-1";
        properties["jibun"] = "31대";
        return feature;
    }

    private static string Collection(IEnumerable<object> features, int total) => JsonSerializer.Serialize(new
    {
        type = "FeatureCollection", features, numberMatched = total, numberReturned = features.Count()
    });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(item => item.Split('=', 2))
        .ToDictionary(item => Uri.UnescapeDataString(item[0]), item => Uri.UnescapeDataString(item[1]));

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
