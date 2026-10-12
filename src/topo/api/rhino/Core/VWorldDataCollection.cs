using System.Globalization;
using System.Text.Json;

namespace Aha.Topo;

internal sealed record DataApiCollection<T>(List<T> Features, int RequestCount);

public sealed partial class VWorldClient
{
    internal const int DataPageSize = 1000;

    /// <summary>One transport and completeness policy for Data API feature layers.</summary>
    internal async Task<DataApiCollection<T>> CollectDataAsync<T>(GeoBounds bounds, string layer,
        Func<JsonElement, T> parseFeature, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var tiles = QueryDataTiles(bounds);
        var all = new Dictionary<string, (T Feature, string Hash)>(StringComparer.Ordinal);
        int requests = 0;
        long collectedRecords = 0;
        for (var tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tiles[tileIndex];
            var tileFeatures = new Dictionary<string, (T Feature, string Hash)>(StringComparer.Ordinal);
            long? expectedTotal = null, expectedPageSize = null, expectedPages = null;
            for (var page = 1; ; page++)
            {
                ct.ThrowIfCancellationRequested();
                requests = checked(requests + 1);
                progress?.Report($"{layer} 조회 {tileIndex + 1}/{tiles.Count} · {page}쪽");
                using var document = await GetJsonAsync("/req/data", new()
                {
                    ["service"] = "data", ["request"] = "GetFeature", ["version"] = "2.0", ["data"] = layer,
                    ["format"] = "json", ["crs"] = "EPSG:4326", ["geometry"] = "true", ["attribute"] = "true",
                    ["geomFilter"] = $"BOX({Number(tile.West)},{Number(tile.South)},{Number(tile.East)},{Number(tile.North)})",
                    ["size"] = DataPageSize.ToString(CultureInfo.InvariantCulture),
                    ["page"] = page.ToString(CultureInfo.InvariantCulture)
                }, ct).ConfigureAwait(false);
                var response = GetResponse(document.RootElement);
                var status = String(response, "status");
                if (status is not ("OK" or "NOT_FOUND"))
                    throw new InvalidOperationException("VWorld 데이터 API가 조회를 완료하지 못했습니다. 키와 조회 범위를 확인하세요.");
                if (!response.TryGetProperty("record", out var record) ||
                    !TryCount(record, "total", out var total) || !TryCount(record, "current", out var current) ||
                    !response.TryGetProperty("page", out var paging) ||
                    !TryCount(paging, "current", out var returnedPage) || !TryCount(paging, "total", out var pages) ||
                    !TryCount(paging, "size", out var pageSize) || pageSize <= 0 || pageSize > DataPageSize)
                    throw DataCollectionError();
                var calculatedPages = Math.Max(1, total / pageSize + (total % pageSize == 0 ? 0 : 1));
                if (returnedPage != page || pages != calculatedPages || returnedPage > pages ||
                    current > pageSize || current > total)
                    throw new InvalidDataException("데이터 API의 페이지 번호·크기·전체 건수가 일치하지 않습니다.");
                if (expectedTotal.HasValue && (expectedTotal.Value != total || expectedPageSize != pageSize || expectedPages != pages))
                    throw new InvalidDataException("데이터 조회 중 전체 건수 또는 페이지 구성이 변경되었습니다. 다시 조회하세요.");
                expectedTotal ??= total; expectedPageSize ??= pageSize; expectedPages ??= pages;

                if (status == "NOT_FOUND")
                {
                    if (page != 1 || total != 0 || current != 0 || response.TryGetProperty("result", out _))
                        throw DataCollectionError();
                    break;
                }
                if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
                    !result.TryGetProperty("featureCollection", out var collection) ||
                    String(collection, "type") != "FeatureCollection" ||
                    !collection.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array ||
                    features.GetArrayLength() != current || current != Math.Min(pageSize, total - tileFeatures.Count))
                    throw DataCollectionError();
                foreach (var raw in features.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var id = String(raw, "id");
                    if (id.Length == 0 || !raw.TryGetProperty("geometry", out var geometry) ||
                        !raw.TryGetProperty("properties", out var properties)) throw DataCollectionError();
                    if (tileFeatures.ContainsKey(id))
                        throw new InvalidDataException("데이터 조회 페이지에 중복 도형이 있어 수집 완전성을 확인할 수 없습니다.");
                    tileFeatures.Add(id, (parseFeature(raw), FeatureContentHash(geometry, properties)));
                }
                if (page == pages)
                {
                    if (tileFeatures.Count != total) throw DataCollectionError();
                    break;
                }
            }
            collectedRecords += tileFeatures.Count;
            foreach (var pair in tileFeatures)
            {
                if (all.TryGetValue(pair.Key, out var earlier))
                {
                    if (earlier.Hash != pair.Value.Hash)
                        throw new InvalidDataException("겹치는 조회 구역에서 같은 도형의 내용이 달라졌습니다. 다시 조회하세요.");
                }
                else all.Add(pair.Key, pair.Value);
            }
        }
        // Data API totals are per query tile; repeated records here are overlaps between complete tiles.
        lock (collectionReports) collectionReports.Add(new CollectionReport(layer, collectedRecords,
            collectedRecords, all.Count, collectedRecords - all.Count, requests));
        return new(all.Values.Select(v => v.Feature).ToList(), requests);
    }

    /// <summary>Deterministic approximately 1 km squares, below the Data API area limit.</summary>
    internal static List<GeoBounds> QueryDataTiles(GeoBounds bounds)
    {
        bounds.Validate();
        var projection = new LocalProjection(new GeoPoint((bounds.West + bounds.East) / 2,
                                                         (bounds.South + bounds.North) / 2));
        var sw = projection.ToLocal(new GeoPoint(bounds.West, bounds.South));
        var ne = projection.ToLocal(new GeoPoint(bounds.East, bounds.North));
        var columns = Math.Max(1, (int)Math.Ceiling((ne.X - sw.X) / 1000 - 1e-9));
        var rows = Math.Max(1, (int)Math.Ceiling((ne.Y - sw.Y) / 1000 - 1e-9));
        var tiles = new List<GeoBounds>();
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
            tiles.Add(new GeoBounds(
                bounds.West + (bounds.East - bounds.West) * x / columns,
                bounds.South + (bounds.North - bounds.South) * y / rows,
                bounds.West + (bounds.East - bounds.West) * (x + 1) / columns,
                bounds.South + (bounds.North - bounds.South) * (y + 1) / rows));
        return tiles;
    }

    internal static GeoFeature ParseBuilding(JsonElement item)
    {
        var id = String(item, "id");
        if (String(item, "type") != "Feature" || id.Length == 0 ||
            !item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            throw DataCollectionError();
        var polygons = ParsePolygons(geometry);
        // The persistent building-management number is not the current cadastral PNU.
        var suppliedPnu = String(properties, "pnu");
        return new GeoFeature
        {
            Id = id, SourceLayer = BuildingSourceLayer, BuildingId = String(properties, "bd_mgt_sn"),
            Pnu = IsPnu(suppliedPnu) ? suppliedPnu : "", Address = String(properties, "addr"), Polygons = polygons,
            Height = null,
            Floors = TryNumber(properties, "gro_flo_co", out var floors) ? floors : null,
            BuildingDetails = ParseBuildingDetails(properties)
        };
    }

    private static BuildingDetails? ParseBuildingDetails(JsonElement properties)
    {
        var fields = new[] { "buld_nm", "buld_nm_dc", "bul_eng_nm", "sido", "sigungu", "gu", "rd_nm", "buld_no" }
            .Select(name => String(properties, name).Trim()).ToArray();
        return fields.All(string.IsNullOrEmpty) ? null : new(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);
    }

    private static InvalidDataException DataCollectionError() =>
        new("VWorld 데이터 응답의 도형·전체 건수·페이지 정보를 확인할 수 없습니다.");
}
