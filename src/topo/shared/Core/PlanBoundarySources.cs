using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>
/// Original finite straight supports behind the quantized planar graph. The
/// graph supplies connectivity; its inserted, rounded nodes do not redefine a
/// cadastral or site side when exact curves are constructed from it.
/// </summary>
internal sealed class PlanBoundarySources
{
    internal enum SourceKind { SamplingGrid, Road, Boundary, Near }

    internal sealed record Segment(int Id, PointD Start, PointD End, SourceKind Kind)
    {
        internal double Dx => End.x - Start.x;
        internal double Dy => End.y - Start.y;
        internal double Length => Math.Sqrt(Dx * Dx + Dy * Dy);
        internal double Parameter(PointD point) => ((point.x - Start.x) * Dx + (point.y - Start.y) * Dy) / (Dx * Dx + Dy * Dy);
        internal PointD PointAt(double parameter) => new(Start.x + parameter * Dx, Start.y + parameter * Dy);
        internal double Distance(PointD point) => Math.Abs((point.x - Start.x) * Dy - (point.y - Start.y) * Dx) / Length;
    }

    private const double GraphCorridor = 2 * ModelPlanPolicy.PrecisionMetres;
    private sealed record GraphSegment(PointD Start, PointD End, Segment Source);
    private readonly STRtree<GraphSegment> index = new();
    private readonly List<Segment> segments = new();

    internal Segment Add(PointD start, PointD end, SourceKind kind = SourceKind.Boundary)
    {
        var segment = new Segment(segments.Count, start, end, kind);
        segments.Add(segment);
        return segment;
    }

    internal void AddGraphSegment(PointD start, PointD end, Segment source)
    {
        if (start.x != end.x || start.y != end.y)
            index.Insert(new Envelope(start.x, end.x, start.y, end.y), new(start, end, source));
    }

    internal Segment Owner(PointD from, PointD to)
    {
        // Noding propagates the original primitive through its Context. Lookup
        // is against those noded graph edges (including refined subintervals),
        // never against whichever nearby original line happens to be closest.
        const double arithmetic = 1e-9;
        var bounds = new Envelope(from.x, to.x, from.y, to.y); bounds.ExpandBy(arithmetic);
        var candidates = index.Query(bounds).Where(edge => edge.Source.Kind != SourceKind.Near && Contains(edge, from) && Contains(edge, to))
            .Select(edge => edge.Source).DistinctBy(source => source.Id).OrderBy(source => source.Id).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("공통 평면 경계의 원본 선분을 찾을 수 없습니다.");
        // A sampling line subdivides a support surface; it must not replace a
        // footprint/road side merely because snap rounding shares their edge.
        // Boundary is the height discontinuity when a road side coincides.
        var kind = candidates.Max(source => source.Kind);
        candidates = candidates.Where(source => source.Kind == kind).ToArray();
        var owner = candidates[0];
        if (candidates.Skip(1).Any(other => owner.Distance(other.Start) > arithmetic || owner.Distance(other.End) > arithmetic))
        {
            // Snap rounding may route both neighbours back over a one-grid-unit
            // source connector. The original finite connector owns that span;
            // the adjoining primitives only contributed graph detours. Require
            // a shared original endpoint and exact finite support, not a nearest
            // line or whichever source happened to be inserted first.
            var connectors = candidates.Where(candidate =>
                candidate.Distance(from) <= arithmetic && candidate.Distance(to) <= arithmetic &&
                candidate.Parameter(from) >= -arithmetic / candidate.Length && candidate.Parameter(from) <= 1 + arithmetic / candidate.Length &&
                candidate.Parameter(to) >= -arithmetic / candidate.Length && candidate.Parameter(to) <= 1 + arithmetic / candidate.Length &&
                candidates.Any(other => other.Id != candidate.Id &&
                    (Same(candidate.Start, other.End) || Same(candidate.End, other.Start)))).ToArray();
            if (connectors.Length != 1)
                throw new InvalidOperationException("공통 평면 경계가 서로 다른 원본 선분에 중복 배정되었습니다.");
            owner = connectors[0];
        }
        return owner;

        static bool Same(PointD a, PointD b) => a.x == b.x && a.y == b.y;

    }

    internal bool IsOnGraph(PointD from, PointD to, SourceKind kind)
    {
        var bounds = new Envelope(from.x, to.x, from.y, to.y); bounds.ExpandBy(1e-9);
        return index.Query(bounds).Any(edge => edge.Source.Kind == kind && Contains(edge, from) && Contains(edge, to));
    }

    private static bool Contains(GraphSegment edge, PointD point)
    {
        const double arithmetic = 1e-9;
        double dx = edge.End.x - edge.Start.x, dy = edge.End.y - edge.Start.y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double along = ((point.x - edge.Start.x) * dx + (point.y - edge.Start.y) * dy) / length;
        double across = Math.Abs((point.x - edge.Start.x) * dy - (point.y - edge.Start.y) * dx) / length;
        return across <= arithmetic && along >= -arithmetic && along <= length + arithmetic;
    }

    internal PathsD Restore(PathsD graphPaths)
    {
        var result = new PathsD();
        foreach (var path in graphPaths)
        {
            var owners = Enumerable.Range(0, path.Count).Select(i => Owner(path[i], path[(i + 1) % path.Count])).ToArray();
            var restored = new PathD();
            for (int i = 0; i < path.Count; i++)
            {
                var previous = owners[(i + path.Count - 1) % path.Count]; var next = owners[i];
                if (previous.Id == next.Id) continue;
                var point = Junction(previous, next, path[i]);
                if (restored.Count == 0 || Distance(restored[^1], point) > 1e-10) restored.Add(point);
            }
            if (restored.Count > 1 && Distance(restored[0], restored[^1]) <= 1e-10) restored.RemoveAt(restored.Count - 1);
            if (restored.Count < 3 || Clipper.Area(restored) * Clipper.Area(path) <= 0)
                throw new InvalidOperationException("원본 선분으로 복원한 공통 평면 경계가 퇴화했습니다.");
            result.Add(restored);
        }
        return result;
    }

    internal static PointD? Intersection(Segment a, Segment b)
    {
        // Use one evaluation order for both incident faces and preserve known
        // axis coordinates exactly. Otherwise equivalent x=0 junctions can
        // become +/-2e-19 and create an extra native NURBS span.
        if (a.Id > b.Id) (a, b) = (b, a);
        if (Same(a.Start, b.Start) || Same(a.Start, b.End)) return a.Start;
        if (Same(a.End, b.Start) || Same(a.End, b.End)) return a.End;
        double cross = a.Dx * b.Dy - a.Dy * b.Dx;
        if (Math.Abs(cross) <= 1e-14 * a.Length * b.Length) return null;
        PointD point;
        if (a.Dx == 0) point = new(a.Start.x, b.Start.y + (a.Start.x - b.Start.x) / b.Dx * b.Dy);
        else if (a.Dy == 0) point = new(b.Start.x + (a.Start.y - b.Start.y) / b.Dy * b.Dx, a.Start.y);
        else if (b.Dx == 0) point = new(b.Start.x, a.Start.y + (b.Start.x - a.Start.x) / a.Dx * a.Dy);
        else if (b.Dy == 0) point = new(a.Start.x + (b.Start.y - a.Start.y) / a.Dy * a.Dx, b.Start.y);
        else point = a.PointAt(((b.Start.x - a.Start.x) * b.Dy - (b.Start.y - a.Start.y) * b.Dx) / cross);
        return point;
        static bool Same(PointD x, PointD y) => x.x == y.x && x.y == y.y;
    }

    private static PointD Junction(Segment a, Segment b, PointD graphPoint)
    {
        if (Intersection(a, b) is { } intersection)
        {
            double t = a.Parameter(intersection), u = b.Parameter(intersection);
            if (t >= -GraphCorridor / a.Length && t <= 1 + GraphCorridor / a.Length &&
                u >= -GraphCorridor / b.Length && u <= 1 + GraphCorridor / b.Length &&
                WithinGraphCorridor(intersection, graphPoint)) return intersection;
            throw new InvalidOperationException("원본 평면 선분의 교점이 공통 그래프의 교점과 대응하지 않습니다.");
        }
        // Collinear source primitives meet at an original endpoint; preserve it
        // rather than projecting the graph's rounded intersection onto a line.
        var endpoints = new[] { a.Start, a.End, b.Start, b.End }
            .Where(point => a.Distance(point) <= 1e-9 && b.Distance(point) <= 1e-9 &&
                a.Parameter(point) >= -1e-9 / a.Length && a.Parameter(point) <= 1 + 1e-9 / a.Length &&
                b.Parameter(point) >= -1e-9 / b.Length && b.Parameter(point) <= 1 + 1e-9 / b.Length &&
                WithinGraphCorridor(point, graphPoint))
            .OrderBy(point => Distance(point, graphPoint)).ToArray();
        if (endpoints.Length == 0)
            throw new InvalidOperationException("같은 직선에 있는 원본 경계의 연결점을 찾을 수 없습니다.");
        return endpoints[0];
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2));

    internal static bool WithinGraphCorridor(PointD a, PointD b)
    {
        var first = PlanarTriangulation.GridKey(a); var second = PlanarTriangulation.GridKey(b);
        const double grid = ModelPlanPolicy.PrecisionMetres;
        // Only canonical grid coordinates use this exact comparison. A genuine
        // non-grid intersection must not be rounded to manufacture acceptance.
        if (a.x == first.X / 1_000_000d && a.y == first.Y / 1_000_000d &&
            b.x == second.X / 1_000_000d && b.y == second.Y / 1_000_000d)
        {
            Int128 dx = (Int128)first.X - second.X, dy = (Int128)first.Y - second.Y;
            long corridor = (long)Math.Round(GraphCorridor / grid);
            return dx * dx + dy * dy <= (Int128)corridor * corridor;
        }
        return Distance(a, b) <= GraphCorridor;
    }
}
