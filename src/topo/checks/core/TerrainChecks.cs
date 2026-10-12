using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aha.Topo;

namespace Aha.Topo.Checks;

/// <summary>Deterministic offline terrain checks: no keys, network or Rhino dependency.</summary>
public static class TerrainChecks
{
    private const int BaseX = 34927;
    private const int BaseY = 14514;

    public static int Run()
    {
        Action[] checks =
        {
            GlobalIndexAndLevelBounds,
            NorthwestOriginAndBilinear,
            FourOuterEdgesAndCorners,
            BothSharedEdges,
            EastSeamMismatch,
            NorthSeamMismatch,
            MissingDuplicateAndMixedTiles,
            LittleEndianAndByteCount,
            NonFiniteHeights,
            OutsideCoordinates,
            RequestBudgetBeforeNetwork,
            CancellationBeforeNetwork,
            BroadAreaPlan,
            ExactResourceBoundary,
            DetailedBroadAreaBudget,
            AcquisitionFailureDoesNotSubstituteHeights,
        };
        foreach (var check in checks)
            check();
        return checks.Length;
    }

    private static List<TerrainTile> PlaneTiles()
    {
        var result = new List<TerrainTile>();
        for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                var heights = new float[TerrainTile.ValueCount];
                for (int row = 0; row < 65; row++)
                    for (int column = 0; column < 65; column++)
                        heights[row * 65 + column] = 100 + 4 * (dx * 64 + column)
                            + 6 * (dy * 64 + 64 - row);
                result.Add(new TerrainTile(12, BaseX + dx, BaseY + dy, heights));
            }
        return result;
    }

    private static void GlobalIndexAndLevelBounds()
    {
        Equal(TerrainTile.IndexAt(new GeoPoint(126.978, 37.5665), 15), (279418, 116113));
        Equal(TerrainTile.IndexAt(new GeoPoint(-180, -90), 0), (0, 0));
        Equal(TerrainTile.IndexAt(new GeoPoint(180, 90), 0), (9, 4));
        Equal(TerrainTile.IndexAt(new GeoPoint(180, 90), 15), ((10 << 15) - 1, (5 << 15) - 1));
        Throws<ArgumentOutOfRangeException>(() => TerrainTile.IndexAt(new GeoPoint(0, 0), -1));
        Throws<ArgumentOutOfRangeException>(() => TerrainTile.IndexAt(new GeoPoint(0, 0), 16));
        Throws<ArgumentOutOfRangeException>(() => TerrainTile.IndexAt(new GeoPoint(double.NaN, 0), 12));
        Throws<ArgumentOutOfRangeException>(() => new TerrainTile(15, 10 << 15, 0, new float[4225]));
    }

    private static void NorthwestOriginAndBilinear()
    {
        var tiles = PlaneTiles();
        var grid = new TerrainGrid(tiles);
        var tile = tiles[0];
        Near(grid.Sample(new GeoPoint(tile.Bounds.West, tile.Bounds.North)), 484);
        Near(grid.Sample(new GeoPoint(tile.Bounds.West, tile.Bounds.South)), 100);
        // Non-node location makes both interpolation weights fractional.
        var point = new GeoPoint(tile.Bounds.West + 2.25 * grid.CellLongitudeDegrees,
            tile.Bounds.North - 3.75 * grid.CellLatitudeDegrees);
        Near(grid.Sample(point), 100 + 4 * 2.25 + 6 * (64 - 3.75));
        Near(grid.CellLongitudeDegrees, 36.0 / 4096 / 64);
    }

    private static void FourOuterEdgesAndCorners()
    {
        var grid = new TerrainGrid(PlaneTiles());
        var b = grid.Bounds;
        Near(grid.Sample(new GeoPoint(b.West, b.South)), 100);
        Near(grid.Sample(new GeoPoint(b.East, b.South)), 612);
        Near(grid.Sample(new GeoPoint(b.West, b.North)), 868);
        Near(grid.Sample(new GeoPoint(b.East, b.North)), 1380);
        double midLon = (b.West + b.East) / 2, midLat = (b.South + b.North) / 2;
        Near(grid.Sample(new GeoPoint(b.West, midLat)), 484);
        Near(grid.Sample(new GeoPoint(b.East, midLat)), 996);
        Near(grid.Sample(new GeoPoint(midLon, b.South)), 356);
        Near(grid.Sample(new GeoPoint(midLon, b.North)), 1124);
    }

    private static void BothSharedEdges()
    {
        var tiles = PlaneTiles();
        var grid = new TerrainGrid(tiles);
        var tile = tiles[0];
        for (int index = 0; index <= 64; index++)
        {
            Near(grid.Sample(new GeoPoint(tile.Bounds.East,
                tile.Bounds.South + index * grid.CellLatitudeDegrees)), 356 + 6 * index);
            Near(grid.Sample(new GeoPoint(tile.Bounds.West + index * grid.CellLongitudeDegrees,
                tile.Bounds.North)), 484 + 4 * index);
        }
    }

    private static void EastSeamMismatch()
    {
        var tiles = PlaneTiles();
        tiles[1].Heights[20 * 65] += 1;
        Throws<InvalidDataException>(() => new TerrainGrid(tiles));
    }

    private static void NorthSeamMismatch()
    {
        var tiles = PlaneTiles();
        tiles[2].Heights[64 * 65 + 20] += 1;
        Throws<InvalidDataException>(() => new TerrainGrid(tiles));
    }

    private static void MissingDuplicateAndMixedTiles()
    {
        var tiles = PlaneTiles();
        Throws<ArgumentException>(() => new TerrainGrid(tiles.Take(3)));
        Throws<ArgumentException>(() => new TerrainGrid(new[] { tiles[0], tiles[0] }));
        Throws<ArgumentException>(() => new TerrainGrid(Array.Empty<TerrainTile>()));
        Throws<ArgumentException>(() => new TerrainGrid(new[]
        {
            tiles[0], new TerrainTile(13, BaseX * 2, BaseY * 2, new float[4225])
        }));
    }

    private static void LittleEndianAndByteCount()
    {
        var body = new byte[TerrainTile.ByteCount];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), BitConverter.SingleToInt32Bits(1.25f));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4, 4), BitConverter.SingleToInt32Bits(-12.5f));
        var values = TerrainTile.Decode(body);
        Near(values[0], 1.25);
        Near(values[^1], -12.5);
        Equal(values.Length, 4225);
        Throws<InvalidDataException>(() => TerrainTile.Decode(new byte[16900 - 1]));
        Throws<InvalidDataException>(() => TerrainTile.Decode(new byte[16900 + 1]));
        Throws<InvalidDataException>(() => TerrainTile.Decode(System.Text.Encoding.UTF8.GetBytes("<Error code=\"ERROR_NO_DATA\"/>")));
    }

    private static void NonFiniteHeights()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var values = new float[4225];
            values[42] = invalid;
            Throws<ArgumentException>(() => new TerrainTile(12, BaseX, BaseY, values));
            var bytes = new byte[16900];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42 * 4, 4), BitConverter.SingleToInt32Bits(invalid));
            Throws<InvalidDataException>(() => TerrainTile.Decode(bytes));
        }
        // Sea-level zero is not inherently missing data; the SDK supplies no validity mask.
        var zero = new TerrainTile(12, BaseX, BaseY, new float[4225]);
        Near(new TerrainGrid(new[] { zero }).Sample(new GeoPoint(zero.Bounds.West, zero.Bounds.South)), 0);
    }

    private static void OutsideCoordinates()
    {
        var grid = new TerrainGrid(PlaneTiles());
        var b = grid.Bounds;
        foreach (var point in new[]
        {
            new GeoPoint(b.West - 0.000001, b.South), new GeoPoint(b.East + 0.000001, b.North),
            new GeoPoint(b.West, b.South - 0.000001), new GeoPoint(b.East, b.North + 0.000001),
            new GeoPoint(double.NaN, b.South), new GeoPoint(b.West, double.PositiveInfinity)
        })
            Throws<ArgumentOutOfRangeException>(() => grid.Sample(point));
    }

    private static void RequestBudgetBeforeNetwork()
    {
        using var client = new TerrainClient("OFFLINE-FAKE-KEY");
        Throws<ArgumentException>(() => client.FetchAsync(new GeoBounds(124, 33, 132, 39), 15,
            CancellationToken.None).GetAwaiter().GetResult());
        Throws<ArgumentOutOfRangeException>(() => client.FetchAsync(new GeoBounds(126, 37, 127, 38), 2,
            CancellationToken.None).GetAwaiter().GetResult());
        Throws<ArgumentException>(() => client.FetchAsync(new GeoBounds(127, 37, 126, 38), 12,
            CancellationToken.None).GetAwaiter().GetResult());
    }

    private static void CancellationBeforeNetwork()
    {
        using var client = new TerrainClient("OFFLINE-FAKE-KEY");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => client.FetchAsync(new GeoBounds(126.977, 37.566, 126.978, 37.567),
            12, cancellation.Token).GetAwaiter().GetResult());
    }

    private static void ExactResourceBoundary()
    {
        var first = new TerrainTile(12, BaseX, BaseY, new float[TerrainTile.ValueCount]);
        var last = new TerrainTile(12, BaseX + 7, BaseY + 7, new float[TerrainTile.ValueCount]);
        double cell = (first.Bounds.East - first.Bounds.West) / 64;
        var bounds = new GeoBounds(first.Bounds.West + 2 * cell, first.Bounds.South + 2 * cell,
            last.Bounds.East - 2 * cell, last.Bounds.North - 2 * cell);
        Equal(TerrainClient.Plan(bounds, 12).TileCount, TerrainGrid.MaximumTiles);
        Throws<ArgumentException>(() => TerrainClient.Plan(bounds with { East = bounds.East + 4 * cell }, 12));
        var projection = new LocalProjection(new GeoPoint(127,37.5));
        var field = TerrainField.FromSamples(projection.Bounds(500), projection, 999, 999, new double[1_000_000]);
        Equal(field.Heights.Length, 1_000_000);
        Throws<ArgumentException>(() => TerrainField.FromSamples(projection.Bounds(500), projection, 1000, 999, Array.Empty<double>()));
    }

    private static void BroadAreaPlan()
    {
        var bounds = new LocalProjection(new GeoPoint(126.97834678, 37.566701)).Bounds(1500);
        var plan = TerrainClient.Plan(bounds, 12);
        if (plan.TileCount is < 1 or > 64 || plan.MeshVertices is < 10000 or > 1000000)
            throw new InvalidOperationException("A 3km level-12 area must fit the terrain request budget.");
        Equal(plan.TileCount, (plan.MaxX - plan.MinX + 1) * (plan.MaxY - plan.MinY + 1));
    }

    private static void DetailedBroadAreaBudget()
    {
        var bounds = new LocalProjection(new GeoPoint(126.97834678, 37.566701)).Bounds(1500);
        Throws<ArgumentException>(() => TerrainClient.Plan(bounds, 15));
    }

    private static void AcquisitionFailureDoesNotSubstituteHeights()
    {
        var bounds = new GeoBounds(126.977, 37.566, 126.978, 37.567);
        foreach (string failure in new[] { "auth", "status", "html", "truncated", "oversized", "nonfinite", "network" })
        {
            int tileRequests = 0;
            using var client = new TerrainClient("offline-private-key", new Handler(uri =>
            {
                if (uri.AbsolutePath.Contains("selectApiKeyJson"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                        failure == "auth" ? "{\"secret\":\"offline-private-key\"}" : "{\"apiKey\":\"offline-sdk-token\"}") };
                tileRequests++;
                if (failure == "network") throw new HttpRequestException("offline-sdk-token");
                var body = new byte[failure == "truncated" ? 4 : failure == "oversized" ? TerrainTile.ByteCount + 1 : TerrainTile.ByteCount];
                if (failure == "nonfinite") BinaryPrimitives.WriteSingleLittleEndian(body, float.NaN);
                var response = new HttpResponseMessage(failure == "status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new ByteArrayContent(body) };
                response.Content.Headers.ContentType = new(failure == "html" ? "text/html" : "application/octet-stream");
                return response;
            }));
            Exception? caught = null;
            try { client.FetchAsync(bounds, 12, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception error) { caught = error; }
            if (caught is not (InvalidDataException or InvalidOperationException) ||
                caught.Message.Contains("offline-private-key") || caught.Message.Contains("offline-sdk-token"))
                throw new Exception("DEM failure must reject the complete result without leaking credentials: " + failure);
            Equal(tileRequests, failure == "auth" ? 0 : 1);
        }
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request.RequestUri!)); }
    }

    private static void Near(double actual, double expected)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Terrain check: expected {expected}, received {actual}.");
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"Terrain check: expected {expected}, received {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Terrain check: expected {typeof(T).Name}.");
    }
}
