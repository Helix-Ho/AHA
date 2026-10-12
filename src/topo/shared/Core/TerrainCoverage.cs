namespace Aha.Topo;

/// <summary>Ground required by the fixed output and every whole generated building, without growing the output.</summary>
public static class TerrainCoverage
{
    public static GeoBounds For(SiteSelection selection, LocalProjection projection, GeoBounds outputBounds,
        IReadOnlyList<GeoFeature> buildings, ModelOptions options)
    {
        BuildingRules.Validate(options); outputBounds.Validate();
        if (options.UseNearRegion) EditRegion.Create(selection, projection, outputBounds, options.NearRatio);
        double west = outputBounds.West, south = outputBounds.South, east = outputBounds.East, north = outputBounds.North;
        foreach (var building in buildings)
        {
            try { if (!IsGenerated(building, projection, outputBounds, options.MinArea)) continue; }
            catch (BuildingSourceException)
            {
                // PlanarReferences owns invalid-footprint warnings and preserves valid source curves.
                continue;
            }
            foreach (var point in building.Polygons.SelectMany(polygon => polygon).SelectMany(ring => ring))
            {
                west = Math.Min(west, point.Lon); south = Math.Min(south, point.Lat);
                east = Math.Max(east, point.Lon); north = Math.Max(north, point.Lat);
            }
        }
        return new(west, south, east, north);
    }

    /// <summary>The same fixed-output eligibility rule is used before acquisition and before placement.</summary>
    public static bool IsGenerated(GeoFeature feature, LocalProjection projection, GeoBounds bounds, double minimumArea)
    {
        if (BuildingRules.IsUnderground(feature) || !BuildingRules.IsEligibleFootprint(feature, projection, bounds, minimumArea)) return false;
        var centre = Centre(feature, projection);
        return centre.Lon >= bounds.West - 1e-10 && centre.Lon <= bounds.East + 1e-10 &&
            centre.Lat >= bounds.South - 1e-10 && centre.Lat <= bounds.North + 1e-10;
    }

    private static GeoPoint Centre(GeoFeature feature, LocalProjection projection)
    {
        double weightedX = 0, weightedY = 0, totalArea = 0;
        foreach (var polygon in feature.Polygons)
        for (var ring = 0; ring < polygon.Count; ring++)
        {
            var points = polygon[ring].Select(projection.ToLocal).ToArray();
            var origin = points[0];
            double twiceArea = 0, cx = 0, cy = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                double ax = a.X - origin.X, ay = a.Y - origin.Y, bx = b.X - origin.X, by = b.Y - origin.Y;
                double cross = ax * by - bx * ay;
                twiceArea += cross; cx += (ax + bx) * cross; cy += (ay + by) * cross;
            }
            if (Math.Abs(twiceArea) < 1e-12) continue;
            double weight = Math.Abs(twiceArea) / 2 * (ring == 0 ? 1 : -1);
            weightedX += (origin.X + cx / (3 * twiceArea)) * weight;
            weightedY += (origin.Y + cy / (3 * twiceArea)) * weight;
            totalArea += weight;
        }
        if (totalArea <= 0) throw new ArgumentException("건물의 면적 중심을 계산할 수 없습니다.");
        return projection.ToGeo(weightedX / totalArea, weightedY / totalArea);
    }
}
