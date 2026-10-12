using Clipper2Lib;

namespace Aha.Topo.Checks;

internal sealed record ModelPlanFixture(double Radius,LocalProjection Projection,GeoBounds Bounds,PathsD Domain,
    PathsD Raw,GeoFeature Target,EditRegion Region);

internal static class ModelPlanFixtures
{
    internal static ModelPlanFixture Create(bool aoiContact)
    {
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        double radius=aoiContact?10:20;var bounds=projection.Bounds(radius);
        var domain=Rectangle(-radius,-radius,radius,radius);
        PathsD raw;
        if(aoiContact)raw=new(){new(){new(6,2),new(10,0),new(8,4)},new(){new(6,-2),new(8,-4),new(10,0)}};
        else
        {
            raw=Rectangle(-15,-6.25,15,-5.75);
            raw=Clipper.Difference(raw,Rectangle(-.01,-6.01,.01,-5.99),FillRule.NonZero,6);
            raw.AddRange(Rectangle(4.99,-5.75,5.01,5));
            raw.Add(new(){new(.063092,-.004071),new(.063092,-.004070),new(-.004354,.024567)});
            raw.AddRange(Rectangle(-8,2,-4,6));raw.AddRange(Rectangle(-4,6,0,10));
            raw=Clipper.BooleanOp(ClipType.Union,raw,null,FillRule.NonZero,6);
        }
        var target=new GeoFeature{Id="model-cleanup-target",Polygons=new(){new(){new(){projection.ToGeo(-3,-4),
            projection.ToGeo(3,-4),projection.ToGeo(3,4),projection.ToGeo(-3,4),projection.ToGeo(-3,-4)}}}};
        return new(radius,projection,bounds,domain,raw,target,EditRegion.Create(target,projection,bounds,2));
    }

    private static PathsD Rectangle(double x0,double y0,double x1,double y1)=>new(){new(){new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)}};
}
