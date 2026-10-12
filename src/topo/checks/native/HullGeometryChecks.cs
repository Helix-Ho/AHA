using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class HullGeometryChecks
{
    internal static Dictionary<string,object?> Run()
    {
        var results=new List<Dictionary<string,object?>>();
        var projection=new LocalProjection(new GeoPoint(127,37.5));var bounds=projection.Bounds(40);
        var field=TerrainField.FromSamples(bounds,projection,32,32,Enumerable.Repeat(20d,33*33).ToArray());
        int parcelNumber=0;
        GeoFeature Parcel(string id,double x,double y,double radius)
        {
            double h=radius/(2*Math.Sqrt(2));
            return new(){Id=id,Pnu=$"99000000001{++parcelNumber:D4}0000",Polygons=new(){new(){new(){projection.ToGeo(x-h,y-h),projection.ToGeo(x+h,y-h),
                projection.ToGeo(x+h,y+h),projection.ToGeo(x-h,y+h),projection.ToGeo(x-h,y-h)}}}};
        }
        var cases=new[]{
            ("equal-road",new[]{Parcel("a",-8,0,10),Parcel("b",8,0,10)},true),
            ("unequal",new[]{Parcel("a",0,0,10),Parcel("b",12,0,4)},false),
            ("triangle-road",new[]{Parcel("a",0,0,10),Parcel("b",16,0,10),Parcel("c",8,Math.Sqrt(192),10)},true),
            ("repeat-hull-merge",new[]{Parcel("a",-8,0,10),Parcel("b",8,0,10),Parcel("c",0,11,1),Parcel("d",-4,10.9,.1)},false)};
        foreach(var (name,parcels,withRoad) in cases)foreach(bool nurbs in new[]{false,true})foreach(double scale in new[]{1d,1000d})
        {
            var region=EditRegion.Create(SiteSelection.Create(parcels,projection),projection,bounds,2);
            var roads=RoadGeometry.Build(withRoad?new[]{new GeoFeature{Id="road",Pnu="9900000000199990000",SourceLayer="dt_d002",ParcelLabel="1 도",LandCategoryCode=14,
                Polygons=new(){new(){new(){projection.ToGeo(-40,6.5),projection.ToGeo(40,6.5),projection.ToGeo(40,9.5),projection.ToGeo(-40,9.5),projection.ToGeo(-40,6.5)}}}}}:Array.Empty<GeoFeature>(),projection,bounds);
            TerrainSurface? result=null;
            try
            {
                var beforeHeights=field.Heights.ToArray();
                result=TerrainSurface.Build(field,roads,.2,scale,.001,nurbs,true,region,30);
                var tops=result.Pieces.Where(p=>p.Role is "Land" or "Road").ToArray();
                var near=tops.Where(p=>p.Zone=="Near").Select(p=>(Brep)p.Geometry).ToArray();
                var boundary=NearBoundaryChecks.AuditBoundary(region,near,scale,.001,false);
                var common=NearGeometryChecks.Check(result.Pieces,region,field,.2,scale,true,30,result.BottomElevationMetres);
                var displays=near.Select(b=>SolidDisplayChecks.Check(b,scale)).ToArray();
                var orientations=near.Select((b,i)=>NearSolidOrientationChecks.Check(b,scale,displays[i])).ToArray();
                var quality=ModelSurfaceQualityChecks.Check(tops,scale);
                double nearArea=0,heightError=0,minimumVolume=double.PositiveInfinity;
                int invalid=0,missingCaches=0;
                foreach(var piece in tops)
                {
                    if(!piece.Geometry.IsValid)invalid++;
                    if(piece.Geometry is not Brep brep)continue;
                    if(piece.Zone=="Near")
                    {
                        using var volume=VolumeMassProperties.Compute(brep);
                        if(!brep.IsSolid||volume is null||volume.Volume<=Math.Abs(volume.VolumeError))invalid++;
                        else minimumVolume=Math.Min(minimumVolume,volume.Volume/(scale*scale*scale));
                    }
                    foreach(var face in brep.Faces)
                    {
                        var cache=face.GetMesh(MeshType.Render);if(cache is null){missingCaches++;continue;}
                        if(face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z<=1e-8)continue;
                        if(piece.Zone=="Near")
                        {
                            using var projected=face.DuplicateFace(false);projected.Transform(Transform.PlanarProjection(Plane.WorldXY));
                            using var area=AreaMassProperties.Compute(projected);if(area is null)invalid++;else nearArea+=area.Area/(scale*scale);
                        }
                        foreach(var p in cache.Vertices.ToPoint3dArray())
                            if(face.ClosestPoint(p,out double u,out double v))heightError=Math.Max(heightError,Math.Abs(face.PointAt(u,v).Z/scale-(piece.Role=="Road"?19.8:20)));
                    }
                }
                double nearError=Math.Abs(nearArea-region.Boundaries.Sum(HullChecks.Area));
                double perimeter=region.Boundaries.SelectMany(b=>b.Segments).Sum(s=>s.Circle is {} c?c.RadiusMetres*s.SweepRadians:s.Start.DistanceTo(s.End));
                double bottomError=Math.Abs(result.BottomElevationMetres!.Value-((withRoad?19.8:20)-30));
                bool unchanged=field.Heights.SequenceEqual(beforeHeights);
                bool passed=unchanged&&near.Length>0&&invalid==0&&missingCaches==0&&boundary.Passed&&(bool)common["near_geometry_checks_passed"]!&&quality.Passed
                    &&orientations.All(a=>a.Passed)&&displays.All(a=>a.Passed)&&heightError<=1e-7&&bottomError<=1e-7&&nearError<=perimeter*.001;
                results.Add(new(){["case"]=name,["nurbs_input"]=nurbs,["scale"]=scale,["passed"]=passed,["source_heightfield_unchanged"]=unchanged,
                    ["boundary"]=boundary,["near_geometry"]=common,["surface_quality"]=quality,
                    ["near_exact_area_error_m2"]=nearError,["near_intersection_area_error_bound_m2"]=perimeter*.001,["height_error_m"]=heightError,
                    ["invalid_objects"]=invalid,["missing_caches"]=missingCaches,["near_solids"]=near.Length,["minimum_volume_m3"]=minimumVolume,
                    ["bottom_error_m"]=bottomError,["solid_orientation_audits"]=orientations,["solid_display_meshes"]=displays});
            }
            catch(Exception error){results.Add(new(){["case"]=name,["nurbs_input"]=nurbs,["scale"]=scale,["passed"]=false,["error"]=error.ToString()});}
            finally
            {
                if(result is not null)foreach(var geometry in result.Pieces.Select(p=>p.Geometry).Distinct())geometry.Dispose();
            }
        }
        return new(){["geometry_checks_passed"]=results.All(r=>(bool)r["passed"]!),["unified_hull_cases"]=results,
            ["scope"]="One graded heightfield and one cleaned plan directly produce final Near/Far: equal/unequal disks, three-disk hull and repeated merging; closed solids, actual arc/tangent distance, 1mm seams, XY coverage and independent source preservation in metres and millimetres."};
    }
}
