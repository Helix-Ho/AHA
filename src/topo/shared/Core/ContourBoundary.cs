using Clipper2Lib;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>Exact Near boundary intervals classified against one canonical contour region.</summary>
internal sealed class ContourBoundary
{
    internal sealed record Part(EditBoundarySegment Edge,bool Filled);
    private readonly IndexedPointInAreaLocator? locator;
    private readonly STRtree<EditBoundarySegment> edges=new();

    internal ContourBoundary(PathsD source)
    {
        if(source.Count>0)locator=new(PlanarTriangulation.ToGridGeometry(source));
        foreach(var path in source)
        for(int i=0;i<path.Count;i++)
        {
            var a=path[i];var b=path[(i+1)%path.Count];
            if(a.x==b.x&&a.y==b.y)continue;
            edges.Insert(new Envelope(a.x,b.x,a.y,b.y),new(new(a.x,a.y),new(b.x,b.y)));
        }
        edges.Build();
    }

    internal IReadOnlyList<Part> Split(IEnumerable<EditBoundarySegment> boundaries,bool nearSide)
    {
        var result=new List<Part>();
        foreach(var boundary in boundaries)
        {
            var extent=boundary.Circle is {} circle
                ?new Envelope(circle.CenterX-circle.RadiusMetres,circle.CenterX+circle.RadiusMetres,
                    circle.CenterY-circle.RadiusMetres,circle.CenterY+circle.RadiusMetres)
                :new Envelope(boundary.Start.X,boundary.End.X,boundary.Start.Y,boundary.End.Y);
            extent.ExpandBy(1e-9);
            var cuts=new SortedSet<double>{0,1};
            foreach(var edge in edges.Query(extent))
            foreach(var point in EditBoundaryMath.Intersections(boundary,edge,1e-9))
                cuts.Add(Fraction(boundary,point));
            var values=cuts.ToArray();
            for(int i=1;i<values.Length;i++)
            {
                double from=values[i-1],to=values[i];
                if(to-from<=1e-14)continue;
                var mid=boundary.PointAt((from+to)*.5);
                var location=locator?.Locate(new Coordinate(mid.X*1e6,mid.Y*1e6))??Location.Exterior;
                if(location==Location.Boundary)
                {
                    // Canonical source rings keep material on the left. Near
                    // uses the left side of its CCW boundary; Far uses the right.
                    // Direction resolves coincident edges without offset probes.
                    var tangent=boundary.Circle is {} arc
                        ?new EditBoundaryPoint(-(mid.Y-arc.CenterY),mid.X-arc.CenterX)
                        :boundary.End-boundary.Start;
                    var atPoint=new Envelope(mid.X,mid.X,mid.Y,mid.Y);atPoint.ExpandBy(1e-9);
                    bool filled=edges.Query(atPoint).Any(edge=>EditBoundaryMath.OnSegment(mid,edge,1e-9)&&
                        (nearSide?1:-1)*EditBoundaryMath.Dot(tangent,edge.End-edge.Start)>0);
                    location=filled?Location.Interior:Location.Exterior;
                }
                result.Add(new(new(boundary.PointAt(from),boundary.PointAt(to),boundary.Circle,
                    boundary.StartAngle+boundary.SweepRadians*from,boundary.SweepRadians*(to-from)),location==Location.Interior));
            }
        }
        return result;
    }

    private static double Fraction(EditBoundarySegment segment,EditBoundaryPoint point)
    {
        if(point.DistanceTo(segment.Start)<=1e-9)return 0;
        if(point.DistanceTo(segment.End)<=1e-9)return 1;
        if(segment.Circle is {} circle)
            return Math.Clamp(EditBoundaryMath.PositiveAngle(Math.Atan2(point.Y-circle.CenterY,point.X-circle.CenterX)-segment.StartAngle)/segment.SweepRadians,0,1);
        var delta=segment.End-segment.Start;
        return Math.Clamp(EditBoundaryMath.Dot(point-segment.Start,delta)/delta.LengthSquared,0,1);
    }
}
