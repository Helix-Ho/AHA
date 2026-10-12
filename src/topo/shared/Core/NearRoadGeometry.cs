using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;

namespace Aha.Topo;

public sealed record NearRoadResult(PathsD Roads, int ResolvedComponents,
    double ChangedAreaSquareMetres, int ModelComponents, PathsD NearBoundary)
{
    internal PathsD? ExactSource { get; init; }
    internal IReadOnlyList<ExactNearRegions.RoleChange> ExactChanges { get; init; } = Array.Empty<ExactNearRegions.RoleChange>();
}

/// <summary>
/// Resolves small role components created by the Near/Far cut before height
/// grading. Reassignment stays in the same fixed Near/Far partition. The exact
/// Near boundary and source curves do not move; the source road change band
/// does not restrict fragments introduced by this later partition.
/// </summary>
public static class NearRoadGeometry
{
    private const double Grid = ModelPlanPolicy.PrecisionMetres;
    private sealed record Component(int Zone, bool Road, Polygon Region)
    {
        internal PathsD Paths { get; } = PlanarTriangulation.FromGridPolygon(Region);
        internal double Area => Region.Area * Grid * Grid;
    }

    public static NearRoadResult Resolve(PathsD modelRoads, PathsD domain,
        EditRegion near, ModelPlanPolicy? policy = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelRoads);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(near);
        policy ??= ModelPlanPolicy.Default;
        policy.Validate();
        var original = Union(modelRoads);
        var extent = Union(domain);
        var exact = new ExactNearRegions(near,ct);
        var partition=ResolveExact(exact,extent,original,ct:ct,policy:policy);
        // Query geometry uses the same arc cuts as the authoritative role changes.
        var samples=new BoundarySamples(near,partition.Changes.Select(c=>c.Region));
        var nearDomain=Intersect(samples.Near,extent);
        var region=ContourPaths.UnionGeometry(original);
        foreach(var change in partition.Changes)
            region=ContourPaths.OverlayGeometry(region,ContourPaths.UnionGeometry(samples.Paths(change.Region)),
                change.Road?SpatialFunction.Union:SpatialFunction.Difference);
        var model=ContourPaths.FromGeometry(region);
        return new(model,partition.Changes.Count,Area(Xor(original,model)),partition.Road.Near.Count+partition.Road.Far.Count,nearDomain)
            {ExactSource=original,ExactChanges=partition.Changes};
    }

    internal sealed record ExactRoles(ExactNearRegions.Result Road,ExactNearRegions.Result Land,
        IReadOnlyList<ExactNearRegions.RoleChange> Changes);

    internal static ExactRoles ResolveExact(ExactNearRegions exact,PathsD domain,PathsD roads,
        IReadOnlyList<ExactNearRegions.Component>? omitted=null,bool includeFar=true,CancellationToken ct=default,ModelPlanPolicy? policy=null,ExactNearRegions.Component? included=null)
    {
        var changes=new List<ExactNearRegions.RoleChange>();
        policy ??= ModelPlanPolicy.Default;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var roadParts=exact.Split(domain,omitted,included:included,rolePaths:roads,road:true,roleChanges:changes,includeFar:includeFar);
            var landParts=exact.Split(domain,omitted,included:included,rolePaths:roads,road:false,roleChanges:changes,includeFar:includeFar);
            var parts=roadParts.Near.Select(c=>(Region:c,Road:true,Near:true))
                .Concat(roadParts.Far.Select(c=>(Region:c,Road:true,Near:false)))
                .Concat(landParts.Near.Select(c=>(Region:c,Road:false,Near:true)))
                .Concat(landParts.Far.Select(c=>(Region:c,Road:false,Near:false))).ToArray();
            var small=parts.Where(c=>c.Region.Area<policy.MinimumAreaSquareMetres||
                IsNarrow(c.Region.Sample(),c.Region.Area,policy.MinimumWidthMetres/2-Grid))
                .OrderBy(c=>c.Region.Area).FirstOrDefault(c=>parts.Any(n=>n.Near==c.Near&&n.Road!=c.Road&&Shares(c.Region,n.Region)));
            if(small.Region is null)return new(roadParts,landParts,changes.ToArray());
            changes.Add(new(small.Region,!small.Road));
        }
        static bool Shares(ExactNearRegions.Component a,ExactNearRegions.Component b)
        {
            foreach(var x in a.Rings.SelectMany(r=>r.Edges))
            foreach(var y in b.Rings.SelectMany(r=>r.Edges))
                if(x.Start==y.End&&x.End==y.Start&&x.Source.Circle==y.Source.Circle)return true;
            return false;
        }
    }

    internal static (PathsD Roads, int Count) ResolveFragments(PathsD roads, IReadOnlyList<PathsD> zones, ModelPlanPolicy policy, CancellationToken ct=default)
    {
        double radius = policy.MinimumWidthMetres / 2 - Grid;
        var model = ContourPaths.UnionGeometry(roads);
        var domains = zones.Select(ContourPaths.UnionGeometry).ToArray();
        int resolved = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var components = Partition(model);
            var fragments = components.Where(component => component.Area < policy.MinimumAreaSquareMetres ||
                    IsNarrow(component.Paths, component.Area, radius))
                .OrderBy(component => component.Area).ThenBy(component => component.Zone).ThenBy(component => component.Road)
                .ThenBy(component => component.Paths[0][0].x).ThenBy(component => component.Paths[0][0].y).ToArray();
            bool changed = false;
            foreach (var fragment in fragments)
            {
                var boundary = fragment.Region.Boundary;
                var neighbour = components.Where(component => component.Zone == fragment.Zone && component.Road != fragment.Road)
                    .Select(component => (Component: component,
                        Shared: ContourPaths.OverlayGeometry(boundary, component.Region.Boundary, SpatialFunction.Intersection).Length))
                    .Where(candidate => candidate.Shared > 1)
                    .OrderByDescending(candidate => candidate.Shared).ThenByDescending(candidate => candidate.Component.Area)
                    .ThenBy(candidate => candidate.Component.Paths[0][0].x).ThenBy(candidate => candidate.Component.Paths[0][0].y)
                    .FirstOrDefault();
                // A whole protected zone has no neighbour and retains its boundary.
                if (neighbour.Component is null) continue;
                model = ContourPaths.OverlayGeometry(model,fragment.Region,
                    fragment.Road ? SpatialFunction.Difference : SpatialFunction.Union);
                resolved++; changed = true; break;
            }
            // Every reassignment reduces the edge-connected component count.
            if (!changed) return (ContourPaths.FromGeometry(model), resolved);
        }
        List<Component> Partition(Geometry region)
        {
            var values = new List<Component>();
            // Keep every grid-resolvable component until role reassignment.
            // Discarding a thin ring while constructing a PolyTree would leave
            // that fragment in the original model without ever inspecting it.
            for (int zone = 0; zone < zones.Count; zone++)
            foreach (bool road in new[] { false, true })
            foreach (var component in ContourPaths.Polygons(road
                ? ContourPaths.OverlayGeometry(region,domains[zone],SpatialFunction.Intersection)
                : ContourPaths.OverlayGeometry(domains[zone],region,SpatialFunction.Difference)))
                values.Add(new(zone, road, component));
            return values;
        }
    }

    // One set of arc subdivisions supplies both the planar query boundary
    // and every reassigned role. Independently sampling each curved operand
    // creates artificial slivers between different inscribed chords.
    private sealed class BoundarySamples
    {
        private readonly Dictionary<EditBoundarySegment,List<(double T,EditBoundaryPoint Point)>> samples=new();
        internal PathsD Near { get; }
        internal BoundarySamples(EditRegion region,IEnumerable<ExactNearRegions.Component> changes)
        {
            var cuts=region.Boundaries.SelectMany(loop=>loop.Segments).ToDictionary(
                edge=>edge,edge=>new SortedDictionary<double,EditBoundaryPoint>{{0,edge.Start},{1,edge.End}});
            foreach(var edge in changes.SelectMany(c=>c.Rings).SelectMany(r=>r.Edges))
                if(cuts.TryGetValue(edge.Source,out var values))
                {values[edge.From]=edge.Start;values[edge.To]=edge.End;}
            foreach(var (edge,values) in cuts)
            {
                var result=new List<(double,EditBoundaryPoint)>();var points=values.ToArray();
                for(int i=1;i<points.Length;i++)
                {
                    double from=points[i-1].Key,to=points[i].Key;
                    int count=edge.Circle is {} circle?Math.Max(1,(int)Math.Ceiling(edge.SweepRadians*(to-from)/
                        (2*Math.Acos(1-Math.Min(Grid,circle.RadiusMetres/2)/circle.RadiusMetres)))):1;
                    result.Add((from,points[i-1].Value));
                    for(int j=1;j<count;j++)
                    {double t=from+(to-from)*j/count;result.Add((t,edge.PointAt(t)));}
                }
                result.Add((1,edge.End));samples.Add(edge,result);
            }
            Near=new(region.Boundaries.Select(loop=>new PathD(loop.Segments.SelectMany(edge=>samples[edge].SkipLast(1))
                .Select(p=>new PointD(p.Point.X,p.Point.Y)))));
        }
        internal PathsD Paths(ExactNearRegions.Component component)=>new(component.Rings.Select(ring=>
        {
            var path=new PathD();
            foreach(var edge in ring.Edges)
            {
                IEnumerable<(double T,EditBoundaryPoint Point)> points;
                if(samples.TryGetValue(edge.Source,out var known))
                    points=edge.From<edge.To?known.Where(p=>p.T>=edge.From&&p.T<edge.To):
                        known.AsEnumerable().Reverse().Where(p=>p.T<=edge.From&&p.T>edge.To);
                else points=new[]{(edge.From,edge.Start)};
                foreach(var p in points)path.Add(new(p.Point.X,p.Point.Y));
            }
            return path;
        }));
    }

    private static bool IsNarrow(PathsD paths, double area, double radius)
    {
        if (2 * area / Perimeter(paths) >= 2 * radius) return false;
        var factory = new GeometryFactory();
        LinearRing Ring(PathD path) => factory.CreateLinearRing(path.Append(path[0])
            .Select(point => new Coordinate(point.x, point.y)).ToArray());
        var polygon = factory.CreatePolygon(Ring(paths[0]), paths.Skip(1).Select(Ring).ToArray());
        return polygon.Buffer(-radius).IsEmpty;
    }

    private static PathsD Union(PathsD paths) => ContourPaths.Union(paths);
    private static PathsD Intersect(PathsD a, PathsD b) => ContourPaths.Intersect(a, b);
    private static PathsD Xor(PathsD a, PathsD b) => ContourPaths.FromGeometry(ContourPaths.OverlayGeometry(
        ContourPaths.UnionGeometry(a),ContourPaths.UnionGeometry(b),SpatialFunction.SymDifference));
    private static double Area(PathsD paths) => Math.Abs(Clipper.Area(paths));
    private static double Perimeter(PathsD paths) => paths.Sum(path => Enumerable.Range(0, path.Count).Sum(index =>
    {
        var a = path[index]; var b = path[(index + 1) % path.Count];
        return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
    }));
}
