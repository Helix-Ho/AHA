using Clipper2Lib;
using Rhino;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class ContourGeometryChecks
{
    internal static Dictionary<string,object?> Run()
    {
        var checks=new Dictionary<string,object?>();
        foreach(double scale in new[]{1d,1000d})
        {
            var points=new[]{new EditBoundaryPoint(0,0),new EditBoundaryPoint(5,0),
                new EditBoundaryPoint(5+1e-12,1e-12),new EditBoundaryPoint(10,0)};
            var edges=points.Take(3).Select((p,i)=>new ExactNearRegions.Edge(new(p,points[i+1]),i,0,1,p,points[i+1])).ToList();
            var arc=new EditBoundarySegment(points[^1],points[0],new EditCircle("arc",5,0,5),0,Math.PI);
            edges.Add(new(arc,3,0,1,points[^1],points[0]));
            var ring=new ExactNearRegions.Ring(edges);
            var curves=ExactProfileGeometry.Curves(new(ring,Array.Empty<ExactNearRegions.Ring>()));
            try
            {
                var curve=curves.Single();curve.Transform(Transform.Scale(Point3d.Origin,scale));
                using var extrusion=Extrusion.Create(curve,scale,true);
                using var brep=extrusion?.ToBrep();
                using var volume=brep is null?null:VolumeMassProperties.Compute(brep,true,false,false,false,1e-12,1e-12*Math.Pow(scale,3));
                checks[$"scale_{scale}_mixed_arc_short_straight_chain_preserves_solid"]=curve.IsValid&&curve.IsClosed&&
                    brep is {IsValid:true,IsSolid:true,IsManifold:true}&&volume is not null&&
                    Math.Abs(volume.Volume/Math.Pow(scale,3)-ring.SignedArea)<1e-8;
            }
            finally{foreach(var curve in curves)curve.Dispose();}
        }
        foreach(double scale in new[]{1d,1000d})
        foreach(bool reverse in new[]{false,true})
        {
            var points=new[]{new EditBoundaryPoint(140,88),new EditBoundaryPoint(144,88),
                new EditBoundaryPoint(144,99),new EditBoundaryPoint(144,105),
                new EditBoundaryPoint(144,105+2.2e-10),new EditBoundaryPoint(140,105+2.2e-10)};
            if(reverse)Array.Reverse(points);
            var edges=points.Select((p,i)=>new ExactNearRegions.Edge(new(p,points[(i+1)%points.Length]),i,0,1,p,points[(i+1)%points.Length])).ToArray();
            var ring=new ExactNearRegions.Ring(edges);
            var curves=ExactProfileGeometry.Curves(new(ring,Array.Empty<ExactNearRegions.Ring>()));
            try
            {
                var curve=curves.Single();curve.Transform(Transform.Scale(Point3d.Origin,scale));
                using var area=AreaMassProperties.Compute(curve);
                checks[$"scale_{scale}_reverse_{reverse}_collinear_subdivision_preserves_profile"]=curve.IsValid&&curve.IsClosed&&
                    area is not null&&Math.Abs(area.Area/(scale*scale)-Math.Abs(ring.SignedArea))<1e-8;
            }
            finally{foreach(var curve in curves)curve.Dispose();}
        }
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var a=projection.ToGeo(-20,-20);var b=projection.ToGeo(20,20);var bounds=new GeoBounds(a.Lon,a.Lat,b.Lon,b.Lat);
        var feature=new GeoFeature{Id="site",Polygons=new(){new(){new(){projection.ToGeo(-2,-2),projection.ToGeo(2,-2),projection.ToGeo(2,2),projection.ToGeo(-2,2),projection.ToGeo(-2,-2)}}}};
        var near=EditRegion.Create(feature,projection,bounds,3);
        var sites=SiteSelection.FeaturePaths(feature,projection);
        var field=TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Repeat(100d,25).ToArray());
        foreach(double scale in new[]{1d,1000d})
        {
            var heights=Enumerable.Range(0,5).SelectMany(y=>Enumerable.Range(0,5)
                .Select(x=>100+.1*field.ModelX(x))).ToArray();
            var slope=TerrainField.FromSamples(bounds,projection,4,4,heights);
            var plan=ContourPlan.Build(slope,ContourPaths.Rectangle(2.03,-4,4,4),near,sites,1,0,30,true);
            var pieces=ContourGeometry.Create(plan,slope,scale,.00001);
            try
            {
                var solids=pieces.Where(piece=>piece.Role!="Connection").ToArray();
                checks[$"scale_{scale}_site_neck_solids"]=solids.All(piece=>ActualSolid(piece.Geometry));
                // Default native integration can be less accurate than the analytic volume assertion.
                double actual=solids.Sum(piece=>
                {
                    using var brep=((Extrusion)piece.Geometry).ToBrep();
                    using var volume=VolumeMassProperties.Compute(brep,true,false,false,false,1e-12,1e-12*Math.Pow(scale,3));
                    return volume!.Volume/Math.Pow(scale,3);
                });
                double expected=plan.Plates.Sum(plate=>plate.Exact!.Sum(c=>c.Area)*(plate.Top-plate.Bottom));
                checks[$"scale_{scale}_site_neck_exact_volume"]=Math.Abs(actual-expected)<Math.Max(1e-7,expected*1e-8);
                bool Contains(double z)=>pieces.Where(piece=>piece.Role=="Land").Any(piece=>
                {
                    using var brep=((Extrusion)piece.Geometry).ToBrep();
                    return brep.IsPointInside(new Point3d(2.015*scale,scale,z*scale),.000001*scale,true);
                });
                checks[$"scale_{scale}_site_neck_omitted_with_support"]=!Contains(99.5)&&Contains(plan.DetailLevel-.5);
            }
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        foreach(double scale in new[]{1d,1000d})
        foreach(double roadDrop in new[]{0d,.05,.2,1d,1.2})
        {
            var road=ContourPaths.Rectangle(-20,3,20,6);
            var hole=ContourPaths.Rectangle(-3,3.5,-1,5);
            var plan=ContourPlan.Build(field,road,near,sites,1,roadDrop,30,true,new[]{new ContourBuilding("b",hole,99,true)});
            var pieces=ContourGeometry.Create(plan,field,scale,.00001);
            try
            {
                foreach(var wall in pieces.Where(p=>p.Role=="Connection"))
                    ContourWallChecks.Verify((Brep)wall.Geometry,near,field,road,roadDrop,plan.BaseTop,scale);
                var solids=pieces.Where(p=>p.Role!="Connection").ToArray();
                bool actualExtrusions=solids.Length>0&&solids.All(p=>ActualSolid(p.Geometry));
                bool positiveVolumes=solids.All(p=>{using var volume=VolumeMassProperties.Compute((Extrusion)p.Geometry);return volume is {Volume:>0};});
                double modelVolume=solids.Sum(p=>{using var volume=VolumeMassProperties.Compute((Extrusion)p.Geometry);return volume!.Volume/Math.Pow(scale,3);});
                double expectedApprox=plan.Plates.Sum(p=>ContourPaths.Area(p.Profiles)*(p.Top-p.Bottom));
                // Exact circle output has slightly more area than the chord plan,
                // bounded by its documented 1mm chord envelope.
                double allowance=2*Math.PI*near.Circles[0].RadiusMetres*.001*31;
                bool volumeMatches=Math.Abs(modelVolume-expectedApprox)<=allowance;
                var baseObject=solids.Single(p=>p.Role=="Base").Geometry;
                var box=baseObject.GetBoundingBox(true);
                bool exactCircle=Math.Abs(box.Max.X/scale-near.Circles[0].RadiusMetres)<1e-7&&Math.Abs(box.Min.X/scale+near.Circles[0].RadiusMetres)<1e-7;
                string name=$"scale_{scale}_drop_{roadDrop}";
                checks[name+"_extrusion_type"]=actualExtrusions;
                checks[name+"_solid_volume"]=positiveVolumes&&volumeMatches;
                checks[name+"_exact_circle"]=exactCircle;
            }
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        {
            var first=ContourPaths.Rectangle(-4,3,-2,5);var second=ContourPaths.Rectangle(-2,5,0,7);
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{new ContourBuilding("a",first,99,true),new ContourBuilding("b",second,99,true)});
            var pieces=ContourGeometry.Create(plan,field,1,.00001);
            try{checks["point_touching_foundation_holes_have_valid_extrusions"]=pieces.Where(p=>p.Role!="Connection").All(p=>ActualSolid(p.Geometry));}
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        {
            var first=new PathsD{new PathD{new(-2,4),new(-6,4),new(-6,3.2)}};
            var second=new PathsD{new PathD{new(-2,4),new(-6,2.8),new(-6,2)}};
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{new ContourBuilding("acute-a",first,99,true),new ContourBuilding("acute-b",second,99,true)});
            var pieces=ContourGeometry.Create(plan,field,1,.00001);
            try{checks["acute_point_contacts_split_along_source_diagonals"]=pieces.Where(p=>p.Role!="Connection").All(p=>ActualSolid(p.Geometry));}
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        {
            var plan=ContourPlan.Build(field,ContourPaths.Rectangle(-20,3,20,6),near,sites,1,.2,30,false);
            var pieces=ContourGeometry.Create(plan,field,1,.00001);
            try{checks["overlay_road_is_surface_land_remains_extrusion"]=pieces.Any(p=>p.Role=="Road"&&p.Geometry is Brep{IsSolid:false})&&pieces.Where(p=>p.Role=="Land").All(p=>ActualSolid(p.Geometry));}
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        {
            var courtyard=ContourPaths.Rectangle(-.015,3.5,.015,5.5);
            var shell=ContourPaths.Difference(ContourPaths.Rectangle(-3,3,3,6),courtyard);
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{new ContourBuilding("court",shell,99,true)});
            var pieces=ContourGeometry.Create(plan,field,1,.00001);
            try
            {
                checks["narrow_source_courtyard_remains_actual_solid_extrusion"]=pieces.Any(piece=>
                {
                    if(piece.Geometry is not Extrusion { IsValid:true, IsSolid:true } extrusion)return false;
                    var box=extrusion.GetBoundingBox(true);
                    if(Math.Abs(box.Min.Z-99)>1e-8||Math.Abs(box.Max.Z-100)>1e-8)return false;
                    using var volume=VolumeMassProperties.Compute(extrusion);
                    return Math.Abs(box.Min.X+.015)<1e-8&&Math.Abs(box.Max.X-.015)<1e-8&&
                        Math.Abs((volume?.Volume??0)-.06)<1e-8;
                });
            }
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        {
            var elevated=TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Repeat(100.35,25).ToArray());
            var plan=ContourPlan.Build(elevated,new(),near,sites,1,0,30,true);
            var pieces=ContourGeometry.Create(plan,elevated,1,.00001);
            try
            {
                var walls=pieces.Where(piece=>piece.Role=="Connection").Select(piece=>(Brep)piece.Geometry).ToArray();
                double expected=2*Math.PI*near.Circles[0].RadiusMetres*(100.35-plan.BaseTop);
                double area=walls.Sum(wall=>{using var measure=AreaMassProperties.Compute(wall);return measure?.Area??0;});
                checks["open_outer_wall_reaches_base_top"]=walls.Length>0&&Math.Abs(area-expected)<expected*1e-7;
                foreach(var wall in walls)ContourWallChecks.Verify(wall,near,elevated,new(),0,plan.BaseTop,1);
            }
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        for(int rotation=1;rotation<=7;rotation++)
        {
            double angle=rotation*.137;
            var road=ContourPaths.Union(new(){new PathD(ContourPaths.Rectangle(-20,3,20,6)[0].Select(p=>
                new PointD(Math.Round(p.x*Math.Cos(angle)-p.y*Math.Sin(angle),6),Math.Round(p.x*Math.Sin(angle)+p.y*Math.Cos(angle),6))))});
            var resolved=ContourPlan.Build(field,road,near,sites,1,.2,30,true);
            var pieces=ContourGeometry.Create(resolved,field,1,.001);
            try
            {
                foreach(var wall in pieces.Where(p=>p.Role=="Connection"))
                    ContourWallChecks.Verify((Brep)wall.Geometry,near,field,road,.2,resolved.BaseTop,1);
            }
            finally{foreach(var piece in pieces)piece.Geometry.Dispose();}
        }
        checks["clipped_contour_domain_preserves_original_far_road_transitions"]=true;
        checks["bilinear_boundary_lift_and_wall"]=ContourWallChecks.LiftRegression();
        checks["far_only_geometry_preserves_surfaces_and_road_sides"]=FarSurfaceChecks.Run();
        checks["noncut_near_foundation_uses_completed_plate_ground"]=ContourModelChecks.CompletedGroundFoundationRegression();
        checks["geometry_checks_passed"]=checks.Values.OfType<bool>().All(x=>x);
        if(!(bool)checks["geometry_checks_passed"]!)throw new InvalidOperationException("Contour Rhino: "+string.Join(", ",checks.Where(p=>p.Value is false).Select(p=>p.Key)));
        return checks;
    }
    private static bool ActualSolid(GeometryBase geometry)
    {
        if(geometry is not Extrusion {IsSolid:true,IsValid:true} extrusion)return false;
        using var boundary=extrusion.ToBrep();
        if(boundary is not {IsValid:true,IsSolid:true,IsManifold:true}||
            boundary.Edges.Any(e=>e.Valence!=EdgeAdjacency.Interior))return false;
        using var volume=VolumeMassProperties.Compute(boundary);
        return volume is not null&&double.IsFinite(volume.Volume)&&volume.Volume>0;
    }
}
