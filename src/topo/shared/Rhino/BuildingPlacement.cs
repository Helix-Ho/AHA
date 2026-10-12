using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.OverlayNG;

namespace Aha.Topo;

public sealed class BuildingExtentException(string message) : ArgumentException(message);

public sealed record BuildingPlacementResult(GeoFeature Feature, PathsD Footprint,
    double MinimumElevationMetres, double MeanElevationMetres, double BaseElevationMetres)
{
    public string SourceFeatureId => Feature.Id;
}
public sealed record BuildingPlacementRejection(string FeatureId, string Reason);
public sealed record BuildingPlacementBatch(IReadOnlyList<BuildingPlacementResult> Placements,
    IReadOnlyList<BuildingPlacementRejection> Rejections);

/// <summary>One model footprint and exact continuous-terrain height statistics for every generated building.</summary>
public static class BuildingPlacement
{
    public static BuildingPlacementBatch Prepare(TerrainField field, RoadArea roads,
        double roadDrop, bool separateRoads, IReadOnlyList<GeoFeature> candidates)
    {
        var prepared = new List<(GeoFeature Feature, PathsD Paths)>();
        var rejected = new List<BuildingPlacementRejection>();
        foreach (var feature in candidates)
        {
            try
            {
                BuildingRules.ValidateFootprint(feature, field.Projection);
                var paths = Project(feature, field.Projection);
                CheckBounds(paths, field, feature.Id);
                var domain = new PathsD { new PathD { new(field.X0, field.Y0), new(field.X1, field.Y0),
                    new(field.X1, field.Y1), new(field.X0, field.Y1) } };
                // Model-scale cleanup applies to the outer shape. Courtyards are
                // source voids, not disposable small land islands.
                var outers = new PathsD(paths.Where(path => Clipper.Area(path) > 0));
                var holes = new PathsD(paths.Where(path => Clipper.Area(path) < 0)
                    .Select(path => new PathD(path.AsEnumerable().Reverse())));
                var model = ModelPlanGeometry.Build(outers, domain).Roads;
                if (holes.Count > 0) model = Clipper.Difference(model, holes, FillRule.NonZero, EditRegion.ClipPrecision);
                if (model.Count == 0)
                { rejected.Add(new(feature.Id, "건물 윤곽이 모델 최소 상세보다 작습니다.")); continue; }
                prepared.Add((Copy(feature, model, field.Projection), model));
            }
            catch (BuildingSourceException e)
            { rejected.Add(new(feature.Id, e.Message)); }
        }
        var output = new List<BuildingPlacementResult>();
        var roadGeometry = separateRoads && roadDrop != 0 && roads.Outlines.Count > 0 ? LocalGeometry(roads.Outlines) : null;
        var roadIndex = new STRtree<IPreparedGeometry>();
        if (roadGeometry is not null)
            for (int i = 0; i < roadGeometry.NumGeometries; i++)
            {
                var polygon = roadGeometry.GetGeometryN(i);
                roadIndex.Insert(polygon.EnvelopeInternal, PreparedGeometryFactory.Prepare(polygon));
            }
        for (int i = 0; i < prepared.Count; i++)
        {
            var entry = prepared[i];
            var points = entry.Paths.SelectMany(path => path).ToArray();
            var envelope = new Envelope(points.Min(p => p.x), points.Max(p => p.x), points.Min(p => p.y), points.Max(p => p.y));
            var ground = LocalGeometry(entry.Paths);
            var nearbyRoads = roadIndex.Query(envelope).Where(road => road.Intersects(ground))
                .Select(road => road.Geometry).ToArray();
            // Only disjoint road components that can meet this footprint enter
            // robust overlay; the exact polygon rings and holes are unchanged.
            var localRoads = nearbyRoads.Length == 0 ? null : roadGeometry!.Factory.BuildGeometry(nearbyRoads);
            var statistics = Statistics(field, entry.Paths, localRoads, separateRoads ? roadDrop : 0);
            output.Add(new(entry.Feature, entry.Paths, statistics.Minimum, statistics.Mean, statistics.Minimum - 1));
        }
        return new(output, rejected);
    }

    /// <summary>Shares the lowest floor only across positive-area overlap groups selected by the caller.</summary>
    public static IReadOnlyList<BuildingPlacementResult> ShareLowestFloors(
        IReadOnlyList<BuildingPlacementResult> placements, Func<BuildingPlacementResult, bool>? include = null)
    {
        return ShareLowestFloors(placements, BuildingOverlapGroups.Create(placements
            .Where(item => include is null || include(item)).Select(item => (item.SourceFeatureId, item.Footprint))));
    }

    internal static IReadOnlyList<BuildingPlacementResult> ShareLowestFloors(
        IReadOnlyList<BuildingPlacementResult> placements, IReadOnlyList<string[]> groups)
    {
        var byId = placements.ToDictionary(item => item.SourceFeatureId, StringComparer.Ordinal);
        var floors = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            double bottom = group.Min(id => byId[id].BaseElevationMetres);
            foreach (string id in group) floors.Add(id, bottom);
        }
        return placements.Select(item => floors.TryGetValue(item.SourceFeatureId, out double bottom)
            ? item with { BaseElevationMetres = bottom } : item).ToArray();
    }

    internal static (double Minimum, double Mean) Statistics(TerrainField field, PathsD footprint, PathsD roads, double drop)
        => Statistics(field, footprint, drop != 0 && roads.Count > 0 ? LocalGeometry(roads) : null, drop);

    private static (double Minimum, double Mean) Statistics(TerrainField field, PathsD footprint,
        NetTopologySuite.Geometries.Geometry? roads, double drop)
    {
        double minimum = double.PositiveInfinity, integral = 0, area = 0;
        var points = footprint.SelectMany(path => path).ToArray();
        var low = field.ModelCell(points.Min(p => p.x), points.Min(p => p.y));
        var high = field.ModelCell(points.Max(p => p.x), points.Max(p => p.y));
        // The model footprint is already on the model's precision grid. Do not
        // quantize each new cell/road intersection again: independently rounded
        // intersections change the integration domain and lose whole-footprint area.
        // Floating overlay preserves this domain while the integral remains analytic.
        var footprintGeometry = LocalGeometry(footprint);
        var roadGeometry = roads is null ? null : OverlayNGRobust.Overlay(roads, footprintGeometry, SpatialFunction.Intersection);
        var landGeometry = roadGeometry is null || roadGeometry.IsEmpty ? footprintGeometry
            : OverlayNGRobust.Overlay(footprintGeometry, roadGeometry, SpatialFunction.Difference);
        for (int row = low.Row; row <= high.Row; row++)
        for (int col = low.Column; col <= high.Column; col++)
        {
            double x0 = field.ModelX(col), x1 = field.ModelX(col + 1);
            double y0 = field.ModelY(row), y1 = field.ModelY(row + 1);
            var cell = footprintGeometry.Factory.ToGeometry(new Envelope(x0, x1, y0, y1));
            Accumulate(Rings(OverlayNGRobust.Overlay(landGeometry, cell, SpatialFunction.Intersection)), 0);
            if (roadGeometry is { IsEmpty: false })
                Accumulate(Rings(OverlayNGRobust.Overlay(roadGeometry, cell, SpatialFunction.Intersection)), drop);
            void Accumulate(PathsD paths, double offset)
            {
                double Z(PointD p) => field.SampleModelLocal(p.x, p.y) - offset;
                foreach (var path in paths)
                {
                    var anchor = path[0];
                    for (int k = 0; k < path.Count; k++)
                    {
                        var a = path[k]; var b = path[(k + 1) % path.Count];
                        var mid = new PointD((a.x + b.x) / 2, (a.y + b.y) / 2);
                        double za = Z(a), zb = Z(b), zm = Z(mid);
                        minimum = Math.Min(minimum, Math.Min(za, Math.Min(zb, zm)));
                        double q = 2 * (za + zb - 2 * zm), l = zb - za - q;
                        if (Math.Abs(q) > 1e-15)
                        {
                            double t = -l / (2 * q);
                            if (t > 0 && t < 1) minimum = Math.Min(minimum, za + l * t + q * t * t);
                        }
                        double signedArea = ((a.x - anchor.x) * (b.y - anchor.y) - (a.y - anchor.y) * (b.x - anchor.x)) / 2;
                        // The three-point triangle rule integrates every quadratic,
                        // including a bilinear cell, exactly. Signed fans also handle holes.
                        double average = (At(2.0 / 3, 1.0 / 6, 1.0 / 6) + At(1.0 / 6, 2.0 / 3, 1.0 / 6) +
                            At(1.0 / 6, 1.0 / 6, 2.0 / 3)) / 3;
                        integral += signedArea * average; area += signedArea;
                        double At(double wa, double wb, double wc) => Z(new(anchor.x * wa + a.x * wb + b.x * wc,
                            anchor.y * wa + a.y * wb + b.y * wc));
                    }
                }
            }
        }
        double expected = Clipper.Area(footprint);
        if (!double.IsFinite(minimum) || area <= 0 || Math.Abs(area - expected) > Math.Max(1e-6, expected * 1e-8))
            throw new InvalidOperationException("건물 지반 통계에서 전체 윤곽 면적을 보존하지 못했습니다.");
        return (minimum, integral / area);

        static PathsD Rings(NetTopologySuite.Geometries.Geometry geometry)
        {
            var paths = new PathsD();
            Append(geometry);
            return paths;
            void Append(NetTopologySuite.Geometries.Geometry item)
            {
                if (item is Polygon polygon)
                {
                    Add(polygon.ExteriorRing.Coordinates, false);
                    for (int i = 0; i < polygon.NumInteriorRings; i++) Add(polygon.GetInteriorRingN(i).Coordinates, true);
                }
                else if (item is GeometryCollection collection)
                    foreach (var child in collection.Geometries) Append(child);
            }
            void Add(Coordinate[] coordinates, bool hole)
            {
                if (coordinates.Length < 4) return;
                var path = new PathD(coordinates.Take(coordinates.Length - 1).Select(point => new PointD(point.X, point.Y)));
                if ((Clipper.Area(path) > 0) == hole) path.Reverse();
                paths.Add(path);
            }
        }
    }

    private static NetTopologySuite.Geometries.Geometry LocalGeometry(PathsD paths) =>
        NetTopologySuite.Geometries.Utilities.AffineTransformation.ScaleInstance(1e-6, 1e-6)
            .Transform(PlanarTriangulation.ToGridGeometry(paths));

    private static void CheckBounds(PathsD paths, TerrainField field, string id)
    {
        var points = paths.SelectMany(path => path).ToArray();
        if (points.Any(p => p.x < field.X0 - 1e-6 || p.x > field.X1 + 1e-6 || p.y < field.Y0 - 1e-6 || p.y > field.Y1 + 1e-6))
            throw new BuildingExtentException($"건물 {id}의 전체 지반을 포함하는 지형 자료가 확보되지 않았습니다.");
    }

    private static PathsD Project(GeoFeature feature, LocalProjection projection)
    {
        var paths = new PathsD();
        foreach (var polygon in feature.Polygons)
        for (int ring = 0; ring < polygon.Count; ring++)
        {
            var path = new PathD(polygon[ring].Select(point => { var p = projection.ToLocal(point); return new PointD(p.X, p.Y); }));
            if (path.Count > 1 && path[0] == path[^1]) path.RemoveAt(path.Count - 1);
            if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < 1e-12) throw new InvalidOperationException("유효한 면적을 이루지 않는 건물 윤곽입니다.");
            if ((Clipper.Area(path) > 0) != (ring == 0)) path.Reverse();
            paths.Add(path);
        }
        if (paths.Count == 0 || !PlanarTriangulation.ToGridGeometry(paths).IsValid)
            throw new InvalidOperationException("자기교차·잘못된 구멍을 가진 건물 윤곽입니다.");
        return paths;
    }

    private static GeoFeature Copy(GeoFeature source, PathsD paths, LocalProjection projection)
    {
        var geometry = PlanarTriangulation.ToGridGeometry(paths);
        var polygons = new List<List<List<GeoPoint>>>();
        for (int i = 0; i < geometry.NumGeometries; i++)
        {
            var polygon = (Polygon)geometry.GetGeometryN(i);
            polygons.Add(PlanarTriangulation.FromGridPolygon(polygon)
                .Select(path => path.Select(p => projection.ToGeo(p.x, p.y)).ToList()).ToList());
        }
        return new GeoFeature { Id = source.Id, SourceLayer = source.SourceLayer, BuildingId = source.BuildingId,
            BuildingDetails = source.BuildingDetails, BuildingPositionCode = source.BuildingPositionCode,
            BuildingClassification = source.BuildingClassification, UndergroundFloors = source.UndergroundFloors,
            Pnu = source.Pnu, Address = source.Address, ParcelLabel = source.ParcelLabel, LandCategoryCode = source.LandCategoryCode,
            Height = source.Height, Floors = source.Floors, Polygons = polygons };
    }
}
