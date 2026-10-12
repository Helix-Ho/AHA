using Clipper2Lib;
using Rhino.Geometry;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class ModelPlanNativeChecks
{
    internal static List<Dictionary<string,object?>> Run()
    {
        ModelSurfaceQualityChecks.CheckConnectedComponentFixtures();
        var results=new List<Dictionary<string,object?>>();
        foreach(bool aoiContact in new[]{false,true})
        {
            var fixture=ModelPlanFixtures.Create(aoiContact);
            var projection=fixture.Projection;double radius=fixture.Radius;var bounds=fixture.Bounds;
            var field=TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Repeat(20d,25).ToArray());
            var domain=fixture.Domain;var raw=fixture.Raw;
            var referenceCurves=raw.Select(path=>new PolylineCurve(path.Append(path[0]).Select(p=>new Point3d(p.x,p.y,0)))).ToArray();
            var referenceCrc=referenceCurves.Select(c=>c.DataCRC(0)).ToArray();
            var jsonOptions=new JsonSerializerOptions{IncludeFields=true};string source=JsonSerializer.Serialize(raw,jsonOptions);
            var cleaned=ModelPlanGeometry.Build(raw,domain);
            var target=fixture.Target;
            string targetBefore=JsonSerializer.Serialize(target);
            var region=fixture.Region;
            try
            {
                foreach(bool site in new[]{false,true})foreach(bool nurbs in new[]{false,true})foreach(double scale in new[]{1d,1000d})
                {
                    TerrainSurface? model=null;
                    try
                    {
                        model=TerrainSurface.Build(field,new(cleaned.Roads,Math.Abs(Clipper.Area(cleaned.Roads))),
                            .2,scale,.001,nurbs,true,site?region:null,30);
                        var pieces=model.Pieces;
                        var top=pieces.Where(p=>p.Role is "Land" or "Road").ToArray();
                        var solids=top.Where(p=>p.Zone=="Near").Select(p=>(Brep)p.Geometry).ToArray();
                        var displays=solids.Select(b=>SolidDisplayChecks.Check(b,scale)).ToArray();
                        var orientations=solids.Select((b,i)=>NearSolidOrientationChecks.Check(b,scale,displays[i])).ToArray();
                        var quality=ModelSurfaceQualityChecks.Check(top,scale);
                        bool sourceUnchanged=JsonSerializer.Serialize(raw,jsonOptions)==source
                            &&referenceCurves.Select(c=>c.DataCRC(0)).SequenceEqual(referenceCrc)
                            &&JsonSerializer.Serialize(target)==targetBefore;
                        bool probes=aoiContact||Math.Abs(model.SampleLocal(0,-6)-19.8)<1e-8
                            &&Math.Abs(model.SampleLocal(5,4)-20)<1e-8&&Math.Abs(model.SampleLocal(.04061,.00548)-20)<1e-8;
                        bool passed=sourceUnchanged&&probes&&quality.Passed&&orientations.All(a=>a.Passed)&&displays.All(a=>a.Passed)
                            &&(!site||solids.Length>0)&&top.All(p=>p.Geometry.IsValid)&&Math.Abs(model.PlanAreaSquareMetres-4*radius*radius)<1e-6
                            &&model.PlanAreaResidualSquareMetres<1e-6;
                        results.Add(new(){["case"]=aoiContact?"aoi-near-tangent-contact":"small-features-and-normal-road",
                            ["near_enabled"]=site,["nurbs"]=nurbs,["scale"]=scale,["passed"]=passed,["raw_and_target_curves_unchanged"]=sourceUnchanged,
                            ["normal_lane_and_removed_feature_probes"]=probes,["model_plan"]=cleaned.Diagnostics,["surface_quality"]=quality,
                            ["orientations"]=orientations,["display_topology"]=displays,["coverage_residual_m2"]=model.PlanAreaResidualSquareMetres});
                    }
                    catch(Exception error)
                    {
                        results.Add(new(){["case"]=aoiContact?"aoi-near-tangent-contact":"small-features-and-normal-road",
                            ["near_enabled"]=site,["nurbs"]=nurbs,["scale"]=scale,["passed"]=false,["error"]=error.ToString()});
                    }
                    finally
                    {
                        if(model is not null)foreach(var geometry in model.Pieces.Select(p=>p.Geometry).Distinct())geometry.Dispose();
                    }
                }
            }
            finally{foreach(var curve in referenceCurves)curve.Dispose();}
        }
        return results;
    }

}

internal sealed record ModelSurfaceQualityAudit(bool Passed,int Components,int TopFaces,int TinyComponents,int TinyTopFaces,
    double MinimumComponentPlanAreaSquareMetres,double MinimumTopFacePlanAreaSquareMetres,int InvalidTopFaces);

internal static class ModelSurfaceQualityChecks
{
    internal static ModelSurfaceQualityAudit Check(IEnumerable<SurfacePiece> pieces,double scale)
    {
        int components=0,faces=0,tinyComponents=0,tinyFaces=0,invalidFaces=0;
        double minimumComponent=double.PositiveInfinity,minimumFace=double.PositiveInfinity;
        double threshold=ModelPlanPolicy.Default.MinimumAreaSquareMetres;
        // The actual output may split one role at patch and Near boundaries.
        // Only connected plan components within the same zone/role own the size policy.
        foreach(var group in pieces.Where(p=>p.Role is "Land" or "Road").GroupBy(p=>(p.Zone,p.Role)))
        {
            var paths=new PathsD();
            foreach(var piece in group)
            {
                if(!piece.Geometry.IsValid){invalidFaces++;continue;}
                if(piece.Geometry is Mesh mesh)
                {
                    paths.AddRange(MeshBoundaryPaths(mesh,scale));
                }
                else if(piece.Geometry is Brep brep)
                {
                    foreach(var face in brep.Faces)
                    {
                        if(face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z<=1e-8)continue;
                        using var projected=face.DuplicateFace(false);
                        projected.Transform(Transform.PlanarProjection(Plane.WorldXY));
                        using var mass=AreaMassProperties.Compute(projected);
                        double area=mass is null?0:Math.Abs(mass.Area)/(scale*scale);
                        faces++;minimumFace=Math.Min(minimumFace,area);
                        if(!face.IsValid||!double.IsFinite(area)||area<=0){invalidFaces++;continue;}
                        if(area<threshold-1e-8)tinyFaces++;
                        foreach(var loop in face.Loops)
                        {
                            using var boundary=loop.To3dCurve();
                            if(boundary is null)throw new InvalidOperationException("상면 경계를 읽을 수 없습니다.");
                            boundary.Transform(Transform.Scale(Point3d.Origin,1/scale));
                            boundary.Transform(Transform.PlanarProjection(Plane.WorldXY));
                            Polyline line;
                            if(!boundary.TryGetPolyline(out line))
                            {
                                using var polygon=boundary.ToPolyline(ModelPlanPolicy.PrecisionMetres,Math.PI/36,0,0);
                                if(polygon is null||!polygon.TryGetPolyline(out line))
                                    throw new InvalidOperationException("상면 경계를 평면 정밀도로 읽을 수 없습니다.");
                            }
                            if(!boundary.IsClosed||line.Count<4)
                                throw new InvalidOperationException("상면 평면 경계가 닫혀 있지 않습니다.");
                            var path=new PathD(line.Take(line.Count-1).Select(point=>new PointD(point.X,point.Y)));
                            if((Clipper.Area(path)>0)!=(loop.LoopType==BrepLoopType.Outer))path.Reverse();
                            paths.Add(path);
                        }
                    }
                }
                else invalidFaces++;
            }
            // Union boundary loops rather than every render triangle. Hole direction is
            // retained, including courtyards and independent components nested in holes.
            var tree=new PolyTreeD();var clipper=new ClipperD(6);clipper.AddSubject(paths);
            if(!clipper.Execute(ClipType.Union,FillRule.NonZero,tree))
                throw new InvalidOperationException("출력 상면의 연결 평면을 합칠 수 없습니다.");
            Visit(tree);
            void Visit(PolyPathD node)
            {
                for(int i=0;i<node.Count;i++)
                {
                    var child=node[i];
                    if(!child.IsHole)
                    {
                        double area=Math.Abs(Clipper.Area(child.Polygon!));
                        for(int j=0;j<child.Count;j++)if(child[j].IsHole)area-=Math.Abs(Clipper.Area(child[j].Polygon!));
                        components++;minimumComponent=Math.Min(minimumComponent,area);
                        if(!double.IsFinite(area)||area<threshold-1e-8)tinyComponents++;
                    }
                    Visit(child);
                }
            }
        }
        return new(components>0&&tinyComponents==0&&invalidFaces==0,components,faces,tinyComponents,tinyFaces,
            double.IsFinite(minimumComponent)?minimumComponent:0,double.IsFinite(minimumFace)?minimumFace:0,invalidFaces);
    }

    // Linear half-edge incidence avoids Rhino's expensive joining of many disjoint
    // naked curves and does not union/cache all display triangles.
    private static PathsD MeshBoundaryPaths(Mesh mesh,double scale)
    {
        var points=new List<Point3d>();var indices=new Dictionary<Point3d,int>();
        var map=new int[mesh.Vertices.Count];
        for(int i=0;i<map.Length;i++)
        {
            var point=mesh.Vertices.Point3dAt(i);
            if(!indices.TryGetValue(point,out int id)){id=points.Count;points.Add(point);indices.Add(point,id);}
            map[i]=id;
        }
        var edges=new Dictionary<(int,int),(int A,int B,int Count)>();
        foreach(var face in mesh.Faces)
        {
            var a=mesh.Vertices.Point3dAt(face.A);var b=mesh.Vertices.Point3dAt(face.B);var c=mesh.Vertices.Point3dAt(face.C);
            var normal=Vector3d.CrossProduct(b-a,c-a);
            if(face.IsQuad)normal+=Vector3d.CrossProduct(c-a,mesh.Vertices.Point3dAt(face.D)-a);
            // Far pit walls keep their terrain role but have no projected top area.
            // Match the Brep upward-face filter before computing boundary incidence.
            if(normal.Z<=1e-8*normal.Length)continue;
            Add(face.A,face.B);Add(face.B,face.C);
            if(face.IsQuad){Add(face.C,face.D);Add(face.D,face.A);}else Add(face.C,face.A);
        }
        var next=new Dictionary<int,Stack<int>>();
        foreach(var edge in edges.Values.Where(edge=>edge.Count==1))
        {
            if(!next.TryGetValue(edge.A,out var list))next[edge.A]=list=new();
            list.Push(edge.B);
        }
        var paths=new PathsD();
        foreach(int start in next.Keys)
        while(next[start].Count>0)
        {
            var path=new PathD();int current=start;
            do
            {
                path.Add(new(points[current].X/scale,points[current].Y/scale));
                if(!next.TryGetValue(current,out var list)||list.Count==0)
                    throw new InvalidOperationException("지형 Mesh의 평면 외곽이 닫혀 있지 않습니다.");
                current=list.Pop();
            }while(current!=start);
            if(path.Count<3||Clipper.Area(path)==0)throw new InvalidOperationException("지형 Mesh의 평면 외곽 면적이 없습니다.");
            paths.Add(path);
        }
        return paths;
        void Add(int first,int second)
        {
            int a=map[first],b=map[second];
            if(a==b)throw new InvalidOperationException("지형 Mesh에 퇴화한 변이 있습니다.");
            var key=a<b?(a,b):(b,a);
            if(edges.TryGetValue(key,out var edge))
            {
                if(edge.Count>1||edge.A==a)throw new InvalidOperationException("지형 Mesh에 일관되지 않은 공유 변이 있습니다.");
                edges[key]=(edge.A,edge.B,2);
            }
            else edges[key]=(a,b,1);
        }
    }

    internal static void CheckConnectedComponentFixtures()
    {
        foreach(double scale in new[]{1d,1000d})
        {
            var fixtures=new List<SurfacePiece>();
            try
            {
                var main=Rectangle(0,0,1,1,"Land","Near");
                var cap=Rectangle(1,0,1.03,.03,"Land","Near");
                var connected=Check(new[]{main,cap},scale);
                Require(connected.Passed&&connected.Components==1&&connected.TinyTopFaces==1,"Connected small trim is preserved");
                var isolated=Rectangle(2,0,2.04,.04,"Road","Near");
                Require(!Check(new[]{main,isolated},scale).Passed,"Independent 40mm road component is rejected");
                var otherRole=cap with{Role="Road"};
                Require(!Check(new[]{main,otherRole},scale).Passed,"Different roles cannot hide a small component");
                var otherZone=cap with{Zone="Far"};
                Require(!Check(new[]{main,otherZone},scale).Passed,"Different zones cannot hide a small component");
                using var outer=new PolylineCurve(new[]{new Point3d(3,0,0)*scale,new Point3d(3.11,0,0)*scale,
                    new Point3d(3.11,.11,0)*scale,new Point3d(3,.11,0)*scale,new Point3d(3,0,0)*scale});
                using var hole=new PolylineCurve(new[]{new Point3d(3.01,.01,0)*scale,new Point3d(3.01,.10,0)*scale,
                    new Point3d(3.10,.10,0)*scale,new Point3d(3.10,.01,0)*scale,new Point3d(3.01,.01,0)*scale});
                var ring= Brep.CreatePlanarBreps(new Curve[]{outer,hole},ModelPlanPolicy.PrecisionMetres*scale).Single();
                var ringPiece=new SurfacePiece(ring,"","Land","Near");fixtures.Add(ringPiece);
                Require(!Check(new[]{ringPiece},scale).Passed,"Holes are subtracted from connected component area");
                var mesh=new Mesh();mesh.Vertices.Add(0,0,0);mesh.Vertices.Add(scale,0,0);mesh.Vertices.Add(scale,scale,0);mesh.Vertices.Add(0,scale,0);
                mesh.Faces.AddFace(0,1,2,3);var meshPiece=new SurfacePiece(mesh,"","Land","Near");fixtures.Add(meshPiece);
                Require(Check(new[]{meshPiece,cap},scale).Passed,"Mesh boundary and NURBS cap share one projected component");
                var wall=new Mesh();wall.Vertices.Add(2*scale,0,0);wall.Vertices.Add(2*scale,scale,0);
                wall.Vertices.Add(2*scale,scale,scale);wall.Vertices.Add(2*scale,0,scale);wall.Faces.AddFace(0,1,2,3);
                var wallPiece=new SurfacePiece(wall,"","Land","Near");fixtures.Add(wallPiece);
                var withWall=Check(new[]{main,wallPiece},scale);
                Require(withWall.Passed&&withWall.Components==1,"Vertical pit-wall Mesh is not a zero-area top component");
            }
            finally{foreach(var geometry in fixtures.Select(p=>p.Geometry).Distinct())geometry.Dispose();}
            SurfacePiece Rectangle(double x0,double y0,double x1,double y1,string role,string zone)
            {
                using var surface=new PlaneSurface(Plane.WorldXY,new Interval(x0*scale,x1*scale),new Interval(y0*scale,y1*scale));
                var geometry=surface.ToBrep();
                var piece=new SurfacePiece(geometry,"",role,zone);fixtures.Add(piece);return piece;
            }
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    }
}
