using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aha.Topo;

public sealed record TerrainRequestPlan(int MinX, int MinY, int MaxX, int MaxY, int TileCount, long MeshVertices);

/// <summary>Experimental adapter for the public SDK's DEM transport, using the user's own key.</summary>
public sealed class TerrainClient : IDisposable
{
    public const int MaximumTiles = TerrainGrid.MaximumTiles;
    private const string AuthenticationEndpoint = "https://map.vworld.kr/dtkmap/selectApiKeyJson.do";
    private const string TileEndpoint = "https://xdworld.vworld.kr/XDServer/3DData";
    private readonly HttpClient _http;
    private readonly string _userKey;
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);
    private string? _sdkToken;

    public TerrainClient(string userKey) : this(userKey,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal TerrainClient(string userKey, HttpMessageHandler handler)
    {
        if (string.IsNullOrWhiteSpace(userKey))
            throw new ArgumentException("VWorld 인증키가 필요합니다.", nameof(userKey));
        _userKey = userKey.Trim();
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(40)
        };
    }

    public static TerrainRequestPlan Plan(GeoBounds bounds, int level)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        TerrainTile.ValidateLevel(level);
        if (level < 3)
            throw new ArgumentOutOfRangeException(nameof(level), "SDK 설정의 지형 레벨 범위는 3~15입니다.");
        if (!double.IsFinite(bounds.West) || !double.IsFinite(bounds.South)
            || !double.IsFinite(bounds.East) || !double.IsFinite(bounds.North)
            || bounds.West < -180 || bounds.East > 180 || bounds.South < -90 || bounds.North > 90
            || bounds.West >= bounds.East || bounds.South >= bounds.North)
            throw new ArgumentException("유효한 지형 조회 범위가 필요합니다.", nameof(bounds));
        double cell = 36.0 / (1 << level) / 64;
        var first = TerrainTile.IndexAt(new GeoPoint(Math.Max(-180, bounds.West - cell),
            Math.Max(-90, bounds.South - cell)), level);
        var last = TerrainTile.IndexAt(new GeoPoint(Math.Min(180, bounds.East + cell),
            Math.Min(90, bounds.North + cell)), level);
        long count = (long)(last.X - first.X + 1) * (last.Y - first.Y + 1);
        if (count > MaximumTiles)
            throw new ArgumentException($"지형 요청 한도 초과 · {count:N0} / {MaximumTiles}타일");
        long columns = Math.Max(1, (long)Math.Ceiling((bounds.East - bounds.West) / cell));
        long rows = Math.Max(1, (long)Math.Ceiling((bounds.North - bounds.South) / cell));
        long vertices = (columns + 1) * (rows + 1);
        if (vertices > 1_000_000) throw new ArgumentException("지형 높이 표본 한도 초과 · 1,000,000개");
        return new TerrainRequestPlan(first.X, first.Y, last.X, last.Y, (int)count, vertices);
    }

    /// <summary>Loads one-cell padding with a fixed level; never substitutes another level.</summary>
    public async Task<TerrainGrid> FetchAsync(GeoBounds bounds, int level, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var plan = Plan(bounds, level);
        var first = (X: plan.MinX, Y: plan.MinY);
        var last = (X: plan.MaxX, Y: plan.MaxY);
        var count = plan.TileCount;
        ct.ThrowIfCancellationRequested();
        progress?.Report("VWorld 지형 인증을 확인합니다.");
        string token = await GetSdkTokenAsync(ct).ConfigureAwait(false);
        var tiles = new List<TerrainTile>((int)count);
        for (int y = first.Y; y <= last.Y; y++)
        {
            for (int x = first.X; x <= last.X; x++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"지형 타일 {tiles.Count + 1}/{count} (레벨 {level})를 가져옵니다.");
                string url = $"{TileEndpoint}?Version=2.0.0.0&Request=GetLayer&Layer=dem&Level={level}&IDX={x}&IDY={y}&Key={Uri.EscapeDataString(token)}";
                byte[] body = await GetBytesAsync(url, TerrainTile.ByteCount, ct, requireBinary: true).ConfigureAwait(false);
                try
                {
                    tiles.Add(new TerrainTile(level, x, y, TerrainTile.Decode(body)));
                }
                catch (InvalidDataException)
                {
                    throw new InvalidDataException($"레벨 {level}의 지형 타일을 읽을 수 없습니다. 현재 레벨의 데이터 부재 또는 잘못된 응답입니다. 다른 레벨로 자동 변경하지 않았습니다.");
                }
            }
        }
        progress?.Report("지형 타일의 경계 높이를 확인합니다.");
        return new TerrainGrid(tiles);
    }

    private async Task<string> GetSdkTokenAsync(CancellationToken ct)
    {
        await _authenticationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sdkToken is not null)
                return _sdkToken;
            string url = $"{AuthenticationEndpoint}?apiKey={Uri.EscapeDataString(_userKey)}&output=json";
            byte[] body = await GetBytesAsync(url, 65536, ct).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("apiKey", out var key)
                    && key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
                {
                    _sdkToken = key.GetString()!;
                    return _sdkToken;
                }
            }
            catch (JsonException)
            {
                // The authentication body may contain credentials; never echo it.
            }
            throw new InvalidDataException("VWorld SDK 지형 인증에 실패했습니다. 인증키와 이용 설정을 확인해 주세요.");
        }
        finally
        {
            _authenticationLock.Release();
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, int limit, CancellationToken ct, bool requireBinary = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var requestToken = timeout.Token;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"VWorld 지형 서버가 HTTP {(int)response.StatusCode}를 반환했습니다.");
            if (requireBinary && !string.Equals(response.Content.Headers.ContentType?.MediaType,
                "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("현재 레벨의 지형 응답이 고도 격자가 아닙니다. 다른 레벨로 자동 변경하지 않았습니다.");
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidDataException("VWorld 지형 응답이 예상 크기를 초과했습니다.");
            using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), requestToken).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                output.Write(buffer, 0, read);
                if (output.Length > limit)
                    throw new InvalidDataException("VWorld 지형 응답이 예상 크기를 초과했습니다.");
            }
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("VWorld 지형 서버에 연결하지 못했습니다.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("VWorld 지형 요청 시간이 초과되었습니다.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("VWorld 지형 응답을 끝까지 읽지 못했습니다.");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authenticationLock.Dispose();
    }
}
