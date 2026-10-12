using Clipper2Lib;
using System.Runtime.CompilerServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;

namespace Aha.Topo;

/// <summary>Owned normalized regions for one contour calculation. Returned paths
/// are read-only inputs; normalized values live only as long as their paths.</summary>
internal sealed class ContourRegions
{
    private sealed class Value(Geometry geometry)
    {
        internal readonly Geometry Geometry=geometry;
        internal PathsD? Omitted;
        internal PathsD? Canonical;
        internal NetTopologySuite.Geometries.Prepared.IPreparedGeometry? Boundary;
    }
    // A region lives with its paths, not with every intermediate ever computed
    // during a preparation. Raw inputs must not be changed after registration.
    private readonly ConditionalWeakTable<PathsD,Value> owned = new();
    private Value Get(PathsD paths)=>owned.GetValue(paths,p=>new(ContourPaths.UnionGeometry(p)));
    internal Geometry Region(PathsD paths)=>Get(paths).Geometry;
    internal PathsD Keep(Geometry region)
    {
        var paths=ContourPaths.FromGeometry(region);owned.Add(paths,new(region){Canonical=paths});return paths;
    }
    internal PathsD Union(PathsD paths)
    {
        var value=Get(paths);
        return value.Canonical??=Keep(value.Geometry);
    }
    internal PathsD Union(PathsD a,PathsD b)=>a.Count==0?Union(b):b.Count==0?Union(a):
        Keep(ContourPaths.OverlayGeometry(Region(a),Region(b),SpatialFunction.Union));
    internal PathsD Intersect(PathsD a,PathsD b)=>a.Count==0||b.Count==0?new PathsD():
        Keep(ContourPaths.OverlayGeometry(Region(a),Region(b),SpatialFunction.Intersection));
    internal PathsD Difference(PathsD a,PathsD b)=>ReferenceEquals(a,b)||a.Count==0?new PathsD():b.Count==0?Union(a):
        Keep(ContourPaths.OverlayGeometry(Region(a),Region(b),SpatialFunction.Difference));
    // An already resolved role must not be opened again away from a new cut.
    // Offsets identify candidate details; only components incident to the
    // actual cutting boundary can be newly introduced by this operation.
    internal PathsD NewDetailOmissions(PathsD source, params PathsD[] cutters)
    {
        var boundaries=cutters.Where(p=>p.Count>0).Select(p=>
        {
            var value=Get(p);
            return value.Boundary??=NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(value.Geometry.Boundary);
        }).Where(g=>!g.Geometry.IsEmpty).ToArray();
        if(boundaries.Length==0)return new();
        var lost=Region(DetailOmissions(source));
        var selected=ContourPaths.Polygons(lost).Where(p=>boundaries.Any(b=>
            b.Geometry.EnvelopeInternal.Intersects(p.EnvelopeInternal)&&b.Intersects(p))).ToArray();
        return selected.Length==0?new():Keep(lost.Factory.CreateMultiPolygon(selected));
    }
    internal PathsD DetailOmissions(PathsD source)
    {
        var value=Get(source);
        if(value.Omitted is {} omitted)return omitted;
        const double radius=.05-ModelPlanPolicy.PrecisionMetres;
        // Compute the loss before rounding the opened contour back to the
        // model grid. Subtracting two separately rounded long edges creates
        // false wedges instead of the local detail which was actually removed.
        int offsetPrecision=Math.Min(8,EditRegion.ClipPrecision+2);
        var removed=new PathsD();
        foreach(var region in ContourPaths.Polygons(value.Geometry))
        {
            var component=ContourPaths.FromGeometry(region);
            if(region.Area*ModelPlanPolicy.PrecisionMetres*ModelPlanPolicy.PrecisionMetres<ModelPlanPolicy.Default.MinimumAreaSquareMetres)
            {removed.AddRange(component);continue;}
            var core=Clipper.InflatePaths(component,-radius,JoinType.Miter,EndType.Polygon,2,offsetPrecision);
            if(core.Count==0){removed.AddRange(component);continue;}
            var opened=Clipper.InflatePaths(core,radius,JoinType.Miter,EndType.Polygon,2,offsetPrecision);
            removed.AddRange(Clipper.Difference(component,opened,FillRule.NonZero,offsetPrecision));
        }
        // The one model-grid normalization is applied to the omission itself.
        // The original source remains the operand for actual output geometry.
        return value.Omitted=Union(removed);
    }
}
