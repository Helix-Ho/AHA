using Rhino;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class NearGeometryChecks
{
    internal static Dictionary<string,object?> Run(RhinoDoc doc,ModelResult result,EditRegion region,TerrainField field,ModelOptions options,
        IReadOnlyList<GeoFeature> buildings,IReadOnlyList<GeoFeature> parcels)
    {
        double scale=RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem);
        var objects=result.ObjectIds.Select(id=>doc.Objects.FindId(id)??throw new InvalidOperationException("생성한 Topo 객체가 문서에서 누락되었습니다.")).ToArray();
        var pieces=objects.Where(o=>o.Attributes.GetUserString("AHA.Role") is "Land" or "Road")
            .Select(o=>new SurfacePiece(o.Geometry,"",o.Attributes.GetUserString("AHA.Role")!,o.Attributes.GetUserString("AHA.Zone")!)).ToArray();
        return Check(pieces,region,field,options.EffectiveRoadDrop,scale,options.SeparateRoads,options.NearDepthMetres,result.NearBottomElevationMetres);
    }
    internal static Dictionary<string,object?> Check(IReadOnlyList<SurfacePiece> pieces,EditRegion region,TerrainField field,
        double drop,double scale,bool separateRoads,double depth,double? bottom)
    {
        var expectedHeight=new TerrainElevationExpectation(field);
        var near=pieces.Where(p=>p.Zone=="Near"&&p.Role is "Land" or "Road").ToArray();
        var solids=near.Where(p=>p.Role=="Land"||separateRoads).Select(p=>p.Geometry as Brep).ToArray();
        var displays=solids.Where(b=>b is not null).Select(b=>SolidDisplayChecks.Check(b!,scale)).ToArray();
        var orientations=solids.Where(b=>b is not null).Select((b,i)=>NearSolidOrientationChecks.Check(b!,scale,displays[i])).ToArray();
        var boundary=NearBoundaryChecks.AuditBoundary(region,near.Select(p=>(Brep)p.Geometry),scale,.001,false);
        int bottomFaces=0,missing=0,samples=0;double bottomError=0,minTop=double.PositiveInfinity,heightError=0;
        foreach(var piece in near)
        {
            var brep=(Brep)piece.Geometry;
            foreach(var face in brep.Faces)
            {
                double normalZ=face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z;
                if(normalZ<-.999999)
                {
                    if(!face.TryGetPlane(out Plane plane,scale*.000001)){missing++;continue;}
                    bottomFaces++;bottomError=Math.Max(bottomError,Math.Abs(plane.Origin.Z/scale-(bottom??double.NaN)));continue;
                }
                if(normalZ<=1e-8)continue;
                var cache=face.GetMesh(MeshType.Render);if(cache is null){missing++;continue;}
                foreach(var point in cache.Vertices.ToPoint3dArray()) CheckPoint(point);
                foreach(var triangle in cache.Faces)
                {
                    var a=cache.Vertices.Point3dAt(triangle.A);var b=cache.Vertices.Point3dAt(triangle.B);var c=cache.Vertices.Point3dAt(triangle.C);
                    CheckPoint((a+b+c)/3);
                    if(triangle.IsQuad)CheckPoint((a+c+cache.Vertices.Point3dAt(triangle.D))/3);
                }
                void CheckPoint(Point3d point)
                {
                    if(!face.ClosestPoint(point,out double u,out double v)||face.IsPointOnFace(u,v)==PointFaceRelation.Exterior)return;
                    var p=face.PointAt(u,v)/scale;minTop=Math.Min(minTop,p.Z);samples++;
                    heightError=Math.Max(heightError,expectedHeight.Error(p,piece.Role=="Road"?drop:0));
                }
                foreach(var edge in face.Loops.SelectMany(l=>l.Trims).Select(t=>t.Edge).Where(e=>e is not null).Distinct())
                foreach(double t in (edge.ExtremeParameters(Vector3d.ZAxis)??Array.Empty<double>()).Concat(new[]{edge.Domain.Min,edge.Domain.Max}))
                    minTop=Math.Min(minTop,edge.PointAt(t).Z/scale);
            }
        }
        double depthError=bottom.HasValue?Math.Abs(minTop-bottom.Value-depth):1e20;
        bool passed=solids.Length>0&&solids.All(b=>b is {IsValid:true,IsSolid:true,IsManifold:true})
            &&displays.All(a=>a.Passed)&&orientations.All(a=>a.Passed)&&boundary.Passed&&bottomFaces>=solids.Length&&missing==0
            &&bottomError<=1e-7&&depthError<=.0011&&samples>0&&heightError<=.0011;
        return new(){["near_geometry_checks_passed"]=passed,["near_solids"]=solids.Length,["near_boundary"]=boundary,
            ["near_solid_display"]=displays,["near_solid_orientation"]=orientations,["near_bottom_faces"]=bottomFaces,
            ["near_missing_faces_or_caches"]=missing,["near_common_bottom_error_m"]=bottomError,["near_depth_error_m"]=depthError,
            ["near_top_height_samples"]=samples,["near_max_expected_height_error_m"]=heightError,
            ["near_height_check_scope"]="Continuous graded field and role-specific road drop; vertices and triangle interiors use the same height tolerance."};
    }
}
