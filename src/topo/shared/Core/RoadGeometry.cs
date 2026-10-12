using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace Aha.Topo;

public sealed record RoadGeometryDiagnostics(int ParcelCount, int RoadParcelCount, int UnknownCategoryCount,
    int RepairedParcelCount, double RoadUnionAreaSquareMetres, double ConflictAreaSquareMetres,
    double QuantizationAreaErrorSquareMetres)
{
    public static RoadGeometryDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    // Invalid crossing rings have algebraic area, not a valid surface area.
    public double RegularizationAreaChangeSquareMetres { get; init; }
    public ModelPlanDiagnostics? ModelCleanup { get; init; }
}

public sealed record RoadArea(PathsD Outlines, double AreaSquareMetres)
{
    public RoadGeometryDiagnostics Diagnostics { get; init; } = RoadGeometryDiagnostics.Empty;
    public PathsD? PlanarNearBoundary { get; init; }
    internal PathsD? ExactSource { get; init; }
    internal IReadOnlyList<ExactNearRegions.RoleChange> ExactChanges { get; init; } = Array.Empty<ExactNearRegions.RoleChange>();
}

/// <summary>
/// Cadastral road parcels clipped to the model extent. This is registered road
/// land, not a surveyed pavement outline. Raw parcel coordinates stay unchanged.
/// </summary>
public static class RoadGeometry
{
    public const int ClipPrecision = EditRegion.ClipPrecision;
    private const int ReferencePrecision = 8;

    public static RoadArea Build(IReadOnlyList<GeoFeature> parcels, LocalProjection projection, GeoBounds bounds,
        GeoBounds? outputBounds = null, EditRegion? near = null, CancellationToken ct = default)
    {
        if (outputBounds is not null)
        {
            outputBounds.Validate();
            if (outputBounds.West < bounds.West || outputBounds.South < bounds.South ||
                outputBounds.East > bounds.East || outputBounds.North > bounds.North)
                throw new ArgumentException("도로 계산 범위가 모델 출력 범위를 포함해야 합니다.");
        }
        var source = BuildSource(parcels, projection, bounds);
        var model = ModelPlanGeometry.Build(source.Outlines, Domain(bounds));
        var roads = model.Roads;
        var diagnostics = model.Diagnostics;
        PathsD? planarNearBoundary = null;
        NearRoadResult? exactPlan = null;
        if (outputBounds is not null && outputBounds != bounds)
        {
            var outputDomain = Domain(outputBounds);
            // An output cut can turn a valid road into a sub-100mm strip. Clean
            // that domain against the original source, not an already moved edge.
            // Reconcile once before grading so buildings and terrain share it.
            var output = ModelPlanGeometry.Build(source.Outlines, outputDomain);
            var outside = Clipper.Difference(roads, outputDomain, FillRule.NonZero, ClipPrecision);
            roads = Clipper.BooleanOp(ClipType.Union, new PathsD(outside.Concat(output.Roads)), null, FillRule.NonZero, ClipPrecision);
            diagnostics = output.Diagnostics;
        }
        if (near is not null)
        {
            var domain = Domain(outputBounds ?? bounds);
            var resolved = NearRoadGeometry.Resolve(roads, domain, near, ct:ct);
            exactPlan=resolved;
            if (resolved.ResolvedComponents > 0) planarNearBoundary = resolved.NearBoundary;
            // A validation-only Near cut must preserve the existing plan and
            // its measurements, without another rounded Boolean evaluation.
            if (resolved.ResolvedComponents > 0)
            {
                roads = resolved.Roads;
                var sourceOutput = Clipper.Intersect(source.Outlines, domain, FillRule.NonZero, ClipPrecision);
                var modelOutput = Clipper.Intersect(roads, domain, FillRule.NonZero, ClipPrecision);
                diagnostics = diagnostics with
                {
                    ModelAreaSquareMetres = Math.Abs(Clipper.Area(modelOutput)),
                    ChangedAreaSquareMetres = Math.Abs(Clipper.Area(Clipper.Xor(sourceOutput, modelOutput, FillRule.NonZero, ClipPrecision))),
                    AddedRoadAreaSquareMetres = Math.Abs(Clipper.Area(Clipper.Difference(modelOutput, sourceOutput, FillRule.NonZero, ClipPrecision))),
                    RemovedRoadAreaSquareMetres = Math.Abs(Clipper.Area(Clipper.Difference(sourceOutput, modelOutput, FillRule.NonZero, ClipPrecision))),
                    ModelComponents = resolved.ModelComponents,
                    NearResolvedComponents = resolved.ResolvedComponents,
                    NearChangedAreaSquareMetres = resolved.ChangedAreaSquareMetres
                };
            }
        }
        return new(roads, Math.Abs(Clipper.Area(roads)))
        { Diagnostics = source.Diagnostics with { ModelCleanup = diagnostics }, PlanarNearBoundary = planarNearBoundary, ExactSource=exactPlan?.ExactSource, ExactChanges=exactPlan?.ExactChanges??Array.Empty<ExactNearRegions.RoleChange>() };

        PathsD Domain(GeoBounds box)
        {
            var sw = projection.ToLocal(new GeoPoint(box.West, box.South));
            var ne = projection.ToLocal(new GeoPoint(box.East, box.North));
            return new() { new() { new(sw.X, sw.Y), new(ne.X, sw.Y), new(ne.X, ne.Y), new(sw.X, ne.Y) } };
        }
    }

    /// <summary>Classify source cadastral geometry before model-only cleanup.</summary>
    public static RoadArea BuildSource(IReadOnlyList<GeoFeature> parcels, LocalProjection projection, GeoBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(parcels);
        ArgumentNullException.ThrowIfNull(projection);
        bounds.Validate();
        var roadPaths = new PathsD();
        var landPaths = new PathsD();
        int roads = 0, unknown = 0, repaired = 0;
        double regularizationChange = 0;
        var factory = new GeometryFactory();
        foreach (var parcel in parcels)
        {
            ArgumentNullException.ThrowIfNull(parcel);
            bool isRoad = parcel.LandCategoryCode == ParcelLandCategory.Road;
            if (isRoad) roads++;
            if (!ParcelLandCategory.IsKnown(parcel.LandCategoryCode)) unknown++;
            bool changed = false;
            var parts = new PathsD();
            foreach (var polygon in parcel.Polygons)
            {
                if (polygon.Count == 0) throw InvalidParcel(parcel, "경계가 비어 있습니다");
                var rings = polygon.Select(ring => LocalRing(ring, projection, parcel)).ToArray();
                var source = factory.CreatePolygon(NtsRing(rings[0], factory),
                    rings.Skip(1).Select(ring => NtsRing(ring, factory)).ToArray());
                bool invalid = !source.IsValid;
                changed |= invalid;
                // Roles come from GeoJSON, not winding. Node the original segments
                // and polygonize each ring with even/odd fill. Crossing shells may
                // become several components; holes always remain subtractive.
                // No buffer, smoothing, minimum-width deletion or vertex shift.
                var shell = Regularize(rings[0]);
                if (shell.Count == 0) throw InvalidParcel(parcel, "외곽이 면적을 이루지 않습니다");
                var holes = new PathsD();
                foreach (var hole in rings.Skip(1)) holes.AddRange(Regularize(hole));
                var area = holes.Count == 0 ? shell : Clipper.Difference(shell, Union(holes, ReferencePrecision),
                    FillRule.NonZero, ReferencePrecision);
                if (area.Count == 0) throw InvalidParcel(parcel, "내부 경계가 외곽 면적을 모두 제거합니다");
                double algebraicArea = Math.Max(0, Math.Abs(Clipper.Area(rings[0])) -
                    rings.Skip(1).Sum(ring => Math.Abs(Clipper.Area(ring))));
                if (invalid) regularizationChange += Math.Abs(Math.Abs(Clipper.Area(area)) - algebraicArea);
                parts.AddRange(area);
            }
            if (parts.Count == 0) throw InvalidParcel(parcel, "면 형상이 없습니다");
            if (changed) repaired++;
            (isRoad ? roadPaths : landPaths).AddRange(parts);
        }

        var sw = projection.ToLocal(new GeoPoint(bounds.West, bounds.South));
        var ne = projection.ToLocal(new GeoPoint(bounds.East, bounds.North));
        var box = new PathsD { new PathD { new(sw.X, sw.Y), new(ne.X, sw.Y), new(ne.X, ne.Y), new(sw.X, ne.Y) } };
        var referenceRoad = ClipUnion(roadPaths, box, ReferencePrecision);
        var referenceLand = ClipUnion(landPaths, box, ReferencePrecision);
        // A conflict does not establish a road: known non-road and unclassified
        // parcels both take precedence. Never push a road into their land.
        var reference = Clipper.Difference(referenceRoad, referenceLand, FillRule.NonZero, ReferencePrecision);
        var roadUnion = ClipUnion(roadPaths, box, ClipPrecision);
        var landUnion = ClipUnion(landPaths, box, ClipPrecision);
        var outlines = SimplePolygons(Clipper.Difference(roadUnion, landUnion, FillRule.NonZero, ClipPrecision), factory);
        var conflict = Clipper.Intersect(roadUnion, landUnion, FillRule.NonZero, ClipPrecision);
        double roundingError = Math.Abs(Clipper.Area(Clipper.Xor(reference, outlines, FillRule.NonZero, ReferencePrecision)));
        // Bound fixed-grid error by boundary length, not a region-specific cutoff.
        double delta = Math.Sqrt(2) * Math.Pow(10, -ClipPrecision);
        double perimeter = Perimeter(referenceRoad) + Perimeter(referenceLand) + Perimeter(box);
        double roundingBound = 2 * perimeter * delta + Math.PI * delta * delta *
            (referenceRoad.Sum(path => path.Count) + referenceLand.Sum(path => path.Count) + 4);
        if (roundingError > roundingBound)
            throw new InvalidOperationException("도로 필지의 좌표 정밀도 처리에서 허용 범위를 넘는 면적 변화가 발생했습니다.");
        return new RoadArea(outlines, Math.Abs(Clipper.Area(outlines)))
        {
            Diagnostics = new(parcels.Count, roads, unknown, repaired, Math.Abs(Clipper.Area(roadUnion)),
                Math.Abs(Clipper.Area(conflict)), roundingError)
            { RegularizationAreaChangeSquareMetres = regularizationChange }
        };
    }

    private static PathD LocalRing(IReadOnlyList<GeoPoint> ring, LocalProjection projection, GeoFeature parcel)
    {
        var path = new PathD();
        foreach (var point in ring)
        {
            var xy = projection.ToLocal(point);
            if (path.Count == 0 || path[^1].x != xy.X || path[^1].y != xy.Y) path.Add(new(xy.X, xy.Y));
        }
        if (path.Count > 1 && path[0].x == path[^1].x && path[0].y == path[^1].y) path.RemoveAt(path.Count - 1);
        if (path.Select(point => (point.x, point.y)).Distinct().Count() < 3)
            throw InvalidParcel(parcel, "서로 다른 경계점이 세 개 미만입니다");
        return path;
    }

    private static PathsD Regularize(PathD ring) =>
        Clipper.BooleanOp(ClipType.Union, new PathsD { ring }, null, FillRule.EvenOdd, ReferencePrecision);
    private static PathsD Union(PathsD paths, int precision) =>
        Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.NonZero, precision);
    private static PathsD ClipUnion(PathsD paths, PathsD box, int precision) =>
        Clipper.Intersect(Union(paths, precision), box, FillRule.NonZero, precision);
    private static LinearRing NtsRing(PathD path, GeometryFactory factory) => factory.CreateLinearRing(
        path.Append(path[0]).Select(point => new Coordinate(point.x, point.y)).ToArray());
    private static double Perimeter(PathsD paths) => paths.Sum(path => Enumerable.Range(0, path.Count).Sum(i =>
    {
        var a = path[i]; var b = path[(i + 1) % path.Count];
        return Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2));
    }));

    private static PathsD SimplePolygons(PathsD paths, GeometryFactory factory)
    {
        var result = new PathsD();
        var clipper = new ClipperD(ClipPrecision);
        clipper.AddSubject(paths);
        var tree = new PolyTreeD();
        if (!clipper.Execute(ClipType.Union, FillRule.NonZero, tree))
            throw new InvalidOperationException("도로 필지의 평면 위상을 구성하지 못했습니다.");
        Visit(tree);
        return result;
        void Visit(PolyPathD parent)
        {
            for (int i = 0; i < parent.Count; i++)
            {
                var node = parent[i];
                if (!node.IsHole && node.Polygon is { Count: >= 3 } shell)
                {
                    var holes = Enumerable.Range(0, node.Count).Select(j => node[j].Polygon)
                        .Where(path => path is { Count: >= 3 }).Select(path => NtsRing(path!, factory)).ToArray();
                    var polygon = factory.CreatePolygon(NtsRing(shell, factory), holes);
                    // Clipper may return point-touching lobes as one pinched
                    // ring. Split that topology into valid polygon components;
                    // the symmetric-difference check above bounds any change.
                    var simple = polygon.IsValid ? polygon : GeometryFixer.Fix(polygon);
                    if (!simple.IsValid || simple.IsEmpty || simple.Area <= 0)
                        throw new InvalidOperationException("정규화한 도로 필지가 유효한 면 위상을 이루지 못합니다: " +
                            new NetTopologySuite.Operation.Valid.IsValidOp(simple).ValidationError);
                    Add(simple);
                }
                Visit(node);
            }
        }
        void Add(Geometry geometry)
        {
            if (geometry is Polygon polygon)
            {
                Append(polygon.ExteriorRing, true);
                for (int i = 0; i < polygon.NumInteriorRings; i++) Append(polygon.GetInteriorRingN(i), false);
            }
            else if (geometry is MultiPolygon multi)
                for (int i = 0; i < multi.NumGeometries; i++) Add(multi.GetGeometryN(i));
            else throw new InvalidOperationException("도로 필지 정규화가 면 이외의 형상을 반환했습니다.");
        }
        void Append(LineString ring, bool outer)
        {
            var path = new PathD(ring.Coordinates.Take(ring.NumPoints - 1).Select(p => new PointD(p.X, p.Y)));
            if ((Clipper.Area(path) > 0) != outer) path.Reverse();
            result.Add(path);
        }
    }

    private static InvalidOperationException InvalidParcel(GeoFeature parcel, string reason) =>
        new($"필지 {(!string.IsNullOrWhiteSpace(parcel.Pnu) ? parcel.Pnu : parcel.Id)}: {reason}.");
}
