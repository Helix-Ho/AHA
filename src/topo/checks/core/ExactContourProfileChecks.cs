using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ExactContourProfileChecks
{
    internal static int Run()
    {
        int checks=0;
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var bounds=projection.Bounds(100);
        var site=new GeoFeature { Id="site", Polygons=new(){new(){new(){projection.ToGeo(-10,-10),projection.ToGeo(10,-10),projection.ToGeo(10,10),projection.ToGeo(-10,10)}}}};
        var near=EditRegion.Create(site,projection,bounds,2);
        var template=TerrainField.FromSamples(bounds,projection,8,8,new double[81]);
        foreach (var height in new Func<double,double,double>[] { (x,y)=>20+.03*x+.07*y+.001*x*y, (x,y)=>100+.012*x*y,
            (x,y)=>100+3*Math.Sin(x/8)+2*Math.Cos(y/13) })
        {
            var field=TerrainField.FromSamples(bounds,projection,8,8,Enumerable.Range(0,81)
                .Select(i=>height(template.ModelX(i%9),template.ModelY(i/9))).ToArray());
            var roads=ContourPaths.Rectangle(-100,5,100,11);
            field=RoadGrading.Apply(field,new(roads,ContourPaths.Area(roads))).Field;
            var plan=ContourPlan.Build(field,roads,near,SiteSelection.FeaturePaths(site,projection),1,.2,30,true);
            foreach(var plate in plan.Plates)
            foreach(var component in plate.Exact!)
            foreach(var loop in component.Rings)
            {
                if(loop.Edges.Count==0||component.Area<=0)throw new InvalidOperationException("Exact profile empty");
                for(int i=0;i<loop.Edges.Count;i++)
                    if(loop.Edges[i].End!=loop.Edges[(i+1)%loop.Edges.Count].Start)
                        throw new InvalidOperationException("Exact profile lost shared event identity");
                checks++;
            }
        }
        return checks;
    }
}
