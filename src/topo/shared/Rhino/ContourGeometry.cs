using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Converts contour profiles directly to lightweight Rhino objects.</summary>
public static class ContourGeometry
{
    public static IReadOnlyList<SurfacePiece> Create(ContourPlan plan, TerrainField field,
        double scale, double toleranceMetres)
    {
        if(!double.IsFinite(scale)||scale<=0||!double.IsFinite(toleranceMetres)||toleranceMetres<=0)
            throw new ArgumentException("Contour 출력 단위와 허용오차가 유효하지 않습니다.");
        var output=new List<SurfacePiece>();
        var exactNear=new ExactNearRegions(plan.Near);
        try
        {
            foreach(var plate in plan.Plates)
            foreach(var component in plate.Exact??exactNear.Split(plate.SourceProfiles,plate.Excluded).Near)
                AddPlate(plate,component);
            foreach(var exposure in plan.RoadOverlays)
            {
                foreach(var component in exposure.Exact??exactNear.Split(exposure.SourceProfiles,exposure.Excluded).Near)
                {
                    var curves=ExactProfileGeometry.Curves(component);
                    try
                    {
                        var brep=OverlaySurface(curves,exposure.Top,toleranceMetres);
                        try
                        {
                            brep.Transform(Transform.Scale(Point3d.Origin,scale));
                            var display=OverlayMesh(component.Sample(),exposure.Top,scale);
                            TerrainSurfaceGeometry.AttachDisplayMesh(brep,display);
                            output.Add(new(brep,"Near::Surfaces::Road","Road","Near"));
                        }
                        catch { brep.Dispose(); throw; }
                    }
                    finally{foreach(var curve in curves)curve.Dispose();}
                }
            }
            AddOuterWalls();
            return output;
        }
        catch{foreach(var piece in output)piece.Geometry.Dispose();throw;}

        void AddPlate(ContourPlate plate,ExactNearRegions.Component component)
        {
            var proxy=component.Sample();
            if(ProfileContact.TryFind(proxy,out var contact))
            {
                var pieces=ProfileContact.Domains(proxy,contact)
                    .SelectMany(domain=>exactNear.Split(ContourPaths.Union(domain),included:component).Near).ToArray();
                if(pieces.Length<2||Math.Abs(pieces.Sum(p=>p.Area)-component.Area)>Math.Max(1e-8,component.Area*1e-9))
                    throw new InvalidOperationException("접촉 단면 분리가 정확 영역의 면적을 보존하지 못했습니다.");
                foreach(var part in pieces)AddPlate(plate,part);
                return;
            }
            var profiles=ExactProfileGeometry.Curves(component);
            try
            {
                var ordered=profiles;
                using var shape=Extrusion.Create(ordered[0],Plane.WorldXY,plate.Top-plate.Bottom,true)
                    ??throw new InvalidOperationException("등고판 Extrusion 프로파일을 만들 수 없습니다.");
                foreach(var inner in ordered.Skip(1))
                    if(!shape.AddInnerProfile(inner))throw new InvalidOperationException("등고판의 내부 경계를 보존할 수 없습니다.");
                if(!shape.IsValid||!shape.IsSolid)throw new InvalidOperationException("등고판 Extrusion이 유효한 닫힌 입체가 아닙니다.");
                shape.Transform(Transform.Translation(0,0,plate.Bottom));
                shape.Transform(Transform.Scale(Point3d.Origin,scale));
                var geometry=shape.Duplicate();
                string layer="Near::Surfaces::"+plate.Role;
                output.Add(new(geometry,layer,plate.Role,"Near"));
            }
            finally{foreach(var profile in profiles)profile.Dispose();}
        }

        void AddOuterWalls()
        {
            var farRoads = new ContourBoundary(plan.SeparateRoads ? plan.FarRoads : new PathsD());
            foreach (var part in farRoads.Split(plan.Near.Boundaries.SelectMany(loop => loop.Segments), false))
            {
                using var upper = TerrainBoundaryGeometry.Lift(part.Edge, field, part.Filled ? plan.RoadDrop : 0);
                var wall = TerrainBoundaryGeometry.VerticalWall(upper, _ => plan.BaseTop);
                wall.Transform(Transform.Scale(Point3d.Origin, scale));
                output.Add(new(wall, "Near::Surfaces::Connections", "Connection", "Near"));
            }
        }
    }

    internal static Brep OverlaySurface(IReadOnlyList<Curve> loops, double elevation, double tolerance)
    {
        var bounds = BoundingBox.Empty;
        foreach (var loop in loops) bounds.Union(loop.GetBoundingBox(true));
        using var support = new PlaneSurface(new Plane(new Point3d(0, 0, elevation), Vector3d.ZAxis),
            new Interval(bounds.Min.X, bounds.Max.X), new Interval(bounds.Min.Y, bounds.Max.Y));
        // The planar calculation already owns the outer/hole relationship.
        // Use the same explicit XY=UV trims as continuous terrain patches.
        return TerrainSurfaceGeometry.Trim(loops, support, tolerance);
    }

    internal static Mesh OverlayMesh(PathsD component, double elevation, double scale)
    {
        var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
        try
        {
            var indices = new Dictionary<(long, long), int>();
            foreach (var triangle in PlanarTriangulation.Triangulate(ContourPaths.Union(component)))
                mesh.Faces.AddFace(Vertex(triangle[0]), Vertex(triangle[1]), Vertex(triangle[2]));
            mesh.Normals.ComputeNormals();
            return mesh;

            int Vertex(PointD point)
            {
                // Preserve the triangulator's shared vertex identity. Rhino's
                // post-hoc merge can collapse distinct double-precision points.
                var key = PlanarTriangulation.GridKey(point);
                if (indices.TryGetValue(key, out int index)) return index;
                index = mesh.Vertices.Add(point.x * scale, point.y * scale, elevation * scale);
                indices.Add(key, index); return index;
            }
        }
        catch { mesh.Dispose(); throw; }
    }
}
