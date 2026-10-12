using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class HullChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("near boundary hull: "+name);count++;}
        EditCircle C(string id,double x,double y,double radius)=>new(id,x,y,radius);
        var cases=new[]
        {
            ("single",new[]{C("a",0,0,10)},1,Math.PI*100),
            ("equal",new[]{C("a",-8,0,10),C("b",8,0,10)},1,Math.PI*100+320),
            ("unequal",new[]{C("a",0,0,10),C("b",12,0,4)},1,double.NaN),
            ("triangle",new[]{C("a",0,0,10),C("b",16,0,10),C("c",8,Math.Sqrt(192),10)},1,8*Math.Sqrt(192)+480+Math.PI*100),
            ("four",new[]{C("a",-8,-8,10),C("b",8,-8,10),C("c",8,8,10),C("d",-8,8,10)},1,256+640+Math.PI*100),
            ("contained",new[]{C("a",0,0,10),C("b",3,0,2)},1,Math.PI*100),
            ("duplicate",new[]{C("a",0,0,10),C("b",0,0,10)},1,Math.PI*100),
            ("tangent",new[]{C("a",0,0,10),C("b",20,0,10)},1,Math.PI*100+400),
            ("disjoint",new[]{C("a",0,0,10),C("b",21,0,10)},2,Math.PI*200),
            ("transitive",new[]{C("a",0,0,10),C("b",16,0,10),C("c",32,0,10)},1,Math.PI*100+640),
            // A's capsule touches B although their source disks do not. Its new
            // hull then reaches C, which met neither original hull. Repeat merging.
            ("repeat",new[]{C("a",-8,0,10),C("b",8,0,10),C("c",0,11,1),C("d",-4,10.9,.1)},1,double.NaN)
        };
        foreach(var (name,circles,groups,expectedArea) in cases)
        {
            var loops=EditBoundaryHull.Create(circles);
            Check(loops.Count==groups,name+" overlap groups reach the stable partition");
            Check(circles.All(c=>Enumerable.Range(0,64).All(i=>loops.Any(loop=>loop.Contains(new(c.CenterX+c.RadiusMetres*Math.Cos(i*Math.PI/32),c.CenterY+c.RadiusMetres*Math.Sin(i*Math.PI/32)))))),name+" includes every source disk");
            double area=loops.Sum(Area);
            if(double.IsFinite(expectedArea))Check(Math.Abs(area-expectedArea)<1e-7,name+" exact capsule/rounded-polygon area");
            foreach(var loop in loops)
            {
                Check(loop.Segments.Count>0&&loop.Segments.Select((s,i)=>Distance(s.End,loop.Segments[(i+1)%loop.Segments.Count].Start)).Max()<1e-8,name+" closes without gaps");
                Check(loop.Segments.Select((s,i)=>Dot(Tangent(s,true),Tangent(loop.Segments[(i+1)%loop.Segments.Count],false))).Min()>1-1e-9,name+" outer tangents meet arcs continuously");
                Check(Enumerable.Range(0,360).All(i=>
                {
                    double angle=i*Math.PI/180,nx=Math.Cos(angle),ny=Math.Sin(angle);
                    double expected=loop.Circles.Max(c=>nx*c.CenterX+ny*c.CenterY+c.RadiusMetres);
                    return Math.Abs(loop.Segments.Max(s=>Support(s,nx,ny))-expected)<1e-7;
                }),name+" exact outline support agrees with the convex hull of its disks");
            }
            var reversed=EditBoundaryHull.Create(circles.Reverse().ToArray());
            Check(reversed.Count==loops.Count&&Math.Abs(reversed.Sum(Area)-area)<1e-7,name+" is independent of selection order");
        }
        var capsule=EditBoundaryHull.Create(new[]{C("a",-8,0,10),C("b",8,0,10)}).Single();
        Check(capsule.Contains(new(0,9))&&!capsule.Contains(new(0,10.01)),"bridge area is filled only up to the tangent");
        var separate=EditBoundaryHull.Create(new[]{C("a",0,0,10),C("b",21,0,10)});
        Check(separate.All(l=>!l.Contains(new(10.5,0))),"independent groups retain their separating gap");
        var first=EditBoundaryHull.Create(new[]{C("a",-8,0,10),C("b",8,0,10)}).Single();
        var second=EditBoundaryHull.Create(new[]{C("c",0,11,1)}).Single();
        var third=EditBoundaryHull.Create(new[]{C("d",-4,10.9,.1)}).Single();
        Check(first.Intersects(second)&&!first.Intersects(third)&&!second.Intersects(third),"repeat-merge fixture first requires a hull contact, then a new contact");
        var two=EditBoundaryHull.Create(new[]{C("a",-8,0,10),C("b",8,0,10),C("c",0,11,1)}).Single();
        Check(two.Intersects(third),"the enlarged hull reaches a previously independent group");

        var wrapCircle=C("wrap",0,0,10);double start=2*Math.PI-.2,sweep=.4,angularTolerance=1e-6;
        EditBoundaryPoint CirclePoint(double angle)=>new(10*Math.Cos(angle),10*Math.Sin(angle));
        var wrapArc=new EditBoundarySegment(CirclePoint(start),CirclePoint(start+sweep),wrapCircle,start,sweep);
        foreach(var (angle,expected,label) in new[]{
            (start,true,"exact start"),(start+sweep,true,"exact end"),(0d,true,"crosses zero"),
            (start-.5*angularTolerance,true,"before start inside tolerance"),(start-2*angularTolerance,false,"before start outside tolerance"),
            (start+sweep+.5*angularTolerance,true,"after end inside tolerance"),(start+sweep+2*angularTolerance,false,"after end outside tolerance"),
            (4*Math.PI,true,"equivalent angle after two full turns")})
            Check(EditBoundaryMath.AngleOnArc(angle,wrapArc,angularTolerance)==expected,"arc wrap: "+label);
        Check(EditBoundaryMath.AngleOnArc(start-5e-13,wrapArc),"arc start accepts floating round-off with the default tolerance");
        var nearStart=CirclePoint(start-.5*angularTolerance);
        var ray=new EditBoundarySegment(new(0,0),new(nearStart.X*2,nearStart.Y*2));
        Check(EditBoundaryMath.Intersections(wrapArc,ray,10*angularTolerance).Count==1,"line/arc endpoint contact obeys the same angular tolerance across zero");

        var projection=new LocalProjection(new GeoPoint(127,37.5));
        GeoFeature Rect(string id,double x0,double y0,double x1,double y1)=>new(){Id=id,Pnu=id,Polygons=new(){new(){new(){projection.ToGeo(x0,y0),projection.ToGeo(x1,y0),projection.ToGeo(x1,y1),projection.ToGeo(x0,y1),projection.ToGeo(x0,y0)}}}};
        double half=10/(2*Math.Sqrt(2));
        var sites=SiteSelection.Create(new[]{Rect("left",-8-half,-half,-8+half,half),Rect("right",8-half,-half,8+half,half)},projection);
        var region=EditRegion.Create(sites,projection,projection.Bounds(30),2);
        Check(region.Circles.Count==2&&region.Boundaries.Count==1,"dissolved site circles flow through the shared hull definition");
        Check(region.ContainsLocal(0,9)&&region.ContainsStrict(Rect("bridge-building",-.5,8.5,.5,9.5),projection),"a building in the filled bridge becomes near even outside both disks");
        Check(!region.Intersects(Rect("outside-bridge",-.5,10.01,.5,10.5),projection),"building above the tangent remains far");
        Check(!region.ContainsStrict(Rect("crosses-bridge",-.5,9.5,.5,10.5),projection),"whole building crossing the hull tangent is Far");
        Check(!region.ContainsStrict(Rect("touches-bridge",-.5,9,.5,10),projection),"building touching the straight tangent is Far");
        Check(region.Paths.Count==1&&region.Paths.SelectMany(p=>p).All(p=>DistanceToBoundary(new(p.x,p.y),region.Boundaries)<.000002),"preview vertices follow the exact same arcs and tangents");
        Check(region.Paths.All(path=>Enumerable.Range(0,path.Count).All(i=>DistanceToBoundary(new((path[i].x+path[(i+1)%path.Count].x)/2,(path[i].y+path[(i+1)%path.Count].y)/2),region.Boundaries)<=EditRegion.MaximumChordErrorMetres+.000002)),"preview chords stay within the declared one-millimetre error");
        bool rejected=false;try{EditRegion.Create(sites,projection,projection.Bounds(17.99),2);}catch(ArgumentException){rejected=true;}
        Check(rejected,"exact hull extents must fit inside the model AOI");
        return count;
    }

    internal static double Area(EditBoundaryLoop loop)=>loop.Segments.Sum(s=>s.Circle is {} c
        ? (c.RadiusMetres*(c.CenterX*(Math.Sin(s.StartAngle+s.SweepRadians)-Math.Sin(s.StartAngle))-c.CenterY*(Math.Cos(s.StartAngle+s.SweepRadians)-Math.Cos(s.StartAngle)))+c.RadiusMetres*c.RadiusMetres*s.SweepRadians)/2
        : (s.Start.X*s.End.Y-s.End.X*s.Start.Y)/2);
    private static (double X,double Y) Tangent(EditBoundarySegment s,bool end)
    {
        if(s.Circle is not null){double angle=s.StartAngle+(end?s.SweepRadians:0);return(-Math.Sin(angle),Math.Cos(angle));}
        double length=Distance(s.Start,s.End);return((s.End.X-s.Start.X)/length,(s.End.Y-s.Start.Y)/length);
    }
    private static double Dot((double X,double Y) a,(double X,double Y) b)=>a.X*b.X+a.Y*b.Y;
    private static double Distance(EditBoundaryPoint a,EditBoundaryPoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static bool OnArc(double angle,EditBoundarySegment s)
    {double delta=(angle-s.StartAngle)%(2*Math.PI);if(delta<0)delta+=2*Math.PI;return delta<=s.SweepRadians+1e-12;}
    private static double Support(EditBoundarySegment s,double nx,double ny)
    {
        double best=Math.Max(nx*s.Start.X+ny*s.Start.Y,nx*s.End.X+ny*s.End.Y);
        if(s.Circle is {} c&&OnArc(Math.Atan2(ny,nx),s))best=Math.Max(best,nx*c.CenterX+ny*c.CenterY+c.RadiusMetres);
        return best;
    }
    internal static double DistanceToBoundary(EditBoundaryPoint p,IReadOnlyList<EditBoundaryLoop> loops)
        =>loops.SelectMany(l=>l.Segments).Min(s=>DistanceToSegment(p,s));
    internal static double DistanceToSegment(EditBoundaryPoint p,EditBoundarySegment s)
        {
            if(s.Circle is {} c)
            {
                double angle=Math.Atan2(p.Y-c.CenterY,p.X-c.CenterX);
                return OnArc(angle,s)?Math.Abs(Distance(p,new(c.CenterX,c.CenterY))-c.RadiusMetres):Math.Min(Distance(p,s.Start),Distance(p,s.End));
            }
            double dx=s.End.X-s.Start.X,dy=s.End.Y-s.Start.Y,length=dx*dx+dy*dy;
            double t=Math.Clamp(((p.X-s.Start.X)*dx+(p.Y-s.Start.Y)*dy)/length,0,1);
            return Distance(p,new(s.Start.X+t*dx,s.Start.Y+t*dy));
        }
}
