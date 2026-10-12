using Rhino.Geometry;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using System.Numerics;
using NetTopologySuite.Triangulate.Tri;

namespace Aha.Topo;

public readonly record struct SurfaceTriangle(Point2d A, Point2d B, Point2d C, int PartId);

/// <summary>
/// Builds the shared triangulation of one source DEM cell. Shared edges are split
/// once for every incident part. The triangles are either direct mesh geometry
/// or NURBS render caches, depending on the requested output representation.
/// </summary>
public static class PlanarSurfaceMesh
{
    public const double BoundaryHeightToleranceMetres = 0.001;
    public const double InteriorHeightToleranceMetres = 0.05;
    private const double CoordinateTolerance = 0.000001;

    public static List<SurfaceTriangle> ConformAndRefine(IEnumerable<SurfaceTriangle> source,
        TerrainField field,
        IEnumerable<Point2d>? boundaryConstraints = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(field);
        var triangles = source.Select(t => Orient(new(Canonical(t.A), Canonical(t.B), Canonical(t.C), t.PartId))).ToList();
        if (triangles.Count == 0) return triangles;
        ValidateSingleCell(triangles, field);
        // Parts separate land/road, optional visibility zones and individual Brep faces.
        // Part changes therefore include every shared display boundary.
        triangles = RestoreBoundaryVertices(triangles, boundaryConstraints);
        while (true)
        {
            var edges = EdgeTable(triangles);
            var split = new Dictionary<EdgeKey, Point2d>();
            foreach (var pair in edges)
            {
                var edge = pair.Value;
                var tolerance = edge.Count == 1 || edge.PartA != edge.PartB
                    ? BoundaryHeightToleranceMetres : InteriorHeightToleranceMetres;
                var midpoint = Midpoint(edge.A, edge.B);
                var chordHeight = (field.SampleModelLocal(edge.A.X, edge.A.Y) +
                    field.SampleModelLocal(edge.B.X, edge.B.Y)) * 0.5;
                var error = Math.Abs(field.SampleModelLocal(midpoint.X, midpoint.Y) - chordHeight);
                if (error <= tolerance) continue;
                if (PointKey.Of(midpoint) == PointKey.Of(edge.A) || PointKey.Of(midpoint) == PointKey.Of(edge.B))
                    throw new InvalidOperationException("지형 메시 경계를 현재 좌표 정밀도 안에서 더 분할할 수 없습니다.");
                split.Add(pair.Key, midpoint);
            }
            if (split.Count == 0) return triangles;
            var refined = new List<SurfaceTriangle>(triangles.Count);
            foreach (var triangle in triangles)
            {
                var boundary = new List<Point2d>(6) { triangle.A };
                AddSplit(triangle.A, triangle.B);
                boundary.Add(triangle.B); AddSplit(triangle.B, triangle.C);
                boundary.Add(triangle.C); AddSplit(triangle.C, triangle.A);
                if (boundary.Count == 3) Add(refined, triangle);
                else InsertBoundaryVertices(triangle, boundary, refined);

                void AddSplit(Point2d a, Point2d b)
                {
                    if (split.TryGetValue(EdgeKey.Of(a, b), out var midpoint)) boundary.Add(midpoint);
                }
            }
            triangles = ImproveInteriorDiagonals(refined);
        }
    }

    private static List<SurfaceTriangle> RestoreBoundaryVertices(List<SurfaceTriangle> triangles,
        IEnumerable<Point2d>? boundaryConstraints)
    {
        var points = triangles.SelectMany(t => new[] { t.A, t.B, t.C })
            .Concat((boundaryConstraints ?? Array.Empty<Point2d>()).Select(Canonical))
            .DistinctBy(PointKey.Of).OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var conformed = new List<SurfaceTriangle>(triangles.Count);
        foreach (var triangle in triangles)
        {
            var boundary = new List<Point2d>();
            Edge(triangle.A, triangle.B); Edge(triangle.B, triangle.C); Edge(triangle.C, triangle.A);
            if (boundary.Count == 3) Add(conformed, triangle);
            else InsertBoundaryVertices(triangle, boundary, conformed);

            void Edge(Point2d a, Point2d b)
            {
                boundary.Add(a);
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lengthSquared = dx * dx + dy * dy;
                double minX = Math.Min(a.X, b.X) - CoordinateTolerance;
                double maxX = Math.Max(a.X, b.X) + CoordinateTolerance;
                var candidates = new List<(double Parameter, Point2d Point)>();
                for (var i = LowerBound(points, minX); i < points.Length && points[i].X <= maxX; i++)
                {
                    var p = points[i];
                    if (PointKey.Of(p) == PointKey.Of(a) || PointKey.Of(p) == PointKey.Of(b)) continue;
                    double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
                    if (t <= 0 || t >= 1) continue;
                    // Clipper's source vertices share one integer grid. Only exact
                    // collinearity can identify a removed boundary vertex; a distance
                    // tolerance would merge distinct, valid narrow source regions.
                    var ak = GridKey.Of(a); var bk = GridKey.Of(b); var pk = GridKey.Of(p);
                    var cross = (Int128)(bk.X - ak.X) * (pk.Y - ak.Y) -
                        (Int128)(bk.Y - ak.Y) * (pk.X - ak.X);
                    if (cross == 0) candidates.Add((t, p));
                }
                foreach (var p in candidates.OrderBy(p => p.Parameter)) boundary.Add(p.Point);
            }
        }
        return conformed.Count == triangles.Count ? conformed : ImproveInteriorDiagonals(conformed);
    }

    private static List<SurfaceTriangle> ImproveInteriorDiagonals(List<SurfaceTriangle> triangles)
    {
        // Re-establish constrained Delaunay diagonals after shared edge insertion.
        // Each Part is linked independently, so cell/road/near/patch boundaries
        // stay constraints and every existing boundary point is retained.
        var result = new List<SurfaceTriangle>(triangles.Count);
        foreach (var group in triangles.GroupBy(triangle => triangle.PartId))
        {
            var linked = group.Select(triangle => Tri.Create(
                new Coordinate(triangle.A.X, triangle.A.Y),
                new Coordinate(triangle.C.X, triangle.C.Y),
                new Coordinate(triangle.B.X, triangle.B.Y))).ToList();
            TriangulationBuilder.Build(linked);
            Improve(linked);
            foreach (var triangle in linked)
            {
                var a = triangle.GetCoordinate(0); var b = triangle.GetCoordinate(2); var c = triangle.GetCoordinate(1);
                Add(result, Orient(new(new(a.X, a.Y), new(b.X, b.Y), new(c.X, c.Y), group.Key)));
            }
        }
        return result;

        static void Improve(List<Tri> linked)
        {
            // Lawson flips affect only two triangles and their neighbours.
            // Exact in-circle signs strictly improve a non-Delaunay diagonal;
            // cocircular ties are retained, so there is no scan-count cutoff.
            var queue = new Queue<Tri>(linked);
            var pending = new HashSet<Tri>(linked);
            while (queue.TryDequeue(out var triangle))
            {
                pending.Remove(triangle);
                for (int edge = 0; edge < 3; edge++)
                {
                    var neighbour = triangle.GetAdjacent(edge);
                    if (neighbour == null) continue;
                    var a = triangle.GetCoordinate(edge);
                    var b = triangle.GetCoordinate((edge + 1) % 3);
                    var c = triangle.GetCoordinate((edge + 2) % 3);
                    int opposite = neighbour.GetIndex(triangle);
                    if (opposite < 0) throw new InvalidOperationException("지형 삼각형의 인접 관계가 일치하지 않습니다.");
                    var d = neighbour.GetCoordinate((opposite + 2) % 3);
                    var first = Orientation.Index(c, d, a); var second = Orientation.Index(c, d, b);
                    if (first == OrientationIndex.Collinear || second == OrientationIndex.Collinear || first == second) continue;
                    // Tri uses clockwise vertices; the in-circle predicate
                    // requires counterclockwise order. Exact cocircularity must
                    // keep the existing diagonal, otherwise flips can cycle.
                    if (!InCircle(a, c, b, d)) continue;
                    triangle.Flip(edge);
                    Schedule(triangle); Schedule(neighbour);
                    for (int i = 0; i < 3; i++) { Schedule(triangle.GetAdjacent(i)); Schedule(neighbour.GetAdjacent(i)); }
                }
            }
            void Schedule(Tri? item) { if (item is not null && pending.Add(item)) queue.Enqueue(item); }
        }
    }

    internal static bool InCircle(Coordinate a, Coordinate b, Coordinate c, Coordinate d)
    {
        double ax = a.X - d.X, ay = a.Y - d.Y, bx = b.X - d.X, by = b.Y - d.Y,
            cx = c.X - d.X, cy = c.Y - d.Y;
        double bc1 = bx * cy, bc2 = cx * by, ca1 = cx * ay, ca2 = ax * cy,
            ab1 = ax * by, ab2 = bx * ay;
        double al = ax * ax + ay * ay, bl = bx * bx + by * by, cl = cx * cx + cy * cy;
        double det = al * (bc1 - bc2) + bl * (ca1 - ca2) + cl * (ab1 - ab2);
        double permanent = (Math.Abs(bc1) + Math.Abs(bc2)) * al
            + (Math.Abs(ca1) + Math.Abs(ca2)) * bl + (Math.Abs(ab1) + Math.Abs(ab2)) * cl;
        // Shewchuk's first-stage in-circle error bound. This is a numerical
        // sign filter, not a geometric tolerance or coordinate adjustment.
        const double epsilon = 1.1102230246251565e-16;
        double bound = (10 + 96 * epsilon) * epsilon * permanent;
        if (double.IsFinite(det) && Math.Abs(det) > bound) return det > 0;

        // Doubles are exact dyadic rationals. Scale their original bit patterns
        // to integers only for uncertain signs; this also resolves exact zero,
        // which extended-precision floating arithmetic cannot always preserve.
        Span<double> values = stackalloc double[] { a.X, a.Y, b.X, b.Y, c.X, c.Y, d.X, d.Y };
        Span<long> mantissas = stackalloc long[8];
        Span<int> exponents = stackalloc int[8];
        int minimum = int.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            long bits = BitConverter.DoubleToInt64Bits(values[i]);
            int exponent = (int)((bits >> 52) & 0x7ff);
            long mantissa = bits & 0x000fffffffffffffL;
            if (exponent != 0) mantissa |= 1L << 52;
            mantissas[i] = bits < 0 ? -mantissa : mantissa;
            exponents[i] = exponent == 0 ? -1074 : exponent - 1075;
            if (mantissa != 0) minimum = Math.Min(minimum, exponents[i]);
        }
        if (minimum == int.MaxValue) return false;
        var exact = new BigInteger[8];
        for (int i = 0; i < exact.Length; i++)
            if (mantissas[i] != 0) exact[i] = new BigInteger(mantissas[i]) << (exponents[i] - minimum);
        var eax = exact[0] - exact[6]; var eay = exact[1] - exact[7];
        var ebx = exact[2] - exact[6]; var eby = exact[3] - exact[7];
        var ecx = exact[4] - exact[6]; var ecy = exact[5] - exact[7];
        return ((eax * eax + eay * eay) * (ebx * ecy - ecx * eby)
            + (ebx * ebx + eby * eby) * (ecx * eay - eax * ecy)
            + (ecx * ecx + ecy * ecy) * (eax * eby - ebx * eay)).Sign > 0;
    }

    private static int LowerBound(Point2d[] values, double x)
    {
        var first = 0; var last = values.Length;
        while (first < last)
        {
            var mid = first + (last - first) / 2;
            if (values[mid].X < x) first = mid + 1; else last = mid;
        }
        return first;
    }

    private static Dictionary<EdgeKey, EdgeUse> EdgeTable(List<SurfaceTriangle> triangles)
    {
        var result = new Dictionary<EdgeKey, EdgeUse>();
        foreach (var triangle in triangles)
        {
            Edge(triangle.A, triangle.B); Edge(triangle.B, triangle.C); Edge(triangle.C, triangle.A);
            void Edge(Point2d a, Point2d b)
            {
                var key = EdgeKey.Of(a, b);
                if (!result.TryGetValue(key, out var edge)) result.Add(key, new EdgeUse(a, b, triangle.PartId));
                else
                {
                    if (edge.Count >= 2 || PointKey.Of(edge.A) == PointKey.Of(a))
                        throw new InvalidOperationException("지형 메시의 평면 분할 경계가 겹치거나 비다양체입니다.");
                    edge.Count++; edge.PartB = triangle.PartId;
                }
            }
        }
        return result;
    }

    private static void InsertBoundaryVertices(SurfaceTriangle source, List<Point2d> boundary, List<SurfaceTriangle> result)
    {
        // Split the incident triangle at each required boundary point. A centroid
        // fan adds unnecessary interior vertices arbitrarily close to a boundary;
        // lifting those needle triangles to a bilinear field can reverse their
        // apparent normals even though the height error is within tolerance.
        var pieces = new List<SurfaceTriangle> { source };
        var corners = new[] { source.A, source.B, source.C };
        for (int side = 0; side < 3; side++)
        {
            var from = corners[side]; var to = corners[(side + 1) % 3];
            int start = boundary.FindIndex(p => PointKey.Of(p) == PointKey.Of(from));
            for (int i = (start + 1) % boundary.Count; PointKey.Of(boundary[i]) != PointKey.Of(to); i = (i + 1) % boundary.Count)
            {
                var point = boundary[i]; bool inserted = false;
                for (int index = 0; index < pieces.Count && !inserted; index++)
                {
                    var triangle = pieces[index]; var p = new[] { triangle.A, triangle.B, triangle.C };
                    for (int edge = 0; edge < 3; edge++)
                    {
                        if (PointKey.Of(p[edge]) != PointKey.Of(from) || PointKey.Of(p[(edge + 1) % 3]) != PointKey.Of(to)) continue;
                        var opposite = p[(edge + 2) % 3];
                        pieces[index] = Orient(new(from, point, opposite, source.PartId));
                        Add(pieces, Orient(new(point, to, opposite, source.PartId)));
                        inserted = true; break;
                    }
                }
                if (!inserted) throw new InvalidOperationException("지형 삼각형에 공유 경계점을 삽입할 수 없습니다.");
                from = point;
            }
        }
        foreach (var triangle in pieces) Add(result, triangle);
    }

    private static void Add(List<SurfaceTriangle> triangles, SurfaceTriangle triangle)
    {
        triangles.Add(triangle);
    }

    private static SurfaceTriangle Orient(SurfaceTriangle triangle)
    {
        var a = triangle.A; var b = triangle.B; var c = triangle.C;
        var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (PointKey.Of(a) == PointKey.Of(b) || PointKey.Of(b) == PointKey.Of(c) || PointKey.Of(c) == PointKey.Of(a) ||
            !double.IsFinite(cross) || cross == 0)
            throw new InvalidOperationException("지형 평면 분할에 퇴화한 삼각형이 포함되어 있습니다.");
        return cross > 0 ? new(a, b, c, triangle.PartId) : new(a, c, b, triangle.PartId);
    }

    private static Point2d Canonical(Point2d point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("지형 메시 좌표가 유한하지 않습니다.");
        return new(Math.Round(point.X, EditRegion.ClipPrecision), Math.Round(point.Y, EditRegion.ClipPrecision));
    }

    private static Point2d Midpoint(Point2d a, Point2d b) => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

    private static void ValidateSingleCell(List<SurfaceTriangle> triangles, TerrainField field)
    {
        var points = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var west = points.Min(p => p.X); var south = points.Min(p => p.Y);
        var east = points.Max(p => p.X); var north = points.Max(p => p.Y);
        var cell = field.ModelCell((west + east) * 0.5, (south + north) * 0.5);
        if (west < field.ModelX(cell.Column) - CoordinateTolerance || east > field.ModelX(cell.Column + 1) + CoordinateTolerance ||
            south < field.ModelY(cell.Row) - CoordinateTolerance || north > field.ModelY(cell.Row + 1) + CoordinateTolerance)
            throw new ArgumentException("지형 메시 분할은 하나의 원본 DEM 격자 안에서 실행해야 합니다.");
    }

    private readonly record struct PointKey(double X, double Y)
    {
        public static PointKey Of(Point2d p) => new(p.X, p.Y);
        public bool Before(PointKey other) => X < other.X || X == other.X && Y <= other.Y;
    }
    private readonly record struct GridKey(long X, long Y)
    {
        public static GridKey Of(Point2d p) => new((long)Math.Round(p.X * 1_000_000), (long)Math.Round(p.Y * 1_000_000));
    }
    private readonly record struct EdgeKey(PointKey A, PointKey B)
    {
        public static EdgeKey Of(Point2d a, Point2d b)
        {
            var p = PointKey.Of(a); var q = PointKey.Of(b);
            return p.Before(q) ? new(p, q) : new(q, p);
        }
    }
    private sealed class EdgeUse
    {
        public Point2d A { get; }
        public Point2d B { get; }
        public int PartA { get; }
        public int PartB { get; set; }
        public int Count { get; set; } = 1;
        public EdgeUse(Point2d a, Point2d b, int partId) { A = a; B = b; PartA = PartB = partId; }
    }
}
