namespace Aha.Topo.Checks;

internal static class TerrainCoverageChecks
{
    internal static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var target = Rectangle("target", -5, -5, 5, 5);
        var selection = SiteSelection.Create(new[] { target }, projection);
        var output = Bounds(-50, -50, 50, 50);
        var east = Rectangle("east", 10, -4, 80, 4);
        var north = Rectangle("north", -4, 10, 4, 90);
        var far = Rectangle("far", 40, 40, 500, 500);
        var tiny = Rectangle("tiny", 10, 5, 300, 5.001);
        var underground = Rectangle("underground", -700, -4, -10, 4, underground: true);
        var invalid = new GeoFeature { Id = "invalid", Polygons = new() { new() { new()
            { projection.ToGeo(-900, -4), projection.ToGeo(-10, 4), projection.ToGeo(-900, 4), projection.ToGeo(-10, -4) } } } };
        var buildings = new[] { east, north, far, tiny, underground, invalid };
        var expected = Bounds(-50, -50, 80, 90);
        Check(TerrainCoverage.For(selection, projection, output, buildings, new()) == expected,
            "all generated whole footprints expand both axes; outside-centroid, tiny, underground and invalid geometry cannot expand ground");
        Check(TerrainCoverage.For(selection, projection, output, buildings.Reverse().ToArray(), new()) == expected,
            "coverage does not depend on first offending building or source order");
        Check(TerrainCoverage.For(selection, projection, output, buildings, new(SeparateRoads: false)) == expected,
            "coverage does not depend on road representation");
        Check(TerrainCoverage.For(selection, projection, output, buildings, new(UseNearRegion: false)) == expected,
            "Near OFF still acquires the whole ground of every generated building");
        Check(TerrainCoverage.For(selection, projection, output, Array.Empty<GeoFeature>(), new()) == output,
            "no generated building means no acquisition expansion");
        Check(TerrainCoverage.For(selection, projection, Bounds(-100, -100, 100, 100), buildings, new()) == Bounds(-100, -100, 100, 100),
            "no building extends beyond this larger output");
        var multi = new GeoFeature { Id = "multipart", Polygons = new()
            { east.Polygons[0], Rectangle("other-part", -120, -130, -100, -110).Polygons[0] } };
        Check(TerrainCoverage.IsGenerated(multi, projection, output, 10), "multipart eligibility uses the complete area centroid");
        Check(TerrainCoverage.For(selection, projection, output, new[] { multi }, new()) == Bounds(-120, -130, 80, 50),
            "a generated multipart building has ground for all its parts");
        var crossing = Rectangle("crossing", 35, -5, 70, 5);
        Check(!TerrainCoverage.IsGenerated(crossing, projection, output, 10) &&
            TerrainCoverage.For(selection, projection, output, new[] { crossing }, new()) == output,
            "a Near-intersecting building whose centroid is outside output is not generated or reacquired");
        var farEdge = Rectangle("far-edge", -10, 35, 10, 60);
        Check(TerrainCoverage.IsGenerated(farEdge, projection, output, 10) &&
            TerrainCoverage.For(selection, projection, output, new[] { farEdge }, new()) == Bounds(-50,-50,50,60),
            "Far edge building with an inside centroid receives its whole ground");
        Check(BuildingRules.IsEligibleFootprint(underground, projection, output, 1_000_000),
            "underground source curves remain eligible independently of model size filtering");
        return count;

        GeoBounds Bounds(double x0, double y0, double x1, double y1)
        { var a = projection.ToGeo(x0, y0); var b = projection.ToGeo(x1, y1); return new(a.Lon, a.Lat, b.Lon, b.Lat); }
        GeoFeature Rectangle(string id, double x0, double y0, double x1, double y1, bool underground = false) => new()
        {
            Id = id, BuildingClassification = BuildingClassificationStatus.Verified, BuildingPositionCode = underground ? "1" : "0",
            Polygons = new() { new() { new() { projection.ToGeo(x0,y0), projection.ToGeo(x1,y0),
                projection.ToGeo(x1,y1), projection.ToGeo(x0,y1), projection.ToGeo(x0,y0) } } }
        };
        void Check(bool value, string message)
        { count++; if (!value) throw new InvalidOperationException("Terrain coverage: " + message); }
    }
}
