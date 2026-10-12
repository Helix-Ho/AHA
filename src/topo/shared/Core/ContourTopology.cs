using Clipper2Lib;

namespace Aha.Topo;

/// <summary>A conforming, height-bounded linearization owned by one contour operation.</summary>
internal sealed class ContourTopology
{
    internal readonly record struct Vertex(double X, double Y, double Z);
    internal readonly record struct Triangle(int A, int B, int C, long First, long Last);
    internal sealed class Edge(int a, int b, int owner)
    {
        internal readonly int A = a, B = b, Owner = owner;
        internal int Uses = 1;
    }
    private readonly record struct Cell(int Column, int Row, int Count);
    internal readonly List<Vertex> Points = new();
    internal readonly List<Triangle> Triangles = new();
    internal readonly Dictionary<(int A, int B), Edge> Edges = new();
    internal double Minimum { get; private set; } = double.PositiveInfinity;
    internal double Maximum { get; private set; } = double.NegativeInfinity;
    internal double HeightError { get; }
    internal long ActiveVisits { get; private set; }
    internal static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);
    private static long Q(double value) => checked((long)Math.Round(value / ModelPlanPolicy.PrecisionMetres));
    private static double M(long value) => value * ModelPlanPolicy.PrecisionMetres;

    internal ContourTopology(TerrainField field, double interval, PathsD region, CancellationToken ct)
    {
        HeightError = Math.Min(.01, interval * .01);
        var bounds = Clipper.GetBounds(region);
        var normalized=ContourPaths.UnionGeometry(region);
        // Preserve each component's original rectangular section domain. Cutting
        // it to the curved Near perimeter here would introduce new endpoints
        // into the subsequent small-detail cleanup.
        var windows=normalized.Factory.CreateGeometryCollection(ContourPaths.Polygons(normalized)
            .Select(p=>normalized.Factory.ToGeometry(p.EnvelopeInternal)).ToArray());
        var domain = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(windows);
        var cells = new List<Cell>();
        var vertical = new Dictionary<long, SortedSet<long>>();
        var horizontal = new Dictionary<long, SortedSet<long>>();
        for (int row = 0; row < field.Rows; row++)
            for (int col = 0; col < field.Columns; col++)
            {
                ct.ThrowIfCancellationRequested();
                double x0 = field.ModelX(col), x1 = field.ModelX(col + 1), y0 = field.ModelY(row), y1 = field.ModelY(row + 1);
                if (x1 < bounds.left - .002 || x0 > bounds.right + .002 || y1 < bounds.top - .002 || y0 > bounds.bottom + .002) continue;
                // A bounding rectangle may span large Far gaps between Near islands.
                // Retain the restoration margin, but only linearize contributing cells.
                var query = domain.Geometry.Factory.ToGeometry(new NetTopologySuite.Geometries.Envelope(
                    Q(x0 - .002), Q(x1 + .002), Q(y0 - .002), Q(y1 + .002)));
                if (!domain.Intersects(query)) continue;
                double a = field.Node(col, row), b = field.Node(col + 1, row), c = field.Node(col + 1, row + 1), d = field.Node(col, row + 1);
                Minimum = Math.Min(Minimum, Math.Min(Math.Min(a, b), Math.Min(c, d)));
                Maximum = Math.Max(Maximum, Math.Max(Math.Max(a, b), Math.Max(c, d)));
                cells.Add(new(col, row, Subdivisions(col,row)));
            }
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum)) throw new ArgumentException("Contour 영역에 지형 표본이 없습니다.");
        if (Math.Max(Math.Abs(Minimum / interval), Math.Abs(Maximum / interval)) > 9e15)
            throw new ArgumentException("Contour 간격으로 현재 높이 범위를 표현할 수 없습니다. 간격을 조정하세요.");
        // Adjacent Far cells can contribute subdivision stations to a shared
        // edge even though their triangles and heights are not output. Keep
        // those stations so pruning Far does not change the Near linearization.
        var stationCells=cells.ToDictionary(c=>(c.Column,c.Row));
        foreach(var cell in cells)
        foreach(var (col,row) in new[]{(cell.Column-1,cell.Row),(cell.Column+1,cell.Row),
            (cell.Column,cell.Row-1),(cell.Column,cell.Row+1)})
        {
            if(col<0||row<0||col>=field.Columns||row>=field.Rows||stationCells.ContainsKey((col,row)))continue;
            double x0=field.ModelX(col),x1=field.ModelX(col+1),y0=field.ModelY(row),y1=field.ModelY(row+1);
            if(x1<bounds.left-.002||x0>bounds.right+.002||y1<bounds.top-.002||y0>bounds.bottom+.002)continue;
            stationCells.Add((col,row),new(col,row,Subdivisions(col,row)));
        }
        // Only cell-boundary stations are allocated before the exact conforming count.
        foreach (var cell in stationCells.Values)
        {
            ct.ThrowIfCancellationRequested();
            double x0 = field.ModelX(cell.Column), x1 = field.ModelX(cell.Column + 1), y0 = field.ModelY(cell.Row), y1 = field.ModelY(cell.Row + 1);
            for (int i = 0; i <= cell.Count; i++)
            {
                long x = Q(x0 + (x1 - x0) * i / cell.Count), y = Q(y0 + (y1 - y0) * i / cell.Count);
                Station(vertical, Q(x0), y); Station(vertical, Q(x1), y);
                Station(horizontal, Q(y0), x); Station(horizontal, Q(y1), x);
            }
        }
        long predicted = 0, predictedVisits = 0;
        // The same traversal counts before allocating vertices, edges or event arrays.
        var nodes = new Dictionary<(long, long), int>();
        VisitCells(false);
        // Capacity is a CLR representation constraint, not an arbitrary work budget.
        Triangles.Capacity = checked((int)predicted);
        VisitCells(true);
        if (Triangles.Count != predicted || ActiveVisits != predictedVisits || Edges.Values.Any(e => e.Uses > 2))
            throw new InvalidOperationException("Contour 공유 위상과 계산량 예측이 일치하지 않습니다.");

        void VisitCells(bool build)
        {
            foreach (var cell in cells)
            {
                ct.ThrowIfCancellationRequested();
                double x0 = field.ModelX(cell.Column), x1 = field.ModelX(cell.Column + 1), y0 = field.ModelY(cell.Row), y1 = field.ModelY(cell.Row + 1);
                Vertex Point(int u, int v)
                {
                    double x = M(Q(x0 + (x1 - x0) * u / cell.Count)), y = M(Q(y0 + (y1 - y0) * v / cell.Count));
                    return new(x, y, field.SampleModelLocal(x, y));
                }
                for (int v = 0; v < cell.Count; v++)
                    for (int u = 0; u < cell.Count; u++)
                    {
                        var a = Point(u, v); var b = Point(u + 1, v); var c = Point(u + 1, v + 1); var d = Point(u, v + 1);
                        Split(a, b, c); Split(a, c, d);
                    }
                void Split(Vertex a, Vertex b, Vertex c)
                {
                    var ring = new List<Vertex>(3); Side(a, b); Side(b, c); Side(c, a);
                    if (ring.Count == 3) Add(a, b, c);
                    else
                    {
                        // Interior fan point stays on the existing linear triangle.
                        var center = new Vertex((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                        int centerId = build ? Points.Count : -1;
                        if (build) Points.Add(center);
                        for (int i = 0; i < ring.Count; i++) Add(ring[i], ring[(i + 1) % ring.Count], center, centerId);
                    }
                    void Side(Vertex from, Vertex to)
                    {
                        ring.Add(from);
                        long px = Q(from.X), py = Q(from.Y), qx = Q(to.X), qy = Q(to.Y);
                        if (px == qx && vertical.TryGetValue(px, out var ys))
                            foreach (long y in Ordered(ys, py, qy)) ring.Add(new(M(px), M(y), field.SampleModelLocal(M(px), M(y))));
                        else if (py == qy && horizontal.TryGetValue(py, out var xs))
                            foreach (long x in Ordered(xs, px, qx)) ring.Add(new(M(x), M(py), field.SampleModelLocal(M(x), M(py))));
                    }
                }
                void Add(Vertex a, Vertex b, Vertex c, int centerId = -1)
                {
                    if (((build ? Triangles.Count : predicted) & 255) == 0) ct.ThrowIfCancellationRequested();
                    long first = (long)Math.Ceiling(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / interval);
                    long last = (long)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / interval);
                    long visits = Math.Max(0, last - first + 1);
                    if (!build)
                    {
                        predicted = checked(predicted + 1); predictedVisits = checked(predictedVisits + visits);
                        return;
                    }
                    int ai = Node(a), bi = Node(b), ci = centerId >= 0 ? centerId : Node(c), id = Triangles.Count;
                    Triangles.Add(new(ai, bi, ci, first, last)); ActiveVisits += visits;
                    EdgeUse(ai, bi, id); EdgeUse(bi, ci, id); EdgeUse(ci, ai, id);
                }
            }
        }
        int Node(Vertex point)
        {
            var key = (Q(point.X), Q(point.Y));
            if (nodes.TryGetValue(key, out int id)) return id;
            id = Points.Count; nodes.Add(key, id); Points.Add(point); return id;
        }
        void EdgeUse(int a, int b, int owner)
        {
            var key = Pair(a, b);
            if (Edges.TryGetValue(key, out var edge)) edge.Uses++;
            else Edges.Add(key, new(a, b, owner));
        }
        static IEnumerable<long> Ordered(SortedSet<long> values, long start, long end)
        {
            var range = values.GetViewBetween(Math.Min(start, end), Math.Max(start, end)).Where(v => v != start && v != end);
            return start < end ? range : range.Reverse();
        }
        static void Station(Dictionary<long, SortedSet<long>> map, long at, long value)
        { if (!map.TryGetValue(at, out var set)) map[at] = set = new(); set.Add(value); }
        int Subdivisions(int col,int row)
        {
            double mixed=field.Node(col,row)-field.Node(col+1,row)+field.Node(col+1,row+1)-field.Node(col,row+1);
            double count=Math.Max(1,Math.Ceiling(Math.Sqrt(Math.Abs(mixed)/(4*HeightError))));
            if(!double.IsFinite(count)||count>=int.MaxValue||
                (field.ModelX(col+1)-field.ModelX(col))/count<ModelPlanPolicy.PrecisionMetres||
                (field.ModelY(row+1)-field.ModelY(row))/count<ModelPlanPolicy.PrecisionMetres)
                throw new ArgumentException("Contour 높이 변화를 현재 좌표 정밀도로 표현할 수 없습니다.");
            return (int)count;
        }
    }
}
