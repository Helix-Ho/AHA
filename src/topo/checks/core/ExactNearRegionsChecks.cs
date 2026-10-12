using Clipper2Lib;

namespace Aha.Topo.Checks;
internal static class ExactNearRegionsChecks
{
    internal static int Run()
    {
        int count=0;
        var circles=new[]{new EditCircle("circle",0,0,10)};
        var clip=new ExactNearRegions(EditBoundaryHull.Create(circles));
        var square=ContourPaths.Rectangle(-20,-20,20,20);
        var all=clip.Split(square);
        var roles=ContourPaths.Difference(ContourPaths.Rectangle(-100,-100,100,100),
            ContourPaths.Rectangle(-2,-3,2,3));
        roles.AddRange(ContourPaths.Rectangle(1000,1000,1010,1010));
        var ownedRoles=new ExactNearRegions.SourceRegion(roles);
        foreach(var window in new[]{square,ContourPaths.Rectangle(-12,-4,0,4),square})
        foreach(bool road in new[]{false,true})
        {
            var fresh=clip.Split(window,rolePaths:roles,road:road);
            var reused=clip.Split(window,rolePaths:roles,road:road,roleSource:ownedRoles);
            Check(Signature(fresh)==Signature(reused),
                "reused role index preserves enclosing winding, holes, finite edges and operation isolation");
        }
        static string Signature(ExactNearRegions.Result result)=>string.Join(";",
            result.Near.Concat(result.Far).Select(c=>string.Join("|",c.Rings.Select(r=>
                string.Join(",",r.Edges.Select(e=>$"{e.Start.X:R}/{e.Start.Y:R}/{e.End.X:R}/{e.End.Y:R}/{e.Sweep:R}"))))));
        Check(all.Near.Count==1&&all.Far.Count==1&&all.Far[0].Holes.Count==1,"full circle owns the Far hole");
        Check(Math.Abs(all.Near.Sum(p=>p.Area)-100*Math.PI)<1e-10,"analytic circle area");
        Check(Math.Abs(all.Near.Sum(p=>p.Area)+all.Far.Sum(p=>p.Area)-1600)<1e-10,"complement coverage");
        var circumscribed=clip.Split(ContourPaths.Rectangle(-10,-10,10,10));
        Check(circumscribed.Near.Count==1&&Math.Abs(circumscribed.Near[0].Area-100*Math.PI)<1e-10,
            "grid-valued arc endpoints classify the arc midpoint, not its chord midpoint");
        Check(circumscribed.Far.Count==4&&Math.Abs(circumscribed.Far.Sum(c=>c.Area)-(400-100*Math.PI))<1e-10,
            "four tangencies preserve four independent outside corners");
        foreach(double x in new[]{0d,9.999999,10,10.000001,-10})
        {
            var finite=ContourPaths.Rectangle(x,-20,20,20);var divided=clip.Split(finite);
            Check(Math.Abs(divided.Near.Sum(p=>p.Area)+divided.Far.Sum(p=>p.Area)-ContourPaths.Area(finite))<1e-8,
                "tangent and almost tangent regions preserve coverage");
        }
        var triangularHull=new ExactNearRegions(EditBoundaryHull.Create(new[]{
            new EditCircle("a",0,0,10),new EditCircle("b",8,12,10),new EditCircle("c",16,0,10)}));
        var quadrant=triangularHull.Split(ContourPaths.Rectangle(-20,-20,0,0));
        Check(quadrant.Near.Count==1&&quadrant.Far.Count==1&&
            Math.Abs(quadrant.Near.Sum(c=>c.Area)+quadrant.Far.Sum(c=>c.Area)-400)<1e-10,
            "supporting-circle root beyond a finite arc stays on the common tangent only");
        var result=clip.Split(ContourPaths.Rectangle(-8,-8,8,8));
        Check(Math.Abs(result.Near.Sum(c=>c.Area)+result.Far.Sum(c=>c.Area)-256)<1e-8,
            "synthetic finite circle split preserves total area");
        foreach(var regions in new[]{all,result})foreach(var c in regions.Near.Concat(regions.Far))foreach(var ring in c.Rings)
            for(int i=0;i<ring.Edges.Count;i++)Check(ring.Edges[i].End==ring.Edges[(i+1)%ring.Edges.Count].Start,"shared event coordinate identity");
        var horizontal=PlanarRationalPoint.Mid(PlanarRationalPoint.Grid(1000000,2000000),
            PlanarRationalPoint.Grid(1000001,2000000));
        Check(horizontal.Point.Y==2&&horizontal.Point.X==1.0000005,
            "rational horizontal event retains an unsnapped midpoint");
        Check(!ProfileContact.TryFind(new PathsD{new(){new(0,0),new(.0000004,0),new(1,1),new(0,1)}},out _),
            "distinct finite intersections sharing a grid pixel are not profile contact");
        var adjacent=PlanarBoundaryNoding.Node(new[]{
            new PathsD{new(){new(-2,0),new(.0000004,0),new(.0000004,5),new(-2,5)}},
            new PathsD{new(){new(0,0),new(2,0),new(2,5),new(0,5),new(0,2.5)}}},
            new PathsD{new(){new(-2,2.5),new(2,2.5)}});
        var shared=adjacent.Select(paths=>paths[0].Where(p=>p.x==0).Select(p=>p.y).Order().ToArray()).ToArray();
        Check(shared[0].SequenceEqual(shared[1])&&shared[0].SequenceEqual(new[]{0d,2.5,5}),
            "adjacent sampled boundaries share grid and constraint events");
        return count;
        void Check(bool ok,string what){count++;if(!ok)throw new InvalidOperationException("Exact Near: "+what);}
    }
}
