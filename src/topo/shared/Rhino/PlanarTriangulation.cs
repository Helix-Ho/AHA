using Clipper2Lib;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Triangulate.Polygon;

namespace Aha.Topo;

/// <summary>Source-vertex polygon triangulation shared by terrain and direct building meshes.</summary>
public static class PlanarTriangulation
{
    private const double GridScale = 1_000_000;
    public const double CoordinateToleranceMetres = 1 / GridScale;

    public static (long X, long Y) GridKey(PointD point)
    {
        if (!double.IsFinite(point.x) || !double.IsFinite(point.y))
            throw new ArgumentException("평면 경계 좌표가 유한하지 않습니다.");
        return (checked((long)Math.Round(point.x * GridScale)), checked((long)Math.Round(point.y * GridScale)));
    }

    public static PathsD Triangulate(PathsD outlines)
    {
        if (outlines.Count == 0) return new PathsD();
        var source = outlines.Select(CanonicalRing).ToList();
        var vertices = source.SelectMany(path => path).Distinct().ToArray();
        var sourceSet = vertices.ToHashSet();
        var geometry = Geometry(source);
        var output = new List<List<GridPoint>>();
        foreach (var triangle in Triangles(geometry))
        {
            var points = Enumerable.Range(0, 3).Select(index => {
                var p = triangle[index];
                var point = new GridPoint(checked((long)p.X), checked((long)p.Y));
                if (p.X != point.X || p.Y != point.Y || !sourceSet.Contains(point))
                    throw new InvalidOperationException("평면 삼각분할이 원본에 없는 정점을 반환했습니다.");
                return point;
            }).ToList();
            var area = TwiceArea(points);
            if (area == 0) throw new InvalidOperationException("평면 삼각분할이 퇴화한 면을 반환했습니다.");
            if (area < 0) points.Reverse();
            output.Add(points);
        }
        Validate(source, output, vertices);
        return new PathsD(output.Select(path => new PathD(path.Select(p => new PointD(p.X / GridScale, p.Y / GridScale)))));
    }

    /// <summary>
    /// Triangulates independent footprints at their original double precision.
    /// Unlike a shared terrain partition, a building must not have its topology
    /// rounded and then restored: a narrow valid corner can change orientation.
    /// Output contains only the exact input vertices, with counterclockwise faces.
    /// </summary>
    public static PathsD TriangulateSource(PathsD outlines)
    {
        if (outlines.Count == 0) return new();
        var source = outlines.Select(path =>
        {
            var points = new List<Coordinate>();
            foreach (var p in path)
            {
                if (!double.IsFinite(p.x) || !double.IsFinite(p.y))
                    throw new ArgumentException("평면 경계 좌표가 유한하지 않습니다.");
                var point = new Coordinate(p.x, p.y);
                if (points.Count == 0 || !points[^1].Equals2D(point)) points.Add(point);
            }
            if (points.Count > 1 && points[0].Equals2D(points[^1])) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) throw new ArgumentException("평면 삼각분할의 경계가 퇴화했습니다.");
            return points.ToArray();
        }).ToList();
        var geometry = Geometry(source);
        if (!geometry.IsValid || geometry.Area <= 0)
            throw new ArgumentException("원본 건물 경계가 유효한 평면 영역이 아닙니다.");
        var sourceVertices = source.SelectMany(path => path).DistinctBy(p => (p.X, p.Y)).ToArray();
        var sourceSet = sourceVertices.Select(p => (p.X, p.Y)).ToHashSet();
        var output = new List<Coordinate[]>();
        foreach (var triangle in Triangles(geometry))
        {
            if (triangle.Any(p => !sourceSet.Contains((p.X, p.Y))))
                throw new InvalidOperationException("평면 삼각분할이 원본에 없는 정점을 반환했습니다.");
            var orientation = Orientation.Index(triangle[0], triangle[1], triangle[2]);
            if (orientation == OrientationIndex.Collinear)
                throw new InvalidOperationException("평면 삼각분할이 퇴화한 면을 반환했습니다.");
            if (orientation == OrientationIndex.Clockwise) Array.Reverse(triangle);
            output.Add(triangle);
        }
        ValidateSource(source, output, sourceVertices, geometry.Area);
        return new PathsD(output.Select(path => new PathD(path.Select(p => new PointD(p.X, p.Y)))));
    }

    // Ear clipping may connect almost collinear boundary vertices into long
    // needle triangles. Lifting those to a bilinear height field can invert the
    // apparent surface normal even when the height error is very small. The
    // constrained Delaunay form changes only internal diagonals while retaining
    // the exact source vertices and polygon boundary checked below.
    private static IEnumerable<Coordinate[]> Triangles(NetTopologySuite.Geometries.Geometry geometry) =>
        new ConstrainedDelaunayTriangulator(geometry).GetTriangles()
            .Select(triangle => Enumerable.Range(0, 3).Select(triangle.GetCoordinate).ToArray());

    internal static NetTopologySuite.Geometries.Geometry ToGridGeometry(PathsD outlines) => Geometry(outlines.Select(CanonicalRing).ToList());

    internal static PathsD FromGridPolygon(Polygon polygon)
    {
        var paths = new PathsD { Ring(polygon.ExteriorRing, true) };
        for (int index = 0; index < polygon.NumInteriorRings; index++) paths.Add(Ring(polygon.GetInteriorRingN(index), false));
        return paths;
        static PathD Ring(LineString ring, bool outer)
        {
            var path = new PathD(ring.Coordinates.Take(ring.NumPoints - 1).Select(p => new PointD(p.X / GridScale, p.Y / GridScale)));
            if ((Clipper.Area(path) > 0) != outer) path.Reverse();
            return path;
        }
    }

    private static NetTopologySuite.Geometries.Geometry Geometry(List<List<GridPoint>> source)
        => Geometry(source.Select(path => path.Select(p => new Coordinate(p.X, p.Y)).ToArray()).ToList());

    private static NetTopologySuite.Geometries.Geometry Geometry(List<Coordinate[]> source)
    {
        var factory = new GeometryFactory();
        var rings = source.Select(path => path.Append(path[0]).ToArray()).ToList();
        var shells = rings.Where(Orientation.IsCCW).ToList();
        var holes = rings.Where(path => !Orientation.IsCCW(path)).ToList();
        var grouped = shells.Select(_ => new List<LinearRing>()).ToArray();
        var shellPolygons = holes.Count == 0 ? Array.Empty<Polygon>() : shells.Select(factory.CreatePolygon).ToArray();
        var areas = shellPolygons.Select(p => p.Area).ToArray();
        var prepared = shellPolygons.Select(PreparedGeometryFactory.Prepare).ToArray();
        var shellIndex = new STRtree<int>();
        for (int index = 0; index < shellPolygons.Length; index++)
            shellIndex.Insert(shellPolygons[index].EnvelopeInternal, index);
        shellIndex.Build();
        foreach (var hole in holes)
        {
            var owner = -1; double ownerArea = double.PositiveInfinity;
            var holePolygon = factory.CreatePolygon(hole);
            foreach (int index in shellIndex.Query(holePolygon.EnvelopeInternal))
            {
                var area = areas[index];
                // Several overlapping operands may share the first hole point.
                // Its owner must contain the complete hole, not merely that
                // vertex (which can lie on an unrelated smaller shell).
                if (area < ownerArea && shellPolygons[index].EnvelopeInternal.Covers(holePolygon.EnvelopeInternal) &&
                    prepared[index].Covers(holePolygon)) { owner = index; ownerArea = area; }
            }
            if (owner < 0) throw new InvalidOperationException("평면 경계의 내부 구멍을 외곽선에 연결할 수 없습니다.");
            grouped[owner].Add(factory.CreateLinearRing(hole));
        }
        var polygons = shells.Select((shell, index) => factory.CreatePolygon(factory.CreateLinearRing(shell), grouped[index].ToArray())).ToArray();
        if (polygons.Length == 0 && source.Count != 0) throw new ArgumentException("평면 삼각분할에 외곽 경계가 없습니다.");
        return factory.CreateMultiPolygon(polygons);
    }

    private static void ValidateSource(List<Coordinate[]> source, List<Coordinate[]> triangles,
        Coordinate[] vertices, double expectedArea)
    {
        double actualArea = triangles.Sum(triangle => Area.OfRing(triangle.Append(triangle[0]).ToArray()));
        if (triangles.Count == 0 || Math.Abs(actualArea - expectedArea) > Math.Max(1e-12, expectedArea * 1e-10))
            throw new InvalidOperationException("원본 건물과 삼각형의 면적이 일치하지 않습니다.");
        var vertexIndex = new STRtree<Coordinate>();
        foreach (var point in vertices) vertexIndex.Insert(new Envelope(point), point);
        vertexIndex.Build();
        var expected = Edges(source, false); var actual = Edges(triangles, true);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var winding) || winding != pair.Value))
            throw new InvalidOperationException("원본 건물과 삼각형의 외곽·구멍 경계가 일치하지 않습니다.");

        Dictionary<(double Ax, double Ay, double Bx, double By), int> Edges(List<Coordinate[]> paths, bool trianglesOnly)
        {
            var edges = new Dictionary<(double Ax, double Ay, double Bx, double By), (int Count, int Winding)>();
            foreach (var path in paths)
            for (int i = 0; i < path.Length; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Length];
                var points = vertexIndex.Query(new Envelope(a, b)).Where(p => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) &&
                    p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y) &&
                    Orientation.Index(a, b, p) == OrientationIndex.Collinear)
                    .OrderBy(p => a.X != b.X ? (p.X - a.X) / (b.X - a.X) : (p.Y - a.Y) / (b.Y - a.Y)).ToArray();
                for (int j = 1; j < points.Length; j++)
                {
                    var from = points[j - 1]; var to = points[j];
                    bool forward = from.X < to.X || from.X == to.X && from.Y < to.Y;
                    var key = forward ? (from.X, from.Y, to.X, to.Y) : (to.X, to.Y, from.X, from.Y);
                    var value = edges.GetValueOrDefault(key);
                    value = (value.Count + 1, value.Winding + (forward ? 1 : -1));
                    if (trianglesOnly && (value.Count > 2 || Math.Abs(value.Winding) > 1))
                        throw new InvalidOperationException("건물 삼각분할에 중첩되거나 같은 방향으로 접한 면이 있습니다.");
                    edges[key] = value;
                }
            }
            return edges.Where(pair => pair.Value.Winding != 0).ToDictionary(pair => pair.Key, pair => pair.Value.Winding);
        }
    }

    private static List<GridPoint> CanonicalRing(PathD path)
    {
        var result = new List<GridPoint>();
        foreach (var p in path)
        {
            var key = GridKey(p);
            var point = new GridPoint(key.X, key.Y);
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        if (result.Count < 3 || TwiceArea(result) == 0) throw new ArgumentException("평면 삼각분할의 경계가 퇴화했습니다.");
        return result;
    }

    private static void Validate(List<List<GridPoint>> source, List<List<GridPoint>> triangles, GridPoint[] vertices)
    {
        Int128 expectedArea = 0, actualArea = 0;
        foreach (var path in source) expectedArea += TwiceArea(path);
        foreach (var triangle in triangles) actualArea += TwiceArea(triangle);
        if (expectedArea != actualArea) Fail("원본과 삼각형의 정수 격자 면적이 일치하지 않습니다.");
        var expected = Edges(source, false); var actual = Edges(triangles, true);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out int winding) || winding != pair.Value))
            Fail("평면 삼각분할의 외곽·구멍 경계가 원본과 일치하지 않습니다.");

        Dictionary<EdgeKey, int> Edges(List<List<GridPoint>> paths, bool trianglesOnly)
        {
            var edges = new Dictionary<EdgeKey, (int Count, int Winding)>();
            foreach (var path in paths)
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Count];
                // Compare atomic segments, so retaining or removing a redundant
                // collinear vertex does not create a false topology difference.
                var points = vertices.Where(p => OnSegment(a, b, p))
                    .OrderBy(p => a.X != b.X ? (p.X - (double)a.X) / (b.X - (double)a.X) :
                        (p.Y - (double)a.Y) / (b.Y - (double)a.Y)).ToArray();
                for (int j = 1; j < points.Length; j++)
                {
                    var from = points[j - 1]; var to = points[j];
                    var key = EdgeKey.Of(from, to); var direction = from == key.A ? 1 : -1;
                    var value = edges.GetValueOrDefault(key);
                    value = (value.Count + 1, value.Winding + direction);
                    if (trianglesOnly && (value.Count > 2 || Math.Abs(value.Winding) > 1))
                        Fail("평면 삼각분할에 중첩되거나 같은 방향으로 접한 면이 있습니다.");
                    edges[key] = value;
                }
            }
            return edges.Where(pair => pair.Value.Winding != 0).ToDictionary(pair => pair.Key, pair => pair.Value.Winding);
        }

        void Fail(string reason)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static bool OnSegment(GridPoint a, GridPoint b, GridPoint p) =>
        p.X >= Math.Min(a.X,b.X) && p.X <= Math.Max(a.X,b.X) &&
        p.Y >= Math.Min(a.Y,b.Y) && p.Y <= Math.Max(a.Y,b.Y) && Cross(a,b,p) == 0;
    private static Int128 Cross(GridPoint a, GridPoint b, GridPoint c) =>
        (Int128)(b.X-a.X)*(c.Y-a.Y)-(Int128)(b.Y-a.Y)*(c.X-a.X);
    private static Int128 TwiceArea(IReadOnlyList<GridPoint> path)
    {
        Int128 sum = 0;
        for (int i = 1; i + 1 < path.Count; i++) sum += Cross(path[0], path[i], path[i+1]);
        return sum;
    }
    private readonly record struct GridPoint(long X, long Y)
    {
        public bool Before(GridPoint other) => X < other.X || X == other.X && Y < other.Y;
    }
    private readonly record struct EdgeKey(GridPoint A, GridPoint B)
    {
        public static EdgeKey Of(GridPoint a, GridPoint b) => a.Before(b) ? new(a,b) : new(b,a);
    }
}
