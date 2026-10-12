using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Editable terrain patches with world XY as their exact UV coordinates.</summary>
public static class TerrainSurfaceGeometry
{
    /// <summary>
    /// Counts are cells, not grid points. Degree-one tensor interpolation preserves source
    /// heights without overshoot and limits each patch to the requested local control net.
    /// Coordinates, domains and heights are metres. The caller owns the surface.
    /// </summary>
    public static NurbsSurface CreatePatch(TerrainField field, int startColumn, int startRow,
        int columnCount, int rowCount)
    {
        if (startColumn < 0 || startRow < 0 || columnCount < 1 || rowCount < 1 ||
            startColumn + columnCount > field.Columns || startRow + rowCount > field.Rows ||
            field.Heights.Length != (field.Columns + 1) * (field.Rows + 1) ||
            !double.IsFinite(field.X0) || !double.IsFinite(field.Y0) ||
            !double.IsFinite(field.Dx) || !double.IsFinite(field.Dy) || field.Dx <= 0 || field.Dy <= 0)
            throw new ArgumentException("invalid_terrain_patch_range");

        var xs = Enumerable.Range(startColumn, columnCount + 1).Select(field.ModelX).ToArray();
        var ys = Enumerable.Range(startRow, rowCount + 1).Select(field.ModelY).ToArray();
        double Height(int column, int row) => field.Node(startColumn + column, startRow + row);
        for (int row = 0; row <= rowCount; row++)
        for (int column = 0; column <= columnCount; column++)
            if (!double.IsFinite(Height(column, row))) throw new InvalidOperationException("non_finite_terrain_height");
        // Redundant degree-one knots do not add terrain information. Keeping
        // them can make native trim integration fail even on a horizontal
        // support. Remove only exactly linear spans; retain every real kink.
        var columns = EssentialKnots(xs, rowCount + 1, Height);
        var rows = EssentialKnots(ys, columnCount + 1, (row, column) => Height(column, row));
        var surface = NurbsSurface.Create(3, false, 2, 2, columns.Length, rows.Length)
            ?? throw new InvalidOperationException("terrain_patch_allocation_failed");
        try
        {
            // Rhino omits the two superfluous end knots. For order 2 the remaining
            // knot count equals the control-point count, with one knot at each node.
            // The planar graph is noded on the common micrometre grid. Its
            // source-cell boundaries and the support's knot breaks must use
            // those identical XY values: a geographic round-trip residue near
            // a tangent trim can otherwise introduce a spurious sub-nanometre
            // surface span in native integration. Source node heights remain
            // unchanged; this is the same coordinate normalization as the graph.
            for (var column = 0; column < columns.Length; column++)
                surface.KnotsU[column] = xs[columns[column]];
            for (var row = 0; row < rows.Length; row++)
                surface.KnotsV[row] = ys[rows[row]];
            for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < columns.Length; column++)
            {
                var x = surface.KnotsU[column];
                var y = surface.KnotsV[row];
                var z = Height(columns[column], rows[row]);
                surface.Points.SetPoint(column, row, new Point3d(x, y, z));
            }
            if (!surface.IsValid) throw new InvalidOperationException("terrain_patch_not_valid");
            return surface;
        }
        catch { surface.Dispose(); throw; }

    }

    private static int[] EssentialKnots(double[] positions, int crossCount, Func<int, int, double> height)
    {
        bool Linear(int first, int middle, int last)
        {
            double fraction = (positions[middle] - positions[first]) / (positions[last] - positions[first]);
            for (int cross = 0; cross < crossCount; cross++)
            {
                double a = height(first, cross), b = height(last, cross);
                if (height(middle, cross) != a + (b - a) * fraction) return false;
            }
            return true;
        }
        var candidates = new List<int> { 0 };
        for (int i = 1; i + 1 < positions.Length; i++)
            if (!Linear(i - 1, i, i + 1)) candidates.Add(i);
        candidates.Add(positions.Length - 1);
        var retained = new List<int> { 0 };
        for (int span = 1; span < candidates.Count; span++)
        {
            int first = candidates[span - 1], last = candidates[span];
            // Local equality must also hold against the retained endpoints;
            // do not accumulate roundoff by removing a chain of short spans.
            bool linear = Enumerable.Range(first + 1, last - first - 1).All(i => Linear(first, i, last));
            if (!linear) for (int i = first + 1; i < last; i++) retained.Add(i);
            retained.Add(last);
        }
        return retained.ToArray();
    }

    /// <summary>
    /// Transfers a known planar partition onto a surface with XY=UV. Positive paths
    /// are outer rings and negative paths are holes, as returned by Clipper boolean
    /// operations. No spatial Brep split or global surface shrinking is performed.
    /// Every returned Brep has one face. The caller owns all returned geometry.
    /// </summary>
    public static IReadOnlyList<Brep> Trim(PathsD regions, Surface patch, double toleranceMetres)
    {
        var result = new List<Brep>();
        try
        {
            foreach (var group in GroupRegions(regions))
            {
                var loops = group.Select(path => (Curve)new PolylineCurve(path
                    .Select(p => new Point3d(p.x, p.y, 0)).Append(new Point3d(path[0].x, path[0].y, 0)))).ToArray();
                try { result.Add(Trim(loops, patch, toleranceMetres)); }
                finally { foreach (var loop in loops) loop.Dispose(); }
            }
            return result;
        }
        catch { foreach (var brep in result) brep.Dispose(); throw; }
    }

    /// <summary>Transfers exact planar Line/Arc loops to a surface's UV parameter space.</summary>
    public static Brep Trim(IReadOnlyList<Curve> loops, Surface patch, double toleranceMetres)
    {
        if (!patch.IsValid || loops.Count == 0 || !double.IsFinite(toleranceMetres) || toleranceMetres <= 0)
            throw new ArgumentException("invalid_terrain_trim_input");
        var brep = new Brep();
        try
        {
            using var plane = new PlaneSurface(Plane.WorldXY, patch.Domain(0), patch.Domain(1));
            var face = brep.Faces.Add(brep.AddSurface(plane));
            for (int ring = 0; ring < loops.Count; ring++)
            {
                using var curve = loops[ring].DuplicateCurve();
                var bounds = curve.GetBoundingBox(true); var u = patch.Domain(0); var v = patch.Domain(1);
                if (!curve.IsValid || !curve.IsClosed || bounds.Min.X < u.Min - toleranceMetres ||
                    bounds.Max.X > u.Max + toleranceMetres || bounds.Min.Y < v.Min - toleranceMetres ||
                    bounds.Max.Y > v.Max + toleranceMetres)
                    throw new InvalidOperationException("terrain_trim_outside_patch");
                var orientation = ring == 0 ? CurveOrientation.CounterClockwise : CurveOrientation.Clockwise;
                if (curve.ClosedCurveOrientation(Plane.WorldXY) != orientation) curve.Reverse();
                if (brep.Loops.AddPlanarFaceLoop(face.FaceIndex,
                        ring == 0 ? BrepLoopType.Outer : BrepLoopType.Inner, new[] { curve }) == null)
                    throw new InvalidOperationException("terrain_trim_loop_failed");
            }
            if (!face.ChangeSurface(brep.AddSurface(patch))) throw new InvalidOperationException("terrain_trim_lift_failed");
            brep.SetTrimIsoFlags();
            // Native fitting's requested tolerance is not a strict maximum.
            // Reserve one tenth of the 1mm shared-boundary budget for fitting;
            // the actual geometry/display boundary is still checked at 1mm.
            if (!face.RebuildEdges(Math.Min(toleranceMetres, .0001), true, true)) throw new InvalidOperationException("terrain_trim_edge_rebuild_failed");
            PreserveModelEdges(brep, patch);
            brep.Compact();
            if (!brep.IsValidWithLog(out var validityLog) || brep.Faces.Count != 1 || brep.Loops.Count != loops.Count)
                throw new InvalidOperationException("terrain_trim_not_valid: " + validityLog);
            return brep;
        }
        catch { brep.Dispose(); throw; }
    }

    private static void PreserveModelEdges(Brep brep, Surface patch)
    {
        // World XY is the exact UV map. Native edge fitting may displace XY
        // even for a straight trim; the bilinear support instead gives an exact
        // degree-two 3D curve in each knot interval. Horizontal supports lift
        // the original UV curve by translation; other curved trims retain the
        // native approximation under the existing edge tolerance.
        if (patch.Degree(0) > 1 || patch.Degree(1) > 1)
            throw new ArgumentException("정확한 직선 경계 높이 계산에는 U/V 차수 1의 XY=UV 지형이 필요합니다.");
        using var support = patch.ToNurbsSurface();
        double level = support.Points.GetControlPoint(0, 0).Location.Z;
        bool horizontal = support.Points.All(point => point.Location.Z == level);
        var uBreaks = patch.GetSpanVector(0); var vBreaks = patch.GetSpanVector(1);
        var vertices = new Dictionary<int, Point3d>();
        foreach (var edge in brep.Edges)
        {
            var trim = brep.Trims[edge.TrimIndices().Single()];
            Point3d Lift(Point3d p) => new(p.X, p.Y, patch.PointAt(p.X, p.Y).Z);
            vertices.TryAdd(edge.StartVertex.VertexIndex, Lift(trim.IsReversed() ? trim.PointAtEnd : trim.PointAtStart));
            vertices.TryAdd(edge.EndVertex.VertexIndex, Lift(trim.IsReversed() ? trim.PointAtStart : trim.PointAtEnd));
        }
        foreach (var edge in brep.Edges)
        {
            var trim = brep.Trims[edge.TrimIndices().Single()];
            using var uv = trim.DuplicateCurve();
            if (horizontal)
            {
                if (!uv.ChangeDimension(3) || !uv.Transform(Transform.Translation(0, 0, level)))
                    throw new InvalidOperationException("수평 지형의 원본 경계를 높이 이동할 수 없습니다.");
                if (trim.IsReversed()) uv.Reverse();
                uv.Domain = edge.Domain;
                if (!edge.SetEdgeCurve(brep.Curves3D.Add(uv)))
                    throw new InvalidOperationException("수평 지형의 원본 경계 높이를 설정할 수 없습니다.");
                edge.StartVertex.Location = uv.PointAtStart; edge.EndVertex.Location = uv.PointAtEnd;
                continue;
            }
            if (!uv.TryGetPolyline(out var polyline))
            {
                // Approximate curved interiors keep their existing error
                // budget, but every incident edge must share the exact UV
                // vertex lifted to the support, including line/arc junctions.
                var first = vertices[edge.StartVertex.VertexIndex];
                var last = vertices[edge.EndVertex.VertexIndex];
                using var curved = edge.DuplicateCurve();
                if (!curved.SetStartPoint(first) || !curved.SetEndPoint(last))
                    throw new InvalidOperationException("곡선 지형 경계의 공통 끝점을 보존할 수 없습니다.");
                curved.Domain = edge.Domain;
                if (!edge.SetEdgeCurve(brep.Curves3D.Add(curved)))
                    throw new InvalidOperationException("곡선 지형 경계의 공통 정점을 연결할 수 없습니다.");
                edge.StartVertex.Location = first; edge.EndVertex.Location = last;
                continue;
            }
            using var composite = new PolyCurve();
            for (int i = 1; i < polyline.Count; i++)
            {
                var a = polyline[i - 1]; var b = polyline[i];
                var fractions = new SortedSet<double> { 0, 1 };
                if (a.X != b.X) foreach (double u in uBreaks) { double t = (u - a.X) / (b.X - a.X); if (t > 0 && t < 1) fractions.Add(t); }
                if (a.Y != b.Y) foreach (double v in vBreaks) { double t = (v - a.Y) / (b.Y - a.Y); if (t > 0 && t < 1) fractions.Add(t); }
                var stops = fractions.ToArray();
                for (int j = 1; j < stops.Length; j++)
                {
                    Point3d At(double t)
                    {
                        double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
                        return new(x, y, patch.PointAt(x, y).Z);
                    }
                    var first = At(stops[j - 1]); var last = At(stops[j]); var middle = At((stops[j - 1] + stops[j]) / 2);
                    using var segment = BilinearEdge(first, middle, last);
                    if (!composite.AppendSegment(segment)) throw new InvalidOperationException("직선 지형 경계의 정확한 높이 구간을 연결할 수 없습니다.");
                }
            }
            using var lifted = composite.ToNurbsCurve();
            if (trim.IsReversed()) lifted.Reverse();
            if (!lifted.SetStartPoint(vertices[edge.StartVertex.VertexIndex]) ||
                !lifted.SetEndPoint(vertices[edge.EndVertex.VertexIndex]))
                throw new InvalidOperationException("직선 지형 경계의 공통 끝점을 보존할 수 없습니다.");
            lifted.Domain = edge.Domain;
            if (!edge.SetEdgeCurve(brep.Curves3D.Add(lifted)))
                throw new InvalidOperationException("직선 지형 경계의 정확한 3D 곡선을 연결할 수 없습니다.");
            edge.StartVertex.Location = lifted.PointAtStart; edge.EndVertex.Location = lifted.PointAtEnd;
        }
    }

    /// <summary>Exact lift of a straight XY segment within one bilinear cell; middle is at parameter 0.5.</summary>
    internal static NurbsCurve BilinearEdge(Point3d first, Point3d middle, Point3d last)
    {
        var curve = new NurbsCurve(3, false, 3, 3);
        curve.Knots[0] = curve.Knots[1] = 0; curve.Knots[2] = curve.Knots[3] = 1;
        curve.Points.SetPoint(0, first);
        curve.Points.SetPoint(1, new Point3d((first.X + last.X) / 2, (first.Y + last.Y) / 2,
            2 * middle.Z - (first.Z + last.Z) / 2));
        curve.Points.SetPoint(2, last);
        return curve;
    }

    /// <summary>
    /// Attaches the caller's constrained render mesh to a single-face Brep. Complete
    /// scaling and transforms before calling. Rhino reparents the mesh to the face;
    /// callers must not subsequently modify or attach that mesh to another face.
    /// </summary>
    public static void AttachDisplayMesh(Brep brep, Mesh mesh)
    {
        if (!brep.IsValid || brep.Faces.Count != 1 || !mesh.IsValid || mesh.Faces.Count == 0)
            throw new ArgumentException("invalid_terrain_display_mesh");
        if (mesh.Normals.Count != mesh.Vertices.Count) mesh.Normals.ComputeNormals();
        mesh.Compact();
        if (!brep.Faces[0].SetMesh(MeshType.Render, mesh))
            throw new InvalidOperationException("terrain_display_mesh_cache_failed");
    }

    /// <summary>
    /// Creates the vertical curb side directly from a bilinear terrain boundary.
    /// Each A/Mid/B segment must lie in one grid cell, with Mid at parameter 0.5.
    /// Coordinates and drop are metres. Consecutive segments must already connect
    /// and have land on the left; the natural surface normal faces right.
    /// The caller owns the returned Brep.
    /// </summary>
    public static Brep CreateRoadSide(IReadOnlyList<(Point3d A, Point3d Mid, Point3d B)> segments,
        double drop)
    {
        if (segments.Count == 0 || !double.IsFinite(drop) || drop <= 0)
            throw new ArgumentException("invalid_road_side_input");
        using var boundary = new PolyCurve();
        for (var index = 0; index < segments.Count; index++)
        {
            var (a, mid, b) = segments[index];
            if (!a.IsValid || !mid.IsValid || !b.IsValid || a.DistanceTo(b) < 1e-9 ||
                (index > 0 && segments[index - 1].B.DistanceTo(a) > 1e-7))
                throw new InvalidOperationException("disconnected_road_side_segments");
            using var curve = BilinearEdge(a, mid, b);
            if (!curve.IsValid || curve.PointAt(0.5).DistanceTo(mid) > 1e-7 || !boundary.Append(curve))
                throw new InvalidOperationException("road_side_boundary_failed");
        }
        // A single exact NURBS boundary keeps this as one editable surface instead
        // of allowing a PolyCurve extrusion to create one Brep face per segment.
        using var profile = boundary.ToNurbsCurve()
            ?? throw new InvalidOperationException("road_side_profile_failed");
        // Build the ruled NURBS from the lower row to the upper row. With land
        // on the left of the directed boundary, U×V then naturally faces the
        // road on the right; no Brep orientation flag or later flip is needed.
        var brep = TerrainBoundaryGeometry.VerticalWall(profile, z => z - drop);
        if (!brep.IsValidWithLog(out var validityLog) || brep.Faces.Count != 1)
        {
            brep.Dispose();
            throw new InvalidOperationException("road_side_not_valid: " + validityLog);
        }
        return brep;
    }

    private static IReadOnlyList<List<PathD>> GroupRegions(PathsD regions)
    {
        if (regions.Any(path => path.Count < 3 || Clipper.Area(path) == 0))
            throw new InvalidOperationException("degenerate_terrain_trim_ring");
        var outlines = regions.Where(path => Clipper.Area(path) > 0).ToList();
        var groups = outlines.Select(path => new List<PathD> { path }).ToList();
        foreach (var hole in regions.Where(path => Clipper.Area(path) < 0))
        {
            var owner = -1; var ownerArea = double.PositiveInfinity;
            for (var index = 0; index < outlines.Count; index++)
            {
                var area = Math.Abs(Clipper.Area(outlines[index]));
                if (area < ownerArea && Clipper.PointInPolygon(hole[0], outlines[index], 6) != PointInPolygonResult.IsOutside)
                {
                    owner = index;
                    ownerArea = area;
                }
            }
            if (owner < 0) throw new InvalidOperationException("terrain_trim_hole_without_outer_ring");
            groups[owner].Add(hole);
        }
        return groups;
    }
}
