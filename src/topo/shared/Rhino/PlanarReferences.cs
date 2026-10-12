using System.Globalization;
using Aha.Info;
using Rhino.Geometry;

namespace Aha.Topo;

internal sealed record PlanarReference(IReadOnlyList<GeoPoint> Path, string Layer, string Name,
    Dictionary<string, string> Tags, GeoFeature Feature, bool IsTargetOuter = false);

internal sealed class PlanarReferenceSet
{
    internal List<PlanarReference> Paths { get; } = new();
    internal List<GeoFeature> EligibleBuildings { get; } = new();
    internal List<string> InvalidBuildingIds { get; } = new();
    internal List<string> Warnings { get; } = new();
    internal int ClippedParcelRings { get; set; }
}

/// <summary>Source planar context, independent of terrain cleanup and successful 3D materialization.</summary>
internal static class PlanarReferences
{
    internal static PlanarReferenceSet Prepare(SiteSelection selection, IReadOnlyList<GeoFeature> parcels,
        IReadOnlyList<GeoFeature> buildings, LocalProjection projection, GeoBounds bounds,
        bool includeRoads, double minBuildingArea, Func<GeoFeature, bool>? detailed = null, bool targetSite = false)
    {
        ValidateMinimumArea(minBuildingArea); bounds.Validate();
        ValidateTargetBounds(selection, projection, bounds);
        var result = new PlanarReferenceSet();
        foreach (var component in selection.Components)
            AddParcel(component.Boundary, component);
        foreach (var parcel in parcels)
            AddParcel(parcel, null);
        if (includeRoads)
        foreach (var parcel in parcels.Where(p => p.LandCategoryCode == ParcelLandCategory.Road))
        {
            string info = TopoObjectInfo.Parcel(new[] { parcel }, detailed: detailed?.Invoke(parcel) == true);
            foreach (var polygon in parcel.Polygons)
            foreach (var ring in polygon)
            foreach (var path in ClipRing(ring, bounds))
                Add(path, "RoadBoundary", "RoadBoundary_" + parcel.Id, parcel, new()
                {
                    ["AHA.Role"] = "RoadBoundary", ["AHA.Source"] = "VWorld dt_d002 road-category parcels",
                    ["AHA.FeatureId"] = parcel.Id, [InfoData.Key] = info,
                    ["AHA.RoadBoundaryMethod"] = "Original cadastral road-parcel boundary; clipped only to model extent"
                });
        }
        foreach (var building in buildings)
        {
            try
            {
                if (!BuildingRules.IsEligibleFootprint(building, projection, bounds, minBuildingArea)) continue;
                result.EligibleBuildings.Add(building);
                string info = TopoObjectInfo.Building(building, detailed: detailed?.Invoke(building) == true);
                foreach (var polygon in building.Polygons)
                foreach (var ring in polygon)
                foreach (var path in ClipRing(ring, bounds))
                    Add(path, "BuildingFootprints", "Footprint_" + building.Id, building, new()
                    {
                        ["AHA.Role"] = "BuildingFootprint", ["AHA.FeatureId"] = building.Id,
                        ["AHA.SourceLayer"] = building.SourceLayer, [InfoData.Key] = info,
                        ["AHA.BuildingPositionCode"] = building.BuildingPositionCode,
                        ["AHA.BuildingClassification"] = building.BuildingClassification.ToString(),
                        ["AHA.ExcludedUnderground"] = BuildingRules.IsUnderground(building).ToString()
                    });
            }
            catch (BuildingSourceException error)
            {
                result.InvalidBuildingIds.Add(building.Id);
                result.Warnings.Add("건물 " + building.Id + "의 XY 제외: " + error.Message);
            }
        }
        if (result.ClippedParcelRings > 0)
            result.Warnings.Add("주변 필지 경계 " + result.ClippedParcelRings + "개 링을 모델링 범위에 맞춰 잘랐습니다.");
        return result;

        void AddParcel(GeoFeature feature, SiteComponent? component)
        {
            bool isTarget = component is not null;
            foreach (var polygon in feature.Polygons)
            for (int ringIndex = 0; ringIndex < polygon.Count; ringIndex++)
            {
                var ring = polygon[ringIndex];
                if (ring.Count < 3) continue;
                bool clipped = !isTarget && ring.Any(p => !Inside(p, bounds));
                if (clipped) result.ClippedParcelRings++;
                bool outer = isTarget && ringIndex == 0;
                string info = TopoObjectInfo.Parcel(component?.Parcels ?? new[] { feature }, targetSite && outer,
                    detailed: isTarget || detailed?.Invoke(feature) == true);
                IEnumerable<IReadOnlyList<GeoPoint>> paths = isTarget ? new[] { ring } : ClipRing(ring, bounds);
                foreach (var path in paths)
                    Add(path, isTarget ? "TargetBoundary" : "ParcelBoundary",
                        isTarget ? "Target_" + feature.Id : "Parcel_" + feature.Pnu, feature, new()
                        {
                            ["AHA.Source"] = "VWorld continuous cadastral map", ["AHA.FeatureId"] = feature.Id,
                            [InfoData.Key] = info, ["AHA.Role"] = isTarget ? "TargetBoundary" : "ParcelBoundary",
                            ["AHA.SiteRadiusMetres"] = component?.RadiusMetres.ToString("R", CultureInfo.InvariantCulture) ?? "",
                            ["AHA.ClippedToAOI"] = clipped.ToString()
                        }, outer);
            }
        }
        void Add(IReadOnlyList<GeoPoint> path, string layer, string name, GeoFeature feature,
            Dictionary<string, string> tags, bool outer = false)
        {
            if (path.Count < 2) return;
            result.Paths.Add(new(path, layer, name, tags, feature, outer));
        }
    }

    internal static ModelDocumentObject[] CreateObjects(PlanarReferenceSet references, LocalProjection projection,
        double scale, IReadOnlyDictionary<string, string>? buildingInfoByFeatureId = null)
    {
        var objects = new List<ModelDocumentObject>();
        try
        {
            foreach (var reference in references.Paths)
            {
                var tags = new Dictionary<string, string>(reference.Tags)
                { ["AHA.CurveSpace"] = "XY", ["AHA.ElevationMethod"] = "Source XY at Z=0" };
                if (reference.Layer == "BuildingFootprints" && buildingInfoByFeatureId is not null
                    && buildingInfoByFeatureId.TryGetValue(reference.Feature.Id, out var info)) tags[InfoData.Key] = info;
                var curve = ModelCoordinates.XYCurve(reference.Path, projection, scale);
                if (!curve.IsValid || reference.Layer == "TargetBoundary" && !curve.IsClosed)
                { curve.Dispose(); throw new InvalidOperationException("유효하고 닫힌 대지경계 또는 XY 참조선을 만들 수 없습니다."); }
                objects.Add(new(curve, "XY::" + reference.Layer, "XY_" + reference.Name, tags));
            }
            return objects.ToArray();
        }
        catch { foreach (var obj in objects) obj.Geometry.Dispose(); throw; }
    }

    private static void ValidateTargetBounds(SiteSelection selection, LocalProjection projection, GeoBounds bounds)
    {
        // Source parcels must fit exactly. Their union may move by the shared planar grid precision;
        // keep that closed boundary instead of cutting off a sub-micrometre edge into an open site.
        if (selection.Parcels.SelectMany(p => p.Polygons).SelectMany(p => p).SelectMany(r => r)
            .Any(p => p.Lon < bounds.West || p.Lon > bounds.East || p.Lat < bounds.South || p.Lat > bounds.North))
            throw new ArgumentException("모델 범위가 선택한 모든 필지를 포함해야 합니다. 범위를 넓히세요.");
        var sw = projection.ToLocal(new(bounds.West, bounds.South));
        var ne = projection.ToLocal(new(bounds.East, bounds.North));
        double precision = Math.Pow(10, -EditRegion.ClipPrecision);
        if (selection.Components.SelectMany(c => c.Boundary.Polygons).SelectMany(p => p).SelectMany(r => r)
            .Select(projection.ToLocal).Any(p => p.X < sw.X - precision || p.X > ne.X + precision || p.Y < sw.Y - precision || p.Y > ne.Y + precision))
            throw new ArgumentException("합친 대지경계가 평면 정밀도 범위를 넘어 모델 범위를 벗어났습니다.");
    }

    internal static void ValidateMinimumArea(double area)
    {
        if (!double.IsFinite(area) || area < 0) throw new ArgumentException("최소 건물 면적은 0 이상의 유한한 값이어야 합니다.");
    }

    private static bool Inside(GeoPoint p, GeoBounds b) => p.Lon >= b.West - 1e-10 && p.Lon <= b.East + 1e-10 && p.Lat >= b.South - 1e-10 && p.Lat <= b.North + 1e-10;

    // Clip each source edge; do not close paths along the extent or simplify narrow source features.
    private static List<List<GeoPoint>> ClipRing(IReadOnlyList<GeoPoint> ring, GeoBounds bounds)
    {
        var paths = new List<List<GeoPoint>>();
        List<GeoPoint>? current = null;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            if (!ClipSegment(a, b, bounds, out var start, out var end)) { current = null; continue; }
            if (Same(start, end)) continue;
            if (current is null || !Same(current[^1], start)) { current = new() { start }; paths.Add(current); }
            current.Add(end);
        }
        if (paths.Count > 1 && Same(paths[^1][^1], paths[0][0]))
        { paths[^1].AddRange(paths[0].Skip(1)); paths.RemoveAt(0); }
        return paths;
    }
    private static bool Same(GeoPoint a, GeoPoint b) => Math.Abs(a.Lon - b.Lon) < 1e-12 && Math.Abs(a.Lat - b.Lat) < 1e-12;
    private static bool ClipSegment(GeoPoint a, GeoPoint b, GeoBounds box, out GeoPoint start, out GeoPoint end)
    {
        double dx = b.Lon - a.Lon, dy = b.Lat - a.Lat, lower = 0, upper = 1;
        var p = new[] { -dx, dx, -dy, dy };
        var q = new[] { a.Lon - box.West, box.East - a.Lon, a.Lat - box.South, box.North - a.Lat };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-20) { if (q[i] < 0) { start = end = a; return false; } continue; }
            double t = q[i] / p[i];
            if (p[i] < 0) lower = Math.Max(lower, t); else upper = Math.Min(upper, t);
            if (lower > upper) { start = end = a; return false; }
        }
        start = new(a.Lon + lower * dx, a.Lat + lower * dy);
        end = new(a.Lon + upper * dx, a.Lat + upper * dy);
        return true;
    }
}
