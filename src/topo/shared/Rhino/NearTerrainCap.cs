using Rhino.Geometry;

namespace Aha.Topo;

public sealed record NearCapBoundary(int SourceEdgeIndex, int StartVertex, int EndVertex, Curve Profile);

/// <summary>Orders terrain boundary loops and measures their actual projected area.</summary>
public static class NearTerrainCap
{
    /// <summary>
    /// Builds one downward planar face from the walls' exact lower profiles.
    /// Each profile sequence closes at Z=bottom; the sole outer loop is CCW
    /// and holes are CW. Curves, knots and their segmentation are copied directly,
    /// without planar-region reconstruction, simplification or edge rebuilding.
    /// The caller owns the returned Brep; input curves remain unchanged.
    /// </summary>
    internal static Brep CreateSegmentedPlanarCap(IReadOnlyList<IReadOnlyList<Curve>> bottomLoops, double bottom, double tolerance,
        out int[][] edgeIndices)
    {
        ArgumentNullException.ThrowIfNull(bottomLoops);
        if (bottomLoops.Count == 0 || !double.IsFinite(bottom) || !double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentException("근접 바닥의 경계 또는 높이가 유효하지 않습니다.");

        var bounds = BoundingBox.Empty;
        var areas = new double[bottomLoops.Count];
        for (int i = 0; i < bottomLoops.Count; i++)
        {
            var sources = bottomLoops[i];
            if (sources is null || sources.Count == 0 || sources.Any(source => source is null || !source.IsValid))
                throw new ArgumentException("근접 바닥의 원본 경계는 유효한 곡선의 닫힌 고리여야 합니다.");
            var anchor = sources[0].PointAtStart;
            for (int segment = 0; segment < sources.Count; segment++)
            {
                var source = sources[segment];
                var next = sources[(segment + 1) % sources.Count];
                if (source.PointAtEnd.DistanceTo(next.PointAtStart) > tolerance)
                    throw new ArgumentException("근접 바닥의 원본 경계 구간이 닫힌 고리로 연결되지 않습니다.");
                var box = source.GetBoundingBox(true);
                if (!box.IsValid || Math.Abs(box.Min.Z - bottom) > tolerance || Math.Abs(box.Max.Z - bottom) > tolerance)
                    throw new ArgumentException("근접 바닥의 원본 경계 높이가 일치하지 않습니다.");
                bounds.Union(box);
                // Enclose each source curve's control hull; never concatenate
                // the loop into a new curve that must be split again at walls.
                using var nurbs = source.ToNurbsCurve()
                    ?? throw new InvalidOperationException("근접 바닥 경계의 NURBS 제어 영역을 읽을 수 없습니다.");
                foreach (var point in nurbs.Points) bounds.Union(point.Location);
                areas[i] += SignedAreaIntegral(source, anchor);
            }
            if (!double.IsFinite(areas[i]) || areas[i] == 0)
                throw new InvalidOperationException("근접 바닥의 원본 경계가 면적을 이루지 않습니다.");
        }
        if (areas.Count(area => area > 0) != 1 || bounds.Max.X <= bounds.Min.X || bounds.Max.Y <= bounds.Min.Y)
            throw new InvalidOperationException("근접 바닥은 하나의 외곽 경계와 내부 경계로 구성되어야 합니다.");

        edgeIndices = bottomLoops.Select(loop => new int[loop.Count]).ToArray();
        var cap = new Brep();
        try
        {
            // World XY is the UV parameterization. Keeping the original domains
            // avoids fitting a second boundary to the wall's lower NURBS curve.
            using var plane = new PlaneSurface(new Plane(new Point3d(0, 0, bottom), Vector3d.ZAxis),
                new Interval(bounds.Min.X, bounds.Max.X), new Interval(bounds.Min.Y, bounds.Max.Y));
            var face = cap.Faces.Add(cap.AddSurface(plane));
            foreach (int index in Enumerable.Range(0, bottomLoops.Count).OrderByDescending(i => areas[i] > 0))
            {
                var sources = bottomLoops[index];
                var loop = cap.Loops.Add(areas[index] > 0 ? BrepLoopType.Outer : BrepLoopType.Inner, face);
                var vertices = new BrepVertex[sources.Count];
                for (int segment = 0; segment < sources.Count; segment++)
                {
                    var start = sources[segment].PointAtStart;
                    var previousEnd = sources[(segment + sources.Count - 1) % sources.Count].PointAtEnd;
                    var previousOnPlane = new Point3d(previousEnd.X, previousEnd.Y, bottom);
                    double error = Math.Max(start.DistanceTo(previousEnd),
                        Math.Max(start.DistanceTo(previousOnPlane), Math.Abs(start.Z - bottom)));
                    vertices[segment] = cap.Vertices.Add(start, error);
                }
                for (int segment = 0; segment < sources.Count; segment++)
                {
                    var source = sources[segment];
                    using var uv = source.DuplicateCurve();
                    if (!uv.ChangeDimension(2))
                        throw new InvalidOperationException("근접 바닥의 원본 경계를 평면 매개변수로 옮길 수 없습니다.");
                    int curve3d = cap.Curves3D.Add(source);
                    int curve2d = cap.Curves2D.Add(uv);
                    if (curve3d < 0 || curve2d < 0)
                        throw new InvalidOperationException("근접 바닥의 원본 경계를 복사할 수 없습니다.");
                    var box = source.GetBoundingBox(true);
                    double heightError = Math.Max(Math.Abs(box.Min.Z - bottom), Math.Abs(box.Max.Z - bottom));
                    var edge = cap.Edges.Add(vertices[segment], vertices[(segment + 1) % sources.Count], curve3d, heightError);
                    edgeIndices[index][segment] = edge.EdgeIndex;
                    var trim = cap.Trims.Add(edge, false, loop, curve2d);
                    trim.TrimType = BrepTrimType.Boundary;
                    trim.SetTolerances(0, 0);
                }
            }
            cap.SetTrimIsoFlags();
            cap.Flip();
            if (!cap.IsValidWithLog(out var validity) || cap.Faces.Count != 1 || cap.Loops.Count != bottomLoops.Count)
                throw new InvalidOperationException("근접 바닥의 원본 경계 연결이 유효하지 않습니다: " + validity);
            return cap;
        }
        catch { cap.Dispose(); throw; }
    }

    internal sealed record DirectedEdge(NearCapBoundary Source, bool Reversed)
    {
        public int StartVertex => Reversed ? Source.EndVertex : Source.StartVertex;
        public int EndVertex => Reversed ? Source.StartVertex : Source.EndVertex;
        public Curve Copy()
        {
            var curve = Source.Profile.DuplicateCurve();
            if (Reversed && !curve.Reverse()) { curve.Dispose(); throw new InvalidOperationException("바닥 경계 방향을 맞출 수 없습니다."); }
            return curve;
        }
    }

    internal static IReadOnlyList<DirectedEdge[]> OrderedLoops(Brep owner, IReadOnlyList<NearCapBoundary> edges)
    {
        // Projection can bring separate boundary fans to the same bottom
        // vertex. Preserve the shell's face adjacency there: a vertex's degree
        // alone cannot identify which two boundary edges belong to one loop.
        var byEdge = edges.Select((edge, index) => (edge.SourceEdgeIndex, index))
            .ToDictionary(pair => pair.SourceEdgeIndex, pair => pair.index);
        var trims = edges.Select(source =>
        {
            var edge = owner.Edges[source.SourceEdgeIndex];
            var indices = edge.TrimIndices();
            if (edge.Valence != EdgeAdjacency.Naked || indices.Length != 1)
                throw new InvalidOperationException("근접 바닥 경계에 하나의 원본 면이 대응해야 합니다.");
            return owner.Trims[indices[0]];
        }).ToArray();
        var sourceForward = edges.Select((source, index) =>
        {
            var trim = trims[index];
            if (source.StartVertex == Start(trim) && source.EndVertex == End(trim)) return true;
            if (source.StartVertex == End(trim) && source.EndVertex == Start(trim)) return false;
            throw new InvalidOperationException("근접 바닥 경계와 원본 정점의 대응이 일치하지 않습니다.");
        }).ToArray();
        var visited = new bool[edges.Count]; var loops = new List<DirectedEdge[]>();
        for (int first = 0; first < edges.Count; first++)
        {
            if (visited[first]) continue;
            var loop = new List<DirectedEdge>();
            int current = first; bool forward = sourceForward[first];
            do
            {
                if (visited[current])
                    throw new InvalidOperationException("근접 바닥의 원본 면 연결이 다른 경계 고리에 합류합니다.");
                visited[current] = true;
                loop.Add(new(edges[current], forward != sourceForward[current]));
                var trim = trims[current];
                int vertex = forward ? End(trim) : Start(trim);
                var fan = new HashSet<(int Trim, bool Forward)>();
                while (true)
                {
                    if (!fan.Add((trim.TrimIndex, forward)))
                        throw new InvalidOperationException("근접 바닥 정점의 원본 면 연결에서 다음 경계를 찾을 수 없습니다.");
                    var siblings = trim.Loop.Trims.ToArray();
                    int position = Array.FindIndex(siblings, item => item.TrimIndex == trim.TrimIndex);
                    if (position < 0)
                        throw new InvalidOperationException("근접 바닥 정점의 원본 절단 고리를 찾을 수 없습니다.");
                    var next = siblings[(position + (forward ? 1 : siblings.Length - 1)) % siblings.Length];
                    var edge = next.Edge;
                    if (edge is null || (forward ? Start(next) : End(next)) != vertex)
                        throw new InvalidOperationException("근접 바닥 정점의 원본 면 연결이 끊어졌습니다.");
                    if (edge.Valence == EdgeAdjacency.Naked)
                    {
                        if (!byEdge.TryGetValue(edge.EdgeIndex, out current))
                            throw new InvalidOperationException("근접 바닥 고리에 포함되지 않은 열린 경계가 있습니다.");
                        break;
                    }
                    var adjacent = edge.TrimIndices();
                    if (edge.Valence != EdgeAdjacency.Interior || adjacent.Length != 2)
                        throw new InvalidOperationException("근접 바닥 정점의 면 연결이 다양체를 이루지 않습니다.");
                    int other = adjacent[0] == next.TrimIndex ? adjacent[1] :
                        adjacent[1] == next.TrimIndex ? adjacent[0] : -1;
                    if (other < 0)
                        throw new InvalidOperationException("근접 바닥 정점의 인접 절단 경계를 찾을 수 없습니다.");
                    // Cross the shared edge in the opposite direction, then
                    // continue around the same vertex on its other face.
                    bool alongEdge = forward != next.IsReversed();
                    trim = owner.Trims[other];
                    forward = !alongEdge != trim.IsReversed();
                }
            } while (current != first);
            if (forward != sourceForward[first])
                throw new InvalidOperationException("근접 바닥의 원본 경계 고리가 반대 방향으로 되돌아왔습니다.");
            loops.Add(loop.ToArray());
        }
        return loops;

        static int Start(BrepTrim trim) => trim.IsReversed() ? trim.Edge.EndVertex.VertexIndex : trim.Edge.StartVertex.VertexIndex;
        static int End(BrepTrim trim) => trim.IsReversed() ? trim.Edge.StartVertex.VertexIndex : trim.Edge.EndVertex.VertexIndex;
    }

    internal static double SignedAreaIntegral(Curve curve, Point3d anchor)
    {
        // Green's theorem is exact for the source's line/polyline/circular-arc
        // boundaries and does not need native region reconstruction. Translating
        // the integral near the loop reduces cancellation for very small holes.
        if (curve.TryGetPolyline(out var polyline))
        {
            double sum = 0;
            for (int i = 1; i < polyline.Count; i++)
            {
                var a = polyline[i - 1] - anchor; var b = polyline[i] - anchor;
                sum += a.X * b.Y - a.Y * b.X;
            }
            return sum;
        }
        if (curve.TryGetArc(out var arc, 1e-9))
        {
            var center = arc.Center - anchor;
            var delta = curve.PointAtEnd - curve.PointAtStart;
            return center.X * delta.Y - center.Y * delta.X +
                arc.Plane.ZAxis.Z * arc.Radius * arc.Radius * arc.Angle;
        }
        if (curve.TryGetCircle(out var circle, 1e-9))
        {
            var radial = curve.PointAtStart - circle.Center;
            double sign = Math.Sign(Vector3d.CrossProduct(radial, curve.TangentAtStart).Z);
            return sign * 2 * Math.PI * circle.Radius * circle.Radius;
        }
        if (curve is PolyCurve polycurve)
        {
            var segments = polycurve.DuplicateSegments();
            try { return segments.Sum(segment => SignedAreaIntegral(segment, anchor)); }
            finally { foreach (var segment in segments) segment.Dispose(); }
        }
        // Joined profiles can contain very short knot intervals next to long
        // ones. Subdividing those original parameters amplifies floating-point
        // cancellation instead of improving the integral. Extract exact Bezier
        // spans and evaluate each in local coordinates on [0,1]. This changes
        // neither the boundary nor the integration tolerance.
        using var nurbs = curve.ToNurbsCurve()
            ?? throw new InvalidOperationException("실제 NURBS 경계의 면적을 계산할 수 없습니다.");
        double total = 0;
        // ConvertSpanToBezier indexes the control-point knot spans, including
        // repeated knots; Curve.SpanCount counts only nonempty spans.
        for (int span = 0; span <= nurbs.Points.Count - nurbs.Order; span++)
        {
            if (nurbs.Knots[span + nurbs.Order - 2] == nurbs.Knots[span + nurbs.Order - 1]) continue;
            using var bezier = nurbs.ConvertSpanToBezier(span)
                ?? throw new InvalidOperationException("NURBS 경계의 구간을 읽을 수 없습니다.");
            using var local = bezier.ToNurbsCurve();
            local.Translate(-anchor.X, -anchor.Y, -anchor.Z);
            local.Domain = new Interval(0, 1);
            total += Integrate(local, 0, 1, 0);
        }
        return total;

        static double Integrate(Curve local, double a, double b, int depth)
        {
            double whole = Gauss(local, a, b), middle = (a + b) / 2;
            double refined = Gauss(local, a, middle) + Gauss(local, middle, b);
            if (depth >= 16 || Math.Abs(whole - refined) <= 1e-11 * Math.Max(1, Math.Abs(refined))) return refined;
            return Integrate(local, a, middle, depth + 1) + Integrate(local, middle, b, depth + 1);
        }
        static double Gauss(Curve local, double a, double b)
        {
            double middle = (a + b) / 2, half = (b - a) / 2;
            return half * (0.568888888888889 * Value(local, middle) +
                0.478628670499366 * (Value(local, middle - half * .538469310105683) + Value(local, middle + half * .538469310105683)) +
                0.236926885056189 * (Value(local, middle - half * .906179845938664) + Value(local, middle + half * .906179845938664)));
        }
        static double Value(Curve local, double parameter)
        {
            var derivatives = local.DerivativeAt(parameter, 1);
            if (derivatives is not { Length: >= 2 })
                throw new InvalidOperationException("실제 NURBS 경계의 면적을 계산할 수 없습니다.");
            return derivatives[0].X * derivatives[1].Y - derivatives[0].Y * derivatives[1].X;
        }
    }
}
