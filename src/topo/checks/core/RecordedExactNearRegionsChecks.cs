using Clipper2Lib;
using System.Text.Json;

namespace Aha.Topo.Checks;

// The regression geometry is read from .local at execution time, never compiled into this assembly.
internal static class RecordedExactNearRegionsChecks
{
    internal static int Run()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("recorded-exact-near.json")));
        var data=document.RootElement;
        int count=0;
        void Check(bool ok,string name){count++;if(!ok)throw new InvalidOperationException("Exact Near recorded: "+name);}
        static PointD Point(JsonElement value)=>new(value[0].GetDouble(),value[1].GetDouble());
        static PathD Path(JsonElement value)=>new(value.EnumerateArray().Select(Point));
        static PathsD Paths(JsonElement value)=>new(){Path(value)};
        static EditCircle Circle(string id,JsonElement value)=>new(id,value[0].GetDouble(),value[1].GetDouble(),value[2].GetDouble());
        static PlanarRationalPoint Grid(JsonElement value)=>PlanarRationalPoint.Grid(value[0].GetInt64(),value[1].GetInt64());

        using(var fixtures=JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("near-finite-regions.json"))))
        foreach(var fixture in fixtures.RootElement.EnumerateArray())
        {
            var source=new PathsD(fixture.GetProperty("source").EnumerateArray().Select(Path));
            var segments=fixture.GetProperty("boundary").Deserialize<EditBoundarySegment[]>()!;
            var loops=new List<EditBoundaryLoop>();var current=new List<EditBoundarySegment>();
            foreach(var segment in segments)
            {
                current.Add(segment);
                if(segment.End.DistanceTo(current[0].Start)<1e-10)
                {loops.Add(new(current.Where(s=>s.Circle is not null).Select(s=>s.Circle!).Distinct().ToArray(),current.ToArray()));current.Clear();}
            }
            if(current.Count!=0)throw new InvalidOperationException("Incomplete recorded boundary");
            var divided=new ExactNearRegions(loops).Split(source);
            Check(Math.Abs(divided.Near.Sum(c=>c.Area)+divided.Far.Sum(c=>c.Area)-ContourPaths.Area(source))<1e-7,
                fixture.GetProperty("name").GetString()!+" finite region coverage");
        }

        var triangularHull=new ExactNearRegions(EditBoundaryHull.Create(data.GetProperty("triangular_hull").EnumerateArray()
            .Select((circle,index)=>Circle(index.ToString(),circle)).ToArray()));
        var quadrant=triangularHull.Split(ContourPaths.Rectangle(-20,-20,0,0));
        Check(quadrant.Near.Count==1&&quadrant.Far.Count==1&&
            Math.Abs(quadrant.Near.Sum(c=>c.Area)+quadrant.Far.Sum(c=>c.Area)-400)<1e-10,
            "supporting-circle root stays on the finite common tangent");

        var circles=data.GetProperty("oblique_circles");
        var oblique=new ExactNearRegions(EditBoundaryHull.Create(circles.EnumerateArray()
            .Select((circle,index)=>Circle(index.ToString(),circle)).ToArray()));
        var wide=ContourPaths.Rectangle(-400,-400,400,400);
        var lower=oblique.Split(wide).Near;
        Check(oblique.Split(wide,lower).Near.Count==0,"reused arc and tangent junctions do not self-loop");

        var finiteData=data.GetProperty("finite_arc");
        var finite=new ExactNearRegions(EditBoundaryHull.Create(new[]{Circle("finite",finiteData.GetProperty("circle"))}));
        var difference=finite.Split(Paths(finiteData.GetProperty("source")),
            finite.Split(Paths(finiteData.GetProperty("mask"))).Near,includeFar:false).Near;
        Check(difference.Count>0&&difference.All(c=>c.Area>0),"close finite arc crossings stay distinct");
        Check(difference.SelectMany(c=>c.Rings).All(r=>r.Edges.Select((e,i)=>e.End==r.Edges[(i+1)%r.Edges.Count].Start).All(x=>x)),
            "sub-angle-resolution arc preserves its endpoint cycle");

        var containing=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("large",0,0,1000)}));
        var fragments=containing.Split(Paths(data.GetProperty("corner")),
            containing.Split(Paths(data.GetProperty("lower_mask"))).Near
                .Concat(containing.Split(Paths(data.GetProperty("upper_mask"))).Near).ToArray()).Near;
        Check(fragments.All(c=>c.Area>0),"sub-grid events preserve corner connectivity");
        var result=containing.Split(Paths(data.GetProperty("strip")));
        Check(result.Near.Count==1&&result.Near[0].Area>0,"close opposite sides retain positive area");

        var line=data.GetProperty("grid_line");
        foreach(var move in new[]{(X:0d,Y:0d,QuarterTurn:false),(X:100d,Y:-200d,QuarterTurn:true)})
        {
            PointD Move(PointD p)=>move.QuarterTurn?new(-p.y+move.X,p.x+move.Y):new(p.x+move.X,p.y+move.Y);
            var a=Move(Point(line.GetProperty("a")));var b=Move(Point(line.GetProperty("b")));
            var mid=Move(Point(line.GetProperty("mid")));
            var sourceTail=line.GetProperty("source_tail").EnumerateArray().Select(x=>Move(Point(x))).ToArray();
            var maskTail=line.GetProperty("mask_tail").EnumerateArray().Select(x=>Move(Point(x))).ToArray();
            var source=ContourPaths.Union(new PathsD{new(){a,b,sourceTail[0],sourceTail[1]}});
            var mask=ContourPaths.Union(new PathsD{new(){mid,a,maskTail[0],maskTail[1]}});
            var subtraction=containing.Split(source,containing.Split(mask).Near).Near;
            Check(Math.Abs(subtraction.Sum(c=>c.Area)-ContourPaths.Area(ContourPaths.Difference(source,mask)))<1e-7,
                "shared grid half-edge survives rotation and translation");
        }

        var rational=data.GetProperty("rational_ring");
        foreach(bool transformed in new[]{false,true})
        {
            PlanarRationalPoint P(JsonElement value)
            {
                long x=value[0].GetInt64(),y=value[1].GetInt64();
                return transformed?PlanarRationalPoint.Grid(-y+100_000_000,x-200_000_000):PlanarRationalPoint.Grid(x,y);
            }
            var a=P(rational.GetProperty("a"));var b=P(rational.GetProperty("b"));var c=P(rational.GetProperty("c"));
            var d=P(rational.GetProperty("d"));var e=P(rational.GetProperty("e"));
            var p=PlanarRationalPoint.Crossing(a,b,c,d)!;var q=PlanarRationalPoint.Crossing(a,b,c,e)!;
            ExactNearRegions.Edge Edge(PlanarRationalPoint x,PlanarRationalPoint y,PlanarRationalPoint s,PlanarRationalPoint t,int id)=>
                new(new(s.Point,t.Point),id,0,1,x.Point,y.Point){ExactStart=x,ExactEnd=y};
            var ring=new ExactNearRegions.Ring(new[]{Edge(c,p,c,d,0),Edge(p,q,a,b,1),Edge(q,c,e,c,2)});
            Check(Math.Abs(Math.Abs(ring.SignedArea)/rational.GetProperty("expected_area").GetDouble()-1)<1e-12,
                "rational ring area survives coordinate rounding and transform");
        }
        var horizontal=data.GetProperty("horizontal");
        var midpoint=PlanarRationalPoint.Mid(Grid(horizontal.GetProperty("first")),Grid(horizontal.GetProperty("second")));
        Check(midpoint.Point.X==horizontal.GetProperty("expected")[0].GetDouble()&&
            midpoint.Point.Y==horizontal.GetProperty("expected")[1].GetDouble(),"rational midpoint stays off the integer grid");
        return count;
    }
}
