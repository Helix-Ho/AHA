using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>
/// Native regression helper for direct building meshes. Builds caps and walls from source rings;
/// no Brep, extrusion, surface, or Brep-to-mesh conversion occurs in Build.
/// </summary>
internal static class BuildingMeshChecks
{

    /// <summary>Uses the same direct mesh construction as the production plugin.</summary>
    public static Mesh Build(GeoFeature feature, LocalProjection projection,
        double groundMetres, double heightMetres, double modelScale = 1) =>
        BuildingGeometry.CreateMesh(feature, projection, groundMetres, heightMetres, modelScale);

    private sealed record CheckResult(double BoundaryDistance, double HeightError,
        double RelativeVolumeError, double AreaError, double Volume);

    private static CheckResult CheckMesh(Mesh mesh, GeoFeature feature, LocalProjection projection,
        double ground, double height, double scale)
    {
        if (!mesh.IsValid || !mesh.IsClosed) throw new InvalidOperationException("Mesh is not closed and valid.");
        var area = Geo.Area(feature, projection);
        using var mass = VolumeMassProperties.Compute(mesh);
        var expectedVolume = area * height * scale * scale * scale;
        var actualVolume = mass?.Volume ?? throw new InvalidOperationException("Cannot measure mesh volume.");
        var relativeVolumeError = Math.Abs(actualVolume - expectedVolume) / expectedVolume;
        var bounds = mesh.GetBoundingBox(true);
        var heightError = Math.Max(Math.Abs(bounds.Min.Z / scale - ground), Math.Abs(bounds.Max.Z / scale - ground - height));
        var capEdges = new Dictionary<(int A, int B), int>();
        double topArea = 0;
        foreach (var face in mesh.Faces)
        {
            var a = mesh.Vertices.Point3dAt(face.A); var b = mesh.Vertices.Point3dAt(face.B); var c = mesh.Vertices.Point3dAt(face.C);
            if (!face.IsTriangle || Math.Abs(a.Z / scale - ground - height) > 1e-7 ||
                Math.Abs(b.Z / scale - ground - height) > 1e-7 || Math.Abs(c.Z / scale - ground - height) > 1e-7) continue;
            topArea += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2 / (scale * scale);
            Count(face.A, face.B); Count(face.B, face.C); Count(face.C, face.A);
        }
        var boundary = capEdges.Where(e => e.Value == 1).Select(e =>
            (A: mesh.Vertices.Point3dAt(e.Key.A) / scale, B: mesh.Vertices.Point3dAt(e.Key.B) / scale)).ToList();
        var sourceEdges = new List<(Point3d A, Point3d B)>();
        foreach (var ring in feature.Polygons.SelectMany(p => p))
        {
            var points = ring.Select(projection.ToLocal).Select(p => new Point3d(p.X, p.Y, ground + height)).ToArray();
            for (var i = 0; i < points.Length; i++)
                if (points[i].DistanceTo(points[(i + 1) % points.Length]) > 1e-12)
                    sourceEdges.Add((points[i], points[(i + 1) % points.Length]));
        }
        double distance = 0;
        // Both directions; include edge interiors so missing courtyard boundaries cannot hide.
        foreach (var edge in boundary)
        foreach (var p in new[] { edge.A, edge.B, (edge.A + edge.B) / 2 })
            distance = Math.Max(distance, sourceEdges.Min(e => SegmentDistance(p, e.A, e.B)));
        foreach (var edge in sourceEdges)
        foreach (var p in new[] { edge.A, edge.B, (edge.A + edge.B) / 2 })
            distance = Math.Max(distance, boundary.Min(e => SegmentDistance(p, e.A, e.B)));
        var areaError = Math.Abs(topArea - area);
        if (distance > 0.00001 || heightError > 0.00001 || relativeVolumeError > 0.00001 ||
            actualVolume <= 0 || areaError > Math.Max(0.0001, area * 0.000001))
            throw new InvalidOperationException($"Source footprint/height/volume check failed: XY={distance:R}, H={heightError:R}, V={relativeVolumeError:R}, area={areaError:R}");
        return new CheckResult(distance, heightError, relativeVolumeError, areaError, actualVolume);

        void Count(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            capEdges.TryGetValue(key, out var count);
            capEdges[key] = count + 1;
        }
    }

    private static double SegmentDistance(Point3d point, Point3d a, Point3d b)
    {
        var d = b - a;
        var denominator = d.SquareLength;
        var t = denominator > 0 ? Math.Clamp(((point - a) * d) / denominator, 0, 1) : 0;
        return point.DistanceTo(a + t * d);
    }

    public static int CheckSynthetic()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        List<GeoPoint> Ring(params (double X, double Y)[] points) => points.Select(p => projection.ToGeo(p.X, p.Y)).ToList();
        var courtyard = new GeoFeature { Id = "courtyard", Polygons = new() { new() {
            Ring((0, 0), (5, 0), (10, 0), (10, 10), (0, 10), (0, 0)),
            Ring((3, 3), (7, 3), (7, 7), (3, 7), (3, 3)) } } };
        var multipart = new GeoFeature { Id = "multipart", Polygons = new() {
            courtyard.Polygons[0], new() { Ring((20, 0), (24, 0), (24, 6), (20, 6), (20, 0)) } } };
        int checks = 0;
        foreach (var source in new[] { courtyard, multipart })
        foreach (var scale in new[] { 1d, 1000d })
        {
            using var mesh = Build(source, projection, 123.45, 9.2, scale);
            CheckMesh(mesh, source, projection, 123.45, 9.2, scale);
            // Roofs and walls must remain sharp even after the mesh is shaded.
            mesh.FaceNormals.ComputeFaceNormals();
            foreach (var face in mesh.Faces.Select((face, index) => (face, index)))
            foreach (var vertex in new[] { face.face.A, face.face.B, face.face.C, face.face.D }.Distinct())
                if (Vector3d.Multiply(new Vector3d(mesh.Normals[vertex]), new Vector3d(mesh.FaceNormals[face.index])) < 0.999999)
                    throw new InvalidOperationException("Building corner shading is not sharp.");
            var editable = BuildingGeometry.CreateEditable(source, projection, 123.45, 9.2, scale, 0.001 * scale);
            try
            {
                if (editable.Count != source.Polygons.Count || editable.Any(g => g is not Extrusion))
                    throw new InvalidOperationException("Editable buildings must remain lightweight extrusions.");
                double totalVolume = 0;
                for (var part = 0; part < editable.Count; part++)
                {
                    var extrusion = (Extrusion)editable[part];
                    if (!extrusion.IsValid || !extrusion.IsSolid || extrusion.ProfileCount != source.Polygons[part].Count)
                        throw new InvalidOperationException("Editable building lost a profile or its solid topology.");
                    var bounds = extrusion.GetBoundingBox(true);
                    if (Math.Abs(bounds.Min.Z / scale - 123.45) > 1e-7 || Math.Abs(bounds.Max.Z / scale - 132.65) > 1e-7)
                        throw new InvalidOperationException("Editable building height or placement differs.");
                    using var mass = VolumeMassProperties.Compute(extrusion);
                    totalVolume += mass?.Volume ?? throw new InvalidOperationException("Cannot measure editable building volume.");
                }
                var expectedVolume = Geo.Area(source, projection) * 9.2 * Math.Pow(scale, 3);
                if (Math.Abs(totalVolume - expectedVolume) > expectedVolume * 1e-6)
                    throw new InvalidOperationException("Editable building volume or courtyard differs.");
            }
            finally { foreach (var geometry in editable) geometry.Dispose(); }
            checks++;
        }
        return checks;
    }

    /// <summary>Valid source corner whose orientation changes on a 1-micrometre grid.</summary>
    public static int CheckSourcePrecision()
    {
        var witness = FixtureData.Witness("building");
        var origin = witness.GetProperty("origin");
        var projection = new LocalProjection(new GeoPoint(origin[0].GetDouble(), origin[1].GetDouble()));
        var ring = witness.GetProperty("ring").EnumerateArray()
            .Select(p => new GeoPoint(p[0].GetDouble(), p[1].GetDouble())).ToList();
        var source = new GeoFeature { Id = "source-precision-corner", Polygons = new() { new() { ring } } };
        int checks = 0;
        foreach (double scale in new[] { 1d, 1000d })
        {
            using var mesh = Build(source, projection, 123.45, 3, scale);
            CheckMesh(mesh, source, projection, 123.45, 3, scale);
            var sourceXY = source.Polygons.SelectMany(p => p).SelectMany(r => r)
                .Select(projection.ToLocal).Select(p => (p.X * scale, p.Y * scale)).ToHashSet();
            if (mesh.Vertices.ToPoint3dArray().Any(p => !sourceXY.Contains((p.X, p.Y))))
                throw new InvalidOperationException("Direct building mesh moved a source footprint vertex.");
            checks++;
        }
        return checks;
    }

}
