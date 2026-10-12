using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Noding;
using NetTopologySuite.Noding.Snapround;
using NetTopologySuite.Operation.Polygonize;

namespace Aha.Topo;

public sealed record PlanRegion(int Column, int Row, bool Near, bool Road, Polygon Polygon)
{
    public PathsD Paths { get; } = PlanarTriangulation.FromGridPolygon(Polygon);
}

public sealed record PlanPatch(IReadOnlyList<PlanRegion> Regions, IReadOnlySet<(int Column, int Row)> Blocks);

/// <summary>One globally noded planar graph for all output surfaces and query cells.</summary>
public sealed class PlanarPartition
{
    private const double Scale = 1_000_000;
    private readonly long[] xs, ys;
    private readonly long west, south, east, north;
    public double West => west / Scale;
    public double South => south / Scale;
    public double East => east / Scale;
    public double North => north / Scale;
    public int CellCount => Regions.Select(region => (region.Column, region.Row)).Distinct().Count();
    public double PlanAreaSquareMetres => Regions.Sum(region => region.Polygon.Area) / (Scale * Scale);
    private readonly PathsD? clip;
    public IReadOnlyList<PlanRegion> Regions { get; }
    public int DroppedZeroAreaBranches { get; }
    public double ReassignedRoleAreaSquareMetres { get; }
    internal PlanBoundarySources SourceBoundaries { get; }

    private PlanarPartition(long[] xLines, long[] yLines, IReadOnlyList<PlanRegion> regions, int droppedZeroAreaBranches,
        long west, long south, long east, long north, PlanBoundarySources sourceBoundaries, PathsD? clip, double reassignedRoleArea)
    {
        xs = xLines; ys = yLines; Regions = regions; DroppedZeroAreaBranches = droppedZeroAreaBranches;
        this.west = west; this.south = south; this.east = east; this.north = north;
        SourceBoundaries = sourceBoundaries; this.clip = clip; ReassignedRoleAreaSquareMetres = reassignedRoleArea;
    }

    public static PlanarPartition Build(TerrainField field, PathsD roads, PathsD near, GeoBounds? outputBounds = null, PathsD? clip = null)
        => Build(field, roads, near, outputBounds, clip, null);

    internal static PlanarPartition Build(TerrainField field, PathsD roads, PathsD near, GeoBounds? outputBounds,
        PathsD? clip, RectD? localBounds)
    {
        var xs = Enumerable.Range(0, field.Columns + 1).Select(i => (long)Math.Round(field.ModelX(i) * Scale)).ToArray();
        var ys = Enumerable.Range(0, field.Rows + 1).Select(i => (long)Math.Round(field.ModelY(i) * Scale)).ToArray();
        outputBounds ??= field.Bounds;
        outputBounds.Validate();
        var sw = localBounds is {} window ? (X:window.left,Y:window.top)
            : field.Projection.ToLocal(new(outputBounds.West, outputBounds.South));
        var ne = localBounds is {} local ? (X:local.right,Y:local.bottom)
            : field.Projection.ToLocal(new(outputBounds.East, outputBounds.North));
        long west = (long)Math.Round(sw.X * Scale), south = (long)Math.Round(sw.Y * Scale),
            east = (long)Math.Round(ne.X * Scale), north = (long)Math.Round(ne.Y * Scale);
        if (west < xs[0] || south < ys[0] || east > xs[^1] || north > ys[^1] || west >= east || south >= north)
            throw new ArgumentException("출력 범위를 포함하는 지형 자료가 필요합니다.");
        var factory = new GeometryFactory(new PrecisionModel(1));
        var sources = new PlanBoundarySources();
        var lines = new List<ISegmentString>();
        // Keep each finite source as noder context. Snap rounding still happens
        // once, at the same integer grid, but every resulting edge retains the
        // primitive that owns it for exact trims.
        foreach (var x in xs.Where(x => x > west && x < east).Prepend(west).Append(east))
            AddLine(new(x / Scale, south / Scale), new(x / Scale, north / Scale), PlanBoundarySources.SourceKind.SamplingGrid);
        foreach (var y in ys.Where(y => y > south && y < north).Prepend(south).Append(north))
            AddLine(new(west / Scale, y / Scale), new(east / Scale, y / Scale), PlanBoundarySources.SourceKind.SamplingGrid);
        Add(roads, PlanBoundarySources.SourceKind.Road); Add(near, PlanBoundarySources.SourceKind.Near);
        if (clip is not null) Add(clip, PlanBoundarySources.SourceKind.Boundary);
        var noder = new SnapRoundingNoder(new PrecisionModel(1));
        noder.ComputeNodes(lines);
        var edges = new HashSet<Edge>();
        var polygonizer = new Polygonizer();
        foreach (var line in noder.GetNodedSubstrings())
        {
            var coordinates = line.Coordinates;
            for (int i = 1; i < coordinates.Length; i++)
            {
                var a = coordinates[i - 1]; var b = coordinates[i];
                var edge = Edge.Of(new((long)a.X, (long)a.Y), new((long)b.X, (long)b.Y));
                if (edge.A == edge.B) continue;
                sources.AddGraphSegment(new(a.X / Scale, a.Y / Scale), new(b.X / Scale, b.Y / Scale),
                    (PlanBoundarySources.Segment)line.Context);
                if (edges.Add(edge)) polygonizer.Add(factory.CreateLineString(new[] { a, b }));
            }
        }
        var polygons = polygonizer.GetPolygons().Cast<Polygon>().ToArray();
        // Fixed-precision noding can collapse a sub-grid-width slit to a line.
        // Bridges and dangling lines have no face area; polygonization omits them.
        // Invalid rings and any missing model area remain hard failures below.
        int droppedZeroAreaBranches = polygonizer.GetDangles().Count + polygonizer.GetCutEdges().Count;
        if (polygonizer.GetInvalidRingLines().Count != 0)
        {
            throw new InvalidOperationException($"지형 공통 평면 그래프: invalid {polygonizer.GetInvalidRingLines().Count}, dangles {polygonizer.GetDangles().Count}, cuts {polygonizer.GetCutEdges().Count}.");
        }
        var roadArea = PreparedGeometryFactory.Prepare(PlanarTriangulation.ToGridGeometry(roads));
        var nearArea = PreparedGeometryFactory.Prepare(PlanarTriangulation.ToGridGeometry(near));
        var included = clip is null ? null : PreparedGeometryFactory.Prepare(PlanarTriangulation.ToGridGeometry(clip));
        var result = new List<PlanRegion>(polygons.Length);
        Int128 sum = 0;
        foreach (var polygon in polygons)
        {
            var envelope = polygon.EnvelopeInternal;
            int col = Index(xs, (envelope.MinX + envelope.MaxX) * .5);
            int row = Index(ys, (envelope.MinY + envelope.MaxY) * .5);
            if (envelope.MinX < xs[col] || envelope.MaxX > xs[col + 1] ||
                envelope.MinY < ys[row] || envelope.MaxY > ys[row + 1])
                throw new InvalidOperationException("공통 평면 영역이 하나의 지형 격자를 벗어납니다.");
            var interior = InteriorSample(polygon);
            if (included is null || included.Covers(interior))
                result.Add(new(col, row, nearArea.Covers(interior), roadArea.Covers(interior), polygon));
            sum += TwiceArea(polygon);
        }
        Int128 expected = (Int128)2 * (east - west) * (north - south);
        if (sum != expected) throw new InvalidOperationException("공통 평면 그래프가 전체 모델 범위를 정확히 채우지 못했습니다.");
        double reassigned = clip is null ? ResolveRoleFragments(result) : 0;
        return new(xs, ys, result, droppedZeroAreaBranches, west, south, east, north, sources, clip, reassigned);

        void Add(PathsD paths, PlanBoundarySources.SourceKind kind)
        {
            foreach (var path in paths)
            for (int i = 0; i < path.Count; i++) AddLine(path[i], path[(i + 1) % path.Count], kind);
        }

        void AddLine(PointD start, PointD end, PlanBoundarySources.SourceKind kind)
        {
            double dx = end.x - start.x, dy = end.y - start.y;
            if (dx == 0 && dy == 0) return;
            double from = 0, to = 1;
            // Clip individual source segments, not closed role polygons: the
            // domain already supplies its boundary. Exact finite source data
            // survives even when an intersection is rounded in the graph.
            if (!Clip(-dx, start.x - west / Scale) || !Clip(dx, east / Scale - start.x) ||
                !Clip(-dy, start.y - south / Scale) || !Clip(dy, north / Scale - start.y) || to <= from) return;
            var owner = sources.Add(start, end, kind);
            var a = new Coordinate(Math.Round((start.x + from * dx) * Scale), Math.Round((start.y + from * dy) * Scale));
            var b = new Coordinate(Math.Round((start.x + to * dx) * Scale), Math.Round((start.y + to * dy) * Scale));
            if (!a.Equals2D(b)) lines.Add(new NodedSegmentString(new[] { a, b }, owner));

            bool Clip(double direction, double distance)
            {
                if (direction == 0) return distance >= 0;
                double t = distance / direction;
                if (direction < 0) { if (t > to) return false; from = Math.Max(from, t); }
                else { if (t < from) return false; to = Math.Min(to, t); }
                return true;
            }
        }
    }

    internal static Point InteriorSample(Polygon polygon)
    {
        // Boundary nodes use integer grid coordinates; a classification sample
        // must not be rounded back onto that grid. In a one-grid-unit wedge the
        // rounded Geometry.InteriorPoint can lie outside its source polygon.
        var coordinate = NetTopologySuite.Algorithm.InteriorPointArea.GetInteriorPoint(polygon)
            ?? throw new InvalidOperationException("평면 영역의 내부 표본을 찾을 수 없습니다.");
        var point = new GeometryFactory().CreatePoint(coordinate);
        if (!polygon.Contains(point))
            throw new InvalidOperationException("평면 영역의 내부 표본이 원본 영역 안에 있지 않습니다.");
        return point;
    }

    /// <summary>
    /// Group sampling blocks without turning their incidental cuts into tiny
    /// editable objects. A small same-role fragment borrows its neighbour's
    /// support; semantic boundaries do not move. Model detail applies to the
    /// connected role, not each required trim within its joined terrain body.
    /// </summary>
    public IReadOnlyList<PlanPatch> Patches(int blockCells, bool separateRoads, bool allNurbs, out int mergedBlocks, bool includeNear = true)
    {
        var regions = includeNear ? Regions : Regions.Where(region => !region.Near).ToArray();
        var blocks = regions.GroupBy(region => (X: region.Column / blockCells,
            Y: region.Row / blockCells, region.Near)).ToArray();
        if (!allNurbs && !regions.Any(region => region.Near))
        {
            mergedBlocks = 0;
            return blocks.Select(group => new PlanPatch(group.ToArray(),
                new HashSet<(int Column, int Row)> { (group.Key.X, group.Key.Y) })).ToArray();
        }
        var parent = Enumerable.Range(0, blocks.Length).ToArray();
        var owner = new Dictionary<PlanRegion, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < blocks.Length; index++)
            foreach (var region in blocks[index]) owner.Add(region, index);
        var incident = new Dictionary<Edge, List<PlanRegion>>();
        foreach (var region in regions)
        foreach (var path in region.Paths)
        for (int index = 0; index < path.Count; index++)
        {
            var edge = Edge.Of(Node.Of(path[index]), Node.Of(path[(index + 1) % path.Count]));
            if (!incident.TryGetValue(edge, out var values)) incident.Add(edge, values = new());
            values.Add(region);
        }
        var neighbours = new Dictionary<PlanRegion, List<(PlanRegion Region, double Length)>>(ReferenceEqualityComparer.Instance);
        foreach (var region in regions) neighbours.Add(region, new());
        foreach (var pair in incident.Where(pair => pair.Value.Count == 2))
        {
            var a = pair.Value[0]; var b = pair.Value[1];
            if (a.Near != b.Near) continue;
            double dx = (double)pair.Key.B.X - pair.Key.A.X, dy = (double)pair.Key.B.Y - pair.Key.A.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            neighbours[a].Add((b, length)); neighbours[b].Add((a, length));
        }
        mergedBlocks = 0;
        while (true)
        {
            bool changed = false;
            foreach (var group in regions.GroupBy(region => Root(owner[region])).OrderBy(group => group.Key))
            {
                if (!allNurbs && !group.First().Near) continue;
                foreach (bool road in new[] { false, true })
                {
                    var selected = group.Where(region => road ? region.Road : !separateRoads || !region.Road).ToArray();
                    foreach (var component in Merge(selected))
                    {
                        if (!BelowModelDetail(component)) continue;
                        var contains = PreparedGeometryFactory.Prepare(component);
                        var lengths = new Dictionary<int, double>();
                        foreach (var region in selected.Where(region => contains.Covers(region.Polygon)))
                        foreach (var neighbour in neighbours[region])
                        {
                            if (road ? !neighbour.Region.Road : separateRoads && neighbour.Region.Road) continue;
                            int candidate = Root(owner[neighbour.Region]);
                            if (candidate == group.Key) continue;
                            lengths[candidate] = lengths.GetValueOrDefault(candidate) + neighbour.Length;
                        }
                        if (lengths.Count == 0) continue;
                        int target = lengths.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
                        parent[group.Key] = target; mergedBlocks++; changed = true; break;
                    }
                    if (changed) break;
                }
                if (changed) break;
            }
            if (!changed) break;
        }
        return regions.GroupBy(region => Root(owner[region])).Select(group =>
            new PlanPatch(group.ToArray(), group.Select(region =>
                (region.Column / blockCells, region.Row / blockCells)).ToHashSet())).ToArray();

        int Root(int index)
        {
            while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; }
            return index;
        }
    }

    private static double ResolveRoleFragments(List<PlanRegion> regions)
    {
        // Classify the final noded graph, not another independently clipped
        // outline. Snap-rounding intersections may create an otherwise valid
        // one-grid face; moving or deleting that face would break coverage.
        var incident = new Dictionary<Edge, List<int>>();
        for (int i = 0; i < regions.Count; i++)
        foreach (var path in regions[i].Paths)
        for (int k = 0; k < path.Count; k++)
        {
            var edge = Edge.Of(Node.Of(path[k]), Node.Of(path[(k + 1) % path.Count]));
            if (!incident.TryGetValue(edge, out var owners)) incident.Add(edge, owners = new());
            owners.Add(i);
        }
        var neighbours = Enumerable.Range(0,regions.Count).Select(_ => new List<(int Id,double Length)>()).ToArray();
        foreach (var (edge,owners) in incident)
        {
            if (owners.Count != 2 || regions[owners[0]].Near != regions[owners[1]].Near) continue;
            double dx = edge.B.X - (double)edge.A.X, dy = edge.B.Y - (double)edge.A.Y;
            double length = Math.Sqrt(dx*dx+dy*dy);
            neighbours[owners[0]].Add((owners[1],length)); neighbours[owners[1]].Add((owners[0],length));
        }
        var parent = Enumerable.Range(0,regions.Count).ToArray();
        var role = regions.Select(region => region.Road).ToArray();
        for (int i=0;i<regions.Count;i++)
        foreach (var neighbour in neighbours[i])
            if (role[i] == role[neighbour.Id]) Union(i,neighbour.Id);
        while (true)
        {
            var groups = Enumerable.Range(0,regions.Count).GroupBy(Find)
                .Select(group => (Root:group.Key,Ids:group.ToArray(),Area:group.Sum(i => regions[i].Polygon.Area)))
                .OrderBy(group => group.Area).ThenBy(group => group.Root).ToArray();
            bool changed = false;
            foreach (var group in groups)
            {
                // A broad connected group has at least one substantial face;
                // otherwise merge its cells before measuring its true width.
                if (group.Ids.Any(i => !BelowModelDetail(regions[i].Polygon))) continue;
                if (Merge(group.Ids.Select(i => regions[i])).Any(polygon => !BelowModelDetail(polygon))) continue;
                var shared = new Dictionary<int,double>();
                foreach (int i in group.Ids)
                foreach (var neighbour in neighbours[i])
                {
                    int other = Find(neighbour.Id);
                    if (other != group.Root && role[other] != role[group.Root])
                        shared[other] = shared.GetValueOrDefault(other) + neighbour.Length;
                }
                // A whole protected Near/Far zone has no same-zone neighbour.
                // It is not an incidental role fragment and cannot be enlarged.
                if (shared.Count == 0) continue;
                int target = shared.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
                bool targetRole = role[target];
                foreach (int i in group.Ids) role[i] = targetRole;
                parent[group.Root] = target;
                foreach (int i in group.Ids)
                foreach (var neighbour in neighbours[i])
                    if (role[Find(neighbour.Id)] == targetRole) Union(target,neighbour.Id);
                changed = true; break;
            }
            if (!changed) break;
        }
        double area = 0;
        for (int i=0;i<regions.Count;i++)
        {
            bool next = role[Find(i)];
            if (next == regions[i].Road) continue;
            area += regions[i].Polygon.Area / (Scale*Scale);
            regions[i] = regions[i] with { Road = next };
        }
        return area;

        int Find(int i)
        { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a,int b)
        { a=Find(a);b=Find(b);if(a!=b)parent[b]=a; }
    }

    private static bool BelowModelDetail(Polygon polygon)
    {
        var policy = ModelPlanPolicy.Default;
        if (polygon.Area < policy.MinimumAreaSquareMetres * Scale * Scale) return true;
        double radius = (policy.MinimumWidthMetres / 2 - ModelPlanPolicy.PrecisionMetres) * Scale;
        // Cheap sufficient bounds keep buffering away from the usual broad
        // patches. Only narrow candidates need the actual inward-clearance test.
        if (2 * polygon.Area / polygon.Length >= 2 * radius) return false;
        return polygon.Buffer(-radius).IsEmpty;
    }

    /// <summary>Merge existing faces by cancelling shared edges, without a second overlay.</summary>
    public static IReadOnlyList<Polygon> Merge(IEnumerable<PlanRegion> regions)
    {
        var edges = new Dictionary<Edge, int>();
        Int128 expected = 0;
        foreach (var region in regions)
        {
            expected += TwiceArea(region.Polygon);
            foreach (var path in region.Paths)
            for (int index = 0; index < path.Count; index++)
            {
                var a = Node.Of(path[index]); var b = Node.Of(path[(index + 1) % path.Count]);
                var edge = Edge.Of(a, b); int direction = a == edge.A ? 1 : -1;
                int value = edges.GetValueOrDefault(edge) + direction;
                if (Math.Abs(value) > 1) throw new InvalidOperationException("지형 영역의 공통 면이 중첩되었습니다.");
                if (value == 0) edges.Remove(edge); else edges[edge] = value;
            }
        }
        if (edges.Count == 0) return Array.Empty<Polygon>();
        var factory = new GeometryFactory(new PrecisionModel(1));
        var polygonizer = new Polygonizer(true);
        foreach (var edge in edges.Keys)
            polygonizer.Add(factory.CreateLineString(new[] { new Coordinate(edge.A.X, edge.A.Y), new Coordinate(edge.B.X, edge.B.Y) }));
        var result = polygonizer.GetPolygons().Cast<Polygon>().ToArray();
        Int128 sum = 0;
        foreach (var polygon in result) sum += TwiceArea(polygon);
        if (sum != expected || polygonizer.GetInvalidRingLines().Count != 0 || polygonizer.GetDangles().Count != 0)
            throw new InvalidOperationException("지형 영역의 공통 경계를 합칠 수 없습니다.");
        return result;
    }

    public bool OnClipBoundary(double ax, double ay, double bx, double by)
    {
        if (clip is null) return false;
        const double tolerance = 2 / Scale;
        foreach (var path in clip)
        for (int i = 0; i < path.Count; i++)
        {
            var a = path[i]; var b = path[(i + 1) % path.Count];
            double dx = b.x - a.x, dy = b.y - a.y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length == 0) continue;
            bool On(double x, double y)
            {
                double along = ((x - a.x) * dx + (y - a.y) * dy) / length;
                return Math.Abs((x - a.x) * dy - (y - a.y) * dx) / length <= tolerance &&
                    along >= -tolerance && along <= length + tolerance;
            }
            if (On(ax, ay) && On(bx, by)) return true;
        }
        return false;
    }

    public (int Column, int Row) CellAt(double x, double y)
    {
        double gx = x * Scale, gy = y * Scale;
        if (gx < west - 1 || gx > east + 1 || gy < south - 1 || gy > north + 1)
            throw new ArgumentOutOfRangeException(nameof(x), "모델 평면 범위 밖의 셀을 요청했습니다.");
        return (Math.Clamp(Index(xs, gx), Index(xs, west), Index(xs, east - .5)),
            Math.Clamp(Index(ys, gy), Index(ys, south), Index(ys, north - .5)));
    }

    public IEnumerable<double> LocalSegmentCuts(double ax, double ay, double bx, double by)
    {
        var cuts = new SortedSet<double> { 0, 1 };
        Add(xs, ax, bx); Add(ys, ay, by);
        return cuts;
        void Add(long[] lines, double from, double to)
        {
            if (from == to) return;
            foreach (var line in lines)
            {
                double t = (line / Scale - from) / (to - from);
                if (t > 0 && t < 1) cuts.Add(t);
            }
        }
    }

    private static int Index(long[] lines, double value)
    {
        int first = 0, last = lines.Length;
        while (first < last)
        {
            int mid = first + (last - first) / 2;
            if (lines[mid] <= value) first = mid + 1; else last = mid;
        }
        return Math.Clamp(first - 1, 0, lines.Length - 2);
    }

    private static Int128 TwiceArea(Polygon polygon)
    {
        Int128 area = Int128.Abs(Ring(polygon.ExteriorRing));
        for (int i = 0; i < polygon.NumInteriorRings; i++) area -= Int128.Abs(Ring(polygon.GetInteriorRingN(i)));
        return area;
        static Int128 Ring(LineString ring)
        {
            var coordinates = ring.Coordinates;
            var origin = coordinates[0]; Int128 sum = 0;
            for (int i = 1; i + 1 < coordinates.Length; i++)
            {
                var a = coordinates[i]; var b = coordinates[i + 1];
                if (a.X != Math.Round(a.X) || a.Y != Math.Round(a.Y))
                    throw new InvalidOperationException("공통 평면 교점이 정수 격자 위에 있지 않습니다.");
                sum += (Int128)(long)(a.X - origin.X) * (long)(b.Y - origin.Y) -
                    (Int128)(long)(a.Y - origin.Y) * (long)(b.X - origin.X);
            }
            return sum;
        }
    }

    private readonly record struct Node(long X, long Y)
    {
        public static Node Of(PointD p) { var key = PlanarTriangulation.GridKey(p); return new(key.X, key.Y); }
        public bool Before(Node other) => X < other.X || X == other.X && Y < other.Y;
    }
    private readonly record struct Edge(Node A, Node B)
    {
        public static Edge Of(Node a, Node b) => a.Before(b) ? new(a, b) : new(b, a);
    }
}
