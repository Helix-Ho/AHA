using Clipper2Lib;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>Finite straight regions clipped by the one analytic Near perimeter.
/// Connectivity is carried by intersection events, never recovered from sampled chords.</summary>
internal sealed class ExactNearRegions(IReadOnlyList<EditBoundaryLoop> boundaries, CancellationToken cancellation = default)
{
    internal ExactNearRegions(EditRegion region, CancellationToken cancellation = default) : this(region.Boundaries,cancellation) { }

    internal sealed record Edge(EditBoundarySegment Source, int SourceId, double From, double To,
        EditBoundaryPoint Start, EditBoundaryPoint End)
    {
        internal PlanarRationalPoint? ExactStart { get; init; }
        internal PlanarRationalPoint? ExactEnd { get; init; }
        internal double Sweep
        {
            get
            {
                double angle=Source.SweepRadians*(To-From);
                if(Source.Circle is {} circle&&Math.Abs(angle)<.01&&Start!=End)
                {
                    var radius=Start-new EditBoundaryPoint(circle.CenterX,circle.CenterY);var delta=End-Start;
                    return Math.Atan2(EditBoundaryMath.Cross(radius,delta),radius.LengthSquared+EditBoundaryMath.Dot(radius,delta));
                }
                return angle;
            }
        }
        private int Direction => To!=From?Math.Sign(To-From):
            ExactStart is {} a&&ExactEnd is {} b?
                (Source.Start.X!=Source.End.X?b.CompareX(a)*Math.Sign(Source.End.X-Source.Start.X):
                    b.CompareY(a)*Math.Sign(Source.End.Y-Source.Start.Y)):1;
        internal Edge Reverse() => new(Source, SourceId, To, From, End, Start){ExactStart=ExactEnd,ExactEnd=ExactStart};
        internal EditBoundaryPoint At(double t)
        {
            if(t==0)return Start;if(t==1)return End;
            if(Source.Circle is {} circle)
            {
                var radius=Start-new EditBoundaryPoint(circle.CenterX,circle.CenterY);double a=Sweep*t;
                return Start+radius*(-2*Math.Pow(Math.Sin(a/2),2))+new EditBoundaryPoint(-radius.Y,radius.X)*Math.Sin(a);
            }
            return Start+(End-Start)*t;
        }
        internal EditBoundaryPoint Tangent(bool atEnd)
        {
            if (Source.Circle is not { } circle) return (Source.End-Source.Start)*Direction;
            var p = atEnd ? End : Start;
            return new EditBoundaryPoint(-(p.Y - circle.CenterY), p.X - circle.CenterX) * Math.Sign(Sweep);
        }
    }
    internal sealed record Ring(IReadOnlyList<Edge> Edges)
    {
        private STRtree<Edge>? index;
        internal STRtree<Edge> Index
        {
            get
            {
                if(index is not null)return index;
                index=new();foreach(var edge in Edges)index.Insert(EdgeBounds(edge),edge);
                index.Build();return index;
            }
        }
        internal Envelope Bounds { get; } = RingBounds(Edges);
        internal double SignedArea { get; } = Measure(Edges);
        private static double Measure(IReadOnlyList<Edge> edges)
        {
            var exactAnchor=edges[0].ExactStart;var anchor=edges[0].Start;double sum=0;
            foreach(var edge in edges)
            {
                sum+=exactAnchor is not null&&edge.ExactStart is {} a&&edge.ExactEnd is {} b
                    ?exactAnchor.Cross(a,b):EditBoundaryMath.Cross(edge.Start-anchor,edge.End-anchor);
                if(edge.Source.Circle is {} circle)
                {
                    double angle=edge.Sweep;
                    double segment=Math.Abs(angle)<.01?angle*angle*angle/6*(1-angle*angle/20*(1-angle*angle/42)):angle-Math.Sin(angle);
                    sum+=circle.RadiusMetres*circle.RadiusMetres*segment;
                }
            }
            return sum*.5;
        }
        internal PathD Sample(double error = EditRegion.MaximumChordErrorMetres - ModelPlanPolicy.PrecisionMetres)
        {
            var path = new PathD();
            foreach (var edge in Edges)
            {
                int count = edge.Source.Circle is { } circle
                    ? Math.Max(1,(int)Math.Ceiling(Math.Abs(edge.Sweep)/(2*Math.Acos(1-Math.Min(error,circle.RadiusMetres/2)/circle.RadiusMetres)))) : 1;
                for(int i=0;i<count;i++) { var p=edge.At((double)i/count);path.Add(new(p.X,p.Y)); }
            }
            return path;
        }
    }
    internal sealed record Component(Ring Outer, IReadOnlyList<Ring> Holes)
    {
        internal IReadOnlyList<Ring> Rings { get; } = new[] { Outer }.Concat(Holes).ToArray();
        private STRtree<Edge>? edges;
        internal STRtree<Edge> EdgeIndex
        {
            get
            {
                if(edges is not null)return edges;
                edges=new();foreach(var edge in Rings.SelectMany(r=>r.Edges))edges.Insert(EdgeBounds(edge),edge);
                edges.Build();return edges;
            }
        }
        internal Envelope Bounds { get; } = ComponentBounds(Outer,Holes);
        internal double Area { get; } = Outer.SignedArea + Holes.Sum(r=>r.SignedArea);
        private PathsD? sample;
        // Consumers treat the component and its default proxy as read-only.
        // Keep the proxy with the exact region throughout this preparation.
        internal PathsD Sample(double error = EditRegion.MaximumChordErrorMetres - ModelPlanPolicy.PrecisionMetres)
            => error == EditRegion.MaximumChordErrorMetres - ModelPlanPolicy.PrecisionMetres
                ? sample ??= new(Rings.Select(r=>r.Sample(error)))
                : new(Rings.Select(r=>r.Sample(error)));
    }
    internal sealed record RoleChange(Component Region, bool Road);
    internal sealed record Result(IReadOnlyList<Component> Near,IReadOnlyList<Component> Far);

    private sealed class Node(EditBoundaryPoint point)
    {
        internal PlanarRationalPoint? Exact;
        internal Node? Parent;
        internal EditBoundaryPoint Point=point;
        internal Node Root => Parent is null ? this : Parent=Parent.Root;
    }
    private sealed class Primitive(EditBoundarySegment source,int id,bool near,Node start,Node end)
    {
        internal EditBoundarySegment Source=source;
        internal int Id=id;
        internal bool Near=near;
        internal List<(double T,Node Node)> Cuts=new(){(0,start),(1,end)};
    }
    private sealed record Use(Edge Edge,Node Start,Node End);

    internal Result Split(PathsD normalized, IReadOnlyList<Component>? omitted = null, Component? included = null,
        PathsD? rolePaths = null, bool road = true, IReadOnlyList<RoleChange>? roleChanges = null, bool includeFar = true,
        SourceRegion? roleSource = null)
        => SplitCore(normalized,omitted,included,rolePaths,road,roleChanges,includeFar,false,roleSource).Regions;

    // Use the same finite edges and sided material test as output, without
    // sampling arcs, re-noding the resulting rings, or constructing a new ring.
    internal bool Intersects(PathsD normalized,Component included)
        => SplitCore(normalized,null,included,null,true,null,false,true).HasInterior;

    private (Result Regions,bool HasInterior) SplitCore(PathsD normalized,IReadOnlyList<Component>? omitted,
        Component? included,PathsD? rolePaths,bool road,IReadOnlyList<RoleChange>? roleChanges,bool includeFar,bool queryOnly,
        SourceRegion? roleSource = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if(normalized.Count==0)return (new(Array.Empty<Component>(),Array.Empty<Component>()),false);
        var sourceBounds=Clipper.GetBounds(normalized);
        var domainBounds=new Envelope(sourceBounds.left,sourceBounds.right,sourceBounds.top,sourceBounds.bottom);
        var vertices=new Dictionary<EditBoundaryPoint,Node>();
        var rationalNodes=new Dictionary<PlanarRationalPoint,Node>();
        Node Vertex(EditBoundaryPoint p)
        {
            if(vertices.TryGetValue(p,out var n))return n;
            var exact=GridPoint(p);
            if(exact is not null&&rationalNodes.TryGetValue(exact,out n))
            {vertices.Add(p,n);return n;}
            n=new(exact?.Point??p){Exact=exact};vertices.Add(p,n);
            if(exact is not null)rationalNodes.Add(exact,n);
            return n;
        }
        var lines=new List<Primitive>(); var perimeter=new List<Primitive>(); int id=0;
        foreach(var path in normalized)
        for(int i=0;i<path.Count;i++)
        {
            var a=new EditBoundaryPoint(path[i].x,path[i].y);var b=new EditBoundaryPoint(path[(i+1)%path.Count].x,path[(i+1)%path.Count].y);
            if((id&255)==0)cancellation.ThrowIfCancellationRequested();
            if(a!=b&&domainBounds.Intersects(new Envelope(a.X,b.X,a.Y,b.Y)))
                lines.Add(new(new(a,b),id++,false,Vertex(a),Vertex(b)));
        }
        var roleLocator=rolePaths is {Count: >0}?roleSource??new SourceRegion(rolePaths):null;
        if(roleLocator is not null)
            foreach(var edge in roleLocator.Edges(domainBounds))
            {
                if((id&255)==0)cancellation.ThrowIfCancellationRequested();
                lines.Add(new(edge,id++,false,Vertex(edge.Start),Vertex(edge.End)));
            }
        foreach(var loop in boundaries)
        {
            var nodes=loop.Segments.Select(s=>Vertex(s.Start)).ToArray();
            for(int i=0;i<loop.Segments.Count;i++)
            {
                var primitive=new Primitive(loop.Segments[i],id++,true,nodes[i],nodes[(i+1)%nodes.Length]);
                if(primitive.Source.Circle is null)lines.Add(primitive);else perimeter.Add(primitive);
            }
        }
        omitted ??= Array.Empty<Component>();
        roleChanges ??= Array.Empty<RoleChange>();
        foreach(var component in omitted.Concat(included is null?Array.Empty<Component>():new[]{included}).Concat(roleChanges.Select(c=>c.Region)))
        foreach(var ring in component.Rings)
        foreach(var edge in ring.Edges.Where(edge=>edge.Source.Circle is null&&domainBounds.Intersects(EdgeBounds(edge))))
            lines.Add(new(edge.Source,id++,false,Vertex(edge.Source.Start),Vertex(edge.Source.End)));
        // A finite geometric edge is intersected once even when several
        // operands contain it in opposite directions. Operand winding stays
        // in the locators, independently of this arrangement's edge direction.
        lines=lines.GroupBy(p=>
        {
            var a=p.Source.Start;var b=p.Source.End;
            return a.X<b.X||a.X==b.X&&a.Y<b.Y?(a,b):(b,a);
        }).Select(g=>g.OrderByDescending(p=>p.Near).First()).ToList();
        var circleCuts=new Dictionary<(long X,long Y,Int128 C,EditCircle Circle),IReadOnlyList<EditBoundaryPoint>>();
        // All segments on one supporting line must use the same circle
        // roots. Prefer an existing endpoint before any shorter segment
        // populates the cache with a rounded quadratic solution.
        foreach(var circle in perimeter.Select(p=>p.Source.Circle!).Distinct())
        foreach(var group in lines.Select(p=>(Line:p.Source,Key:GridLine(p.Source))).Where(p=>p.Key is not null).GroupBy(p=>p.Key!.Value))
        {
            var known=group.SelectMany(p=>new[]{p.Line.Start,p.Line.End})
                .Where(p=>Math.Abs(p.DistanceTo(new(circle.CenterX,circle.CenterY))-circle.RadiusMetres)<=Arithmetic(p,new(circle.CenterX,circle.CenterY)))
                .Select(p=>GridPoint(p)?.Point??p).OrderBy(p=>p.X).ThenBy(p=>p.Y).Select(p=>(EditBoundaryPoint?)p).FirstOrDefault();
            if(known is not {} point)continue;
            var line=group.First().Line;var d=line.End-line.Start;
            var other=point+d*(-2*EditBoundaryMath.Dot(point-new EditBoundaryPoint(circle.CenterX,circle.CenterY),d)/d.LengthSquared);
            circleCuts[(group.Key.X,group.Key.Y,group.Key.C,circle)]=new[]{point,other}.Distinct().ToArray();
        }
        var index=new STRtree<Primitive>();
        foreach(var line in lines)index.Insert(Bounds(line.Source),line);
        index.Build();
        bool multipleOperands=omitted.Count>0||included is not null||rolePaths is not null;
        foreach(var line in lines)
        foreach(var other in index.Query(Bounds(line.Source)).Where(p=>p.Id>line.Id&&(multipleOperands||line.Near||p.Near)))
            Intersect(line,other,circleCuts,rationalNodes);
        foreach(var arc in perimeter)
        foreach(var line in index.Query(Bounds(arc.Source)))
            Intersect(line,arc,circleCuts,rationalNodes);
        var omissionIndex=new STRtree<Component>();
        foreach(var component in omitted)
        {
            omissionIndex.Insert(component.Bounds,component);
        }
        omissionIndex.Build();
        var sourceLocator=new SourceRegion(normalized);
        var nearEdges=new List<Use>();var farEdges=new List<Use>();
        var nearKeys=new HashSet<(Node,Node,EditCircle?,double)>();
        var farKeys=new HashSet<(Node,Node,EditCircle?,double)>();
        foreach(var primitive in lines.Concat(perimeter))
        foreach(var use in Pieces(primitive))
        {
            cancellation.ThrowIfCancellationRequested();
            var rational=use.Edge.Source.Circle is null&&use.Start.Root.Exact is {} r0&&use.End.Root.Exact is {} r1?PlanarRationalPoint.Mid(r0,r1):null;
            var mid=rational?.Point??use.Edge.At(.5);var tangent=use.Edge.Tangent(false);
            if(use.Edge.Source.Circle is not null)
            {var c=use.Edge.Source.Circle; tangent=new(-(mid.Y-c.CenterY),mid.X-c.CenterX);}
            bool a=Material(true),b=Material(false);
            if(queryOnly&&a!=b)return (new(Array.Empty<Component>(),Array.Empty<Component>()),true);
            Add(nearEdges,nearKeys,a,b);
            if(includeFar)
            {
                a=Material(true,false);b=Material(false,false);Add(farEdges,farKeys,a,b);
            }
            bool Material(bool left,bool insideNear=true)
            {
                if(!sourceLocator.Side(mid,tangent,left,use.Edge.Source,rational)||NearSide(mid,tangent,left,use.Edge.Source,rational)!=insideNear ||
                    omissionIndex.Query(new Envelope(mid.X,mid.X,mid.Y,mid.Y)).Any(c=>ComponentSide(c,mid,tangent,left,use.Edge.Source,rational))||
                    included is not null&&!ComponentSide(included,mid,tangent,left,use.Edge.Source,rational))return false;
                if(rolePaths is null)return true;
                bool value=roleLocator?.Side(mid,tangent,left,use.Edge.Source,rational)??false;
                foreach(var change in roleChanges)
                    if(ComponentSide(change.Region,mid,tangent,left,use.Edge.Source,rational))value=change.Road;
                return value==road;
            }
            void Add(List<Use> output,HashSet<(Node,Node,EditCircle?,double)> keys,bool left,bool right)
            {
                if(left==right)return;
                var selected=left?use:new Use(use.Edge.Reverse(),use.End,use.Start);
                var key=(selected.Start.Root,selected.End.Root,selected.Edge.Source.Circle,Math.Abs(selected.Edge.Sweep));
                if(keys.Add(key))output.Add(selected);
            }
        }
        if(queryOnly)return (new(Array.Empty<Component>(),Array.Empty<Component>()),false);
        return (new(Components(Stitch(nearEdges)),Components(Stitch(farEdges))),nearEdges.Count>0);
    }

    private static Envelope ComponentBounds(Ring outer,IReadOnlyList<Ring> holes)=>outer.Bounds;
    private static Envelope RingBounds(IReadOnlyList<Edge> edges)
    {
        var box=new Envelope();foreach(var edge in edges)box.ExpandToInclude(EdgeBounds(edge));return box;
    }
    private static Envelope EdgeBounds(Edge edge)
    {
        var box=new Envelope(edge.Start.X,edge.End.X,edge.Start.Y,edge.End.Y);
        if(edge.Source.Circle is {} c)for(int i=0;i<4;i++)
        {
            var p=EditBoundaryMath.CirclePoint(c,i*Math.PI/2);double t=Parameter(edge.Source,p);
            if(t>=Math.Min(edge.From,edge.To)&&t<=Math.Max(edge.From,edge.To))box.ExpandToInclude(p.X,p.Y);
        }
        box.ExpandBy(Arithmetic(edge.Start,edge.End));return box;
    }
    private static Envelope Bounds(EditBoundarySegment edge)
    {
        var box=edge.Circle is { } c ? new Envelope(c.CenterX-c.RadiusMetres,c.CenterX+c.RadiusMetres,c.CenterY-c.RadiusMetres,c.CenterY+c.RadiusMetres)
            : new Envelope(edge.Start.X,edge.End.X,edge.Start.Y,edge.End.Y);
        box.ExpandBy(Arithmetic(edge.Start,edge.End));return box;
    }
    // Coordinate round-off bounds, not a modeling or snap distance.
    private static double Arithmetic(params EditBoundaryPoint[] points) =>
        32*2.2204460492503131e-16*Math.Max(1,points.Max(p=>Math.Max(Math.Abs(p.X),Math.Abs(p.Y))));
    private bool NearSide(EditBoundaryPoint point,EditBoundaryPoint direction,bool left,EditBoundarySegment owner,PlanarRationalPoint? exact)
    {
        foreach(var loop in boundaries)
        {
            foreach(var edge in loop.Segments)
                if(SameSupport(owner,edge)&&OnFinite(point,exact,edge))
                {
                    var d=edge.Circle is {} c?new EditBoundaryPoint(-(point.Y-c.CenterY),point.X-c.CenterX):edge.End-edge.Start;
                    return (EditBoundaryMath.Dot(d,direction)>0)==left;
                }
            if(loop.Contains(point,0))return true;
        }
        return false;
    }
    // Immutable finite sources and winding index. Mutable intersection events
    // belong to SplitCore and must never be retained here.
    internal sealed class SourceRegion
    {
        private readonly STRtree<(int Id,EditBoundarySegment Edge)> index=new();
        private readonly double maxX;
        internal SourceRegion(PathsD paths)
        {
            maxX=paths.SelectMany(p=>p).Select(p=>p.x).DefaultIfEmpty(double.NegativeInfinity).Max();
            int id=0;
            foreach(var path in paths)for(int i=0;i<path.Count;i++)
            {
                var a=path[i];var b=path[(i+1)%path.Count];
                if(a.x==b.x&&a.y==b.y)continue;
                var edge=new EditBoundarySegment(new(a.x,a.y),new(b.x,b.y));index.Insert(Bounds(edge),(id++,edge));
            }
            index.Build();
        }
        internal IEnumerable<EditBoundarySegment> Edges(Envelope bounds)
            => index.Query(bounds).Where(item=>bounds.Intersects(new Envelope(
                item.Edge.Start.X,item.Edge.End.X,item.Edge.Start.Y,item.Edge.End.Y)))
                .OrderBy(item=>item.Id).Select(item=>item.Edge);
        internal bool Side(EditBoundaryPoint point,EditBoundaryPoint direction,bool left,EditBoundarySegment owner,PlanarRationalPoint? exact)
        {
            var box=new Envelope(point.X,point.X,point.Y,point.Y);box.ExpandBy(Arithmetic(point));
            foreach(var item in index.Query(box))
            {
                var edge=item.Edge;
                if(SameSupport(owner,edge)&&OnFinite(point,exact,edge))
                    return (EditBoundaryMath.Dot(edge.End-edge.Start,direction)>0)==left;
            }
            if(point.X>maxX)return false;
            int winding=0;var p=new Coordinate(point.X,point.Y);
            foreach(var item in index.Query(new Envelope(point.X,maxX,point.Y,point.Y)))
            {
                var edge=item.Edge;
                var a=edge.Start;var b=edge.End;
                if(exact is not null&&GridPoint(a) is {} ra&&GridPoint(b) is {} rb)
                {
                    int ya=exact.CompareY(ra),yb=exact.CompareY(rb),side=exact.Orientation(ra,rb);
                    if(ya>=0&&yb<0&&side>0)winding++;
                    if(yb>=0&&ya<0&&side<0)winding--;
                }
                else
                {
                    if(a.Y<=point.Y&&point.Y<b.Y&&Orientation.Index(new(a.X,a.Y),new(b.X,b.Y),p)==OrientationIndex.CounterClockwise)winding++;
                    if(b.Y<=point.Y&&point.Y<a.Y&&Orientation.Index(new(a.X,a.Y),new(b.X,b.Y),p)==OrientationIndex.Clockwise)winding--;
                }
            }
            return winding!=0;
        }
    }
    private static bool ComponentSide(Component component,EditBoundaryPoint point,EditBoundaryPoint direction,bool left,EditBoundarySegment owner,PlanarRationalPoint? exact)
    {
        if(!component.Bounds.Contains(point.X,point.Y))return false;
        var atPoint=new Envelope(point.X,point.X,point.Y,point.Y);atPoint.ExpandBy(Arithmetic(point));
        foreach(var edge in component.EdgeIndex.Query(atPoint))
        {
            if(!SameSupport(owner,edge.Source))continue;
            if(edge.Source.Circle is not null)
            {if(!ArcContains(edge,point))continue;}
            else if(exact is not null&&edge.ExactStart is {} start&&edge.ExactEnd is {} end)
            {if(!Between(exact,start,end))continue;}
            else
            {
                double t=Parameter(edge.Source,point);
                if(t<Math.Min(edge.From,edge.To)||t>Math.Max(edge.From,edge.To)||
                    !EditBoundaryMath.OnSegment(point,edge.Source,Arithmetic(point,edge.Start,edge.End)))continue;
            }
            var d=edge.Source.Circle is {} c?new EditBoundaryPoint(-(point.Y-c.CenterY),point.X-c.CenterX)*Math.Sign(edge.To-edge.From):edge.End-edge.Start;
            return (EditBoundaryMath.Dot(d,direction)>0)==left;
        }
        // Components were clipped by this same Near perimeter. At an arc
        // their material must own that finite arc interval; a rounded ray
        // point must not appear inside a different neighbouring interval.
        if(owner.Circle is not null)return false;
        return ContainsRing(component.Outer,point,exact)&&!component.Holes.Any(h=>ContainsRing(h,point,exact));
    }
    private static bool ArcContains(Edge edge,EditBoundaryPoint point)
    {
        if(edge.Start==edge.End&&Math.Abs(edge.Sweep)>=2*Math.PI)return true;
        var circle=edge.Source.Circle!;
        var start=edge.Sweep>0?edge.Start:edge.End;var end=edge.Sweep>0?edge.End:edge.Start;
        int startEnd=Compare(start,end),startPoint=Compare(start,point),pointEnd=Compare(point,end);
        return startEnd<=0?startPoint<=0&&pointEnd<=0:startPoint<=0||pointEnd<=0;
        int Compare(EditBoundaryPoint a,EditBoundaryPoint b)
        {
            int Half(EditBoundaryPoint p)=>p.Y>circle.CenterY||p.Y==circle.CenterY&&p.X>=circle.CenterX?0:1;
            int order=Half(a).CompareTo(Half(b));
            if(order!=0)return order;
            return -(int)Orientation.Index(new(circle.CenterX,circle.CenterY),new(a.X,a.Y),new(b.X,b.Y));
        }
    }
    private static bool OnFinite(EditBoundaryPoint point,PlanarRationalPoint? exact,EditBoundarySegment edge)
    {
        if(edge.Circle is null&&exact is not null&&GridPoint(edge.Start) is {} a&&GridPoint(edge.End) is {} b)
            return Between(exact,a,b);
        return EditBoundaryMath.OnSegment(point,edge,Arithmetic(point,edge.Start,edge.End));
    }
    private static bool Between(PlanarRationalPoint p,PlanarRationalPoint a,PlanarRationalPoint b)
    {
        int first,second;
        if(a.CompareX(b)!=0){first=p.CompareX(a);second=p.CompareX(b);}
        else{first=p.CompareY(a);second=p.CompareY(b);}
        return first==0||second==0||first!=second;
    }
    private static PlanarRationalPoint? GridPoint(EditBoundaryPoint p)
    {
        const double scale=1/ModelPlanPolicy.PrecisionMetres;
        if(Math.Abs(p.X*scale-Math.Round(p.X*scale))>Arithmetic(p)*scale||
            Math.Abs(p.Y*scale-Math.Round(p.Y*scale))>Arithmetic(p)*scale)return null;
        return PlanarRationalPoint.Grid((long)Math.Round(p.X*scale),(long)Math.Round(p.Y*scale));
    }
    private static (long X,long Y,Int128 C)? GridLine(EditBoundarySegment edge)
    {
        const double scale=1/ModelPlanPolicy.PrecisionMetres;
        foreach(var p in new[]{edge.Start,edge.End})
            if(Math.Abs(p.X*scale-Math.Round(p.X*scale))>Arithmetic(p)*scale||
                Math.Abs(p.Y*scale-Math.Round(p.Y*scale))>Arithmetic(p)*scale)return null;
        long ax=(long)Math.Round(edge.Start.X*scale),ay=(long)Math.Round(edge.Start.Y*scale);
        long bx=(long)Math.Round(edge.End.X*scale),by=(long)Math.Round(edge.End.Y*scale);
        long x=by-ay,y=ax-bx;if(x==0&&y==0)return null;
        long u=Math.Abs(x),v=Math.Abs(y);while(v!=0){long r=u%v;u=v;v=r;}
        x/=u;y/=u;if(x<0||x==0&&y<0){x=-x;y=-y;}
        return(x,y,-((Int128)x*ax+(Int128)y*ay));
    }
    private static bool SameSupport(EditBoundarySegment a,EditBoundarySegment b)
    {
        if(a.Circle is not null||b.Circle is not null)return a.Circle is not null&&a.Circle==b.Circle;
        const double scale=1/ModelPlanPolicy.PrecisionMetres;
        var values=new[]{a.Start,a.End,b.Start,b.End};
        if(values.All(p=>Math.Abs(p.X*scale-Math.Round(p.X*scale))<=Arithmetic(p)*scale&&
            Math.Abs(p.Y*scale-Math.Round(p.Y*scale))<=Arithmetic(p)*scale))
        {
            var q=values.Select(p=>(X:(long)Math.Round(p.X*scale),Y:(long)Math.Round(p.Y*scale))).ToArray();
            var dx=q[1].X-q[0].X;var dy=q[1].Y-q[0].Y;
            return q.Skip(2).All(p=>(Int128)dx*(p.Y-q[0].Y)-(Int128)dy*(p.X-q[0].X)==0);
        }
        return Orientation.Index(new(a.Start.X,a.Start.Y),new(a.End.X,a.End.Y),new(b.Start.X,b.Start.Y))==OrientationIndex.Collinear&&
            Orientation.Index(new(a.Start.X,a.Start.Y),new(a.End.X,a.End.Y),new(b.End.X,b.End.Y))==OrientationIndex.Collinear;
    }
    private static double Parameter(EditBoundarySegment edge,EditBoundaryPoint point)
    {
        if(point==edge.Start)return 0;if(point==edge.End)return 1;
        if(edge.Circle is { } c)
        {
            double angle=EditBoundaryMath.PositiveAngle(Math.Atan2(point.Y-c.CenterY,point.X-c.CenterX)-edge.StartAngle);
            if(angle>2*Math.PI-1e-13)angle=0;
            return angle/edge.SweepRadians;
        }
        var d=edge.End-edge.Start;return EditBoundaryMath.Dot(point-edge.Start,d)/d.LengthSquared;
    }
    private static void Intersect(Primitive line,Primitive boundary,
        Dictionary<(long X,long Y,Int128 C,EditCircle Circle),IReadOnlyList<EditBoundaryPoint>> circleCuts,
        Dictionary<PlanarRationalPoint,Node> rationalNodes)
    {
        var a=line.Source;var b=boundary.Source;
        double epsilon=Arithmetic(a.Start,a.End,b.Start,b.End);
        IReadOnlyList<EditBoundaryPoint> intersections;
        var sharedEndpoint=new[]{a.Start,a.End}.Where(p=>p==b.Start||p==b.End).Distinct().ToArray();
        if(line.Near&&a.Circle is null&&b.Circle is not null&&sharedEndpoint.Length>0)
            // Near's straight perimeter is a constructed common tangent.
            // Its arc contact is already known; resolving a quadratic again
            // would manufacture a second root from floating-point round-off.
            intersections=sharedEndpoint;
        else if(a.Circle is null&&b.Circle is null)
        {
            const double scale=1/ModelPlanPolicy.PrecisionMetres;
            bool grid=new[]{a.Start,a.End,b.Start,b.End}.All(p=>
                Math.Abs(p.X*scale-Math.Round(p.X*scale))<=Arithmetic(p)*scale&&
                Math.Abs(p.Y*scale-Math.Round(p.Y*scale))<=Arithmetic(p)*scale);
            Coordinate Input(EditBoundaryPoint p)=>grid?new(Math.Round(p.X*scale),Math.Round(p.Y*scale)):new(p.X,p.Y);
            var intersector=new RobustLineIntersector();
            intersector.ComputeIntersection(Input(a.Start),Input(a.End),Input(b.Start),Input(b.End));
            intersections=Enumerable.Range(0,intersector.IntersectionNum).Select(i=>
            {var p=intersector.GetIntersection(i);return grid?new EditBoundaryPoint(p.X/scale,p.Y/scale):new EditBoundaryPoint(p.X,p.Y);}).ToArray();
        }
        else if(a.Circle is null&&b.Circle is {} cachedCircle&&GridLine(a) is {} support)
        {
            var key=(support.X,support.Y,support.C,cachedCircle);
            if(!circleCuts.TryGetValue(key,out var points))
            {
                var endpoint=new[]{b.Start,b.End}.Where(p=>OnLine(p,a))
                    .Concat(new[]{a.Start,a.End}.Where(p=>Math.Abs(p.DistanceTo(new(cachedCircle.CenterX,cachedCircle.CenterY))-cachedCircle.RadiusMetres)<=epsilon))
                    .Select(p=>(EditBoundaryPoint?)p).FirstOrDefault();
                if(endpoint is {} known)
                {
                    var d=a.End-a.Start;
                    var other=known+d*(-2*EditBoundaryMath.Dot(known-new EditBoundaryPoint(cachedCircle.CenterX,cachedCircle.CenterY),d)/d.LengthSquared);
                    points=new[]{known,other}.Distinct().ToArray();
                }
                else points=EditBoundaryMath.SupportIntersections(a,b,epsilon);
                circleCuts.Add(key,points);
            }
            intersections=points.Where(p=>EditBoundaryMath.OnSegment(p,a,epsilon)&&EditBoundaryMath.OnSegment(p,b,epsilon)).ToArray();
        }
        else if(a.Circle is null&&b.Circle is {} circle&&
            new[]{a.Start,a.End}.Where(p=>Math.Abs(p.DistanceTo(new(circle.CenterX,circle.CenterY))-circle.RadiusMetres)<=epsilon)
                .Select(p=>(EditBoundaryPoint?)p).FirstOrDefault() is {} endpoint)
        {
            // An already known circle endpoint is an exact root. Factoring
            // that root avoids subtractive cancellation in a nearly tangent
            // line/circle quadratic and keeps adjacent edges on one node.
            var direction=a.End-a.Start;
            var other=endpoint+direction*(-2*EditBoundaryMath.Dot(endpoint-new EditBoundaryPoint(circle.CenterX,circle.CenterY),direction)/direction.LengthSquared);
            intersections=new[]{endpoint,other}.Where(p=>EditBoundaryMath.OnSegment(p,a,epsilon)&&EditBoundaryMath.OnSegment(p,b,epsilon)).Distinct().ToArray();
        }
        else intersections=EditBoundaryMath.Intersections(a,b,epsilon);
        PlanarRationalPoint? rationalCross=null;
        if(a.Circle is null&&b.Circle is null&&intersections.Count==1&&GridPoint(a.Start) is {} ga&&GridPoint(a.End) is {} gb&&
            GridPoint(b.Start) is {} gc&&GridPoint(b.End) is {} gd)rationalCross=PlanarRationalPoint.Crossing(ga,gb,gc,gd);
        foreach(var calculated in intersections)
        {
            var rationalEvent=rationalCross??(a.Circle is null&&b.Circle is null&&
                new[]{a.Start,a.End,b.Start,b.End}.All(p=>GridPoint(p) is not null)?GridPoint(calculated):null);
            var point=rationalEvent?.Point??calculated;
            // A supporting circle root can lie immediately beyond a finite
            // arc endpoint. Its angle may round to the endpoint parameter;
            // retain the actual angular order instead of extending the arc.
            if(b.Circle is not null&&!ArcContains(new(b,boundary.Id,0,1,b.Start,b.End),point))continue;
            double t=Parameter(a,point),u=Parameter(b,point);
            double lineLength=Math.Sqrt((a.End-a.Start).LengthSquared);
            double arcLength=b.Circle is {} circle?circle.RadiusMetres*b.SweepRadians:Math.Sqrt((b.End-b.Start).LengthSquared);
            if(t < -epsilon/lineLength||t>1+epsilon/lineLength||u< -epsilon/arcLength||u>1+epsilon/arcLength)continue;
            Node? shared=null;
            if(rationalEvent is not null)
            {
                if(!rationalNodes.TryGetValue(rationalEvent,out shared))rationalNodes.Add(rationalEvent,shared=new(point){Exact=rationalEvent});
            }
            var ln=AtCut(line,Math.Clamp(t,0,1),point,ref shared);
            var bn=AtCut(boundary,Math.Clamp(u,0,1),point,ref shared);
            if(!ReferenceEquals(ln.Root,bn.Root))bn.Root.Parent=ln.Root;
        }
        static bool OnLine(EditBoundaryPoint p,EditBoundarySegment line)
            =>Orientation.Index(new(line.Start.X,line.Start.Y),new(line.End.X,line.End.Y),new(p.X,p.Y))==OrientationIndex.Collinear;
    }
    private static Node AtCut(Primitive primitive,double t,EditBoundaryPoint p,ref Node? shared)
    {
        foreach(var cut in primitive.Cuts)
            if(shared?.Root.Exact is {} exact&&cut.Node.Root.Exact is {} previous
                ?exact==previous:cut.Node.Root.Point==p)
            {shared??=cut.Node.Root;if(!ReferenceEquals(cut.Node.Root,shared.Root))cut.Node.Root.Parent=shared.Root;return cut.Node;}
        var node=shared??=new Node(p);primitive.Cuts.Add((t,node));return node;
    }
    private static IEnumerable<Use> Pieces(Primitive primitive)
    {
        var cuts=primitive.Cuts.ToArray();
        Array.Sort(cuts,(a,b)=>
        {
            if(primitive.Source.Circle is null&&a.Node.Root.Exact is {} first&&b.Node.Root.Exact is {} second)
            {
                var delta=primitive.Source.End-primitive.Source.Start;
                return Math.Abs(delta.X)>=Math.Abs(delta.Y)?first.CompareX(second)*Math.Sign(delta.X):first.CompareY(second)*Math.Sign(delta.Y);
            }
            int order=a.T.CompareTo(b.T);
            if(order!=0||primitive.Source.Circle is not {} circle)return order;
            var radius=a.Node.Root.Point-new EditBoundaryPoint(circle.CenterX,circle.CenterY);
            return -Math.Sign(EditBoundaryMath.Cross(radius,b.Node.Root.Point-a.Node.Root.Point));
        });
        for(int i=1;i<cuts.Length;i++)
        {
            var a=cuts[i-1].Node.Root;var b=cuts[i].Node.Root;
            // Merging an already known endpoint can leave two parameter cuts
            // on the same event. Only a complete circle has material between
            // identical event nodes; a tiny arc self-loop is not a new edge.
            if(ReferenceEquals(a,b)&&(primitive.Source.Circle is null||
                Math.Abs(primitive.Source.SweepRadians*(cuts[i].T-cuts[i-1].T))<2*Math.PI))continue;
            yield return new(new(primitive.Source,primitive.Id,cuts[i-1].T,cuts[i].T,a.Point,b.Point){ExactStart=a.Exact,ExactEnd=b.Exact},a,b);
        }
    }
    private static IReadOnlyList<Ring> Stitch(IReadOnlyList<Use> edges)
    {
        var outgoing=edges.Select((edge,index)=>(edge,index)).GroupBy(x=>x.edge.Start.Root)
            .ToDictionary(g=>g.Key,g=>g.Select(x=>x.index).ToArray());
        var next=new int[edges.Count];
        for(int i=0;i<edges.Count;i++)
        {
            var edge=edges[i];
            if(!outgoing.TryGetValue(edge.End.Root,out var candidates))
                throw new InvalidOperationException($"정확 Near 경계 사건의 연결이 누락됐습니다: {edge.Edge.End}.");
            var incoming=edge.Edge.Tangent(true);double reverse=Math.Atan2(-incoming.Y,-incoming.X);
            next[i]=candidates.OrderBy(j=>
            {var d=edges[j].Edge.Tangent(false);return EditBoundaryMath.PositiveAngle(reverse-Math.Atan2(d.Y,d.X));}).ThenBy(j=>edges[j].Edge.SourceId).First();
        }
        var visited=new bool[edges.Count];var rings=new List<Ring>();
        for(int seed=0;seed<edges.Count;seed++)
        {
            if(visited[seed])continue;var loop=new List<Edge>();int current=seed;
            do
            {
                if(visited[current])throw new InvalidOperationException("정확 Near 경계 사건이 서로 다른 고리에 중복 연결됐습니다.");
                visited[current]=true;loop.Add(edges[current].Edge);current=next[current];
            }while(current!=seed);
            var ring=new Ring(MergeContiguous(loop));
            if(ring.SignedArea==0)throw new InvalidOperationException("정확 Near 경계가 양의 영역을 잃었습니다.");
            rings.Add(ring);
        }
        return rings;
    }
    private static IReadOnlyList<Edge> MergeContiguous(IReadOnlyList<Edge> input)
    {
        var edges=new List<Edge>();
        foreach(var edge in input)
        {
            if(edges.Count>0&&Compatible(edges[^1],edge))edges[^1]=Join(edges[^1],edge);
            else edges.Add(edge);
        }
        if(edges.Count>1&&Compatible(edges[^1],edges[0]))
        {edges[0]=Join(edges[^1],edges[0]);edges.RemoveAt(edges.Count-1);}
        return edges;
        static bool Compatible(Edge a,Edge b)=>a.Source==b.Source&&a.To==b.From&&a.End==b.Start&&
            EditBoundaryMath.Dot(a.Tangent(true),b.Tangent(false))>0;
        static Edge Join(Edge a,Edge b)=>a with {To=b.To,End=b.End,ExactEnd=b.ExactEnd};
    }
    private static IReadOnlyList<Component> Components(IReadOnlyList<Ring> rings)
    {
        var shells=rings.Where(r=>r.SignedArea>0).ToArray();var holes=shells.Select(_=>new List<Ring>()).ToArray();
        foreach(var hole in rings.Where(r=>r.SignedArea<0))
        {
            var point=hole.Edges[0].At(.5);
            int owner=Enumerable.Range(0,shells.Length).Where(i=>ContainsRing(shells[i],point))
                .OrderBy(i=>shells[i].SignedArea).DefaultIfEmpty(-1).First();
            if(owner<0)throw new InvalidOperationException("정확 Near 구멍의 소유 외곽이 없습니다.");
            holes[owner].Add(hole);
        }
        return shells.Select((shell,i)=>new Component(shell,holes[i])).ToArray();
    }
    private static bool ContainsRing(Ring ring,EditBoundaryPoint point,PlanarRationalPoint? exact=null)
    {
        // Ray crossing uses analytic arc intersections, not an inscribed proxy.
        int winding=0;
        if(!ring.Bounds.Contains(point.X,point.Y))return false;
        foreach(var edge in ring.Index.Query(new Envelope(point.X,ring.Bounds.MaxX,point.Y,point.Y)))
        {
            if(edge.Source.Circle is not {} c)
            {
                var a=edge.Start;var b=edge.End;
                if(exact is not null&&edge.ExactStart is {} ra&&edge.ExactEnd is {} rb)
                {
                    int ya=exact.CompareY(ra),yb=exact.CompareY(rb),side=exact.Orientation(ra,rb);
                    if(ya>=0&&yb<0&&side>0)winding++;
                    if(yb>=0&&ya<0&&side<0)winding--;
                }
                else
                {
                    if(a.Y<=point.Y&&point.Y<b.Y&&EditBoundaryMath.Cross(b-a,point-a)>0)winding++;
                    if(b.Y<=point.Y&&point.Y<a.Y&&EditBoundaryMath.Cross(b-a,point-a)<0)winding--;
                }
                continue;
            }
            double dy=point.Y-c.CenterY;if(Math.Abs(dy)>=c.RadiusMetres)continue;
            double dx=Math.Sqrt((c.RadiusMetres-Math.Abs(dy))*(c.RadiusMetres+Math.Abs(dy)));
            foreach(double x in new[]{c.CenterX-dx,c.CenterX+dx})
            {
                if(x<=point.X)continue;
                double t=Parameter(edge.Source,new(x,point.Y));
                double local=(t-edge.From)/(edge.To-edge.From);
                double endpointError=Arithmetic(point,edge.Start,edge.End)/(c.RadiusMetres*Math.Abs(edge.Sweep));
                if(local < -endpointError || local > 1+endpointError)continue;
                int direction=(x-c.CenterX)*Math.Sign(edge.To-edge.From)>0?1:-1;
                // Ray crossing owns the lower Y endpoint, not the first
                // parameter. Adjacent straight/arc edges must use one rule.
                if(Math.Abs(local)<=endpointError&&direction<0 || Math.Abs(local-1)<=endpointError&&direction>0)continue;
                winding+=direction;
            }
        }
        return winding!=0;
    }
}
