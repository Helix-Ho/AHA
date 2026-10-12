using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Closes directly generated terrain sheets using their existing common display graph.</summary>
public static class NearTerrainSolid
{
    public static NearSolidResult Build(IReadOnlyList<SurfacePiece> sheets, double depthMetres,
        double scale, double toleranceMetres, bool separateRoads, double? bottomElevationMetres = null)
    {
        double tolerance = Math.Min(toleranceMetres, .001) * scale;
        bool overlayRoads = !separateRoads;
        var solidSheets = sheets.Where(piece => piece.Role == "Land" || piece.Role == "Road" && !overlayRoads).ToArray();
        if (solidSheets.Length == 0) throw new InvalidOperationException("입체로 만들 근접 상면이 없습니다.");
        // A bilinear cell has no strict interior extremum. Its retained grid
        // vertices are already in the common display graph; exact trim curves
        // supply extrema between those vertices, including circular boundaries.
        // Unused support control points must not lower the requested bottom.
        double minimum = solidSheets.Min(piece => MinimumTop((Brep)piece.Geometry));
        double bottom = bottomElevationMetres is { } elevation ? elevation * scale : minimum - depthMetres * scale;
        if (!double.IsFinite(bottom) || bottom >= minimum)
            throw new ArgumentException("입체 바닥은 상면의 최저점보다 아래여야 합니다.");
        var output = new List<SurfacePiece>();
        try
        {
            foreach (var role in solidSheets.GroupBy(piece => piece.Role))
            {
                var source = role.Select(piece => (Brep)piece.Geometry).ToArray();
                var tops = JoinTops(source, tolerance);
                try
                {
                    foreach (var top in tops)
                    {
                        var solid = Close(top, bottom, tolerance, scale);
                        output.Add(new(solid, "Near::Surfaces", role.Key, "Near"));
                    }
                }
                finally { foreach (var top in tops) top.Dispose(); }
            }
            if (overlayRoads) output.AddRange(sheets.Where(piece => piece.Role == "Road"));
            return new(output, bottom / scale, output.Count(piece => piece.Geometry is Brep brep && brep.IsSolid));
        }
        catch
        {
            foreach (var piece in output.Where(piece => !sheets.Any(sheet => ReferenceEquals(sheet.Geometry, piece.Geometry)))) piece.Geometry.Dispose();
            throw;
        }
    }

    private static Brep[] JoinTops(Brep[] source, double tolerance)
    {
        // JoinBreps can omit its source map and clear face IDs. Surface user
        // data survives that operation. Tag only private copies, then remove
        // the operation-local tag after restoring each original display mesh.
        string key = "AHA.TopFace." + Guid.NewGuid().ToString("N");
        var originals = new Dictionary<string, BrepFace>();
        var copies = new List<Brep>(); Brep[]? joined = null;
        try
        {
            foreach (var original in source)
            {
                var copy = original.DuplicateBrep(); copies.Add(copy);
                for (int i = 0; i < copy.Faces.Count; i++)
                {
                    string id = originals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    originals.Add(id, original.Faces[i]);
                    using var support = copy.Faces[i].UnderlyingSurface().ToNurbsSurface();
                    support.SetUserString(key, id);
                    int index = copy.AddSurface(support);
                    if (!copy.Faces[i].ChangeSurface(index))
                        throw new InvalidOperationException("근접 상면의 원본 지지면 대응을 보존할 수 없습니다.");
                }
            }
            joined = Brep.JoinBreps(copies, tolerance)
                ?? throw new InvalidOperationException("근접 상면을 연결할 수 없습니다.");
            var seen = new HashSet<string>();
            foreach (var top in joined)
            foreach (var face in top.Faces)
            {
                var support = face.UnderlyingSurface();
                string id = support.GetUserString(key);
                if (id is null || !originals.TryGetValue(id, out var original) || !seen.Add(id))
                    throw new InvalidOperationException("근접 상면의 원본 면 대응이 보존되지 않았습니다.");
                var mesh = original.GetMesh(MeshType.Render)
                    ?? throw new InvalidOperationException("근접 상면의 공통 표시망이 없습니다.");
                if (!face.SetMesh(MeshType.Render, mesh.DuplicateMesh()))
                    throw new InvalidOperationException("근접 상면의 원본 표시망을 전달할 수 없습니다.");
            }
            if (seen.Count != originals.Count)
                throw new InvalidOperationException("근접 상면 연결에서 원본 면이 누락되었습니다.");
            foreach (var top in joined)
            foreach (var support in top.Surfaces) support.SetUserString(key, null);
            return joined;
        }
        catch { if (joined is not null) foreach (var top in joined) top.Dispose(); throw; }
        finally { foreach (var copy in copies) copy.Dispose(); }
    }

    private static double MinimumTop(Brep brep)
    {
        double minimum = double.PositiveInfinity;
        foreach (var face in brep.Faces)
        {
            var mesh = face.GetMesh(MeshType.Render)
                ?? throw new InvalidOperationException("근접 상면의 공통 표시망이 없습니다.");
            for (int i = 0; i < mesh.Vertices.Count; i++) minimum = Math.Min(minimum, mesh.Vertices.Point3dAt(i).Z);
        }
        foreach (var edge in brep.Edges) minimum = Math.Min(minimum, edge.GetBoundingBox(true).Min.Z);
        if (!double.IsFinite(minimum)) throw new InvalidOperationException("근접 상면의 최저 높이를 구할 수 없습니다.");
        return minimum;
    }

    private static Brep Close(Brep top, double bottom, double tolerance, double scale)
    {
        var definitions = new List<NearCapBoundary>(); var upper = new Dictionary<int, Curve>();
        var walls = new List<Brep>(); var bottomLoops = new List<IReadOnlyList<Curve>>(); Brep[]? caps = null, shells = null;
        var lowerDefinitions = new List<NearCapBoundary>();
        var wallLoops = new List<NearTerrainCap.DirectedEdge[]>();
        try
        {
            foreach (var edge in top.Edges.Where(edge => edge.Valence == EdgeAdjacency.Naked))
            {
                var indices = edge.TrimIndices();
                if (indices.Length != 1) throw new InvalidOperationException("근접 상면의 경계 연결 관계가 올바르지 않습니다.");
                var trim = top.Trims[indices[0]];
                var curve = edge.DuplicateCurve();
                // The joined boundary's topological vertex is authoritative
                // for all incident profiles. Keep native curve endpoint residue
                // from becoming an extra connector in the composite wall.
                curve.SetStartPoint(edge.StartVertex.Location);
                curve.SetEndPoint(edge.EndVertex.Location);
                if (trim.IsReversed()) curve.Reverse();
                upper.Add(edge.EdgeIndex, curve);
                var projected = curve.DuplicateCurve();
                if (!projected.Transform(Transform.PlanarProjection(Plane.WorldXY)))
                { projected.Dispose(); throw new InvalidOperationException("실제 상면 경계의 수평 투영을 만들 수 없습니다."); }
                definitions.Add(new(edge.EdgeIndex,
                    trim.IsReversed() ? edge.EndVertex.VertexIndex : edge.StartVertex.VertexIndex,
                    trim.IsReversed() ? edge.StartVertex.VertexIndex : edge.EndVertex.VertexIndex, projected));
            }
            var shell = top.DuplicateBrep();
            shells = new[] { shell };
            foreach (var loop in NearTerrainCap.OrderedLoops(top, definitions))
            {
                wallLoops.Add(loop);
                var curves = loop.Select(edge =>
                {
                    var curve = upper[edge.Source.SourceEdgeIndex].DuplicateCurve();
                    if (edge.Reversed) curve.Reverse();
                    return curve;
                }).ToArray();
                try
                {
                    using var wall = new TerrainShellWall(curves, bottom);
                    walls.Add(wall.Geometry.DuplicateBrep());
                    int offset = shell.Edges.Count;
                    shell.Append(wall.Geometry);
                    for (int i = 0; i < loop.Length; i++)
                        if (!shell.JoinEdges(loop[i].Source.SourceEdgeIndex, offset + wall.UpperEdges[i], tolerance, false))
                            throw new InvalidOperationException("근접 상면과 동일한 외벽 경계를 연결할 수 없습니다.");
                }
                finally { foreach (var curve in curves) curve.Dispose(); }
            }
            shell.Compact();
            foreach (var edge in shell.Edges.Where(edge => edge.Valence == EdgeAdjacency.Naked))
            {
                var box = edge.GetBoundingBox(true);
                if (Math.Abs(box.Min.Z - bottom) > tolerance || Math.Abs(box.Max.Z - bottom) > tolerance)
                    throw new InvalidOperationException("근접 상면과 옆면을 연결한 뒤 바닥 이외의 열린 경계가 남았습니다.");
                var trims = edge.TrimIndices();
                if (trims.Length != 1) throw new InvalidOperationException("근접 하부 경계의 면 대응이 올바르지 않습니다.");
                var trim = shell.Trims[trims[0]];
                var profile = edge.DuplicateCurve();
                // Copy the wall's oriented lower profile; the cap factory
                // reverses its face to provide the downward orientation.
                bool reversed = trim.IsReversed() != trim.Face.OrientationIsReversed;
                if (reversed) profile.Reverse();
                lowerDefinitions.Add(new(edge.EdgeIndex,
                    reversed ? edge.EndVertex.VertexIndex : edge.StartVertex.VertexIndex,
                    reversed ? edge.StartVertex.VertexIndex : edge.EndVertex.VertexIndex, profile));
            }
            var lowerLoops = NearTerrainCap.OrderedLoops(shell, lowerDefinitions);
            foreach (var loop in lowerLoops) bottomLoops.Add(loop.Select(edge => edge.Copy()).ToArray());
            caps = new[] { NearTerrainCap.CreateSegmentedPlanarCap(bottomLoops, bottom, tolerance, out var capEdges) };
            var solid = shell.DuplicateBrep();
            try
            {
                // The shell and cap share the very same curves. Join known
                // pairs directly instead of asking a spatial join to refit and
                // repartition one independently reconstructed closed curve.
                int edgeOffset = solid.Edges.Count;
                solid.Append(caps[0]);
                for (int loop = 0; loop < lowerLoops.Count; loop++)
                for (int segment = 0; segment < lowerLoops[loop].Length; segment++)
                    if (!solid.JoinEdges(lowerLoops[loop][segment].Source.SourceEdgeIndex,
                        edgeOffset + capEdges[loop][segment], tolerance, false))
                        throw new InvalidOperationException("근접 옆면과 동일한 바닥 경계를 연결할 수 없습니다.");
                solid.Compact();
                if (!solid.IsValidWithLog(out var validity) || !solid.IsSolid || !solid.IsManifold)
                {
                    var naked = solid.Edges.Where(edge => edge.Valence != EdgeAdjacency.Interior).ToArray();
                    throw new InvalidOperationException("근접 입체가 유효한 닫힌 NURBS 형상이 아닙니다: " +
                        $"faces={solid.Faces.Count}, solid={solid.IsSolid}, manifold={solid.IsManifold}, " +
                        $"open={naked.Length}, top={top.Faces.Count}, walls={walls.Count}, " + validity);
                }
                // Only signed volume is needed here. Centroid and higher moments
                // add expensive integration over the long trimmed terrain walls.
                // Audit a metre-space copy so native absolute integration
                // tolerances have the same physical meaning in m and mm files.
                using var metricSolid = solid.DuplicateBrep();
                metricSolid.Transform(Transform.Scale(Point3d.Origin, 1 / scale));
                using var volume = VolumeMassProperties.Compute(metricSolid, true, false, false, false);
                if (volume == null || !double.IsFinite(volume.Volume) || volume.Volume <= Math.Abs(volume.VolumeError))
                    throw new InvalidOperationException("근접 입체의 실제 방향 또는 부피가 올바르지 않습니다.");
                if (caps.Length != 1 || solid.Faces.Count != top.Faces.Count + walls.Count + 1)
                    throw new InvalidOperationException("근접 입체의 상면·옆면·바닥 대응이 보존되지 않았습니다.");

                using var display = NearTerrainDisplay.Build(top, definitions, bottom, scale, wallLoops);
                foreach (var pair in display.Tops) Attach(solid.Faces[pair.Key], top.Faces[pair.Key], pair.Value);
                for (int i = 0; i < wallLoops.Count; i++) Attach(solid.Faces[top.Faces.Count + i], walls[i].Faces[0], display.Walls[i]);
                Attach(solid.Faces[^1], caps[0].Faces[0], display.Bottom);
                return solid;

                static void Attach(BrepFace face, BrepFace source, Mesh mesh)
                {
                    if (!GeometryBase.GeometryEquals(face.UnderlyingSurface(), source.UnderlyingSurface()))
                        throw new InvalidOperationException("근접 입체 연결 후 표시망과 원래 지지면의 대응이 달라졌습니다.");
                    if (!face.SetMesh(MeshType.Render, mesh.DuplicateMesh()))
                        throw new InvalidOperationException("근접 입체의 공통 경계 표시 메시를 저장할 수 없습니다.");
                }
            }
            catch { solid.Dispose(); throw; }
        }
        finally
        {
            foreach (var boundary in definitions) boundary.Profile.Dispose();
            foreach (var boundary in lowerDefinitions) boundary.Profile.Dispose();
            foreach (var curve in upper.Values) curve.Dispose();
            foreach (var wall in walls) wall.Dispose();
            foreach (var loop in bottomLoops) foreach (var curve in loop) curve.Dispose();
            foreach (var cap in caps ?? Array.Empty<Brep>()) cap.Dispose();
            foreach (var shell in shells ?? Array.Empty<Brep>()) shell.Dispose();
        }
    }
}
