using Clipper2Lib;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Polygonize;

namespace Aha.Topo;

/// <summary>Sequential superlevel boundaries from shared edge intersections.</summary>
internal sealed class ContourLevels
{
    private readonly ContourTopology topology;
    private readonly double interval;
    private readonly CancellationToken cancellation;
    private readonly (long Level, int Triangle)[] entries, exits;
    private readonly ContourTopology.Edge[] outer;
    private readonly HashSet<int> active = new();
    private int entry, exit;
    private long previous = long.MinValue;
    private readonly record struct GridPoint(long X, long Y) : IComparable<GridPoint>
    {
        public int CompareTo(GridPoint other) => X != other.X ? X.CompareTo(other.X) : Y.CompareTo(other.Y);
        internal Coordinate Coordinate => new(X, Y);
        internal static GridPoint From(double x, double y)
        { var key = PlanarTriangulation.GridKey(new(x, y)); return new(key.X, key.Y); }
    }
    public double MaximumHeightError => topology.HeightError;
    public double Minimum => topology.Minimum;
    public double Maximum => topology.Maximum;

    internal ContourLevels(TerrainField field, double interval, PathsD region, CancellationToken ct = default)
    {
        if (!double.IsFinite(interval) || interval <= 0) throw new ArgumentException("Contour 간격은 양수여야 합니다.");
        this.interval = interval; cancellation = ct;
        topology = new(field, interval, region, ct);
        outer = topology.Edges.Values.Where(e => e.Uses == 1).ToArray();
        entries = topology.Triangles.Select((t, i) => (t, i)).Where(p => p.t.First <= p.t.Last)
            .Select(p => (p.t.First, p.i)).OrderBy(p => p.First).ToArray();
        exits = topology.Triangles.Select((t, i) => (t, i)).Where(p => p.t.First <= p.t.Last)
            .Select(p => (p.t.Last + 1, p.i)).OrderBy(p => p.Item1).ToArray();
    }

    public PathsD At(long level)
    {
        cancellation.ThrowIfCancellationRequested();
        if (level < previous) throw new ArgumentException("Contour 단면은 높이 순서로 요청해야 합니다.");
        previous = level;
        while (entry < entries.Length && entries[entry].Level <= level) active.Add(entries[entry++].Triangle);
        while (exit < exits.Length && exits[exit].Level <= level) active.Remove(exits[exit++].Triangle);
        double height = level * interval;
        var crossings = new Dictionary<(int, int), GridPoint>();
        var segments = new Dictionary<(GridPoint A, GridPoint B), int>();
        var points = topology.Points;
        foreach (int id in active)
        {
            cancellation.ThrowIfCancellationRequested();
            var t = topology.Triangles[id];
            var ids = new[] { t.A, t.B, t.C };
            // A non-flat triangle touching the level only at its maximum has zero area.
            if (ids.All(i => points[i].Z <= height) && ids.Any(i => points[i].Z < height)) continue;
            var ring = new List<(GridPoint Point, int Source, bool OnLevel)>(4);
            for (int i = 0; i < 3; i++)
            {
                int a = ids[i], b = ids[(i + 1) % 3]; bool inside = points[a].Z >= height, next = points[b].Z >= height;
                if (inside) ring.Add((Position(a), a, points[a].Z == height));
                if (inside != next)
                {
                    int source = points[a].Z == height ? a : points[b].Z == height ? b : -1;
                    ring.Add((Cross(a, b), source, true));
                }
            }
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                if (!a.OnLevel || !b.OnLevel) continue;
                if (a.Source >= 0 && b.Source >= 0 && a.Source != b.Source &&
                    topology.Edges[ContourTopology.Pair(a.Source, b.Source)].Uses == 1) continue;
                Segment(a.Point, b.Point);
            }
        }
        foreach (var edge in outer)
        {
            cancellation.ThrowIfCancellationRequested();
            int a = edge.A, b = edge.B;
            var t = topology.Triangles[edge.Owner];
            if (Math.Max(points[t.A].Z, Math.Max(points[t.B].Z, points[t.C].Z)) == height &&
                Math.Min(points[t.A].Z, Math.Min(points[t.B].Z, points[t.C].Z)) < height) continue;
            bool p = points[a].Z >= height, q = points[b].Z >= height;
            if (p && q) Segment(Position(a), Position(b));
            else if (p) Segment(Position(a), Cross(a, b));
            else if (q) Segment(Cross(a, b), Position(b));
        }
        return Polygons(segments, cancellation);

        GridPoint Position(int i) => GridPoint.From(points[i].X, points[i].Y);
        GridPoint Cross(int a, int b)
        {
            if (points[a].Z == height) return Position(a);
            if (points[b].Z == height) return Position(b);
            var key = ContourTopology.Pair(a, b);
            if (crossings.TryGetValue(key, out var found)) return found;
            var p = points[key.Item1]; var q = points[key.Item2]; double fraction = (height - p.Z) / (q.Z - p.Z);
            var result = GridPoint.From(p.X + fraction * (q.X - p.X), p.Y + fraction * (q.Y - p.Y));
            crossings.Add(key, result); return result;
        }
        void Segment(GridPoint a, GridPoint b)
        {
            if (a == b) return;
            var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            int count = segments.GetValueOrDefault(key) + (key.Item1 == a ? 1 : -1);
            if (count == 0) segments.Remove(key); else segments[key] = count;
        }
    }

    private static PathsD Polygons(Dictionary<(GridPoint A, GridPoint B), int> segments, CancellationToken ct)
    {
        if (segments.Count == 0) return new();
        var balance = new Dictionary<GridPoint, int>();
        var factory = new GeometryFactory();
        var index = new STRtree<(Coordinate A, Coordinate B, int Count)>();
        var lines = new List<LineString>(segments.Count);
        foreach (var (edge, count) in segments)
        {
            balance[edge.A] = balance.GetValueOrDefault(edge.A) + count;
            balance[edge.B] = balance.GetValueOrDefault(edge.B) - count;
            var a = edge.A.Coordinate; var b = edge.B.Coordinate;
            lines.Add(factory.CreateLineString(new[] { a, b }));
            index.Insert(new Envelope(a, b), (a, b, count));
        }
        if (balance.Values.Any(value => value != 0)) throw new InvalidOperationException("Contour 공유 변의 방향 연결이 닫히지 않았습니다.");
        ct.ThrowIfCancellationRequested();
        // Quantization may put an intersection on another segment. Node once
        // before polygonization; discarding small segments earlier breaks loops.
        var noded = UnaryUnionNG.Union((Geometry)factory.CreateMultiLineString(lines.ToArray()), new PrecisionModel(1));
        var polygonizer = new Polygonizer(); polygonizer.Add(noded); index.Build();
        var polygons = polygonizer.GetPolygons();
        // Closed oriented chains can collapse to retraced lines on the grid.
        // They have zero winding/area; only polygonal faces carry material.
        if (polygonizer.GetInvalidRingLines().Count != 0)
            throw new InvalidOperationException("Contour 공유 단면의 정밀도 격자 연결이 유효하지 않습니다.");
        var selected = new PathsD();
        foreach (Polygon polygon in polygons)
        {
            ct.ThrowIfCancellationRequested();
            var p = polygon.InteriorPoint.Coordinate;
            int winding = 0;
            foreach (var e in index.Query(new Envelope(p.X, double.MaxValue, p.Y, p.Y)))
            {
                var orientation = Orientation.Index(e.A, e.B, p);
                if (e.A.Y <= p.Y && p.Y < e.B.Y && orientation == OrientationIndex.CounterClockwise) winding += e.Count;
                if (e.B.Y <= p.Y && p.Y < e.A.Y && orientation == OrientationIndex.Clockwise) winding -= e.Count;
            }
            if (winding != 0) selected.AddRange(PlanarTriangulation.FromGridPolygon(polygon));
        }
        return ContourPaths.Union(selected);
    }
}
