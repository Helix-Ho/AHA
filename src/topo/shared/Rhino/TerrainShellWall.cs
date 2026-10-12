using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>One ruled support with the original top-edge segmentation retained in its trims.</summary>
internal sealed class TerrainShellWall : IDisposable
{
    internal Brep Geometry { get; }
    internal int[] UpperEdges { get; }
    internal int[] LowerEdges { get; }

    internal TerrainShellWall(IReadOnlyList<Curve> upper, double bottom)
    {
        using var composite = new PolyCurve();
        foreach (var curve in upper)
            if (!composite.AppendSegment(curve))
                throw new InvalidOperationException("근접 외벽의 원본 경계 구간을 연결할 수 없습니다.");
        using var profile = composite.ToNurbsCurve()
            ?? throw new InvalidOperationException("근접 외벽의 지지 곡선을 만들 수 없습니다.");
        using var support = TerrainBoundaryGeometry.VerticalWall(profile, _ => bottom);
        var wall = new Brep();
        try
        {
            var face = wall.Faces.Add(wall.AddSurface(support.Surfaces[0]));
            var loop = wall.Loops.Add(BrepLoopType.Outer, face);
            int count = upper.Count;
            UpperEdges = new int[count]; LowerEdges = new int[count];
            var highs = upper.Select(c => wall.Vertices.Add(c.PointAtStart, 0)).ToArray();
            var lows = upper.Select(c => wall.Vertices.Add(new Point3d(c.PointAtStart.X, c.PointAtStart.Y, bottom), 0)).ToArray();
            var intervals = Enumerable.Range(0, count).Select(composite.SegmentDomain).ToArray();
            var lower = new Curve[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    lower[i] = upper[i].DuplicateCurve();
                    lower[i].Transform(Transform.PlanarProjection(new Plane(new Point3d(0, 0, bottom), Vector3d.ZAxis)));
                    LowerEdges[i] = Edge(lower[i], lows[i], lows[(i + 1) % count]);
                    UpperEdges[i] = Edge(upper[i], highs[i], highs[(i + 1) % count]);
                    Trim(LowerEdges[i], false, intervals[i].Min, 0, intervals[i].Max, 0);
                }
                using var seam = new LineCurve(lows[0].Location, highs[0].Location);
                int seamEdge = Edge(seam, lows[0], highs[0]);
                Trim(seamEdge, false, profile.Domain.Max, 0, profile.Domain.Max, 1, true);
                for (int i = count - 1; i >= 0; i--)
                    Trim(UpperEdges[i], true, intervals[i].Max, 1, intervals[i].Min, 1);
                Trim(seamEdge, true, profile.Domain.Min, 1, profile.Domain.Min, 0, true);
            }
            finally { foreach (var curve in lower) curve?.Dispose(); }
            wall.SetTrimIsoFlags();
            if (!wall.IsValidWithLog(out var validity))
                throw new InvalidOperationException("근접 외벽의 원본 변 대응이 유효하지 않습니다: " + validity);
            Geometry = wall;

            int Edge(Curve curve, BrepVertex start, BrepVertex end) =>
                wall.Edges.Add(start, end, wall.Curves3D.Add(curve), 0).EdgeIndex;
            void Trim(int edge, bool reverse, double u0, double v0, double u1, double v1, bool seam = false)
            {
                using var uv = new LineCurve(new Point2d(u0, v0), new Point2d(u1, v1));
                var trim = wall.Trims.Add(wall.Edges[edge], reverse, loop, wall.Curves2D.Add(uv));
                trim.TrimType = seam ? BrepTrimType.Seam : BrepTrimType.Boundary;
                trim.SetTolerances(0, 0);
            }
        }
        catch { wall.Dispose(); throw; }
    }

    public void Dispose() => Geometry.Dispose();
}
