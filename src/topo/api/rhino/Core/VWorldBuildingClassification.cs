using System.Globalization;
using System.Text.Json;

namespace Aha.Topo;

public sealed record BuildingClassificationReport(int SourceFeatures, int Requests, int AboveGround,
    int Underground, int MissingIdentity, int NotFound, int UnknownCode);

public sealed partial class VWorldClient
{
    internal const int BuildingClassificationBatchSize = 128;
    public BuildingClassificationReport? BuildingClassificationSummary { get; private set; }

    /// <summary>Join only the same feature and administrative identity. Never replace source geometry.</summary>
    internal async Task<List<GeoFeature>> ClassifyBuildingsAsync(IReadOnlyList<GeoFeature> source,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var identities = new Dictionary<string, GeoFeature>(StringComparer.Ordinal);
        foreach (var feature in source)
        {
            var id = ClassificationIdentity(feature);
            if (id != null && !identities.TryAdd(id, feature))
                throw new InvalidDataException("건물 분류 조회에 중복 도형 식별자가 있습니다.");
        }
        var batches = identities.Keys.Chunk(BuildingClassificationBatchSize).ToArray();
        var responses = new Dictionary<string, ClassificationAttributes>[batches.Length];
        // Bound concurrency and stop scheduling further batches when any request fails.
        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var ids = batches[index];
            using var document = await GetJsonAsync("/req/wfs", new()
            {
                ["service"] = "WFS", ["request"] = "GetFeature", ["version"] = "1.1.0",
                ["typename"] = "lt_c_spbd", ["featureID"] = string.Join(",", ids),
                ["propertyName"] = "bd_mgt_sn,buld_se_cd,gro_flo_co,und_flo_co",
                ["srsName"] = "EPSG:4326", ["maxFeatures"] = ids.Length.ToString(CultureInfo.InvariantCulture),
                ["output"] = "application/json"
            }, token).ConfigureAwait(false);
            responses[index] = ParseBuildingClassifications(document.RootElement, ids, identities);
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var matched = responses.SelectMany(r => r).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var result = new List<GeoFeature>(source.Count);
        foreach (var feature in source)
        {
            var id = ClassificationIdentity(feature);
            var status = id == null ? BuildingClassificationStatus.MissingIdentity : BuildingClassificationStatus.NotFound;
            var code = "";
            double? undergroundFloors = null;
            if (id != null && matched.TryGetValue(id, out var attributes))
            {
                code = attributes.Code; undergroundFloors = attributes.UndergroundFloors;
                status = code is "0" or "1" ? BuildingClassificationStatus.Verified : BuildingClassificationStatus.UnknownCode;
            }
            result.Add(new GeoFeature
            {
                Id = feature.Id, SourceLayer = feature.SourceLayer, BuildingId = feature.BuildingId,
                BuildingDetails = feature.BuildingDetails,
                Pnu = feature.Pnu, Address = feature.Address, ParcelLabel = feature.ParcelLabel, LandCategoryCode = feature.LandCategoryCode,
                Polygons = feature.Polygons, Height = feature.Height, Floors = feature.Floors,
                BuildingPositionCode = code, BuildingClassification = status, UndergroundFloors = undergroundFloors
            });
        }
        BuildingClassificationSummary = new(result.Count, batches.Length,
            result.Count(f => BuildingRules.HasKnownPosition(f) && !BuildingRules.IsUnderground(f)),
            result.Count(BuildingRules.IsUnderground),
            result.Count(f => f.BuildingClassification == BuildingClassificationStatus.MissingIdentity),
            result.Count(f => f.BuildingClassification == BuildingClassificationStatus.NotFound),
            result.Count(f => f.BuildingClassification == BuildingClassificationStatus.UnknownCode));
        return result;
    }

    private sealed record ClassificationAttributes(string Code, double? UndergroundFloors);

    private static string? ClassificationIdentity(GeoFeature feature)
    {
        if (feature.SourceLayer != BuildingSourceLayer || string.IsNullOrWhiteSpace(feature.BuildingId)) return null;
        const string prefix = "LT_C_SPBD.";
        if (!feature.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var suffix = feature.Id[prefix.Length..];
        return suffix.Length is > 0 and <= 20 && suffix.All(c => c is >= '0' and <= '9')
            ? "lt_c_spbd." + suffix : null;
    }

    private static Dictionary<string, ClassificationAttributes> ParseBuildingClassifications(JsonElement collection,
        IReadOnlyList<string> requested, IReadOnlyDictionary<string, GeoFeature> source)
    {
        if (String(collection, "type") != "FeatureCollection" ||
            !collection.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array ||
            !TryCount(collection, "numberMatched", out var total) ||
            !TryCount(collection, "numberReturned", out var returned) ||
            returned != features.GetArrayLength() || total != returned || returned > requested.Count ||
            (collection.TryGetProperty("totalFeatures", out _) &&
                (!TryCount(collection, "totalFeatures", out var declaredTotal) || declaredTotal != total)))
            throw new InvalidDataException("건물 분류 응답의 전체 건수를 확인할 수 없습니다. 다시 조회하세요.");
        var expected = requested.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, ClassificationAttributes>(StringComparer.Ordinal);
        foreach (var item in features.EnumerateArray())
        {
            var id = String(item, "id");
            if (String(item, "type") != "Feature" || !expected.Contains(id) || result.ContainsKey(id) ||
                !item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                String(properties, "bd_mgt_sn") != source[id].BuildingId)
                throw new InvalidDataException("건물 분류의 도형·관리 식별자가 원본과 일치하지 않습니다. 다시 조회하세요.");
            if (TryNumber(properties, "gro_flo_co", out var floors) && source[id].Floors.HasValue && floors != source[id].Floors)
                throw new InvalidDataException("건물 형상과 분류의 지상층수가 달라졌습니다. 다시 조회하세요.");
            result.Add(id, new(String(properties, "buld_se_cd"),
                TryNumber(properties, "und_flo_co", out var below) ? below : null));
        }
        return result;
    }
}
