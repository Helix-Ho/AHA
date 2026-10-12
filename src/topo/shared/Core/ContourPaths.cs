using Clipper2Lib;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Polygonize;

namespace Aha.Topo;

/// <summary>Shared planar operations for contour planning and profile output.</summary>
public static class ContourPaths
{
    private static readonly PrecisionModel Precision=new(1); // Coordinates below are integer micrometres.
    public static PathsD Union(PathsD paths)=>FromGeometry(UnionGeometry(paths));
    public static PathsD Intersect(PathsD a,PathsD b)
    {
        if(a.Count==0||b.Count==0)return new();
        var first=Clipper.GetBounds(a);var second=Clipper.GetBounds(b);
        if(first.right<=second.left||second.right<=first.left||first.bottom<=second.top||second.bottom<=first.top)return new();
        return Overlay(a,b,SpatialFunction.Intersection);
    }
    public static PathsD Difference(PathsD a,PathsD b)=>a.Count==0?new():b.Count==0?Union(a):Overlay(a,b,SpatialFunction.Difference);
    public static double Area(PathsD paths)=>Math.Abs(Clipper.Area(paths));
    public static PathsD Rectangle(double x0,double y0,double x1,double y1)=>new(){new PathD{new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)}};

    // One fixed-precision topology operation nodes touching/retracing edges at
    // the established grid. No independently repeated contour simplification.
    internal static Geometry UnionGeometry(PathsD paths)
    {
        var factory=new GeometryFactory();
        if(paths.Count==0)return factory.CreatePolygon();
        // A flat path list has NonZero winding semantics. Guessing hole ownership
        // before overlapping operands are resolved loses duplicate/nested holes.
        var rings=paths.Select(path=>
        {
            var points=new List<Coordinate>();
            foreach(var p in path)
            {
                var key=PlanarTriangulation.GridKey(p);var point=new Coordinate(key.X,key.Y);
                if(points.Count==0||!points[^1].Equals2D(point))points.Add(point);
            }
            if(points.Count>0&&!points[0].Equals2D(points[^1]))points.Add(points[0]);
            return points.ToArray();
        }).Where(ring=>ring.Length>=4).ToArray();
        if(rings.Length==0)return factory.CreatePolygon();
        var ringIndex=new STRtree<Coordinate[]>();
        foreach(var ring in rings)
        {var envelope=new Envelope();foreach(var point in ring)envelope.ExpandToInclude(point);ringIndex.Insert(envelope,ring);}
        ringIndex.Build();
        var linework=UnaryUnionNG.Union((Geometry)factory.CreateMultiLineString(rings.Select(factory.CreateLineString).ToArray()),Precision);
        var polygonizer=new Polygonizer();polygonizer.Add(linework);
        var selected=polygonizer.GetPolygons().Where(polygon=>
        {
            var point=polygon.InteriorPoint.Coordinate;
            return ringIndex.Query(new Envelope(point)).Sum(ring=>Winding(ring,point))!=0;
        }).Cast<Geometry>().ToArray();
        return selected.Length==0?factory.CreatePolygon():UnaryUnionNG.Union(factory.BuildGeometry(selected),Precision);

        static int Winding(Coordinate[] ring,Coordinate point)
        {
            int count=0;
            for(int i=1;i<ring.Length;i++)
            {
                var a=ring[i-1];var b=ring[i];
                if(a.Y<=point.Y&&point.Y<b.Y&&Orientation.Index(a,b,point)==OrientationIndex.CounterClockwise)count++;
                if(b.Y<=point.Y&&point.Y<a.Y&&Orientation.Index(a,b,point)==OrientationIndex.Clockwise)count--;
            }
            return count;
        }
    }
    private static PathsD Overlay(PathsD a,PathsD b,SpatialFunction operation)
        =>FromGeometry(OverlayGeometry(a,b,operation));

    internal static IReadOnlyList<PathsD> OverlayComponents(PathsD a,PathsD b,SpatialFunction operation)
        =>Polygons(OverlayGeometry(a,b,operation)).Select(PlanarTriangulation.FromGridPolygon).ToArray();

    private static Geometry OverlayGeometry(PathsD a,PathsD b,SpatialFunction operation)
        =>OverlayGeometry(UnionGeometry(a),UnionGeometry(b),operation);

    // These operands already own their shell/hole topology on the integer grid.
    internal static Geometry OverlayGeometry(Geometry a,Geometry b,SpatialFunction operation)
    {
        var overlay=new OverlayNG(a,b,Precision,operation)
            { StrictMode=true };
        return overlay.GetResult();
    }
    internal static PathsD FromGeometry(Geometry geometry)=>new(Polygons(geometry).SelectMany(PlanarTriangulation.FromGridPolygon));
    internal static IEnumerable<Polygon> Polygons(Geometry geometry)=>geometry is Polygon polygon
        ?polygon.IsEmpty?Array.Empty<Polygon>():new[]{polygon}
        :geometry is GeometryCollection collection?collection.Geometries.SelectMany(Polygons):Array.Empty<Polygon>();

    public static IReadOnlyList<PathsD> Components(PathsD paths)
    {
        return Polygons(UnionGeometry(paths)).Select(PlanarTriangulation.FromGridPolygon).ToArray();
    }

    internal static bool HasMinimumWidth(PathsD source)=>Clipper.InflatePaths(source,-(.05-ModelPlanPolicy.PrecisionMetres),
        JoinType.Miter,EndType.Polygon,2,EditRegion.ClipPrecision).Count>0;

}
