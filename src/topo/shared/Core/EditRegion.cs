using Clipper2Lib;

namespace Aha.Topo;

/// <summary>Exact local-metre circle for one dissolved design site.</summary>
public sealed record EditCircle(string SiteComponentId, double CenterX, double CenterY, double RadiusMetres);

/// <summary>Convex envelopes of overlapping centroid circles, shared by every model representation.</summary>
public sealed class EditRegion
{
    public const int ClipPrecision = 6;
    public const double MaximumChordErrorMetres = .001;
    private const double CoordinateTolerance = .000001;
    private readonly double west, south, east, north;
    /// <summary>Per-site circles at the selected ratio; exact output boundaries also include common tangents.</summary>
    public IReadOnlyList<EditCircle> Circles { get; }
    /// <summary>Authoritative exact CCW boundaries, containing circular arcs and common tangent lines.</summary>
    public IReadOnlyList<EditBoundaryLoop> Boundaries { get; }
    /// <summary>Chord approximation of the same exact boundaries for planar processing and map preview.</summary>
    public PathsD Paths { get; }
    public IReadOnlyList<IReadOnlyList<GeoPoint>> Rings { get; }

    private EditRegion(IReadOnlyList<EditCircle> circles, IReadOnlyList<EditBoundaryLoop> boundaries, PathsD paths, LocalProjection projection)
    {
        Circles = circles; Boundaries = boundaries; Paths = paths;
        west = circles.Min(c => c.CenterX - c.RadiusMetres);
        south = circles.Min(c => c.CenterY - c.RadiusMetres);
        east = circles.Max(c => c.CenterX + c.RadiusMetres);
        north = circles.Max(c => c.CenterY + c.RadiusMetres);
        Rings = paths.Select(path => (IReadOnlyList<GeoPoint>)path
            .Append(path[0]).Select(p => projection.ToGeo(p.x, p.y)).ToArray()).ToArray();
    }

    public static EditRegion Create(GeoFeature target, LocalProjection projection, GeoBounds bounds, double ratio)
        => Create(SiteSelection.Create(new[] { target }, projection), projection, bounds, ratio);

    public static EditRegion Create(SiteSelection selection, LocalProjection projection, GeoBounds bounds, double ratio)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        bounds.Validate();
        var region = CreateUnbounded(selection, projection, ratio);
        var sw = projection.ToLocal(new GeoPoint(bounds.West, bounds.South));
        var ne = projection.ToLocal(new GeoPoint(bounds.East, bounds.North));
        if (region.west < sw.X - CoordinateTolerance || region.south < sw.Y - CoordinateTolerance ||
            region.east > ne.X + CoordinateTolerance || region.north > ne.Y + CoordinateTolerance)
        {
            double cx = (sw.X + ne.X) / 2, cy = (sw.Y + ne.Y) / 2;
            double width = Math.Ceiling(2 * Math.Max(cx - region.west, region.east - cx));
            double depth = Math.Ceiling(2 * Math.Max(cy - region.south, region.north - cy));
            throw new ArgumentException(FormattableString.Invariant(
                $"모델 범위 부족 · 근접 영역 최소 {width:0} × {depth:0} m"));
        }
        return region;
    }

    public static EditRegion CreateUnbounded(SiteSelection selection, LocalProjection projection, double ratio)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(projection);
        if (!double.IsFinite(ratio) || ratio < SiteSelection.MinimumNearRatio)
            throw new ArgumentException("근접 영역 배수는 1.5 이상의 유한한 값이어야 합니다. 각 대지 중심에서 가장 먼 경계까지의 거리 R에 배수를 곱한 원을 만듭니다.");
        var circles = selection.Components.Select(component =>
        {
            var centre = projection.ToLocal(component.Centre);
            var radius = ratio * component.RadiusMetres;
            if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentException("근접 영역의 반경을 계산할 수 없습니다.");
            return new EditCircle(component.Boundary.Id, centre.X, centre.Y, radius);
        }).ToArray();
        var boundaries = EditBoundaryHull.Create(circles);
        var paths = new PathsD();
        foreach (var boundary in boundaries)
        {
            var path = new PathD();
            foreach (var segment in boundary.Segments)
            {
                path.Add(new PointD(segment.Start.X, segment.Start.Y));
                if (segment.Circle is not { } circle) continue;
                // Round-off in the shared planar graph must also fit the 1 mm bound.
                double error = Math.Min(MaximumChordErrorMetres - CoordinateTolerance, circle.RadiusMetres / 2);
                double step = Math.Min(2 * Math.PI / 64, 2 * Math.Acos(1 - error / circle.RadiusMetres));
                int count = Math.Max(1, (int)Math.Ceiling(segment.SweepRadians / step));
                for (int index = 1; index < count; index++)
                {
                    var point = segment.PointAt((double)index / count); path.Add(new PointD(point.X, point.Y));
                }
            }
            paths.Add(path);
        }
        var polygon = Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.NonZero, ClipPrecision);
        if (polygon.Count == 0) throw new ArgumentException("근접 영역의 경계를 구성할 수 없습니다.");
        return new EditRegion(circles, boundaries, polygon, projection);
    }

    public bool ContainsLocal(double x, double y) => Boundaries.Any(boundary => boundary.Contains(new(x, y)));

    /// <summary>Every filled outer ring must stay strictly inside one exact convex Near boundary.</summary>
    public bool ContainsStrict(GeoFeature feature, LocalProjection projection) => feature.Polygons.Count > 0 &&
        feature.Polygons.All(polygon => polygon.Count > 0 && StrictRing(polygon[0].Select(point =>
        { var local = projection.ToLocal(point); return new PointD(local.X, local.Y); }).ToArray()));

    public bool ContainsStrict(PathsD paths)
    {
        var outers = paths.Where(path => Clipper.Area(path) > 0).ToArray();
        return outers.Length > 0 && outers.All(StrictRing);
    }

    private bool StrictRing(IReadOnlyList<PointD> ring) => ring.Count >= 3 && Boundaries.Any(boundary =>
        ring.All(point => boundary.Contains(new(point.x, point.y), -CoordinateTolerance)));

    /// <summary>Detect any source intersection with an exact hull, including its tangent bridges.</summary>
    public bool Intersects(GeoFeature feature, LocalProjection projection)
    {
        foreach (var polygon in feature.Polygons)
        {
            if (polygon.Count == 0) continue;
            var rings = polygon.Select(r => new PathD(r.Select(p =>
            {
                var local = projection.ToLocal(p);
                return new PointD(local.X, local.Y);
            }))).Where(r => r.Count >= 3).ToArray();
            if (rings.Length == 0) continue;
            var points = rings.SelectMany(r => r).ToArray();
            if (points.Max(p => p.x) < west - CoordinateTolerance || points.Min(p => p.x) > east + CoordinateTolerance ||
                points.Max(p => p.y) < south - CoordinateTolerance || points.Min(p => p.y) > north + CoordinateTolerance) continue;
            foreach (var boundary in Boundaries)
            {
                foreach (var ring in rings)
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if (boundary.IntersectsSegment(new(a.x, a.y), new(b.x, b.y), CoordinateTolerance)) return true;
                }
                // No polygon edge crosses the hull. It intersects only if this
                // hull lies inside the filled polygon rather than in one of its holes.
                var start = boundary.Segments[0].Start; var point = new PointD(start.X, start.Y);
                if (Clipper.PointInPolygon(point, rings[0], ClipPrecision) != PointInPolygonResult.IsOutside &&
                    !rings.Skip(1).Any(hole => Clipper.PointInPolygon(point, hole, ClipPrecision) != PointInPolygonResult.IsOutside)) return true;
            }
        }
        return false;
    }

}
