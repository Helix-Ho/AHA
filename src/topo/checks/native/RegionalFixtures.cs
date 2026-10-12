using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aha.Topo.Checks;

/// <summary>Portable, normalized source inputs; no saved Rhino model or network request is required.</summary>
internal static class RegionalFixtures
{
    internal sealed record TerrainSamples(int Columns, int Rows, double[] Heights);
    internal sealed record Fixture(int SchemaVersion, string Id, GeoPoint Origin, GeoBounds Bounds,
        TerrainSamples Terrain, List<GeoFeature> Targets, List<GeoFeature> Parcels, List<GeoFeature> Buildings,
        ModelOptions? Options = null, string Units = "Meters", double DocumentToleranceMetres = .001, GeoBounds? CalculationBounds = null);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static string DirectoryPath => FixtureData.DirectoryPath;

    internal static Fixture Load(string id)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DirectoryPath, "manifest.json")));
        var entry = manifest.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        string file = entry.GetProperty("file").GetString()!;
        if (Path.GetFileName(file) != file) throw new InvalidDataException("Invalid fixture file name.");
        using var compressed = File.OpenRead(Path.Combine(DirectoryPath, file));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream(); gzip.CopyTo(output);
        byte[] bytes = output.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != entry.GetProperty("sha256").GetString()) throw new InvalidDataException("Regional fixture checksum differs.");
        var fixture = JsonSerializer.Deserialize<Fixture>(bytes, Json) ?? throw new InvalidDataException("Regional fixture is empty.");
        if (fixture.SchemaVersion != 1 || fixture.Id != id || fixture.Terrain.Columns != entry.GetProperty("columns").GetInt32()
            || fixture.Terrain.Rows != entry.GetProperty("rows").GetInt32()
            || fixture.Terrain.Heights.Length != entry.GetProperty("terrainNodes").GetInt32()
            || fixture.Targets.Count != entry.GetProperty("targets").GetInt32()
            || fixture.Parcels.Count != entry.GetProperty("parcels").GetInt32()
            || fixture.Buildings.Count != entry.GetProperty("buildings").GetInt32())
            throw new InvalidDataException("Regional fixture dimensions or feature counts differ.");
        fixture.Bounds.Validate();
        if (!double.IsFinite(fixture.DocumentToleranceMetres) || fixture.DocumentToleranceMetres <= 0)
            throw new InvalidDataException("Regional fixture document tolerance is invalid.");
        return fixture;
    }
}
