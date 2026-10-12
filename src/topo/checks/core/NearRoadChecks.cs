using Clipper2Lib;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class NearRoadChecks
{
    internal static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(127,37.5));
        var bounds = projection.Bounds(20);
        double half = 10 / (2 * Math.Sqrt(2));
        var site = new GeoFeature { Id = "site", Polygons = new() { new() { new()
        {
            projection.ToGeo(-half,-half), projection.ToGeo(half,-half),
            projection.ToGeo(half,half), projection.ToGeo(-half,half)
        } } } };
        var near = EditRegion.Create(site,projection,bounds,2);
        var domain = Rectangle(-20,-20,20,20);
        var field = TerrainField.FromSamples(bounds,projection,8,8,Enumerable.Repeat(100d,81).ToArray());
        var circle = near.Circles.Single();
        double top = circle.CenterY + circle.RadiusMetres;
        var json = new JsonSerializerOptions { IncludeFields = true };
        string nearBefore = JsonSerializer.Serialize(near.Boundaries,json);
        var cases = new[]
        {
            (Name:"small Near road tip", Roads:Rectangle(-15,top-.01,15,top+2)),
            (Name:"narrow Near road tip", Roads:Rectangle(-15,top-.03,15,top+2)),
            (Name:"protected Near road tip", Roads:Rectangle(-15,top-.08,15,top+2)),
            (Name:"small Near land tip", Roads:Difference(domain,Rectangle(-15,top-.01,15,top+2))),
            (Name:"small Far road tip", Roads:Rectangle(-.2,top-.2,.2,top+.01)),
            (Name:"small Far land tip", Roads:Difference(domain,Rectangle(-.2,top-.2,.2,top+.01)))
        };
        foreach (var fixture in cases)
        {
            string sourceBefore = JsonSerializer.Serialize(fixture.Roads,json);
            var original = ModelPlanGeometry.Build(fixture.Roads,domain).Roads;
            var result = NearRoadGeometry.Resolve(original,domain,near);
            Check(result.ResolvedComponents > 0 && result.ChangedAreaSquareMetres > 0,
                fixture.Name+" resolves a component created by the Near cut");
            Check(sourceBefore == JsonSerializer.Serialize(fixture.Roads,json) &&
                nearBefore == JsonSerializer.Serialize(near.Boundaries,json),fixture.Name+" preserves source and exact circle");
            var changed = Xor(original,result.Roads);
            bool changedNear = Area(Intersect(changed,result.NearBoundary)) > 1e-8;
            bool changedFar = Area(Difference(changed,result.NearBoundary)) > 1e-8;
            Check(fixture.Name.Contains("Near") ? changedNear && !changedFar : changedFar && !changedNear,
                fixture.Name+" reassigns only the fragment's own Near/Far zone");
            var partition = PlanarPartition.Build(field,result.Roads,result.NearBoundary);
            partition.Patches(2,true,false,out _);
            Check(true,fixture.Name+" uses the shared role and Near partition without resampling slivers");
            var rerun = NearRoadGeometry.Resolve(original,domain,near);
            Check(rerun.ResolvedComponents == result.ResolvedComponents && Area(Xor(result.Roads,rerun.Roads))<1e-8,
                fixture.Name+" reuses the source contract deterministically without accumulating boundary movements");
        }
        var crossing = Rectangle(-15,-.5,15,.5);
        var unchanged = NearRoadGeometry.Resolve(crossing,domain,near);
        Check(unchanged.ResolvedComponents==0 && Area(Xor(crossing,unchanged.Roads))==0,
            "ordinary road crossings remain unchanged");
        foreach (double angle in new[] { .27,1.31,2.49,4.76 })
        {
            PointD Rotate(double x,double y)=>new(circle.CenterX+x*Math.Cos(angle)-y*Math.Sin(angle),
                circle.CenterY+x*Math.Sin(angle)+y*Math.Cos(angle));
            var touching = new PathsD { new() { Rotate(0,-1),Rotate(circle.RadiusMetres,0),Rotate(0,1) } };
            var touchingModel = ModelPlanGeometry.Build(touching,domain).Roads;
            var tangentResult = NearRoadGeometry.Resolve(touchingModel,domain,near);
            Check(tangentResult.ResolvedComponents==0 && Area(Xor(touchingModel,tangentResult.Roads))==0,
                "an exact circle contact is not a false Far sliver between preview chords");
        }
        // Acquisition extends beyond output; the correction must not modify
        // that extra ground which supports the full-building statistics.
        var widerSource = Rectangle(-25,top-.01,25,top+2);
        var widerDomain = Rectangle(-30,-30,30,30);
        var widerModel = ModelPlanGeometry.Build(widerSource,widerDomain).Roads;
        var clipped = NearRoadGeometry.Resolve(widerModel,domain,near);
        Check(Area(Xor(Difference(widerModel,domain),Difference(clipped.Roads,domain)))==0,
            "Near cleanup preserves acquired-only roads outside the output range");
        // An oblique road corner is cut into a small component by the curved
        // boundary. Preserve its relative geometry under a rigid transform.
        var tipCase = FixtureData.Witness("near_road_tip");
        foreach (var transform in new[] { (Angle:0d,X:0d,Y:0d),(Angle:.71,X:34d,Y:-19d) })
        {
            PointD Move(double x,double y)=>new(transform.X+x*Math.Cos(transform.Angle)-y*Math.Sin(transform.Angle),
                transform.Y+x*Math.Sin(transform.Angle)+y*Math.Cos(transform.Angle));
            var centreSource = tipCase.GetProperty("center");
            var centre = Move(centreSource[0].GetDouble(), centreSource[1].GetDouble());
            double radius = tipCase.GetProperty("radius").GetDouble();
            double size = radius/(2*Math.Sqrt(2));
            var tipSite = new GeoFeature { Id="slanted-tip",Polygons=new(){new(){new()
            {
                projection.ToGeo(centre.x-size,centre.y-size),projection.ToGeo(centre.x+size,centre.y-size),
                projection.ToGeo(centre.x+size,centre.y+size),projection.ToGeo(centre.x-size,centre.y+size)
            }}}};
            var tipBounds = projection.Bounds(200);
            var tipNear = EditRegion.Create(tipSite,projection,tipBounds,2);
            var tipDomain = Rectangle(-200,-200,200,200);
            var tipSource = new PathsD { new(tipCase.GetProperty("source").EnumerateArray()
                .Select(point => Move(point[0].GetDouble(),point[1].GetDouble()))) };
            string tipBefore=JsonSerializer.Serialize(tipSource,json);
            var tipModel=ModelPlanGeometry.Build(tipSource,tipDomain).Roads;
            var tipField=TerrainField.FromSamples(tipBounds,projection,8,8,Enumerable.Repeat(100d,81).ToArray());
            var unsimplified = PlanarPartition.Build(tipField,tipModel,tipNear.Paths);
            Check(unsimplified.ReassignedRoleAreaSquareMetres > 0,
                "slanted road tip reproduces a subminimum role component on the common graph");
            var fixedTip=NearRoadGeometry.Resolve(tipModel,tipDomain,tipNear);
            Check(fixedTip.ResolvedComponents>0 && fixedTip.ChangedAreaSquareMetres>0,
                "slanted road tip is resolved after rotation and translation");
            PlanarPartition.Build(tipField,fixedTip.Roads,fixedTip.NearBoundary).Patches(2,true,false,out _);
            Check(tipBefore==JsonSerializer.Serialize(tipSource,json),"slanted road source remains unchanged");
            var changedRegion=ContourPaths.Union(new PathsD(ContourPaths.Difference(tipModel,fixedTip.Roads)
                .Concat(ContourPaths.Difference(fixedTip.Roads,tipModel))));
            Check(ContourPaths.Area(ContourPaths.Difference(changedRegion,fixedTip.NearBoundary))<1e-8,
                "slanted Near road correction preserves all Far road area on the common fixed grid");
            var farChange=ContourPaths.Rectangle(transform.X+radius+5,transform.Y,transform.X+radius+6,transform.Y+1);
            Check(ContourPaths.Area(ContourPaths.Difference(farChange,fixedTip.NearBoundary))>.99,
                "a genuine Far role change is not hidden by the fixed-grid comparison");
        }
        return count;

        void Check(bool valid,string name)
        {
            if(!valid) throw new InvalidOperationException("Near road cleanup: "+name);
            count++;
        }
    }

    private static PathsD Rectangle(double x0,double y0,double x1,double y1)=>new(){new(){new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)}};
    private static PathsD Offset(PathsD paths,double d)=>Clipper.InflatePaths(paths,d,JoinType.Round,EndType.Polygon,2,6,.0001);
    private static PathsD Difference(PathsD a,PathsD b)=>Clipper.Difference(a,b,FillRule.NonZero,6);
    private static PathsD Intersect(PathsD a,PathsD b)=>Clipper.Intersect(a,b,FillRule.NonZero,6);
    private static PathsD Xor(PathsD a,PathsD b)=>Clipper.Xor(a,b,FillRule.NonZero,6);
    private static double Area(PathsD paths)=>Math.Abs(Clipper.Area(paths));
}
