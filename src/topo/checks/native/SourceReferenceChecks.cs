using Rhino;
using Rhino.Geometry;
using NetTopologySuite.Geometries;

namespace Aha.Topo.Checks;

internal static class SourceReferenceChecks
{
    internal static Dictionary<string,object?> Run(RhinoDoc doc,ModelResult result,LocalProjection projection,GeoBounds bounds,
        IReadOnlyList<GeoFeature> parcels,IReadOnlyList<GeoFeature> buildings,bool includeRoads)
    {
        double scale=RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem);
        var objects=result.ObjectIds.Select(id=>doc.Objects.FindId(id)!).ToArray();
        var curves=objects.Where(o=>o.Attributes.GetUserString("AHA.CurveSpace")=="XY").ToArray();
        var curvesByFeature=curves.ToLookup(o=>(Role:o.Attributes.GetUserString("AHA.Role"),Id:o.Attributes.GetUserString("AHA.FeatureId")));
        var built=objects.Where(o=>o.Attributes.GetUserString("AHA.Role")=="Building").ToLookup(o=>o.Attributes.GetUserString("AHA.FeatureId"));
        var builtIds=built.Select(g=>g.Key).ToHashSet();
        bool sharedBuildingInfo=built.All(group=>curvesByFeature[("BuildingFootprint",group.Key)].Any()
            &&curvesByFeature[("BuildingFootprint",group.Key)].All(curve=>group.All(model=>
                model.Attributes.GetUserString(Aha.Info.InfoData.Key)==curve.Attributes.GetUserString(Aha.Info.InfoData.Key))));
        var a=projection.ToLocal(new(bounds.West,bounds.South));var b=projection.ToLocal(new(bounds.East,bounds.North));
        var factory=new GeometryFactory();var box=factory.ToGeometry(new Envelope(a.X,b.X,a.Y,b.Y));
        int groups=0,errors=0,segments=0;
        foreach(var (role,features) in new[]{("ParcelBoundary",parcels),
            ("RoadBoundary",(IReadOnlyList<GeoFeature>)(includeRoads?parcels.Where(p=>p.LandCategoryCode==14).ToArray():Array.Empty<GeoFeature>())),
            ("BuildingFootprint",(IReadOnlyList<GeoFeature>)buildings.Where(p=>builtIds.Contains(p.Id)).ToArray())})
        foreach(var feature in features)
        {
            var expected=new List<(Point3d A,Point3d B)>();
            foreach(var ring in feature.Polygons.SelectMany(p=>p))
            for(int i=0;i+1<ring.Count;i++)
            {
                var p=projection.ToLocal(ring[i]);var q=projection.ToLocal(ring[i+1]);
                if(p==q)continue;
                AddClipped(factory.CreateLineString(new[]{new Coordinate(p.X,p.Y),new Coordinate(q.X,q.Y)}).Intersection(box));
            }
            var actual=new List<(Point3d A,Point3d B)>();
            foreach(var obj in curvesByFeature[(role,feature.Id)])
            {
                if(obj.Geometry is not Curve curve||!curve.TryGetPolyline(out var line)){errors++;continue;}
                for(int i=0;i+1<line.Count;i++)if(line[i].DistanceTo(line[i+1])>1e-10*scale)actual.Add((line[i]/scale,line[i+1]/scale));
            }
            groups++;segments+=actual.Count;
            if(!ModelGeometryChecks.BoundaryCoverage(expected,actual)||!ModelGeometryChecks.BoundaryCoverage(actual,expected))errors++;
            void AddClipped(NetTopologySuite.Geometries.Geometry geometry)
            {
                if(geometry is LineString line)
                {var c=line.Coordinates;for(int j=0;j+1<c.Length;j++)if(c[j].Distance(c[j+1])>1e-10)expected.Add((new(c[j].X,c[j].Y,0),new(c[j+1].X,c[j+1].Y,0)));}
                else if(geometry is GeometryCollection collection)foreach(var item in collection.Geometries)AddClipped(item);
            }
        }
        return new(){["raw_xy_source_geometry_passed"]=groups>0&&errors==0&&sharedBuildingInfo,
            ["building_xy_and_model_info_match"]=sharedBuildingInfo,["raw_xy_feature_groups"]=groups,
            ["raw_xy_segments"]=segments,["raw_xy_mismatched_groups"]=errors,
            ["raw_xy_check_scope"]="Original parcel, road-category parcel and retained-building rings independently clipped to AOI; bidirectional segment coverage within 2 micrometres."};
    }
}
