using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>One planar partition and height field, output directly as meshes or NURBS with conforming display meshes.</summary>
public sealed class TerrainSurface
{
    private const int BlockCells = 8;
    public TerrainField Field { get; }
    public RoadArea ConsumedRoadArea { get; private set; } = null!;
    public RoadArea GroundRoadArea { get; private set; } = null!;
    public int MergedPatchBlocks { get; private set; }
    public double ReassignedRoleAreaSquareMetres => partition.ReassignedRoleAreaSquareMetres;
    public IReadOnlyList<SurfacePiece> Pieces => pieces;
    public int SurfaceCount { get; private set; }
    public int DisplayTriangles { get; private set; }
    public int MeshSurfaces { get; private set; }
    public int NurbsSurfaces { get; private set; }
    public double OverlayRoadAreaSquareMetres { get; private set; }
    public double PlanAreaSquareMetres { get; private set; }
    public double PlanAreaResidualSquareMetres { get; private set; }
    public double MaximumBoundaryChordErrorMetres { get; private set; }
    public double MaximumDisplayHeightErrorMetres { get; private set; }
    public int DroppedZeroAreaBranches => partition.DroppedZeroAreaBranches;
    public int NearSolidCount { get; private set; }
    public double? BottomElevationMetres { get; private set; }
    private readonly List<SurfacePiece> pieces = new();
    private readonly List<Part> parts = new();
    private readonly List<SurfaceTriangle>[] cells;
    private readonly List<PlanRegion>[] cellRegions;
    private readonly double drop;
    private readonly bool useNurbs, separateRoads;
    private readonly EditRegion? near;
    private readonly double nearDepthMetres;
    private readonly bool includeNear;
    private readonly PathsD? clip;
    private readonly GeoBounds outputBounds;
    private readonly RectD? localBounds;
    private PlanarPartition partition = null!;
    private PlanBoundarySources sourceBoundaries = null!;
    internal sealed record PatchRange(int Column, int Row, int Columns, int Rows);
    private sealed record Part(bool Road, bool Near, Brep? Brep, DisplayMeshBuilder Display,
        PatchRange? Patch = null, PathsD? Paths = null, IReadOnlyList<ExactNearRegions.Component>? Exact = null);
    private readonly record struct Key(double X, double Y) : IComparable<Key>
    {
        public int CompareTo(Key other) => X == other.X ? Y.CompareTo(other.Y) : X.CompareTo(other.X);
    }
    private readonly record struct Edge(Key A, Key B);
    private readonly record struct Side(Point2d A, Point2d B, bool Near);
    private sealed record EdgeUse(Point2d A, Point2d B, int PartId);

    private TerrainSurface(TerrainField field, double roadDrop, bool useNurbs, bool separateRoads,
        EditRegion? near, double nearDepthMetres, GeoBounds outputBounds, bool includeNear, PathsD? clip, RectD? localBounds)
    {
        Field = field; this.useNurbs = useNurbs; this.separateRoads = separateRoads;
        this.near = near; this.nearDepthMetres = nearDepthMetres;
        this.includeNear = includeNear; this.clip = clip;
        this.outputBounds = outputBounds;
        this.localBounds = localBounds;
        drop = separateRoads ? roadDrop : 0;
        cells = new List<SurfaceTriangle>[field.Columns * field.Rows];
        cellRegions = new List<PlanRegion>[field.Columns * field.Rows];
    }

    /// <summary>
    /// Builds terrain directly as meshes, or NURBS when requested.
    /// With road separation disabled, land remains continuous and roads overlay
    /// the same elevations; roadDrop is ignored and no step sides are generated.
    /// </summary>
    public static TerrainSurface Build(TerrainField field, RoadArea roads,
        double roadDrop, double scale, double toleranceMetres,
        bool useNurbs = false, bool separateRoads = true, EditRegion? near = null, double nearDepthMetres = 30, GeoBounds? outputBounds = null,
        bool includeNear = true, PathsD? clip = null)
        => Prepare(field, roads, roadDrop, useNurbs, separateRoads, near, nearDepthMetres, outputBounds, includeNear, clip)
            .Materialize(scale, toleranceMetres);

    internal static TerrainSurface Prepare(TerrainField field, RoadArea roads, double roadDrop,
        bool useNurbs = false, bool separateRoads = true, EditRegion? near = null, double nearDepthMetres = 30,
        GeoBounds? outputBounds = null, bool includeNear = true, PathsD? clip = null, RectD? localBounds = null)
    {
        if (!double.IsFinite(roadDrop) || roadDrop < 0 ||
            near != null && (!double.IsFinite(nearDepthMetres) || nearDepthMetres <= 0))
            throw new ArgumentException("지형의 도로 단차 또는 입체 깊이가 유효하지 않습니다.");
        var output = new TerrainSurface(field, roadDrop, useNurbs, separateRoads, near, nearDepthMetres, outputBounds ?? field.Bounds, includeNear, clip, localBounds);
        output.PreparePlan(roads);
        return output;
    }

    internal TerrainSurface Materialize(double scale, double toleranceMetres, TerrainSupportSurfaces? supports = null)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(toleranceMetres) || toleranceMetres <= 0)
            throw new ArgumentException("지형의 단위 배율 또는 허용 오차가 유효하지 않습니다.");
        if (supports is not null && !ReferenceEquals(supports.Field, Field))
            throw new ArgumentException("지형 지지면과 계산 높이장이 일치하지 않습니다.");
        try { Generate(scale, toleranceMetres, supports); return this; }
        catch
        {
            foreach (var geometry in parts.SelectMany(p => new GeometryBase?[] { p.Brep, p.Display.Mesh })
                         .Concat(pieces.Select(p => p.Geometry)).OfType<GeometryBase>().Distinct()) geometry.Dispose();
            throw;
        }
    }

    private PatchRange Range(PlanPatch group)
    {
        int column=group.Blocks.Min(block=>block.Column)*BlockCells;
        int row=group.Blocks.Min(block=>block.Row)*BlockCells;
        int lastColumn=Math.Min(Field.Columns,(group.Blocks.Max(block=>block.Column)+1)*BlockCells);
        int lastRow=Math.Min(Field.Rows,(group.Blocks.Max(block=>block.Row)+1)*BlockCells);
        return new(column,row,lastColumn-column,lastRow-row);
    }

    private void PreparePlan(RoadArea roads)
    {
        var nearBoundary = near is null ? new PathsD() : roads.PlanarNearBoundary ?? NearBoundaryPaths(roads.Outlines);
        partition = PlanarPartition.Build(Field, roads.Outlines, nearBoundary, outputBounds, clip, localBounds);
        var domain = new PathsD { new PathD { new(partition.West, partition.South), new(partition.East, partition.South),
            new(partition.East, partition.North), new(partition.West, partition.North) } };
        var outputRoads = partition.ReassignedRoleAreaSquareMetres > 0
            ? new PathsD(PlanarPartition.Merge(partition.Regions.Where(region => region.Road)).SelectMany(PlanarTriangulation.FromGridPolygon))
            : Clipper.Intersect(roads.Outlines, domain, FillRule.NonZero, EditRegion.ClipPrecision);
        ConsumedRoadArea = roads with { Outlines = outputRoads, AreaSquareMetres = Math.Abs(Clipper.Area(outputRoads)) };
        // Buildings can cross output bounds. Resolve their ground with the same
        // final role partition, retaining the acquired-only road area outside it.
        var groundRoads = partition.ReassignedRoleAreaSquareMetres > 0
            ? Clipper.BooleanOp(ClipType.Union, new PathsD(Clipper.Difference(roads.Outlines, domain, FillRule.NonZero, EditRegion.ClipPrecision)
                .Concat(outputRoads)), null, FillRule.NonZero, EditRegion.ClipPrecision)
            : roads.Outlines;
        GroundRoadArea = roads with { Outlines = groundRoads, AreaSquareMetres = Math.Abs(Clipper.Area(groundRoads)),
            PlanarNearBoundary = near is not null && partition.ReassignedRoleAreaSquareMetres > 0
                ? nearBoundary : roads.PlanarNearBoundary };
        sourceBoundaries = partition.SourceBoundaries;
    }

    private void Generate(double scale, double tolerance, TerrainSupportSurfaces? supports)
    {
        var exactNear = near == null ? null : new ExactNearRegions(near);
        var rolePaths = ConsumedRoadArea.ExactSource ?? ConsumedRoadArea.Outlines;
        var roleSource = exactNear is null ? null : new ExactNearRegions.SourceRegion(rolePaths);
        var pendingEdges = new Dictionary<Edge, EdgeUse>();
        var sides = new List<Side>();
        var regionParts = new Dictionary<PlanRegion, int>(ReferenceEqualityComparer.Instance);
        var landParts = new Dictionary<PlanRegion, int>(ReferenceEqualityComparer.Instance);
        var roadParts = new Dictionary<PlanRegion, int>(ReferenceEqualityComparer.Instance);
        var overlayLandParts = new Dictionary<int, int>();
        var meshParts = new Dictionary<(bool Road, bool Near), int>();
        var exactDomains = new Dictionary<string, PathsD>();
        var exactSplits = new Dictionary<(string Blocks, bool Road), ExactNearRegions.Result>();
        var sourceBlocks = partition.Regions.GroupBy(region => (region.Column / BlockCells, region.Row / BlockCells))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var patchGroups = partition.Patches(BlockCells, separateRoads, useNurbs, out int mergedBlocks, includeNear);
        MergedPatchBlocks = mergedBlocks;
        foreach (var group in patchGroups)
        {
            var patchRange = Range(group);
            bool isNear = group.Regions[0].Near;
            AddRole(group.Regions.Where(region => !separateRoads || !region.Road).ToArray(), false, landParts);
            AddRole(group.Regions.Where(region => region.Road).ToArray(), true, roadParts);
            if(exactNear is null) foreach (var region in group.Regions)
            {
                int id = region.Road ? roadParts[region] : landParts[region];
                regionParts.Add(region, id);
                if (!separateRoads && region.Road)
                {
                    int land = landParts[region];
                    if (overlayLandParts.TryGetValue(id, out int previous) && previous != land)
                        throw new InvalidOperationException("도로 표시 영역이 여러 지형 조각에 중복 배정되었습니다.");
                    overlayLandParts[id] = land;
                }
            }

            void AddRole(PlanRegion[] regions, bool road, Dictionary<PlanRegion, int> assignments)
            {
                if (regions.Length == 0) return;
                if (exactNear is null && !useNurbs && !isNear)
                {
                    // Meshes are built directly from the shared triangulation. A
                    // role is one mesh, with no temporary NURBS or per-cell objects.
                    if (!meshParts.TryGetValue((road, isNear), out int id))
                    {
                        id = parts.Count; parts.Add(new(road, isNear, null, new()));
                        meshParts.Add((road, isNear), id);
                    }
                    foreach (var region in regions) assignments.Add(region, id);
                    return;
                }
                if (exactNear is not null)
                {
                    var blockKey=string.Join(";",group.Blocks.OrderBy(b=>b.Row).ThenBy(b=>b.Column).Select(b=>$"{b.Column},{b.Row}"));
                    if(!exactDomains.TryGetValue(blockKey,out var patchDomain))
                        exactDomains.Add(blockKey,patchDomain=new PathsD(PlanarPartition.Merge(group.Blocks.SelectMany(block=>sourceBlocks[block])).SelectMany(PlanarTriangulation.FromGridPolygon)));
                    if(!exactSplits.TryGetValue((blockKey,road),out var split))
                        exactSplits.Add((blockKey,road),split=separateRoads||road
                            ? exactNear.Split(patchDomain,rolePaths:rolePaths,road:road,roleChanges:ConsumedRoadArea.ExactChanges,roleSource:roleSource)
                            : exactNear.Split(patchDomain));
                    foreach(var component in isNear?split.Near:split.Far)
                        parts.Add(new(road,isNear,null,new(),useNurbs||isNear?patchRange:null,Exact:new[]{component}));
                    return;
                }
                foreach (var component in PlanarPartition.Merge(regions))
                {
                    var paths = PlanarTriangulation.FromGridPolygon(component);
                    int id = parts.Count;
                    var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(component);
                    var members = regions.Where(region => prepared.Covers(region.Polygon)).ToArray();
                    parts.Add(new(road, isNear, null, new(), patchRange, paths));
                    foreach (var region in members)
                        if (!assignments.TryAdd(region, id)) throw new InvalidOperationException("지형 평면 영역에 NURBS 면이 중복 배정되었습니다.");
                }
                if (regions.Any(region => !assignments.ContainsKey(region)))
                    throw new InvalidOperationException("지형 평면 영역의 NURBS 면을 찾지 못했습니다.");
            }
        }
        if(exactNear is not null)
        {
            var domain=ContourPaths.Rectangle(partition.West,partition.South,partition.East,partition.North);
            if(clip is not null)domain=ContourPaths.Intersect(domain,clip);
            var expected=includeNear?ContourPaths.Area(domain):exactNear.Split(domain).Far.Sum(c=>c.Area);
            var actual=parts.Where(p=>separateRoads||!p.Road).Sum(p=>p.Exact!.Sum(c=>c.Area));
            if(Math.Abs(actual-expected)>Math.Max(.01,expected*1e-7))
                throw new InvalidOperationException("정확한 땅·도로 분할의 전체 영역 보존 검사에 실패했습니다.");
        }
        double exactSampleArea=0;
        int cellCount = 0;
        var outputRegions = partition.Regions.Where(region => includeNear || !region.Near).ToArray();
        // Preserve partition order for both triangulation and later road-boundary draping.
        foreach (var region in outputRegions)
        {
            int key = region.Row * Field.Columns + region.Column;
            (cellRegions[key] ??= new()).Add(region);
        }
        var exactCells=exactNear is null?null:ExactCells();
        var cellKeys=exactCells?.Keys.Order().ToArray()??outputRegions.Select(r=>r.Row*Field.Columns+r.Column).Distinct().Order().ToArray();
        foreach (int cellKey in cellKeys)
        {
            int row=cellKey/Field.Columns,col=cellKey%Field.Columns;
            var cell=cellRegions[cellKey] ??= new();
            var triangles=exactCells is null?cell.SelectMany(region=>PlanarTriangulation.Triangulate(region.Paths)
                .Select(t=>new SurfaceTriangle(new(t[0].x,t[0].y),new(t[1].x,t[1].y),new(t[2].x,t[2].y),regionParts[region]))).ToList():exactCells[cellKey];
            List<SurfaceTriangle> refined;
            try
            {
                var constraints = triangles.SelectMany(t=>new[]{t.A,t.B,t.C}).ToArray();
                var ground=exactCells is not null&&!separateRoads?triangles.Where(t=>!parts[t.PartId].Road):triangles;
                refined=PlanarSurfaceMesh.ConformAndRefine(ground,Field,constraints);
                if(exactCells is not null&&!separateRoads)
                    foreach(var overlay in triangles.Where(t=>parts[t.PartId].Road).GroupBy(t=>t.PartId))
                        refined.AddRange(PlanarSurfaceMesh.ConformAndRefine(overlay,Field,constraints));
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"지형 셀 {col},{row}: {e.Message}", e);
            }
            cells[cellKey] = refined;
            cellCount++;
            var edges = new Dictionary<Edge, List<EdgeUse>>();
            foreach (var t in refined)
            {
                var part = parts[t.PartId];
                Point3d a = ModelPoint(t.A, part), b = ModelPoint(t.B, part), c = ModelPoint(t.C, part);
                parts[t.PartId].Display.Add(a, b, c);
                if(exactCells is not null&&!separateRoads&&part.Road) { OverlayRoadAreaSquareMetres+=Math.Abs(Cross(t.A,t.B,t.C))/2;continue; }
                double area = Math.Abs(Cross(t.A, t.B, t.C)) / 2;
                PlanAreaSquareMetres += area;
                if (overlayLandParts.TryGetValue(t.PartId, out int landId))
                {
                    // The exact same vertices/triangles also fill the land below
                    // the road overlay, so there is no offset or second approximation.
                    if(parts[landId].Exact is null) parts[landId].Display.Add(a, b, c);
                    OverlayRoadAreaSquareMetres += area;
                }
                AddEdge(t.A, t.B, t.PartId); AddEdge(t.B, t.C, t.PartId); AddEdge(t.C, t.A, t.PartId);
            }
            foreach (var (key, uses) in edges)
            {
                if (uses.Count == 2) Boundary(uses[0], uses[1]);
                else if (uses.Count == 1)
                {
                    if (pendingEdges.Remove(key, out var previous)) Boundary(previous, uses[0]);
                    else pendingEdges.Add(key, uses[0]);
                }
                else throw new InvalidOperationException("지형 분할에 중첩된 면이 있습니다.");
                var e = uses[0]; var m = Mid(e.A, e.B);
                double error = Math.Abs(Field.SampleModelLocal(m.X, m.Y) -
                    (Field.SampleModelLocal(e.A.X, e.A.Y) + Field.SampleModelLocal(e.B.X, e.B.Y)) / 2);
                MaximumDisplayHeightErrorMetres = Math.Max(MaximumDisplayHeightErrorMetres, error);
            }
            void AddEdge(Point2d a, Point2d b, int id)
            {
                var key = EdgeKey(a, b);
                if (!edges.TryGetValue(key, out var uses)) edges[key] = uses = new();
                uses.Add(new(a, b, id));
            }
        }
        if (cellCount != cellKeys.Length) throw new InvalidOperationException("공통 평면 분할에 빠진 지형 셀이 있습니다.");
        // The full partition was checked before filtering. Emitted edges may only
        // end on the outer/clip boundary or the deliberately omitted Near boundary.
        foreach (var edge in pendingEdges.Values)
            if (!OnOuter(edge.A, edge.B) && !partition.OnClipBoundary(edge.A.X, edge.A.Y, edge.B.X, edge.B.Y) &&
                (includeNear || !OnNear(edge.A,edge.B)))
            {
                throw new InvalidOperationException($"지형 공통 경계에 연결되지 않은 선분: {edge.A.X:R},{edge.A.Y:R} → {edge.B.X:R},{edge.B.Y:R}; part {edge.PartId}, remaining {pendingEdges.Count}.");
            }
        // Audit triangles against their authoritative sampled partition. The old
        // Near proxy has different chords and is not the emitted Far domain.
        double expectedOutputArea=exactNear is null?outputRegions.Sum(region=>region.Polygon.Area)/1e12:exactSampleArea;
        PlanAreaResidualSquareMetres = Math.Abs(PlanAreaSquareMetres - expectedOutputArea);
        if (PlanAreaResidualSquareMetres > Math.Max(.01, PlanAreaSquareMetres * 1e-7))
            throw new InvalidOperationException("땅·도로·편집 영역의 면적 보존 검사에 실패했습니다.");
        // Public plan coverage describes the complete partition; the residual
        // above audits only the continuous geometry actually emitted.
        PlanAreaSquareMetres = partition.PlanAreaSquareMetres;
        var patches = new Dictionary<PatchRange, NurbsSurface>();
        try
        {
            for (int id = 0; id < parts.Count; id++)
            {
                var part = parts[id];
                if(part.Exact is not null && part.Patch is {} exactRange)
                {
                    if(!patches.TryGetValue(exactRange,out var exactPatch))
                    {
                        exactPatch=supports is null?TerrainSurfaceGeometry.CreatePatch(Field,exactRange.Column,exactRange.Row,exactRange.Columns,exactRange.Rows)
                            :supports.Get(exactRange.Column,exactRange.Row,exactRange.Columns,exactRange.Rows);
                        patches.Add(exactRange,exactPatch);
                    }
                    foreach(var component in part.Exact)
                    {
                        var loops=ExactProfileGeometry.Curves(component);
                        Brep? face=null;Mesh? display=null;
                        try
                        {
                            face=TerrainSurfaceGeometry.Trim(loops,exactPatch,tolerance);
                            ValidateModelFace(face);
                            if(part.Road)face.Translate(0,0,-drop);
                            face.Transform(Transform.Scale(Point3d.Origin,scale));
                            display=part.Display.Finish();display.Transform(Transform.Scale(Point3d.Origin,scale));
                            DisplayTriangles+=display.Faces.Count;
                            TerrainSurfaceGeometry.AttachDisplayMesh(face,display);display=null;
                            pieces.Add(new(face,Layer(part.Road?"Roads":"Terrain",part.Near),part.Road?"Road":"Land",Zone(part.Near)));
                            face=null;NurbsSurfaces++;SurfaceCount++;
                        }
                        finally{foreach(var loop in loops)loop.Dispose();display?.Dispose();face?.Dispose();}
                    }
                    continue;
                }
                var originalMesh = part.Display.Finish();
                Mesh mesh;
                try { mesh = part.Exact is not null && part.Patch is null ? originalMesh : CoplanarMeshReduction.Reduce(originalMesh); }
                catch { originalMesh.Dispose(); throw; }
                if (!ReferenceEquals(mesh, originalMesh)) originalMesh.Dispose();
                bool meshTransferred = false;
                try
                {
                    if (mesh.Faces.Count == 0) throw new InvalidOperationException("지형 면의 메시가 비어 있습니다.");
                    for (int vertex = 0; vertex < mesh.Vertices.Count; vertex++)
                    {
                        var p = mesh.Vertices.Point3dAt(vertex); var normal = Field.ModelNormalLocal(p.X, p.Y);
                        mesh.Normals.SetNormal(vertex, (float)normal.X, (float)normal.Y, (float)normal.Z);
                    }
                    mesh.Transform(Transform.Scale(Point3d.Origin, scale));
                    GeometryBase geometry;
                    if (part.Patch is { } range)
                    {
                        // Share one support surface across faces in this patch.
                        if (!patches.TryGetValue(range, out var patch))
                        {
                            patch = supports is null
                                ? TerrainSurfaceGeometry.CreatePatch(Field, range.Column, range.Row, range.Columns, range.Rows)
                                : supports.Get(range.Column, range.Row, range.Columns, range.Rows);
                            patches.Add(range, patch);
                        }
                        IReadOnlyList<Brep> trimmed;
                        trimmed = TerrainSurfaceGeometry.Trim(sourceBoundaries.Restore(part.Paths!), patch, tolerance);
                        if (trimmed.Count != 1)
                        {
                            foreach (var failed in trimmed) failed.Dispose();
                            throw new InvalidOperationException("하나의 지형 평면 영역이 하나의 NURBS 면으로 생성되지 않았습니다.");
                        }
                        var brep = trimmed[0];
                        parts[id] = part = part with { Brep = brep };
                        ValidateModelFace(brep);
                        if (part.Road) brep.Translate(0, 0, -drop);
                        brep.Transform(Transform.Scale(Point3d.Origin, scale));
                        TerrainSurfaceGeometry.AttachDisplayMesh(brep, mesh);
                        meshTransferred = true;
                        geometry = brep; NurbsSurfaces++;
                    }
                    else { geometry = mesh; MeshSurfaces++; }
                    DisplayTriangles += mesh.Faces.Count;
                    pieces.Add(new(geometry, Layer(part.Road ? "Roads" : "Terrain", part.Near), part.Road ? "Road" : "Land", Zone(part.Near)));
                    meshTransferred = true;
                    SurfaceCount++;
                }
                finally { if (!meshTransferred) mesh.Dispose(); }
            }
        }
        finally { if (supports is null) foreach (var patch in patches.Values) patch.Dispose(); }
        if(near is not null)
        {
            foreach(var group in pieces.Where(p=>p.Zone=="Far"&&p.Geometry is Mesh&&p.Role is "Land" or "Road").GroupBy(p=>p.Role).ToArray())
            {
                var builder=new DisplayMeshBuilder();
                using var combined=builder.Mesh;
                foreach(var piece in group){builder.Append((Mesh)piece.Geometry);pieces.Remove(piece);piece.Geometry.Dispose();}
                var reduced=CoplanarMeshReduction.Reduce(combined);
                if(ReferenceEquals(reduced,combined))reduced=combined.DuplicateMesh();
                pieces.Add(new(reduced,group.First().Layer,group.Key,"Far"));
                SurfaceCount-=group.Count()-1;MeshSurfaces-=group.Count()-1;
            }
        }
        if (drop > 0) AddSides(near == null ? sides : sides.Where(side => !side.Near).ToList(), scale);
        if (near != null && includeNear)
        {
            var sheets = pieces.Where(piece => piece.Zone == "Near").ToArray();
            var solids = NearTerrainSolid.Build(sheets, nearDepthMetres, scale, tolerance, separateRoads);
            foreach (var sheet in sheets)
            {
                pieces.Remove(sheet);
                if (!solids.Pieces.Any(piece => ReferenceEquals(piece.Geometry, sheet.Geometry))) sheet.Geometry.Dispose();
            }
            pieces.AddRange(solids.Pieces); NearSolidCount = solids.SolidCount;
            BottomElevationMetres = solids.BottomElevationMetres;
            var tops = pieces.Where(piece => piece.Role is "Land" or "Road").ToArray();
            SurfaceCount = tops.Length; NurbsSurfaces = tops.Count(piece => piece.Geometry is Brep);
            MeshSurfaces = tops.Count(piece => piece.Geometry is Mesh);
            DisplayTriangles = pieces.Sum(piece => piece.Geometry is Mesh mesh ? mesh.Faces.Count :
                piece.Geometry is Brep brep ? brep.Faces.Sum(face => face.GetMesh(MeshType.Render)?.Faces.Count ?? 0) : 0);
        }

        bool OnNear(Point2d a,Point2d b)=>near!.Boundaries.SelectMany(loop=>loop.Segments).Any(edge=>
            EditBoundaryMath.OnSegment(new(a.X,a.Y),edge,EditRegion.MaximumChordErrorMetres+2*ModelPlanPolicy.PrecisionMetres)&&
            EditBoundaryMath.OnSegment(new(b.X,b.Y),edge,EditRegion.MaximumChordErrorMetres+2*ModelPlanPolicy.PrecisionMetres));

        Dictionary<int,List<SurfaceTriangle>> ExactCells()
        {
            var edges=parts.SelectMany(p=>p.Exact!).SelectMany(c=>c.Rings).SelectMany(r=>r.Edges).ToArray();
            var samples=new Dictionary<EditBoundarySegment,SortedDictionary<double,EditBoundaryPoint>>();
            NearBoundaryPaths(ConsumedRoadArea.Outlines,edges,(source,t,point)=>
            {
                if(!samples.TryGetValue(source,out var values))samples.Add(source,values=new());
                values[t]=point;
            });
            var raw=new List<PathsD>();
            for(int id=0;id<parts.Count;id++)
            {
                var paths=new PathsD(parts[id].Exact![0].Rings.Select(ring=>new PathD(ring.Edges.SelectMany(edge=>
                {
                    if(edge.Source.Circle is null)return new[]{new PointD(edge.Start.X,edge.Start.Y)};
                    var values=samples[edge.Source].Where(p=>p.Key>Math.Min(edge.From,edge.To)&&p.Key<Math.Max(edge.From,edge.To));
                    if(edge.To<edge.From)values=values.Reverse();
                    return new[]{new PointD(edge.Start.X,edge.Start.Y)}.Concat(values.Select(p=>new PointD(p.Value.X,p.Value.Y)));
                }))));
                raw.Add(paths);
            }
            var grid=new PathsD();
            for(int x=0;x<=Field.Columns;x++)grid.Add(new(){new(Field.ModelX(x),Field.ModelY(0)),new(Field.ModelX(x),Field.ModelY(Field.Rows))});
            for(int y=0;y<=Field.Rows;y++)grid.Add(new(){new(Field.ModelX(0),Field.ModelY(y)),new(Field.ModelX(Field.Columns),Field.ModelY(y))});
            var shared=PlanarBoundaryNoding.Node(raw,grid);
            var result=new Dictionary<int,List<SurfaceTriangle>>();
            for(int id=0;id<parts.Count;id++)
            {
                var paths=shared[id];
                var region=ContourPaths.UnionGeometry(paths);var bounds=Clipper.GetBounds(paths);
                if(separateRoads||!parts[id].Road)exactSampleArea+=region.Area/1e12;
                var lo=Field.ModelCell(bounds.left,bounds.top);var hi=Field.ModelCell(bounds.right,bounds.bottom);
                for(int y=lo.Row;y<=hi.Row;y++)for(int x=lo.Column;x<=hi.Column;x++)
                {
                    var box=PlanarTriangulation.ToGridGeometry(ContourPaths.Rectangle(Field.ModelX(x),Field.ModelY(y),Field.ModelX(x+1),Field.ModelY(y+1)));
                    var section=ContourPaths.FromGeometry(ContourPaths.OverlayGeometry(region,box,NetTopologySuite.Operation.Overlay.SpatialFunction.Intersection));
                    if(section.Count==0)continue;
                    int key=y*Field.Columns+x;
                    if(!result.TryGetValue(key,out var triangles))result.Add(key,triangles=new());
                    triangles.AddRange(PlanarTriangulation.Triangulate(section).Select(t=>new SurfaceTriangle(new(t[0].x,t[0].y),new(t[1].x,t[1].y),new(t[2].x,t[2].y),id)));
                }
            }
            return result;
        }

        void Boundary(EdgeUse a, EdgeUse b)
        {
            var pa = parts[a.PartId]; var pb = parts[b.PartId];
            if (a.PartId != b.PartId)
            {
                var m = Mid(a.A, a.B);
                MaximumBoundaryChordErrorMetres = Math.Max(MaximumBoundaryChordErrorMetres,
                    Math.Abs(Field.SampleModelLocal(m.X, m.Y) -
                    (Field.SampleModelLocal(a.A.X, a.A.Y) + Field.SampleModelLocal(a.B.X, a.B.Y)) / 2));
            }
            if (pa.Road != pb.Road)
            {
                // Orient the step from the land side consistently.
                var landEdge = pa.Road ? b : a;
                sides.Add(new(landEdge.A, landEdge.B, parts[landEdge.PartId].Near));
            }
        }
    }

    private static void ValidateModelFace(Brep brep)
    {
        // The model minimum-detail policy belongs to connected plan regions.
        // A support/role boundary may subdivide one region into smaller trims;
        // each retained trim must still have a finite, positive actual area.
        foreach (var face in brep.Faces)
        {
            double twiceArea = 0;
            foreach (var loop in face.Loops)
            {
                var anchor = loop.Trims[0].PointAtStart;
                foreach (var trim in loop.Trims)
                {
                    using var curve = trim.DuplicateCurve();
                    twiceArea += NearTerrainCap.SignedAreaIntegral(curve, anchor);
                }
            }
            double area = Math.Abs(twiceArea) / 2;
            if (!double.IsFinite(area) || area <= 0)
                throw new InvalidOperationException($"정확한 지형 절단 후 유효한 양의 면적이 필요합니다: {area:R}m².");
        }
    }

    private void AddSides(List<Side> sides, double scale)
    {
        // Match the land representation. Mesh sides are emitted
        // directly; NURBS sides use exact boundary curves and matching render edges.
        AddChains(sides);

        void AddChains(List<Side> edges)
        {
            // Extruding an XY point contact turns it into a whole vertical edge.
            // Putting all curb loops in one mesh would weld four or more faces
            // there. Both representations therefore use the same unbranched
            // chains, preserving source contacts without merging their topology.
            var incident = new Dictionary<Key, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                Add(KeyOf(edges[i].A), i); Add(KeyOf(edges[i].B), i);
            }
            var visited = new bool[edges.Count];
            for (int seed = 0; seed < edges.Count; seed++)
            {
                if (visited[seed]) continue;
                var chain = new LinkedList<(Point2d A, Point2d B)>();
                var side = edges[seed]; visited[seed] = true; chain.AddLast((side.A, side.B));
                Extend(false); Extend(true);
                Brep? sideBrep = null;
                DisplayMeshBuilder? display = null;
                bool transferred = false, meshAttached = false;
                try
                {
                    if (useNurbs)
                    {
                        var segments = chain.Select(e => (Point(e.A, false), Point(Mid(e.A, e.B), false), Point(e.B, false))).ToList();
                        sideBrep = TerrainSurfaceGeometry.CreateRoadSide(segments, drop);
                    }
                    display = new DisplayMeshBuilder();
                    foreach (var e in chain) AddQuad(display, e.A, e.B);
                    var mesh = display.Finish();
                    mesh.Transform(Transform.Scale(Point3d.Origin, scale));
                    if (sideBrep != null)
                    {
                        sideBrep.Transform(Transform.Scale(Point3d.Origin, scale));
                        TerrainSurfaceGeometry.AttachDisplayMesh(sideBrep, mesh);
                        meshAttached = true;
                    }
                    pieces.Add(new(sideBrep is null ? mesh : sideBrep, Layer("RoadSides", false), "RoadSide", Zone(false)));
                    transferred = true;
                }
                finally
                {
                    if (!transferred)
                    {
                        if (!meshAttached) display?.Mesh.Dispose();
                        sideBrep?.Dispose();
                    }
                }
                void Extend(bool front)
                {
                    while (true)
                    {
                        var end = front ? chain.First!.Value.A : chain.Last!.Value.B;
                        var connected = incident[KeyOf(end)];
                        // Use source degree, not remaining unvisited degree:
                        // otherwise later chains would cross the same junction
                        // differently depending on iteration order.
                        if (connected.Count != 2) break;
                        var candidates = connected.Where(i => !visited[i]).ToArray();
                        if (candidates.Length != 1) break;
                        int i = candidates[0]; var next = edges[i];
                        if (KeyOf(front ? next.B : next.A) != KeyOf(end)) break;
                        visited[i] = true;
                        if (front) chain.AddFirst((next.A, next.B)); else chain.AddLast((next.A, next.B));
                    }
                }
            }
            void Add(Key key, int index) { if (!incident.TryGetValue(key, out var list)) incident[key] = list = new(); list.Add(index); }
        }
        void AddQuad(DisplayMeshBuilder display, Point2d from, Point2d to)
        {
            var a = Point(from, false); var b = Point(to, false);
            var c = new Point3d(b.X, b.Y, b.Z - drop); var d = new Point3d(a.X, a.Y, a.Z - drop);
            // Land lies to the left of A→B; the exposed face points right.
            display.Add(a, c, b); display.Add(a, d, c);
        }
    }

    public double Sample(GeoPoint p) { var xy = Field.Projection.ToLocal(p); return SampleLocal(xy.X, xy.Y); }
    public double SampleLocal(double x, double y)
    {
        var (col, row) = partition.CellAt(x, y); var point = new Point2d(x, y);
        foreach (var t in cells[row * Field.Columns + col])
            if (Contains(t, point)) return Height(t, point);
        throw new InvalidOperationException("분할된 지표면에서 높이를 찾을 수 없습니다.");
    }
    private double Height(SurfaceTriangle t, Point2d p)
    {
        var part = parts[t.PartId];
        double height;
        if (part.Patch != null) height = Field.SampleModelLocal(p.X, p.Y);
        else
        {
            double denominator = Cross(t.A, t.B, t.C);
            double u = Cross(t.A, p, t.C) / denominator, v = Cross(t.A, t.B, p) / denominator;
            height = (1 - u - v) * Field.SampleModelLocal(t.A.X, t.A.Y) +
                u * Field.SampleModelLocal(t.B.X, t.B.Y) + v * Field.SampleModelLocal(t.C.X, t.C.Y);
        }
        return height - (part.Road ? drop : 0);
    }

    public List<Point3d> Drape(IReadOnlyList<GeoPoint> path, double scale, double offset = 0)
    {
        var points = new List<Point3d>();
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var aa = Field.Projection.ToLocal(path[i]); var bb = Field.Projection.ToLocal(path[i + 1]);
            var a = new Point2d(aa.X, aa.Y); var b = new Point2d(bb.X, bb.Y);
            var coarse = partition.LocalSegmentCuts(a.X, a.Y, b.X, b.Y).ToArray();
            for (int k = 0; k + 1 < coarse.Length; k++)
            {
                var start = Lerp(a, b, coarse[k]); var end = Lerp(a, b, coarse[k + 1]); var mid = Mid(start, end);
                var (col, row) = partition.CellAt(mid.X, mid.Y); var ts = cells[row * Field.Columns + col];
                var cuts = new SortedSet<double> { 0, 1 };
                // NURBS height only changes formula at source cells and road
                // boundaries. Actual mesh geometry also changes plane at its edges.
                foreach (var triangle in ts.Where(t => parts[t.PartId].Patch == null))
                {
                    Cut(triangle.A, triangle.B); Cut(triangle.B, triangle.C); Cut(triangle.C, triangle.A);
                }
                foreach (var region in cellRegions[row * Field.Columns + col].Where(region => region.Road))
                foreach (var ring in region.Paths)
                for (int edge = 0; edge < ring.Count; edge++)
                {
                    var c = ring[edge]; var d = ring[(edge + 1) % ring.Count];
                    Cut(new(c.x, c.y), new(d.x, d.y));
                }
                var values = cuts.ToArray();
                for (int q = 0; q + 1 < values.Length; q++)
                {
                    var p = Lerp(start, end, values[q]); var r = Lerp(start, end, values[q + 1]); var m = Mid(p, r);
                    int found = ts.FindIndex(t => Contains(t, m));
                    if (found < 0)
                    {
                        throw new InvalidOperationException($"지형 셀 {col},{row}: 경계 곡선을 지표면에 놓을 수 없습니다.");
                    }
                    AddSpan(p, r, ts[found], 0);
                }
                void Cut(Point2d c, Point2d d)
                {
                    var dx = end.X - start.X; var dy = end.Y - start.Y;
                    var ex = d.X - c.X; var ey = d.Y - c.Y; var den = dx * ey - dy * ex;
                    if (Math.Abs(den) < 1e-16) return;
                    var x = c.X - start.X; var y = c.Y - start.Y;
                    var u = (x * ey - y * ex) / den; var v = (x * dy - y * dx) / den;
                    if (u > 1e-10 && u < 1 - 1e-10 && v >= -1e-8 && v <= 1 + 1e-8) cuts.Add(u);
                }
            }
        }
        if (path.Count > 1 && points.Count > 1 && path[0].Lon == path[^1].Lon && path[0].Lat == path[^1].Lat)
        {
            // A closed source can start on a road step. Its last and first
            // spans then have different elevations and need the same explicit
            // vertical connection as an intermediate step crossing.
            if (points[^1].DistanceTo(points[0]) <= 1e-8 * scale) points[^1] = points[0];
            else points.Add(points[0]);
        }
        return points;
        void AddSpan(Point2d a, Point2d b, SurfaceTriangle t, int depth)
        {
            double Z(Point2d p) => Height(t, p);
            var mid = Mid(a, b); double za = Z(a), zb = Z(b), zm = Z(mid);
            if (depth < 15 && Math.Abs(zm - (za + zb) / 2) > .001) { AddSpan(a, mid, t, depth + 1); AddSpan(mid, b, t, depth + 1); return; }
            Push(new(a.X * scale, a.Y * scale, (za + offset) * scale));
            Push(new(b.X * scale, b.Y * scale, (zb + offset) * scale));
        }
        void Push(Point3d p) { if (points.Count == 0 || points[^1].DistanceTo(p) > 1e-8 * scale) points.Add(p); }
    }

    private string Zone(bool isNear) => near == null ? "All" : isNear ? "Near" : "Far";
    private string Layer(string role, bool isNear) => near == null ? role : Zone(isNear) + "::Surfaces";

    private PathsD NearBoundaryPaths(PathsD roads,IReadOnlyList<ExactNearRegions.Edge>? known=null,Action<EditBoundarySegment,double,EditBoundaryPoint>? sample=null)
    {
        // Exact intersections precede chord sampling. Otherwise a source corner
        // that just touches the circle can fall outside an inscribed chord and
        // become a fictitious independent Far sliver. Grid intersections use
        // the same rule so shared cell boundaries inherit those exact nodes.
        var constraints = new List<EditBoundarySegment>();
        foreach (var ring in roads)
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            constraints.Add(new(new(a.x, a.y), new(b.x, b.y)));
        }
        for (int col = 0; col <= Field.Columns; col++)
        {
            double x = Field.ModelX(col);
            constraints.Add(new(new(x, Field.ModelY(0)), new(x, Field.ModelY(Field.Rows))));
        }
        for (int row = 0; row <= Field.Rows; row++)
        {
            double y = Field.ModelY(row);
            constraints.Add(new(new(Field.ModelX(0), y), new(Field.ModelX(Field.Columns), y)));
        }
        var output = new PathsD();
        foreach (var loop in near!.Boundaries)
        {
            var path = new PathD();
            foreach (var segment in loop.Segments)
            {
                var cuts = new SortedSet<double> { 0, 1 };
                foreach(var edge in known??Array.Empty<ExactNearRegions.Edge>())
                    if(edge.Source==segment){cuts.Add(edge.From);cuts.Add(edge.To);}
                foreach (var line in constraints)
                foreach (var point in EditBoundaryMath.Intersections(segment, line, 1e-8))
                {
                    double parameter;
                    if (segment.Circle is { } circle)
                    {
                        double angle = Math.Atan2(point.Y - circle.CenterY, point.X - circle.CenterX);
                        double offset = EditBoundaryMath.PositiveAngle(angle - segment.StartAngle);
                        parameter = offset >= 2 * Math.PI - 1e-12 ? 0 : offset / segment.SweepRadians;
                    }
                    else
                    {
                        var delta = segment.End - segment.Start;
                        parameter = EditBoundaryMath.Dot(point - segment.Start, delta) / delta.LengthSquared;
                    }
                    if (parameter > 1e-12 && parameter < 1 - 1e-12) cuts.Add(parameter);
                }
                var values = cuts.ToArray();
                double from = values[0];
                for (int i = 1; i < values.Length; i++)
                {
                    if (values[i] - from <= 1e-12) continue;
                    Add(segment, from, values[i], 0); from = values[i];
                }
            }
            output.Add(path);
            void Add(EditBoundarySegment segment, double from, double to, int depth)
            {
                var a = segment.PointAt(from); var b = segment.PointAt(to);
                double chord = segment.Circle is { } circle
                    ? circle.RadiusMetres * (1 - Math.Cos(segment.SweepRadians * (to - from) / 2)) : 0;
                double error = chord * (chord > 0 ? LocalSpatialFactor(segment, from, to, a, b) : 1) + HeightError(a, b);
                if (error > .0001 || segment.IsArc && segment.SweepRadians * (to - from) > Math.PI / 16)
                {
                    double middle = (from + to) / 2; Add(segment, from, middle, depth + 1); Add(segment, middle, to, depth + 1); return;
                }
                path.Add(new(a.X, a.Y));sample?.Invoke(segment,from,a);
            }
        }
        return output;

        double LocalSpatialFactor(EditBoundarySegment segment, double from, double to, EditBoundaryPoint a, EditBoundaryPoint b)
        {
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            if (segment.Circle is { } circle)
            {
                double start = segment.StartAngle + segment.SweepRadians * from;
                double end = segment.StartAngle + segment.SweepRadians * to;
                for (double angle = Math.Ceiling(start / (Math.PI / 2)) * (Math.PI / 2); angle <= end; angle += Math.PI / 2)
                {
                    var point = EditBoundaryMath.CirclePoint(circle, angle);
                    minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                    minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
                }
            }
            // This rectangle contains the complete exact arc and its chord.
            // Each bilinear cell's derivatives are linear in the other axis;
            // extrema occur at its ends. Their maxima therefore bound height
            // change throughout the arc/chord gap, without unrelated steep
            // terrain elsewhere in the model forcing extra boundary samples.
            const double rounding = 2 * PlanarTriangulation.CoordinateToleranceMetres;
            var lo = Field.ModelCell(Math.Max(Field.ModelX(0), minX - rounding), Math.Max(Field.ModelY(0), minY - rounding));
            var hi = Field.ModelCell(Math.Min(Field.ModelX(Field.Columns), maxX + rounding), Math.Min(Field.ModelY(Field.Rows), maxY + rounding));
            double sx = 0, sy = 0;
            for (int row = lo.Row; row <= hi.Row; row++)
            for (int col = lo.Column; col <= hi.Column; col++)
            {
                double a00 = Field.Node(col, row), a10 = Field.Node(col + 1, row);
                double a01 = Field.Node(col, row + 1), a11 = Field.Node(col + 1, row + 1);
                sx = Math.Max(sx, Math.Max(Math.Abs(a10 - a00), Math.Abs(a11 - a01)) / (Field.ModelX(col + 1) - Field.ModelX(col)));
                sy = Math.Max(sy, Math.Max(Math.Abs(a01 - a00), Math.Abs(a11 - a10)) / (Field.ModelY(row + 1) - Field.ModelY(row)));
            }
            return Math.Sqrt(1 + sx * sx + sy * sy);
        }

        double HeightError(EditBoundaryPoint a, EditBoundaryPoint b)
        {
            double za = Field.SampleModelLocal(a.X, a.Y), zb = Field.SampleModelLocal(b.X, b.Y), maximum = 0;
            var cuts = Field.ModelSegmentCuts(a.X, a.Y, b.X, b.Y).ToArray();
            for (int i = 1; i < cuts.Length; i++)
            {
                double lo = cuts[i - 1], hi = cuts[i], mid = (lo + hi) / 2;
                double da = Difference(lo), db = Difference(hi), dm = Difference(mid);
                maximum = Math.Max(maximum, Math.Max(Math.Abs(da), Math.Max(Math.Abs(db), Math.Abs(dm))));
                double q = 2 * (da + db - 2 * dm), l = db - da - q;
                if (Math.Abs(q) > 1e-16)
                {
                    double t = -l / (2 * q);
                    if (t > 0 && t < 1) maximum = Math.Max(maximum, Math.Abs(da + l * t + q * t * t));
                }
            }
            return maximum;
            double Difference(double t)
            { var p = a + (b - a) * t; return Field.SampleModelLocal(p.X, p.Y) - (za + (zb - za) * t); }
        }
    }

    private Point3d ModelPoint(Point2d p, Part part) => Point(p, part.Road);

    private Point3d Point(Point2d p, bool road) => new(p.X, p.Y, Field.SampleModelLocal(p.X, p.Y) - (road ? drop : 0));
    private static bool Contains(SurfaceTriangle t, Point2d p)
    {
        double den = Cross(t.A, t.B, t.C), u = Cross(t.A, p, t.C) / den, v = Cross(t.A, t.B, p) / den;
        // Queries retain source XY, while the planar graph uses a fixed metre
        // precision. Use that same distance tolerance, not a dimensionless
        // barycentric tolerance that collapses for narrow triangles.
        const double tolerance = PlanarTriangulation.CoordinateToleranceMetres;
        return u * den >= -tolerance * t.A.DistanceTo(t.C)
            && v * den >= -tolerance * t.A.DistanceTo(t.B)
            && (1 - u - v) * den >= -tolerance * t.B.DistanceTo(t.C);
    }
    private bool OnOuter(Point2d a, Point2d b) =>
        Math.Abs(a.X - partition.West) < 2e-6 && Math.Abs(b.X - partition.West) < 2e-6 ||
        Math.Abs(a.X - partition.East) < 2e-6 && Math.Abs(b.X - partition.East) < 2e-6 ||
        Math.Abs(a.Y - partition.South) < 2e-6 && Math.Abs(b.Y - partition.South) < 2e-6 ||
        Math.Abs(a.Y - partition.North) < 2e-6 && Math.Abs(b.Y - partition.North) < 2e-6;
    private static Point2d Mid(Point2d a, Point2d b) => new((a.X+b.X)/2,(a.Y+b.Y)/2);
    private static Point2d Lerp(Point2d a, Point2d b, double t) => new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t);
    private static double Cross(Point2d a, Point2d b, Point2d c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    private static Key KeyOf(Point2d p) => new(p.X, p.Y);
    private static Edge EdgeKey(Point2d a, Point2d b) { var ka=KeyOf(a); var kb=KeyOf(b); return ka.CompareTo(kb)<0?new(ka,kb):new(kb,ka); }
    internal sealed class DisplayMeshBuilder
    {
        public Mesh Mesh { get; } = new();
        private readonly Dictionary<(double X,double Y,double Z),int> vertices = new();
        public DisplayMeshBuilder() { Mesh.Vertices.UseDoublePrecisionVertices = true; }
        private int Vertex(Point3d p)
        {
            var key=(p.X, p.Y, p.Z);
            if (!vertices.TryGetValue(key,out int index)) { index=Mesh.Vertices.Add(p); vertices.Add(key,index); }
            return index;
        }
        public void Add(Point3d a, Point3d b, Point3d c)
        {
            int ia=Vertex(a),ib=Vertex(b),ic=Vertex(c);
            if(ia==ib||ib==ic||ic==ia)return;
            if ((b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X)<0) Mesh.Faces.AddFace(ia,ic,ib);
            else Mesh.Faces.AddFace(ia,ib,ic);
        }
        internal void Append(Mesh source)
        {
            var map=new int[source.Vertices.Count];
            for(int i=0;i<map.Length;i++)
            {
                map[i]=Vertex(source.Vertices.Point3dAt(i));
                if(source.Normals.Count==map.Length)Mesh.Normals.SetNormal(map[i],source.Normals[i]);
            }
            foreach(var face in source.Faces)
                if(face.IsTriangle)Mesh.Faces.AddFace(map[face.A],map[face.B],map[face.C]);
                else Mesh.Faces.AddFace(map[face.A],map[face.B],map[face.C],map[face.D]);
        }
        public Mesh Finish() { Mesh.Normals.ComputeNormals(); Mesh.Compact(); return Mesh; }
    }
}
