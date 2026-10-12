using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Removes interior tessellation only when the entire existing surface is one plane.</summary>
internal static class CoplanarMeshReduction
{
    // A numerical equality budget, not an additional terrain fitting tolerance.
    private const double PlaneEqualityMetres = 1e-9;

    internal static Mesh Reduce(Mesh source)
    {
        if (source.Faces.Count < 3) return source;
        var first = source.Faces[0];
        var origin = source.Vertices.Point3dAt(first.A);
        var cross = Vector3d.CrossProduct(source.Vertices.Point3dAt(first.B) - origin,
            source.Vertices.Point3dAt(first.C) - origin);
        if (cross.Z == 0) return source;
        double sx = -cross.X / cross.Z, sy = -cross.Y / cross.Z;
        double Height(double x, double y) => origin.Z + sx * (x - origin.X) + sy * (y - origin.Y);
        // Most real terrain leaves this path after a handful of vertices. Do
        // not build adjacency, groups or replacement geometry for curved land.
        for (int i = 0; i < source.Vertices.Count; i++)
        {
            var point = source.Vertices.Point3dAt(i);
            if (Math.Abs(point.Z - Height(point.X, point.Y)) > PlaneEqualityMetres) return source;
        }
        var points = source.Vertices.ToPoint3dArray();
        var triangles = source.Faces.SelectMany(f => f.IsTriangle
            ? new[] { new PlanarMeshTriangle(f.A, f.B, f.C) }
            : new[] { new PlanarMeshTriangle(f.A, f.B, f.C), new PlanarMeshTriangle(f.A, f.C, f.D) }).ToArray();
        var cycles = PlanarMeshTopology.BoundaryCycles(triangles);
        var boundaryIds = cycles.SelectMany(c => c).ToHashSet();
        if (boundaryIds.Count == points.Length) return source;
        var paths = new PathsD(cycles.Select(c => new PathD(c.Select(id => new PointD(points[id].X, points[id].Y)))));
        var reduced = PlanarBoundaryTriangulation.Build(paths);
        if (reduced.Count >= triangles.Length) return source;
        var boundary = boundaryIds.ToDictionary(id => (points[id].X, points[id].Y), id => points[id]);
        var result = new Mesh(); result.Vertices.UseDoublePrecisionVertices = true;
        var vertices = new Dictionary<(double X, double Y), int>();
        try
        {
            foreach (var triangle in reduced)
                result.Faces.AddFace(Vertex(triangle[0]), Vertex(triangle[1]), Vertex(triangle[2]));
            result.Normals.ComputeNormals(); result.Compact();
            if (!result.IsValid) throw new InvalidOperationException("동일 평면 지형 메시의 경량화 결과가 유효하지 않습니다.");
            return result;
        }
        catch { result.Dispose(); throw; }
        int Vertex(PointD point)
        {
            var key = (point.x, point.y);
            if (vertices.TryGetValue(key, out int id)) return id;
            var p = boundary.TryGetValue(key, out var exact) ? exact : new Point3d(point.x, point.y, Height(point.x, point.y));
            id = result.Vertices.Add(p); vertices.Add(key, id); return id;
        }
    }
}
