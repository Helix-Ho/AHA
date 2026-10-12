using Rhino.Geometry;
using Clipper2Lib;

namespace Aha.Topo;

/// <summary>One triangulated boundary shared by the near top, side and bottom display caches.</summary>
internal sealed class NearTerrainDisplay : IDisposable
{
    public Dictionary<int, Mesh> Tops { get; } = new();
    public Dictionary<int, Mesh> Walls { get; } = new();
    public Mesh Bottom { get; private set; } = null!;
    private readonly record struct PointKey(long X, long Y, long Z);
    private readonly List<Point3d> points = new();
    private readonly List<PlanarMeshTriangle> triangles = new();
    private readonly Dictionary<(int, int), HashSet<int>> edgeFaces = new();

    public static NearTerrainDisplay Build(Brep top, IReadOnlyList<NearCapBoundary> boundaries,
        double bottom, double scale, IReadOnlyList<NearTerrainCap.DirectedEdge[]>? wallLoops = null)
    {
        var result = new NearTerrainDisplay();
        try
        {
            result.ReadTop(top, scale);
            var cycles = PlanarMeshTopology.BoundaryCycles(result.triangles);
            var bottomCycles = cycles.ToArray();
            var sourceLoops = wallLoops ?? NearTerrainCap.OrderedLoops(top, boundaries);
            if (cycles.Count != sourceLoops.Count)
                throw new InvalidOperationException("상면과 표시 메시의 경계 고리 수가 일치하지 않습니다.");
            // Both representations retain the original boundary vertices. Match
            // their discrete incidence, never a fitted curve or a closest-point score.
            var cycleKeys = cycles.Select(cycle => cycle.Select(node => Key(result.points[node], scale)).ToHashSet()).ToArray();
            var cycleFaces = cycles.Select(cycle => cycle.SelectMany((node, i) =>
                result.edgeFaces[Pair(node, cycle[(i + 1) % cycle.Count])]).ToHashSet()).ToArray();
            var assigned = new HashSet<int>();
            for (int wall = 0; wall < sourceLoops.Count; wall++)
            {
                var source = sourceLoops[wall];
                var keys = source.Select(edge => Key(top.Vertices[edge.StartVertex].Location, scale)).ToHashSet();
                var faces = source.SelectMany(edge => top.Edges[edge.Source.SourceEdgeIndex].TrimIndices())
                    .Select(i => top.Trims[i].Face.FaceIndex).ToHashSet();
                // A closed analytic edge has an arbitrary seam that need not
                // be a display vertex. Face incidence identifies that cycle;
                // vertex incidence distinguishes multiple loops on one face.
                var owners = Enumerable.Range(0, cycles.Count).Where(i => !assigned.Contains(i) &&
                    faces.SetEquals(cycleFaces[i])).ToArray();
                if (owners.Length > 1)
                    owners = owners.Where(i => keys.Count(cycleKeys[i].Contains) >= Math.Min(2, keys.Count)).ToArray();
                if (owners.Length != 1)
                    throw new InvalidOperationException("근접 외벽과 표시망의 원본 경계 대응이 일치하지 않습니다.");
                assigned.Add(owners[0]);
                result.CreateWall(wallLoops is null ? source[0].Source.SourceEdgeIndex : wall, cycles[owners[0]], bottom);
            }
            result.Bottom = result.CreateBottomMesh(bottom, bottomCycles);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void ReadTop(Brep top, double scale)
    {
        var nodes = new Dictionary<PointKey, int>();
        int Node(Point3d point)
        {
            var key = new PointKey((long)Math.Round(point.X / scale * 1e9),
                (long)Math.Round(point.Y / scale * 1e9), (long)Math.Round(point.Z / scale * 1e9));
            if (nodes.TryGetValue(key, out int node)) return node;
            node = points.Count; points.Add(point); nodes.Add(key, node); return node;
        }
        foreach (var face in top.Faces)
        {
            var mesh = face.GetMesh(MeshType.Render)
                ?? throw new InvalidOperationException("근접 지형 상면의 표시 메시가 없습니다.");
            var canonical = mesh.DuplicateMesh();
            bool retained = false;
            try
            {
                var map = new int[mesh.Vertices.Count];
                for (int i = 0; i < map.Length; i++)
                {
                    map[i] = Node(mesh.Vertices.Point3dAt(i));
                    canonical.Vertices.SetVertex(i, points[map[i]]);
                }
                canonical.Faces.Clear();
                foreach (var f in mesh.Faces)
                {
                    Add(f.A, f.B, f.C);
                    if (f.IsQuad) Add(f.A, f.C, f.D);
                }
                canonical.Normals.ComputeNormals(); canonical.Compact();
                if (!canonical.IsValid || canonical.Faces.Count == 0)
                    throw new InvalidOperationException("근접 상면의 공통 정점으로 유효한 표시 면을 구성할 수 없습니다.");
                Tops.Add(face.FaceIndex, canonical); retained = true;
                void Add(int a, int b, int c)
                {
                    // Rebuild the face cache and boundary graph together when
                    // identifying floating-point copies collapses a triangle.
                    if (map[a] == map[b] || map[b] == map[c] || map[c] == map[a]) return;
                    triangles.Add(new(map[a], map[b], map[c]));
                    foreach (var pair in new[] { Pair(map[a], map[b]), Pair(map[b], map[c]), Pair(map[c], map[a]) })
                    {
                        if (!edgeFaces.TryGetValue(pair, out var owners)) edgeFaces[pair] = owners = new();
                        owners.Add(face.FaceIndex);
                    }
                    canonical.Faces.AddFace(a, b, c);
                }
            }
            finally { if (!retained) canonical.Dispose(); }
        }
    }

    private static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);

    private static PointKey Key(Point3d point, double scale) => new(
        (long)Math.Round(point.X / scale / ModelPlanPolicy.PrecisionMetres),
        (long)Math.Round(point.Y / scale / ModelPlanPolicy.PrecisionMetres),
        (long)Math.Round(point.Z / scale / ModelPlanPolicy.PrecisionMetres));

    private void CreateWall(int loopId, IReadOnlyList<int> cycle, double bottom)
    {
        var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
        try
        {
            for (int i = 0; i < cycle.Count; i++)
            {
                var a = points[cycle[i]]; var b = points[cycle[(i + 1) % cycle.Count]];
                int first = mesh.Vertices.Count;
                mesh.Vertices.Add(a.X, a.Y, bottom); mesh.Vertices.Add(b.X, b.Y, bottom);
                mesh.Vertices.Add(b); mesh.Vertices.Add(a);
                mesh.Faces.AddFace(first, first + 1, first + 2, first + 3);
            }
            mesh.Normals.ComputeNormals(); mesh.Compact();
            if (!mesh.IsValidWithLog(out var validity) || mesh.Faces.Count == 0)
                throw new InvalidOperationException("근접 옆면의 공통 표시 메시가 유효하지 않습니다: " + validity);
            Walls.Add(loopId, mesh);
        }
        catch { mesh.Dispose(); throw; }
    }

    private Mesh CreateBottomMesh(double bottom, IReadOnlyList<List<int>> cycles)
    {
        var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
        var map = new Dictionary<(double X, double Y),int>();
        try
        {
            var outlines = new PathsD(cycles.Select(cycle =>
            {
                var path = new PathD();
                foreach (int node in cycle)
                {
                    var point = new PointD(points[node].X, points[node].Y);
                    if (path.Count == 0 || path[^1].x != point.x || path[^1].y != point.y) path.Add(point);
                }
                if (path.Count > 1 && path[0].x == path[^1].x && path[0].y == path[^1].y) path.RemoveAt(path.Count - 1);
                return path;
            }));
            foreach (var triangle in PlanarBoundaryTriangulation.Build(outlines))
            {
                int a=Vertex(triangle[0]),b=Vertex(triangle[1]),c=Vertex(triangle[2]);
                mesh.Faces.AddFace(c,b,a);
            }
            mesh.Normals.ComputeNormals(); mesh.Compact();
            if (!mesh.IsValid || mesh.Faces.Count == 0) throw new InvalidOperationException("근접 지형의 공통 표시 메시가 유효하지 않습니다.");
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
        int Vertex(PointD point)
        {
            var key = (point.x, point.y);
            if (map.TryGetValue(key,out int index)) return index;
            index=mesh.Vertices.Add(point.x,point.y,bottom);map.Add(key,index);return index;
        }
    }

    public void Dispose()
    {foreach(var mesh in Tops.Values)mesh.Dispose();foreach(var mesh in Walls.Values)mesh.Dispose();Bottom?.Dispose();}
}
