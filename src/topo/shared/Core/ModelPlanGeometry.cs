using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>Physical modelling limits, independent of the Rhino document unit.</summary>
public sealed record ModelPlanPolicy(double MinimumWidthMetres = .1,
    double MinimumAreaSquareMetres = .01, double MaximumBoundaryChangeMetres = .05)
{
    public const int Revision = 1;
    public const double PrecisionMetres = .000001;
    public static ModelPlanPolicy Default { get; } = new();

    public void Validate()
    {
        if (!double.IsFinite(MinimumWidthMetres) || MinimumWidthMetres <= 4 * PrecisionMetres ||
            !double.IsFinite(MinimumAreaSquareMetres) || MinimumAreaSquareMetres <= 0 ||
            !double.IsFinite(MaximumBoundaryChangeMetres) || MaximumBoundaryChangeMetres <= 2 * PrecisionMetres ||
            MaximumBoundaryChangeMetres > MinimumWidthMetres / 2)
            throw new ArgumentException("모델 평면의 최소 폭·면적·경계 변경 한도가 유효하지 않습니다.");
    }
}

public sealed record ModelPlanDiagnostics(int PolicyRevision, double MinimumWidthMetres,
    double MinimumAreaSquareMetres, double MaximumBoundaryChangeMetres,
    double SourceAreaSquareMetres, double ModelAreaSquareMetres,
    double ChangedAreaSquareMetres, double AddedRoadAreaSquareMetres, double RemovedRoadAreaSquareMetres,
    double OutsideChangeBandAreaSquareMetres, double PreservedRoadCoreAreaSquareMetres,
    int SourceComponents, int ModelComponents, int RemovedSmallComponents,
    int FilledSmallHoles, int ResolvedPointContacts)
{
    public double CoverageResidualSquareMetres { get; init; }
    public int ReassignedComponents { get; init; }
    public double ReassignedAreaSquareMetres { get; init; }
    public int NearResolvedComponents { get; init; }
    public double NearChangedAreaSquareMetres { get; init; }
}

public sealed record ModelPlanResult(PathsD Roads, PathsD Land, ModelPlanDiagnostics Diagnostics);

/// <summary>
/// Produces one complementary road/land partition for modelling. Source paths are
/// never edited. Opening/closing proposes the reassignment; protected road cores
/// and a source-boundary band bound its final effect. No independent land buffer
/// is returned, so cleanup cannot leave gaps or overlapping ground surfaces.
/// </summary>
public static class ModelPlanGeometry
{
    private const int Precision = 6;
    private const double Grid = ModelPlanPolicy.PrecisionMetres;
    // Four offset passes and their overlays each round to the integer grid.
    private const double OffsetRoundTripAllowance = 8 * Grid;

    public static ModelPlanResult Build(PathsD sourceRoads, PathsD aoi, ModelPlanPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoads);
        ArgumentNullException.ThrowIfNull(aoi);
        policy ??= ModelPlanPolicy.Default;
        policy.Validate();
        var domain = Union(Copy(aoi));
        if (domain.Count == 0 || Area(domain) <= 0)
            throw new ArgumentException("모델 평면 범위가 유효한 면이 아닙니다.");
        var source = Intersect(Union(Copy(sourceRoads)), domain);
        int sourceCount = Components(source).Count;
        // A channel exactly at the minimum width must retain a positive core.
        // Eroding by exactly half its width would remove it before reconstruction.
        double radius = Math.Min(policy.MinimumWidthMetres / 2, policy.MaximumBoundaryChangeMetres) - Grid;
        var core = Offset(source, -radius);
        var allowed = ChangeBandOffset(source, policy.MaximumBoundaryChangeMetres);
        var immutable = ChangeBandOffset(source, -policy.MaximumBoundaryChangeMetres);
        var band = Difference(allowed, immutable);
        // Clean the authoritative source once before all output partitions.
        var model = source.Count == 0 ? new PathsD() : Offset(Offset(source, -radius), radius);
        model = Offset(Offset(model, radius), -radius);
        model = Constrain(model);

        int removed = 0, filled = 0;
        var kept = new PathsD();
        foreach (var component in Components(model))
        {
            // Area alone must never remove a useful short, wide road or its
            // connection. A component with a minimum-width core is protected.
            if (Area(component) < policy.MinimumAreaSquareMetres &&
                Area(Intersect(component, core)) <= Grid * Grid &&
                Area(Difference(component, band)) <= Grid * Grid)
            { removed++; continue; }
            kept.AddRange(component);
        }
        model = Union(kept);
        var voids = Components(Difference(domain, model));
        var additions = new PathsD();
        foreach (var component in voids)
        {
            if (Area(component) >= policy.MinimumAreaSquareMetres) continue;
            if (Area(Difference(component, band)) <= Grid * Grid)
            { additions.AddRange(component); filled++; continue; }

            // Outside the bounded cleanup band, retain the fragment for the
            // neighbour-role reassignment below instead of inventing a wider island.
        }
        if (additions.Count > 0) model = Union(new PathsD(model.Concat(additions)));
        model = Union(SnapUnchangedVertices(model, source));
        // Opposite integer offsets of an AOI-clipped oblique boundary can leave
        // a one-grid strip between two copies of that same boundary. Reattach
        // surviving road to the authoritative domain before classifying point
        // contacts. Only source road within the offset rounding allowance of both the model and the
        // domain edge is eligible; this cannot restore a removed thin island.
        var domainRim = Difference(domain, Offset(domain, -OffsetRoundTripAllowance));
        var boundaryRepair = Intersect(Intersect(source, domainRim), Offset(model, OffsetRoundTripAllowance));
        model = Union(new PathsD(model.Concat(boundaryRepair)));

        // Offset polygonization can leave exact point contacts. Give those
        // contacts area within the same source-boundary budget instead of
        // extruding them into a four-face vertical line or tiny auxiliary solid.
        var touching = Contacts(model);
        int contacts = touching.Count;
        if (contacts > 0)
        {
            var patches = new PathsD();
            foreach (var point in touching)
                patches.Add(Disk(point, radius));
            model = Constrain(Union(new PathsD(model.Concat(patches))));
        }

        model = Canonical(Intersect(model, domain));
        var changed = Xor(source, model);
        double outsideBand = Area(Difference(changed, band));
        double missingCore = Area(Difference(core, model));
        // One-grid overlays may form sub-square-micrometre numerical remnants.
        double areaTolerance = Math.Max(Grid * Grid, (Perimeter(source) + Perimeter(model)) * Grid * 2);
        if (outsideBand > areaTolerance || missingCore > areaTolerance)
            throw new InvalidOperationException("모델 평면 정리가 경계 변경 한도 또는 도로 연결 보존 범위를 벗어났습니다.");
        // The physical-detail policy takes precedence for residual independent
        // fragments. This second stage does not move the domain boundary.
        var reassigned = NearRoadGeometry.ResolveFragments(model, new[] { domain }, policy);
        double reassignedArea = Area(Xor(model, reassigned.Roads));
        model = Canonical(reassigned.Roads);
        var land = Canonical(Difference(domain, model));
        changed = Xor(source, model);
        outsideBand = Area(Difference(changed, band));
        // Clipping a slanted domain edge can add a rounded intersection vertex.
        // Reuniting the complementary rings may therefore differ from the
        // unsplit domain edge by the same one-grid overlay allowance.
        double coverageResidual = Area(Xor(Union(new PathsD(model.Concat(land))), domain));
        if (Area(Intersect(model, land)) > areaTolerance || coverageResidual > areaTolerance)
            throw new InvalidOperationException("모델용 땅·도로가 전체 범위를 중복 없이 채우지 못했습니다.");

        return new(model, land, new(ModelPlanPolicy.Revision, policy.MinimumWidthMetres,
            policy.MinimumAreaSquareMetres, policy.MaximumBoundaryChangeMetres,
            Area(source), Area(model), Area(changed), Area(Difference(model, source)), Area(Difference(source, model)),
            outsideBand, Area(core), sourceCount, Components(model).Count, removed, filled, contacts)
            { CoverageResidualSquareMetres = coverageResidual,
                ReassignedComponents = reassigned.Count, ReassignedAreaSquareMetres = reassignedArea });

        PathsD Constrain(PathsD candidate) => Intersect(Union(new PathsD(
            Intersect(candidate, allowed).Concat(core))), domain);
        List<PointD> Contacts(PathsD roads)
        {
            var domainContacts = ContactPoints(domain).Select(Key).ToHashSet();
            var roadVertices = roads.SelectMany(path => path).Select(Key).ToHashSet();
            return ContactPoints(roads).Concat(ContactPoints(Difference(domain, roads)))
                .DistinctBy(Key).Where(point => !domainContacts.Contains(Key(point)) ||
                    roadVertices.Contains(Key(point))).ToList();
        }
        static (long X, long Y) Key(PointD point) => ((long)Math.Round(point.x / Grid), (long)Math.Round(point.y / Grid));
    }

    private static PathsD Copy(PathsD paths)
    {
        var result = new PathsD();
        foreach (var path in paths)
        {
            if (path is null || path.Count < 3 || path.Any(p => !double.IsFinite(p.x) || !double.IsFinite(p.y)))
                throw new ArgumentException("모델 평면 경계의 좌표가 유효하지 않습니다.");
            result.Add(new PathD(path));
        }
        return result;
    }

    private static PathsD Offset(PathsD paths, double distance) => paths.Count == 0 ? new() :
        Clipper.InflatePaths(paths, distance, JoinType.Miter, EndType.Polygon, 2, Precision);
    private static PathsD ChangeBandOffset(PathsD paths, double distance) => paths.Count == 0 ? new() :
        Clipper.InflatePaths(paths, distance, JoinType.Round, EndType.Polygon, 2, Precision, .0001);
    private static PathsD Union(PathsD paths) => Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.NonZero, Precision);
    private static PathsD Intersect(PathsD a, PathsD b) => Clipper.Intersect(a, b, FillRule.NonZero, Precision);
    private static PathsD Difference(PathsD a, PathsD b) => Clipper.Difference(a, b, FillRule.NonZero, Precision);
    private static PathsD Xor(PathsD a, PathsD b) => Clipper.Xor(a, b, FillRule.NonZero, Precision);
    private static double Area(PathsD paths) => Math.Abs(Clipper.Area(paths));

    private static List<PathsD> Components(PathsD paths)
    {
        var clipper = new ClipperD(Precision); clipper.AddSubject(paths);
        var tree = new PolyTreeD();
        if (!clipper.Execute(ClipType.Union, FillRule.NonZero, tree))
            throw new InvalidOperationException("모델 평면의 연결 영역을 구성할 수 없습니다.");
        var result = new List<PathsD>(); Visit(tree); return result;
        void Visit(PolyPathD parent)
        {
            for (int i = 0; i < parent.Count; i++)
            {
                var node = parent[i];
                if (!node.IsHole && node.Polygon is { Count: >= 3 } shell)
                {
                    var component = new PathsD { new PathD(shell) };
                    for (int j = 0; j < node.Count; j++)
                        if (node[j].IsHole && node[j].Polygon is { Count: >= 3 } hole) component.Add(new PathD(hole));
                    result.Add(component);
                }
                Visit(node);
            }
        }
    }

    private static List<PointD> ContactPoints(PathsD paths)
    {
        var points = new Dictionary<(long X, long Y), int>();
        foreach (var component in Components(paths))
        foreach (var path in component)
        foreach (var point in path)
        {
            var key = ((long)Math.Round(point.x / Grid), (long)Math.Round(point.y / Grid));
            points[key] = points.GetValueOrDefault(key) + 1;
        }
        return points.Where(p => p.Value > 1).OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y)
            .Select(p => new PointD(p.Key.X * Grid, p.Key.Y * Grid)).ToList();
    }

    private static PathD Disk(PointD point, double radius)
    {
        const int count = 48;
        return new PathD(Enumerable.Range(0, count).Select(i =>
            new PointD(point.x + radius * Math.Cos(i * 2 * Math.PI / count),
                point.y + radius * Math.Sin(i * 2 * Math.PI / count))));
    }

    private static PathsD SnapUnchangedVertices(PathsD model, PathsD source)
    {
        // Opposite offsets of a slanted boundary can round its unchanged
        // vertices by one or two grid units. Restore only that numerical noise;
        // deleted thin features have no surviving vertices to be restored.
        var tree = new STRtree<PointD>();
        foreach (var point in source.SelectMany(path => path))
            tree.Insert(new Envelope(point.x, point.x, point.y, point.y), point);
        tree.Build();
        const double tolerance = 2 * Grid;
        var result = new PathsD();
        foreach (var path in model)
        {
            var snapped = new PathD();
            foreach (var point in path)
            {
                var candidates = tree.Query(new Envelope(point.x - tolerance, point.x + tolerance,
                    point.y - tolerance, point.y + tolerance));
                var nearest = point; double distance = tolerance * tolerance;
                foreach (var candidate in candidates)
                {
                    double dx = candidate.x - point.x, dy = candidate.y - point.y;
                    double squared = dx * dx + dy * dy;
                    if (squared < distance || squared == distance &&
                        (candidate.x < nearest.x || candidate.x == nearest.x && candidate.y < nearest.y))
                    { nearest = candidate; distance = squared; }
                }
                if (snapped.Count == 0 || nearest.x != snapped[^1].x || nearest.y != snapped[^1].y) snapped.Add(nearest);
            }
            if (snapped.Count > 1 && snapped[0].x == snapped[^1].x && snapped[0].y == snapped[^1].y) snapped.RemoveAt(snapped.Count - 1);
            if (snapped.Count >= 3) result.Add(snapped);
        }
        return result;
    }

    private static PathsD Canonical(PathsD paths)
    {
        var rings = new List<PathD>();
        foreach (var path in paths)
        {
            int first = Enumerable.Range(0, path.Count).MinBy(i => (path[i].x, path[i].y));
            rings.Add(new PathD(Enumerable.Range(0, path.Count).Select(i => path[(first + i) % path.Count])));
        }
        return new PathsD(rings.OrderBy(p => p[0].x).ThenBy(p => p[0].y).ThenBy(p => -Math.Abs(Clipper.Area(p))));
    }

    private static double Perimeter(PathsD paths) => paths.Sum(path => Enumerable.Range(0, path.Count).Sum(i =>
    {
        var a = path[i]; var b = path[(i + 1) % path.Count];
        return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
    }));
}
