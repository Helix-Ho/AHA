using System.Globalization;
using System.Text.Json.Nodes;
using Clipper2Lib;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>Audits generated contour objects without rebuilding the product contour plan.</summary>
internal static class ContourModelChecks
{
    private sealed record Plate(RhinoObject Object, Curve[] Profiles, BoundingBox Box, double Bottom, double Top, double Area);

    internal static bool CompletedGroundFoundationRegression()
    {
        var origin = new GeoPoint(127, 37.5);
        var projection = new LocalProjection(origin);
        var bounds = projection.Bounds(20);
        var field = TerrainField.FromSamples(bounds, projection, 4, 4, Enumerable.Repeat(100.3, 25).ToArray());
        var site = Rectangle("site", -2, -2, 2, 2, 8);
        var road = Rectangle("road", 0, -20, 20, 20, ParcelLandCategory.Road);
        var cutter = Rectangle("cut", -4, 3, -.03, 6);
        var ordinary = Rectangle("ordinary", -.03, 1.5, 1, 5);
        foreach (var units in new[] { UnitSystem.Meters, UnitSystem.Millimeters })
        foreach (bool separate in new[] { false, true })
        {
            double scale = RhinoMath.UnitScale(UnitSystem.Meters, units);
            using var doc = RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem = units; doc.ModelAbsoluteTolerance = .001 * scale;
            var options = new ModelOptions(MinArea: 0, NearRatio: 3, LowerRoads: false,
                Contour: true, ContourIntervalMetres: 3, SeparateFoundations: separate);
            var result = CheckModel.Build(doc, origin, bounds, site, new[] { site, road },
                new[] { cutter, ordinary }, field, options);
            var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
            Require(result.NearBuildings == 2 && result.FarBuildings == 0 && result.InvalidBuildings == 0,
                "Both whole footprints remain generated Near buildings");
            foreach (var pair in new[] { (Id: "cut", Bottom: 96d), (Id: "ordinary", Bottom: 95d) })
            {
                var body = objects.Single(o => Role(o) == "Building" && Id(o) == pair.Id);
                Require(body.Geometry is Extrusion { IsValid: true, IsSolid: true }, "Near building is a closed Extrusion");
                double floor = double.Parse(body.Attributes.GetUserString("AHA.FoundationBottomMetres")!, CultureInfo.InvariantCulture);
                var bodyBox = body.Geometry.GetBoundingBox(true);
                Require(Math.Abs(floor - pair.Bottom) < 1e-7 &&
                    Math.Abs(bodyBox.Min.Z / scale - (separate ? 100.3 : pair.Bottom)) < 1e-7 &&
                    Math.Abs(bodyBox.Max.Z / scale - 106.3) < 1e-7,
                    "Actual body follows the settled foundation and unchanged continuous-ground roof");
                var foundations = objects.Where(o => Role(o) == "Foundation" && Id(o) == pair.Id).ToArray();
                Require(foundations.Length == (separate ? 1 : 0), "Foundation separation follows the option");
                foreach (var foundation in foundations)
                {
                    var box = foundation.Geometry.GetBoundingBox(true);
                    Require(foundation.Geometry is Extrusion { IsValid: true, IsSolid: true } &&
                        Math.Abs(box.Min.Z / scale - pair.Bottom) < 1e-7 && Math.Abs(box.Max.Z / scale - 100.3) < 1e-7,
                        "Separated foundation reaches the same settled floor");
                }
            }
            // The cutter removes the adjacent course, leaving a 30mm land strip.
            // Its omission exposes the 96m support under the nonexcavated building.
            // Read emitted profiles instead of repeating the product contour plan.
            double? actualTop = null;
            foreach (var obj in objects.Where(o => Role(o) is "Land" or "Road" or "Base" &&
                o.Attributes.GetUserString("AHA.Zone") == "Near"))
            {
                if (obj.Geometry is not Extrusion extrusion) continue;
                var point = new Point3d(-.015 * scale, 4 * scale, extrusion.PathStart.Z);
                var profiles = Enumerable.Range(0, extrusion.ProfileCount).Select(i => extrusion.Profile3d(i, 0)).ToArray();
                try
                {
                    if (profiles[0].Contains(point, Plane.WorldXY, 1e-8 * scale) != PointContainment.Inside ||
                        profiles.Skip(1).Any(p => p.Contains(point, Plane.WorldXY, 1e-8 * scale) != PointContainment.Outside)) continue;
                    actualTop = Math.Max(actualTop ?? double.NegativeInfinity, extrusion.PathEnd.Z / scale);
                }
                finally { foreach (var profile in profiles) profile.Dispose(); }
            }
            Require(actualTop is { } height && Math.Abs(height - 96) < 1e-7,
                "The actual output beneath the ordinary building is the 96m support");
            if(units==UnitSystem.Meters&&!separate)
            {
                var plate=objects.First(o=>Role(o)=="Land"&&o.Geometry is Extrusion);
                using var broken=new LineCurve(Point3d.Origin,new Point3d(1,0,0));
                Require(doc.Objects.Replace(plate.Id,broken),"Audit isolation control replaces one owned plate");
                var selection=SiteSelection.Create(new[]{site},projection);
                var near=EditRegion.Create(selection,projection,bounds,options.NearRatio);
                var audit=Run(doc,result,selection,near,field,new[]{cutter,ordinary},new[]{site,road},options);
                var independent=(List<Dictionary<string,object?>>)audit["independent_checks"]!;
                Require(audit["geometry_checks_passed"] is false&&
                    independent.Any(b=>Equals(b["id"],"plate_stack")&&Equals(b["status"],"unavailable"))&&
                    independent.Where(b=>((string)b["id"]!).StartsWith("site:")||((string)b["id"]!).StartsWith("building:"))
                        .All(b=>Equals(b["status"],"passed"))&&
                    independent.Count(b=>((string)b["id"]!).StartsWith("site:"))>0&&
                    independent.Count(b=>((string)b["id"]!).StartsWith("building:"))==2,
                    "A bad plate blocks stack inference but does not suppress independent site and building audits");
            }
        }
        return true;

        GeoFeature Rectangle(string id, double x0, double y0, double x1, double y1, int? category = null) => new()
        {
            Id = id, Pnu = category is null ? "" : id == "site" ? "9900000000200010001" : "9900000000200010002",
            LandCategoryCode = category, Floors = category is null ? 2 : null,
            Polygons = new() { new() { new() { projection.ToGeo(x0, y0), projection.ToGeo(x1, y0),
                projection.ToGeo(x1, y1), projection.ToGeo(x0, y1) } } }
        };
        static string Role(RhinoObject obj) => obj.Attributes.GetUserString("AHA.Role") ?? "";
        static string Id(RhinoObject obj) => obj.Attributes.GetUserString("AHA.FeatureId") ?? "";
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Completed contour ground: " + message); }
    }

    internal static Dictionary<string, object?> Run(RhinoDoc doc, ModelResult result, SiteSelection selection,
        EditRegion near, TerrainField field, IReadOnlyList<GeoFeature> buildings, IReadOnlyList<GeoFeature> parcels, ModelOptions options)
    {
        double scale=RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem), interval=options.ContourIntervalMetres;
        var objects=result.ObjectIds.Select(id=>doc.Objects.FindId(id)??throw new InvalidOperationException("Missing generated object")).ToArray();
        var checks=new Dictionary<string,object?>(); var plates=new List<Plate>(); int assertions=0;
        var blocks=new List<Dictionary<string,object?>>();
        void Block(string id,Action action)
        {
            var entry=new Dictionary<string,object?>{{"id",id}};blocks.Add(entry);
            try{action();entry["status"]="passed";}
            catch(Exception error){entry["status"]="failed";entry["error"]=error.ToString();}
        }
        bool PlatesValid()=>blocks.Where(b=>((string)b["id"]!).StartsWith("plate:")).All(b=>Equals(b["status"],"passed"));
        void Require(bool condition,string message){assertions++;if(!condition)throw new InvalidOperationException(message);}
        string Role(RhinoObject o)=>o.Attributes.GetUserString("AHA.Role")??"";
        string Zone(RhinoObject o)=>o.Attributes.GetUserString("AHA.Zone")??"";
        double Tag(RhinoObject o,string name)=>double.Parse(o.Attributes.GetUserString(name)??throw new InvalidOperationException("Missing "+name),CultureInfo.InvariantCulture);
        bool Grid(double value)=>Math.Abs(value/interval-Math.Round(value/interval))<=1e-7;
        try
        {
            var sites=objects.Where(o=>Role(o)=="Site").ToArray();
            Block("model_objects",()=>{
            Require(objects.All(o=>o.Geometry.IsValid),"All contour model objects are valid");
            Require(sites.Length>=selection.Components.Count && result.SiteObjectIds.ToHashSet().SetEquals(sites.Select(o=>o.Id)),"Site result IDs identify the generated site solids");
            Require(objects.All(o=>o.Attributes.GetUserString("AHA.CurveSpace")!="Projected"),"Contour omits projected target curves");
            });
            var sitePaths=SiteSelection.FeaturePaths(selection.Boundary,field.Projection);
            foreach(var obj in objects.Where(o=>Zone(o)=="Near" && Role(o) is "Land" or "Road" or "Base"))
            {
                Block("plate:"+obj.Id,()=>{
                if(!options.SeparateRoads&&Role(obj)=="Road")
                {Require(obj.Geometry is Brep{IsSolid:false},"Overlay road is a face");return;}
                Require(obj.Geometry is Extrusion{IsValid:true,IsSolid:true},"Contour plates are actual closed Extrusions");
                var extrusion=(Extrusion)obj.Geometry;
                var a=extrusion.PathStart/scale;var b=extrusion.PathEnd/scale;
                Require(Math.Abs(a.X-b.X)<1e-9&&Math.Abs(a.Y-b.Y)<1e-9&&b.Z>a.Z,"Contour plate has a positive vertical extrusion path");
                Require(!extrusion.IsMiteredAtStart&&!extrusion.IsMiteredAtEnd,"Contour plates have parallel horizontal ends");
                var profiles=Enumerable.Range(0,extrusion.ProfileCount).Select(i=>extrusion.Profile3d(i,0)).ToArray();
                bool retained=false;
                try
                {
                foreach(var profile in profiles)profile.Transform(Transform.Scale(Point3d.Origin,1/scale));
                double area=0;
                for(int i=0;i<profiles.Length;i++)
                {
                    Require(profiles[i].IsClosed&&profiles[i].IsValid,"Contour profiles preserve closed outer/inner rings");
                    using var mass=AreaMassProperties.Compute(profiles[i]);
                    Require(mass is {Area:>0},"Contour profile area is positive");
                    area+=(i==0?1:-1)*mass!.Area;
                }
                using var boundary=extrusion.ToBrep();
                Require(boundary is {IsValid:true,IsSolid:true,IsManifold:true}&&
                    boundary.Edges.All(edge=>edge.Valence==EdgeAdjacency.Interior),"Contour plate has a closed manifold analytical boundary");
                using var volume=VolumeMassProperties.Compute(boundary,true,false,false,false);
                Require(volume is {Volume:>0}&&Math.Abs(volume.Volume/Math.Pow(scale,3)-area*(b.Z-a.Z))<=Math.Max(1e-7,area*(b.Z-a.Z)*1e-7),"Contour object is a constant section with the expected positive volume");
                if(Role(obj)!="Base")
                {
                    Require(Grid(a.Z)&&b.Z-a.Z<=interval+1e-7,"Contour courses start on the common level grid and are at most one interval thick");
                    Require(Grid(b.Z)||(Role(obj)=="Road"&&Grid(b.Z+options.EffectiveRoadDrop)),"Only road top plates use lowered elevations");
                }
                var box=extrusion.GetBoundingBox(true);box.Transform(Transform.Scale(Point3d.Origin,1/scale));
                plates.Add(new(obj,profiles,box,a.Z,b.Z,area));retained=true;
                }
                finally{if(!retained)foreach(var profile in profiles)profile.Dispose();}
                });
            }
            double baseBottom=result.NearBottomElevationMetres!.Value;
            double baseTop=baseBottom+options.NearDepthMetres-2*interval;
            Block("base_and_support",()=>{
            Require(PlatesValid(),"Support audit requires all plate profiles");
            var bases=plates.Where(p=>Role(p.Object)=="Base").ToArray();
            Require(bases.Length>0,"Contour has a thick base");
            Require(bases.All(p=>Math.Abs(p.Top-baseTop)<1e-8&&Math.Abs(p.Bottom-baseBottom)<1e-8)
                &&Math.Abs(baseTop-baseBottom-options.NearDepthMetres+2*interval)<1e-7
                &&Math.Abs(baseBottom-result.NearBottomElevationMetres!.Value)<1e-7,"Base and two support courses share the requested depth");
            double nearArea=bases.Sum(p=>p.Area),siteArea=Geo.Area(selection.Boundary,field.Projection);
            for(int level=1;level<=2;level++)
            {
                double top=baseTop+level*interval;
                var support=plates.Where(p=>Role(p.Object)!="Base"&&Math.Abs(p.Top-top)<1e-8&&Math.Abs(p.Bottom-top+interval)<1e-8).ToArray();
                Require(Math.Abs(support.Sum(p=>p.Area)-(nearArea-siteArea))<=Math.Max(1e-5,nearArea*1e-7),"Each support course covers Near minus the unchanged selected site");
            }
            });
            foreach(var obj in sites)Block("site:"+obj.Id,()=>
            {
                Require(obj.Geometry is Brep{IsValid:true,IsSolid:true,IsManifold:true},"Selected site remains a closed curved Brep");
                var brep=(Brep)obj.Geometry;
                Require(Math.Abs(brep.GetBoundingBox(true).Min.Z/scale-baseTop)<1e-7,"Selected site bottom contacts the thick base top");
                var info=Aha.Info.InfoData.Parse(obj.Attributes.GetUserString(Aha.Info.InfoData.Key)!);
                Require(info["site"]!.GetValue<bool>()&&info["data"]?["parcels"] is JsonArray{Count:>0},"Selected site stores site=true and source parcels");
                var pnu=info["data"]!["parcels"]!.AsArray().Select(p=>p!["pnu"]?.GetValue<string>()??"").ToHashSet();
                Require(selection.Components.Any(c=>c.Parcels.Select(p=>p.Pnu).ToHashSet().SetEquals(pnu)),"Site parcel metadata belongs to one selected component");
                foreach(var face in brep.Faces.Where(f=>f.NormalAt(f.Domain(0).Mid,f.Domain(1).Mid).Z>1e-8))
                for(int u=1;u<=3;u++)for(int v=1;v<=3;v++)
                {
                    double pu=face.Domain(0).ParameterAt(u/4d),pv=face.Domain(1).ParameterAt(v/4d);
                    if(face.IsPointOnFace(pu,pv)==PointFaceRelation.Exterior)continue;
                    var point=face.PointAt(pu,pv)/scale;
                    Require(Math.Abs(point.Z-field.SampleModelLocal(point.X,point.Y))<=.0011,"Site top follows continuous terrain without road drop");
                }
            });
            int farSurfaceSamples=0,connectionFaces=0;
            foreach(var obj in objects.Where(o=>Zone(o)=="Far"&&Role(o) is "Land" or "Road"))Block("far_surface:"+obj.Id,()=>
            {
                double drop=Role(obj)=="Road"?options.EffectiveRoadDrop:0;
                Require(options.TerrainAsNurbs?obj.Geometry is Brep{Faces.Count:1}:obj.Geometry is Mesh,"Far terrain retains the requested representation");
                void CheckPoint(Point3d value,double tolerance)
                {
                    var point=value/scale;farSurfaceSamples++;
                    Require(Math.Abs(point.Z-field.SampleModelLocal(point.X,point.Y)+drop)<=tolerance,"Far terrain retains continuous field height and road offset");
                }
                void CheckMesh(Mesh mesh)
                {
                    foreach(var point in mesh.Vertices.ToPoint3dArray())CheckPoint(point,.051);
                    foreach(var face in mesh.Faces)
                    {
                        var a=mesh.Vertices.Point3dAt(face.A);var b=mesh.Vertices.Point3dAt(face.B);var c=mesh.Vertices.Point3dAt(face.C);
                        Require(Vector3d.CrossProduct(b-a,c-a).Z>0,"Far terrain faces remain upward");
                        CheckPoint((a+b+c)/3,.051);
                        if(face.IsQuad)CheckPoint((a+c+mesh.Vertices.Point3dAt(face.D))/3,.051);
                    }
                }
                if(obj.Geometry is Mesh mesh)CheckMesh(mesh);
                else foreach(var face in ((Brep)obj.Geometry).Faces)
                {
                    Require(face.UnderlyingSurface() is NurbsSurface,"Far editable terrain retains its NURBS surface");
                    var display=face.GetMesh(MeshType.Render);
                    Require(display is {IsValid:true}&&display.Faces.Count>0,"Far surface includes its render mesh");CheckMesh(display!);
                    for(int u=1;u<=3;u++)for(int v=1;v<=3;v++)
                    {
                        double pu=face.Domain(0).ParameterAt(u/4d),pv=face.Domain(1).ParameterAt(v/4d);
                        if(face.IsPointOnFace(pu,pv)!=PointFaceRelation.Exterior)CheckPoint(face.PointAt(pu,pv),.0011);
                    }
                }
            });
            var roads=RoadGeometry.Build(options.IncludeRoads?parcels:Array.Empty<GeoFeature>(),field.Projection,field.Bounds,result.Bounds,near);
            roads=TerrainSurface.Prepare(field,roads,options.EffectiveRoadDrop,options.TerrainAsNurbs,
                options.SeparateRoads,near,options.NearDepthMetres,result.Bounds,includeNear:false).GroundRoadArea;
            foreach(var obj in objects.Where(o=>Role(o)=="Connection"))Block("connection:"+obj.Id,()=>
            {
                Require(obj.Geometry is Brep{IsValid:true,IsSolid:false},"Near outer wall is an open NURBS face");
                var wall=(Brep)obj.Geometry;
                ContourWallChecks.Verify(wall,near,field,options.SeparateRoads?roads.Outlines:new(),options.EffectiveRoadDrop,baseTop,scale);
                connectionFaces+=wall.Faces.Count;
            });
            roads=roads with{Outlines=Clipper.Difference(roads.Outlines,sitePaths,FillRule.NonZero,EditRegion.ClipPrecision)};
            var sources=buildings.ToDictionary(b=>b.Id,StringComparer.Ordinal);
            var generated=buildings.Where(b=>TerrainCoverage.IsGenerated(b,field.Projection,result.Bounds,options.MinArea)).ToArray();
            var placement=BuildingPlacement.Prepare(field,roads,options.EffectiveRoadDrop,options.SeparateRoads,generated);
            var byId=placement.Placements.ToDictionary(p=>p.SourceFeatureId,StringComparer.Ordinal);
            var bodies=objects.Where(o=>Role(o)=="Building").ToLookup(o=>o.Attributes.GetUserString("AHA.FeatureId")!);
            Block("building_completeness",()=>Require(byId.Keys.ToHashSet().SetEquals(bodies.Select(g=>g.Key)),"Contour retains every valid generated building"));
            int nearBuildings=0,farBuildings=0;
            foreach(var group in bodies)Block("building:"+group.Key,()=>
            {
                var item=byId[group.Key];bool isNear=near.ContainsStrict(sources[group.Key],field.Projection)&&near.ContainsStrict(item.Footprint);
                double roof=item.MeanElevationMetres+BuildingRules.Resolve(item.Feature,options).Height;
                if(isNear)nearBuildings+=group.Count();else farBuildings+=group.Count();
                foreach(var obj in group)
                {
                    Require(Zone(obj)==(isNear?"Near":"Far"),"Only wholly contained original and cleaned buildings are Near");
                    Require(isNear||options.BuildingsAsNurbs?obj.Geometry is Extrusion:obj.Geometry is Mesh,"Near buildings are Extrusions and Far uses the selected representation");
                    Require(ClosedBuildingGeometry(obj.Geometry),"Every contour building has closed actual geometry");
                    var box=obj.Geometry.GetBoundingBox(true);
                    Require(Math.Abs(box.Max.Z/scale-roof)<1e-7&&Math.Abs(Tag(obj,"AHA.ReferenceMeanElevationMetres")-item.MeanElevationMetres)<1e-7,"Building roof preserves the continuous-ground area mean");
                    double floor=Tag(obj,"AHA.FoundationBottomMetres");
                    Require(floor<item.MinimumElevationMetres+1e-7,"Foundation extends below the whole-footprint continuous ground");
                    Require(Math.Abs(box.Min.Z/scale-(options.SeparateFoundations?item.MeanElevationMetres:floor))<1e-7,"Body bottom follows the selected foundation representation");
                }
                var foundations=objects.Where(o=>Role(o)=="Foundation"&&o.Attributes.GetUserString("AHA.FeatureId")==group.Key).ToArray();
                Require(options.SeparateFoundations?foundations.Length==group.Count():foundations.Length==0,"Foundation separation creates the corresponding objects only when enabled");
                foreach(var obj in foundations)
                {
                    Require(isNear||options.BuildingsAsNurbs?obj.Geometry is Extrusion:obj.Geometry is Mesh,"Foundations use the same representation as their building");
                    Require(ClosedBuildingGeometry(obj.Geometry),"Every separate foundation has closed actual geometry");
                    var box=obj.Geometry.GetBoundingBox(true);
                    Require(Math.Abs(box.Min.Z/scale-Tag(obj,"AHA.FoundationBottomMetres"))<1e-7&&Math.Abs(box.Max.Z/scale-item.MeanElevationMetres)<1e-7,"Foundation spans its floor to the original mean height");
                }
            });
            Block("parapets", () => Require(ParapetGeometryChecks.Audit(doc, objects, options, scale), "Near parapets meet their own profile, height, solid and layer contract"));
            int stackSamples=0;
            if(!PlatesValid())blocks.Add(new(){{"id","plate_stack"},{"status","unavailable"},{"reason","One or more actual plate profiles failed"}});
            else Block("plate_stack",()=>{
            var points=near.Paths.SelectMany(p=>p).ToArray();double x0=points.Min(p=>p.x),x1=points.Max(p=>p.x),y0=points.Min(p=>p.y),y1=points.Max(p=>p.y);
            for(int i=1;i<17;i++)for(int j=1;j<17;j++)
            {
                var point=new Point3d(x0+(x1-x0)*i/17,y0+(y1-y0)*j/17,0);
                if(!near.ContainsLocal(point.X,point.Y)||InPaths(sitePaths,point))continue;
                var stack=plates.Where(p=>Contains(p,point)).OrderBy(p=>p.Bottom).ToArray();
                Require(stack.Length>=3,"Every surrounding interior sample is supported by base and two courses");
                for(int k=1;k<stack.Length;k++)Require(Math.Abs(stack[k].Bottom-stack[k-1].Top)<=1e-7,"Contour stack has no vertical gaps or overlaps");
                stackSamples++;
            }
            Require(stackSamples>0,"Contour stack coverage was sampled");
            });
            checks["independent_checks"]=blocks;
            checks["geometry_checks_passed"]=blocks.All(b=>Equals(b["status"],"passed"));checks["contour_model_assertions"]=assertions;
            checks["object_count"]=objects.Length;checks["native_geometry_bytes"]=objects.Sum(o=>(long)o.Geometry.MemoryEstimate());
            checks["contour_plate_objects"]=plates.Count;checks["site_objects"]=sites.Length;checks["contour_stack_samples"]=stackSamples;
            checks["far_surface_samples"]=farSurfaceSamples;checks["connection_faces"]=connectionFaces;
            checks["near_building_objects"]=nearBuildings;checks["far_building_objects"]=farBuildings;checks["checked_building_features"]=bodies.Count;
            return checks;
        }
        finally{foreach(var plate in plates)foreach(var profile in plate.Profiles)profile.Dispose();}
    }
    private static bool ClosedBuildingGeometry(GeometryBase geometry)
    {
        if (geometry is Mesh mesh) return mesh.IsValid && mesh.IsClosed && mesh.Faces.Count > 0;
        if (geometry is not Extrusion { IsValid: true, IsSolid: true } extrusion) return false;
        // Validate the analytical boundary representation, independently of
        // the display mesh Rhino may generate for the lightweight object.
        using var boundary = extrusion.ToBrep();
        return boundary is { IsValid: true, IsSolid: true, IsManifold: true }
            && boundary.Edges.All(edge => edge.Valence == EdgeAdjacency.Interior);
    }
    private static bool Contains(Plate plate,Point3d point)
    {
        if(point.X<=plate.Box.Min.X||point.X>=plate.Box.Max.X||point.Y<=plate.Box.Min.Y||point.Y>=plate.Box.Max.Y)return false;
        var p=new Point3d(point.X,point.Y,plate.Bottom);
        return plate.Profiles[0].Contains(p,Plane.WorldXY,1e-8)==PointContainment.Inside
            &&plate.Profiles.Skip(1).All(curve=>curve.Contains(p,Plane.WorldXY,1e-8)==PointContainment.Outside);
    }
    private static bool InPaths(PathsD paths,Point3d point)
    {
        int winding=0;
        foreach(var path in paths)
        {
            var containment=Clipper.PointInPolygon(new(point.X,point.Y),path,6);
            if(containment==PointInPolygonResult.IsOn)return true;
            if(containment==PointInPolygonResult.IsInside)winding+=Clipper.Area(path)>0?1:-1;
        }
        return winding!=0;
    }
}
