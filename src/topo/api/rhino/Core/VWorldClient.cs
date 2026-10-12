using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aha.Topo;

public sealed record CollectionReport(string Layer, long ApiRecords, long CollectedRecords,
    int UniqueFeatures, long RepeatedRecords, int Requests);

/// <summary>Direct, cookie-free VWorld requests. No credential or response logging.</summary>
public sealed partial class VWorldClient : IDisposable
{
    public const string BuildingSourceLayer = "LT_C_SPBD";
    internal const int FeatureLimit = 1000;
    private const int MaxJsonBytes = 32 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly string key;
    private int totalRequests;
    public int TotalRequests => Volatile.Read(ref totalRequests);
    private readonly List<CollectionReport> collectionReports = new();
    public IReadOnlyList<CollectionReport> CollectionReports
    {
        get { lock (collectionReports) return collectionReports.ToArray(); }
    }

    public VWorldClient(string key) : this(key, new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { }

    // Test seam: lets offline tests exercise real request building and collection.
    internal VWorldClient(string key, HttpMessageHandler handler)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("VWorld API 키를 입력하세요.", nameof(key));
        this.key = key.Trim();
        http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(40) };
    }

    public void Dispose() => http.Dispose();

    public async Task<List<AddressCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("검색할 주소를 입력하세요.", nameof(query));
        var results = new List<AddressCandidate>();
        var pnus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in new[] { "ROAD", "PARCEL" })
        {
            using var document = await GetJsonAsync("/req/search", new()
            {
                ["service"] = "search", ["request"] = "search", ["version"] = "2.0",
                ["type"] = "ADDRESS", ["category"] = category, ["query"] = query.Trim(),
                ["format"] = "json", ["crs"] = "EPSG:4326", ["size"] = "100", ["page"] = "1"
            }, ct).ConfigureAwait(false);
            var response = GetResponse(document.RootElement);
            if (String(response, "status") == "NOT_FOUND") continue;
            RequireSuccess(response);
            if (!response.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw FormatError();
            // Search is a bounded candidate list, unlike complete geometry collection.
            foreach (var item in items.EnumerateArray())
            {
                var pnu = String(item, "id");
                if (!IsPnu(pnu) || pnus.Contains(pnu)) continue;
                if (!item.TryGetProperty("point", out var point) ||
                    !TryNumber(point, "x", out var lon) || !TryNumber(point, "y", out var lat))
                    throw FormatError();
                var location = new GeoPoint(lon, lat);
                Geo.Validate(location);
                if (!item.TryGetProperty("address", out var address)) throw FormatError();
                var label = String(address, category == "ROAD" ? "road" : "parcel");
                if (string.IsNullOrWhiteSpace(label)) label = String(address, "road");
                if (string.IsNullOrWhiteSpace(label)) label = pnu;
                var building = String(address, "bldnm");
                if (!string.IsNullOrWhiteSpace(building)) label += " · " + building;
                results.Add(new AddressCandidate(label, pnu, location));
                pnus.Add(pnu);
            }
        }
        return results;
    }

    public Task<GeoFeature> ParcelAtAsync(GeoPoint point, CancellationToken ct = default)
    {
        Geo.Validate(point);
        return SingleParcelAsync(new() { ["geomFilter"] = $"POINT({Number(point.Lon)} {Number(point.Lat)})" }, ct);
    }

    public Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct = default)
    {
        if (!IsPnu(pnu)) throw new ArgumentException("PNU는 19자리 숫자여야 합니다.", nameof(pnu));
        return SingleParcelAsync(new() { ["attrFilter"] = "pnu:=:" + pnu }, ct);
    }

    public Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct = default) =>
        CollectAsync("/ned/wfs/getCtnlgsSpceWFS", "dt_d002", bounds, ct);

    public async Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct = default)
    {
        var source = (await CollectDataAsync(bounds, BuildingSourceLayer, ParseBuilding, ct).ConfigureAwait(false)).Features;
        return await ClassifyBuildingsAsync(source, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> TileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        if (z < 0 || z > 19 || x < 0 || y < 0 || x >= (1L << z) || y >= (1L << z))
            throw new ArgumentOutOfRangeException(nameof(z), "지도 타일 좌표가 올바르지 않습니다.");
        Interlocked.Increment(ref totalRequests);
        var uri = new Uri($"https://api.vworld.kr/req/wmts/1.0.0/{Uri.EscapeDataString(key)}/Base/{z}/{y}/{x}.png");
        var bytes = await GetBytesAsync(http, uri, 2 * 1024 * 1024, ct).ConfigureAwait(false);
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("VWorld 지도가 이미지 대신 오류 응답을 반환했습니다. API 키를 확인하세요.");
        return bytes;
    }

    private async Task<GeoFeature> SingleParcelAsync(Dictionary<string, string> filter, CancellationToken ct)
    {
        filter["service"] = "data";
        filter["request"] = "GetFeature";
        filter["version"] = "2.0";
        filter["data"] = "LP_PA_CBND_BUBUN";
        filter["format"] = "json";
        filter["crs"] = "EPSG:4326";
        filter["size"] = "1000";
        filter["page"] = "1";
        filter["geometry"] = "true";
        filter["attribute"] = "true";
        using var document = await GetJsonAsync("/req/data", filter, ct).ConfigureAwait(false);
        var response = GetResponse(document.RootElement);
        if (String(response, "status") == "NOT_FOUND")
            throw new InvalidOperationException("선택한 위치의 필지 정보를 찾지 못했습니다.");
        RequireSuccess(response);
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("featureCollection", out var collection)) throw FormatError();
        var features = ParseParcelCollection(collection, "LP_PA_CBND_BUBUN");
        if (!response.TryGetProperty("record", out var record) ||
            !TryCount(record, "total", out var total) || total != features.Count)
            throw new InvalidDataException("필지 응답이 완전하지 않아 선택을 중단했습니다.");
        if (features.Count == 0)
            throw new InvalidOperationException("선택한 위치의 필지 정보를 찾지 못했습니다.");
        if (features.Count > 1)
            throw new InvalidOperationException("필지 경계에서 여러 필지가 조회되었습니다. 필지 안쪽을 다시 선택하세요.");
        var selected = features[0].Feature;
        if (!IsPnu(selected.Pnu) ||
            (filter.TryGetValue("attrFilter", out var requestedPnu) && requestedPnu != "pnu:=:" + selected.Pnu))
            throw new InvalidDataException("API가 요청한 필지와 다른 식별자를 반환했습니다.");
        return selected;
    }

    private async Task<List<GeoFeature>> CollectAsync(string path, string layer, GeoBounds bounds, CancellationToken ct)
    {
        bounds.Validate();
        var unique = new Dictionary<string, GeoFeature>(StringComparer.Ordinal);
        var multiplicities = new Dictionary<string, int>(StringComparer.Ordinal);
        var requestCount = 0;
        long? expectedTotal = null;
        await Visit(bounds, 0).ConfigureAwait(false);
        long records = multiplicities.Values.Sum(value => (long)value);
        if (expectedTotal != records)
            throw new InvalidDataException($"분할 조회 결과의 전체 개수가 일치하지 않습니다 (API {expectedTotal}, 수집 {records}). 누락 가능성이 있어 모델 생성을 중단했습니다.");
        lock (collectionReports) collectionReports.Add(new CollectionReport(layer, expectedTotal!.Value,
            records, unique.Count, records - unique.Count, requestCount));
        return unique.Values.ToList();

        async Task Visit(GeoBounds region, int depth)
        {
            ct.ThrowIfCancellationRequested();
            requestCount = checked(requestCount + 1);
            using var document = await GetJsonAsync(path, new()
            {
                ["typename"] = layer,
                // NED WFS 4326 input axis order differs from its GeoJSON output.
                ["bbox"] = $"{Number(region.South)},{Number(region.West)},{Number(region.North)},{Number(region.East)},EPSG:4326",
                ["maxFeatures"] = FeatureLimit.ToString(CultureInfo.InvariantCulture),
                ["resultType"] = "results", ["srsName"] = "EPSG:4326", ["output"] = "application/json"
            }, ct).ConfigureAwait(false);
            var collection = document.RootElement;
            var features = ParseParcelCollection(collection, layer);
            if (!TryCount(collection, "numberMatched", out var total) &&
                !TryCount(collection, "totalFeatures", out total))
                throw new InvalidDataException("API가 전체 건수를 제공하지 않아 조회 완전성을 확인할 수 없습니다.");
            if (depth == 0) expectedTotal = total;
            if (total < features.Count ||
                (TryCount(collection, "numberReturned", out var returned) && returned != features.Count))
                throw new InvalidDataException("API 응답의 건수와 도형 개수가 일치하지 않습니다.");
            if (total > features.Count)
            {
                // Further queries must distinguish coordinates, not merely stay
                // below a fixed recursion count. Coincident records beyond the
                // provider's response limit cannot be recovered by spatial splits.
                double x=(region.West+region.East)/2,y=(region.South+region.North)/2;
                if(x<=region.West||x>=region.East||y<=region.South||y>=region.North)
                    throw new InvalidDataException("원천 API의 응답 제한 안에서 자료를 분리할 수 없어 전체 자료를 취득하지 못했습니다.");
                foreach (var child in region.Split()) await Visit(child, depth + 1).ConfigureAwait(false);
                return;
            }
            // Distinguish repeated source rows from the same row returned in overlapping
            // spatial queries. A complete leaf sees every coincident duplicate together.
            foreach (var group in features.GroupBy(feature => feature.Identity + ":" + feature.ContentHash))
            {
                unique.TryAdd(group.Key, group.First().Feature);
                multiplicities[group.Key] = Math.Max(multiplicities.GetValueOrDefault(group.Key), group.Count());
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, Dictionary<string, string> parameters, CancellationToken ct)
    {
        Interlocked.Increment(ref totalRequests);
        parameters["key"] = key;
        var query = string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var bytes = await GetBytesAsync(http, new Uri("https://api.vworld.kr" + path + "?" + query), MaxJsonBytes, ct)
            .ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw FormatError(); }
    }

    internal static async Task<byte[]> GetBytesAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient's own timeout at the headers.
        // Keep the same deadline active while consuming the response body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var requestToken = deadline.Token;
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"VWorld 요청이 HTTP {(int)response.StatusCode} 오류로 실패했습니다.");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException("API 응답이 허용 크기를 초과했습니다. 조회 범위를 줄이세요.");
            using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), requestToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > maxBytes)
                    throw new InvalidDataException("API 응답이 허용 크기를 초과했습니다. 조회 범위를 줄이세요.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("VWorld 응답 시간이 초과되었습니다. 다시 시도하세요."); }
        catch (OperationCanceledException)
        { throw new OperationCanceledException("VWorld 요청을 취소했습니다.", ct); }
        catch (HttpRequestException)
        { throw new IOException("VWorld에 연결하지 못했습니다. 인터넷 연결과 API 설정을 확인하세요."); }
        catch (IOException)
        { throw new IOException("VWorld 응답을 끝까지 받지 못했습니다. 다시 시도하세요."); }
    }

    internal sealed record ParsedFeature(GeoFeature Feature, string Identity, string ContentHash);

    internal static List<ParsedFeature> ParseParcelCollection(JsonElement collection, string sourceLayer = "dt_d002")
    {
        if (String(collection, "type") != "FeatureCollection" ||
            !collection.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            throw FormatError();
        var result = new List<ParsedFeature>();
        foreach (var item in features.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
                !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
                throw FormatError();
            var id = String(item, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("API 도형의 식별자가 누락되었습니다.");
            var polygons = ParsePolygons(geometry);
            // GeoServer's generated .fid- values may change across split requests.
            // Stable source feature IDs supplement them; PNU is not a row identity.
            var identity = id;
            var sourceId = String(properties, "src_objectid");
            var sourceRegion = String(properties, "src_signgu_code");
            if (id.Contains(".fid", StringComparison.Ordinal))
            {
                if (sourceId.Length > 0 && sourceRegion.Length > 0) identity = "source:" + sourceRegion + ":" + sourceId;
                else throw new InvalidDataException("분할 조회에 사용할 안정적인 도형 식별자가 없습니다.");
            }
            var parcelLabel = String(properties, sourceLayer == "LP_PA_CBND_BUBUN" ? "jibun" : "lnm_lndcgr_smbol");
            result.Add(new ParsedFeature(new GeoFeature
            {
                Id = id, SourceLayer = sourceLayer, Pnu = String(properties, "pnu"), Address = String(properties, "addr"), Polygons = polygons,
                ParcelLabel = parcelLabel, LandCategoryCode = ParcelLandCategory.ParseLabel(parcelLabel)
            }, identity, FeatureContentHash(geometry, properties)));
        }
        return result;
    }

    private static string FeatureContentHash(JsonElement geometry, JsonElement properties)
    {
        // Canonical object key order excludes GeoServer's request-specific feature ID.
        // Exact source geometry and attributes must agree before rows are deduplicated.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray(); WriteCanonical(writer, geometry); WriteCanonical(writer, properties); writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static List<List<List<GeoPoint>>> ParsePolygons(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            throw FormatError();
        var polygons = new List<List<List<GeoPoint>>>();
        switch (String(geometry, "type"))
        {
            case "Polygon": polygons.Add(ParsePolygon(coordinates)); break;
            case "MultiPolygon":
                foreach (var polygon in coordinates.EnumerateArray()) polygons.Add(ParsePolygon(polygon));
                break;
            default: throw new InvalidDataException("API가 면이 아닌 도형을 반환했습니다.");
        }
        if (polygons.Count == 0) throw new InvalidDataException("API 도형의 경계가 비어 있습니다.");
        return polygons;
    }

    private static List<List<GeoPoint>> ParsePolygon(JsonElement polygon)
    {
        if (polygon.ValueKind != JsonValueKind.Array) throw FormatError();
        var rings = new List<List<GeoPoint>>();
        foreach (var ring in polygon.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array) throw FormatError();
            var points = new List<GeoPoint>();
            foreach (var coordinate in ring.EnumerateArray())
            {
                if (coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() < 2 ||
                    !TryNumber(coordinate[0], out var lon) || !TryNumber(coordinate[1], out var lat)) throw FormatError();
                var point = new GeoPoint(lon, lat);
                Geo.Validate(point);
                if (points.Count == 0 || points[^1] != point) points.Add(point);
            }
            if (points.Count > 0 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
            if (points.Distinct().Count() < 3) throw new InvalidDataException("API 도형의 유효한 꼭짓점이 부족합니다.");
            points.Add(points[0]);
            rings.Add(points);
        }
        if (rings.Count == 0) throw new InvalidDataException("API 도형의 경계가 비어 있습니다.");
        return rings;
    }

    private static JsonElement GetResponse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var response) &&
        response.ValueKind == JsonValueKind.Object ? response : throw FormatError();

    private static void RequireSuccess(JsonElement response)
    {
        if (String(response, "status") != "OK")
            throw new InvalidOperationException("VWorld API가 요청을 처리하지 못했습니다. 키와 조회 조건을 확인하세요.");
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
               TryNumber(property, out value);
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;
        return (element.ValueKind is JsonValueKind.Number or JsonValueKind.String) &&
               double.TryParse(element.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    private static bool TryCount(JsonElement element, string name, out long count)
    {
        count = 0;
        return long.TryParse(String(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static bool IsPnu(string pnu) => pnu is { Length: 19 } && pnu.All(c => c is >= '0' and <= '9');
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static InvalidDataException FormatError() =>
        new("VWorld API 응답 형식을 확인할 수 없습니다. 인증 오류나 서비스 변경 여부를 확인하세요.");
}
