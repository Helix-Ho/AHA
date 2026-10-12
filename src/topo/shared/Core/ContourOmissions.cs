using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Quadtree;
using NetTopologySuite.Operation.Overlay;

namespace Aha.Topo;

/// <summary>Omissions accumulated in height order during one plate pass.
/// Disjoint components stay separate; a later plate queries only its extent.</summary>
internal sealed class ContourOmissions(ContourRegions regions)
{
    private readonly Quadtree<Geometry> straight=new();
    private readonly Quadtree<ExactNearRegions.Component> curved=new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Geometry,ExactMasks> exact=new();
    private sealed record ExactMasks(IReadOnlyList<ExactNearRegions.Component> Regions);

    internal void Add(PathsD paths)
    {
        foreach(var polygon in ContourPaths.Polygons(regions.Region(paths)))
        {
            Geometry merged=polygon;
            // Existing entries are disjoint. Only neighbours of new material
            // participate in a union; distant omissions remain available.
            foreach(var previous in straight.Query(polygon.EnvelopeInternal))
            {
                if(!previous.Intersects(polygon))continue;
                straight.Remove(previous.EnvelopeInternal,previous);
                merged=ContourPaths.OverlayGeometry(merged,previous,SpatialFunction.Union);
            }
            straight.Insert(merged.EnvelopeInternal,merged);
        }
    }
    internal PathsD Masks(PathsD source)
    {
        var region=regions.Region(source);
        return regions.Keep(region.Factory.CreateMultiPolygon(straight.Query(region.EnvelopeInternal)
            .Where(g=>g.EnvelopeInternal.Intersects(region.EnvelopeInternal)).SelectMany(ContourPaths.Polygons).ToArray()));
    }
    internal PathsD Subtract(PathsD source)
    {
        var region=regions.Region(source);
        var candidates=straight.Query(region.EnvelopeInternal)
            .Where(g=>g.EnvelopeInternal.Intersects(region.EnvelopeInternal)).ToArray();
        if(candidates.Length==0)return source;
        var mask=region.Factory.CreateMultiPolygon(candidates.SelectMany(ContourPaths.Polygons).ToArray());
        return regions.Keep(ContourPaths.OverlayGeometry(region,mask,SpatialFunction.Difference));
    }
    internal IReadOnlyList<ExactNearRegions.Component> MaterialMasks(PathsD source,ExactNearRegions boundary)
    {
        var region=regions.Region(source);
        return straight.Query(region.EnvelopeInternal).Where(g=>g.EnvelopeInternal.Intersects(region.EnvelopeInternal))
            .SelectMany(g=>exact.GetValue(g,x=>new(boundary.Split(ContourPaths.FromGeometry(x),includeFar:false).Near)).Regions).ToArray();
    }
    internal void Add(ExactNearRegions.Component component)=>curved.Insert(component.Bounds,component);
    internal ExactNearRegions.Component[] Query(PathsD source)
    {
        var b=Clipper.GetBounds(source);
        var envelope=new Envelope(b.left,b.right,b.top,b.bottom);
        return curved.Query(envelope).Where(c=>c.Bounds.Intersects(envelope)).ToArray();
    }
}

/// <summary>A completed step owns only its contribution and its output. State
/// identity represents the cumulative omissions entering/leaving that step.</summary>
internal sealed record ContourPlateStep(PathsD Input,PathsD Holes,object Before,object After,
    PathsD Omitted,IReadOnlyList<ExactNearRegions.Component> ExactOmitted,ContourPlate? Plate);

internal sealed record ExactRegionValue(IReadOnlyList<ExactNearRegions.Component> Regions);
