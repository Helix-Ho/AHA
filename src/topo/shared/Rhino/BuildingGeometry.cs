using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Creates final building geometry directly from the source footprint.</summary>
public static class BuildingGeometry
{
    internal sealed record Footprint(PathsD Roof, IReadOnlyList<PathsD> Profiles)
    {
        private IReadOnlyList<PathsD>? caps;
        internal IReadOnlyList<PathsD> Caps => caps ??= Profiles.Select(PlanarTriangulation.TriangulateSource).ToArray();
    }

    internal sealed record PreparedExtrusion(Extrusion Geometry, SolidExpectation Expectation);

    internal static Footprint PrepareFootprint(GeoFeature feature, LocalProjection projection)
    {
        var polygons = feature.Polygons.Select(polygon => ProjectPolygon(polygon, projection)).ToArray();
        return new(new PathsD(polygons.SelectMany(polygon => polygon)),
            polygons.SelectMany(ProfileContact.Split).ToArray());
    }

    /// <summary>
    /// Returns one closed mesh, with a disconnected shell per source polygon.
    /// Ground and height are metres; modelScale converts metres to document units.
    /// The caller owns the returned geometry. No extrusion or Brep is constructed.
    /// </summary>
    public static Mesh CreateMesh(GeoFeature feature, LocalProjection projection,
        double groundMetres, double heightMetres, double modelScale = 1)
        => CreateMesh(PrepareFootprint(feature, projection), groundMetres, heightMetres, modelScale);

    internal static Mesh CreateMesh(Footprint footprint, double groundMetres, double heightMetres, double modelScale)
    {
        Validate(footprint.Profiles, groundMetres, heightMetres, modelScale);
        var mesh = new Mesh();
        mesh.Vertices.UseDoublePrecisionVertices = true;
        try
        {
            for (int i = 0; i < footprint.Profiles.Count; i++)
                AddPolygon(mesh, footprint.Profiles[i], footprint.Caps[i], groundMetres, heightMetres, modelScale);
            mesh.Normals.ComputeNormals();
            // Share vertices across coplanar cap triangles, but retain sharp roof/wall
            // and wall/wall corners instead of averaging their shading normals.
            mesh.Unweld(1e-6, true);
            mesh.Compact();
            if (!mesh.IsValid || !mesh.IsClosed || mesh.Faces.Count == 0)
                throw new InvalidOperationException("building_mesh_not_valid_closed");
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
    }

    /// <summary>
    /// Returns editable lightweight extrusions, one per simple source profile, preserving holes and point contacts.
    /// Tolerance is in document units. The caller owns every returned geometry.
    /// </summary>
    public static IReadOnlyList<GeometryBase> CreateEditable(GeoFeature feature, LocalProjection projection,
        double groundMetres, double heightMetres, double modelScale = 1, double tolerance = 0.001)
        => CreateEditable(PrepareFootprint(feature, projection).Profiles, groundMetres, heightMetres, modelScale, tolerance);

    internal static IReadOnlyList<GeometryBase> CreateEditable(IReadOnlyList<PathsD> footprint,
        double groundMetres, double heightMetres, double modelScale, double tolerance)
    {
        var prepared = PrepareEditable(footprint, groundMetres, heightMetres, modelScale, tolerance);
        try
        {
            foreach (var item in prepared)
                ModelDocumentWriter.ValidateSolid(item.Geometry, "Building", item.Expectation);
            return prepared.Select(item => (GeometryBase)item.Geometry).ToArray();
        }
        catch { foreach (var item in prepared) item.Geometry.Dispose(); throw; }
    }

    internal static IReadOnlyList<PreparedExtrusion> PrepareEditable(IReadOnlyList<PathsD> footprint,
        double groundMetres, double heightMetres, double modelScale, double tolerance)
    {
        Validate(footprint, groundMetres, heightMetres, modelScale);
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentException("invalid_tolerance");
        var result = new List<PreparedExtrusion>();
        try
        {
            foreach (var paths in footprint)
            {
                var profiles = new List<Curve>();
                Extrusion? extrusion = null;
                try
                {
                    foreach (var path in paths)
                    {
                        var points = path.Select(p => new Point3d(p.x * modelScale, p.y * modelScale, 0)).ToList();
                        points.Add(points[0]);
                        var profile = new PolylineCurve(points);
                        profiles.Add(profile);
                        if (!profile.IsValid || !profile.IsClosed)
                            throw new InvalidOperationException("invalid_footprint_ring");
                    }
                    extrusion = Extrusion.Create(profiles[0], Plane.WorldXY, heightMetres * modelScale, true)
                        ?? throw new InvalidOperationException("extrusion_failed");
                    foreach (var profile in profiles.Skip(1))
                        if (!extrusion.AddInnerProfile(profile)) throw new InvalidOperationException("inner_profile_failed");
                    if (!extrusion.IsValid || !extrusion.IsSolid || extrusion.ProfileCount != paths.Count)
                        throw new InvalidOperationException("building_extrusion_not_valid_solid");
                    var expected = paths.Sum(Clipper.Area) * heightMetres * Math.Pow(modelScale, 3);
                    var expectation = new SolidExpectation(expected, Math.Max(expected * 1e-6, Math.Pow(tolerance, 3) * 10));
                    if (!extrusion.Transform(Transform.Translation(0, 0, groundMetres * modelScale)))
                        throw new InvalidOperationException("building_placement_failed");
                    result.Add(new(extrusion, expectation));
                    extrusion = null;
                }
                finally
                {
                    extrusion?.Dispose();
                    foreach (var profile in profiles) profile.Dispose();
                }
            }
            return result;
        }
        catch
        {
            foreach (var item in result) item.Geometry.Dispose();
            throw;
        }
    }

    private static void Validate(IReadOnlyList<PathsD> footprint, double ground, double height, double scale)
    {
        if (!double.IsFinite(ground) || !double.IsFinite(height) || height <= 0 ||
            !double.IsFinite(ground + height) || !double.IsFinite(scale) || scale <= 0 || footprint.Count == 0)
            throw new ArgumentException("invalid_building_input");
    }

    private static PathsD ProjectPolygon(List<List<GeoPoint>> polygon, LocalProjection projection)
    {
        if (polygon.Count == 0) throw new InvalidOperationException("empty_footprint_polygon");
        var paths = new PathsD();
        for (var ringIndex = 0; ringIndex < polygon.Count; ringIndex++)
        {
            var path = new PathD();
            foreach (var point in polygon[ringIndex])
            {
                var local = projection.ToLocal(point);
                var p = new PointD(local.X, local.Y);
                if (path.Count == 0 || path[^1].x != p.x || path[^1].y != p.y) path.Add(p);
            }
            if (path.Count > 1 && path[0].x == path[^1].x && path[0].y == path[^1].y)
                path.RemoveAt(path.Count - 1);
            if (path.Count < 3 || Clipper.Area(path) == 0)
                throw new InvalidOperationException("degenerate_footprint_ring");
            if ((Clipper.Area(path) > 0) != (ringIndex == 0)) path.Reverse();
            paths.Add(path);
        }
        if (paths.Sum(Clipper.Area) <= 0) throw new InvalidOperationException("invalid_footprint_holes");
        return paths;
    }

    private static void AddPolygon(Mesh mesh, PathsD paths, PathsD triangles, double ground, double height, double scale)
    {
        if (triangles.Count == 0)
            throw new InvalidOperationException("footprint_triangulation_empty");

        var indices = new Dictionary<(double X, double Y), int>();
        var edges = new Dictionary<(int A, int B), (int From, int To, int Count)>();
        double capArea = 0;
        foreach (var triangle in triangles)
        {
            if (triangle.Count != 3) throw new InvalidOperationException("invalid_cap_triangle");
            var a = Vertex(triangle[0]); var b = Vertex(triangle[1]); var c = Vertex(triangle[2]);
            if (a == b || b == c || c == a) throw new InvalidOperationException("collapsed_cap_triangle");
            var pa = mesh.Vertices.Point3dAt(a); var pb = mesh.Vertices.Point3dAt(b); var pc = mesh.Vertices.Point3dAt(c);
            var signed = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (signed <= 0) throw new InvalidOperationException("invalid_cap_triangle_orientation");
            capArea += signed / (2 * scale * scale);
            mesh.Faces.AddFace(a + 1, b + 1, c + 1);
            mesh.Faces.AddFace(c, b, a);
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        var expectedArea = paths.Sum(Clipper.Area);
        if (Math.Abs(capArea - expectedArea) > Math.Max(expectedArea * 1e-6, 1e-8))
            throw new InvalidOperationException("cap_area_or_holes_mismatch");
        foreach (var edge in edges.Values)
        {
            if (edge.Count > 2) throw new InvalidOperationException("non_manifold_cap_edge");
            if (edge.Count == 1) mesh.Faces.AddFace(edge.From, edge.To, edge.To + 1, edge.From + 1);
        }

        int Vertex(PointD p)
        {
            var key = (p.x, p.y);
            if (indices.TryGetValue(key, out var found)) return found;
            // The triangulator used these exact source coordinates, so output and
            // triangle adjacency have the same precision throughout construction.
            var index = mesh.Vertices.Add(p.x * scale, p.y * scale, ground * scale);
            mesh.Vertices.Add(p.x * scale, p.y * scale, (ground + height) * scale);
            indices.Add(key, index);
            return index;
        }

        void Edge(int from, int to)
        {
            var key = from < to ? (from, to) : (to, from);
            if (edges.TryGetValue(key, out var edge))
            {
                if (edge.From == from) throw new InvalidOperationException("inconsistent_cap_orientation");
                edges[key] = (edge.From, edge.To, edge.Count + 1);
            }
            else edges[key] = (from, to, 1);
        }
    }
}
