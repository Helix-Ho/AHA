using Clipper2Lib;

namespace Aha.Topo;

/// <summary>One preparation's exposed tops. A changed plate invalidates only
/// itself and tops covered by it; finished passes release obsolete entries.</summary>
internal sealed class ContourExposureCache
{
    private sealed record Entry(ContourPlate[] Above,ContourExposure? Exposure);
    private IReadOnlyList<ExactNearRegions.Component>? siteRegions;
    private Dictionary<ContourPlate,Entry> previous=new(ReferenceEqualityComparer.Instance);

    internal IReadOnlyList<ContourExposure> Build(IReadOnlyList<ContourPlate> plates,PathsD sites,
        ExactNearRegions exactNear,ContourRegions regions)
    {
        siteRegions??=exactNear.Split(regions.Union(sites),includeFar:false).Near;
        var next=new Dictionary<ContourPlate,Entry>(ReferenceEqualityComparer.Instance);
        var result=new List<ContourExposure>();
        var starts=plates.OrderBy(p=>p.Bottom).ThenBy(p=>p.Top).ThenBy(p=>p.Role,StringComparer.Ordinal).ToArray();
        var ends=plates.OrderBy(p=>p.Top).ToArray();
        var active=new HashSet<ContourPlate>(ReferenceEqualityComparer.Instance);
        int start=0,end=0;
        foreach(var level in plates.GroupBy(p=>p.Top).OrderBy(g=>g.Key))
        {
            while(start<starts.Length&&starts[start].Bottom<=level.Key)active.Add(starts[start++]);
            while(end<ends.Length&&ends[end].Top<=level.Key)active.Remove(ends[end++]);
            var candidates=active.OrderBy(p=>p.Bottom).ThenBy(p=>p.Top).ThenBy(p=>p.Role,StringComparer.Ordinal).ToArray();
            foreach(var plate in level)
            {
                var material=plate.Exact??exactNear.Split(plate.SourceProfiles,plate.Excluded,includeFar:false).Near;
                var covering=candidates.Where(above=>above.Exact is {} coverRegions&&
                    coverRegions.Any(cover=>material.Any(part=>cover.Bounds.Intersects(part.Bounds)))).ToArray();
                if(previous.TryGetValue(plate,out var cached)&&cached.Above.Length==covering.Length&&
                    covering.All(above=>cached.Above.Contains(above,ReferenceEqualityComparer.Instance)))
                {next.Add(plate,cached);if(cached.Exposure is {} reused)result.Add(reused);continue;}
                var source=plate.SourceProfiles;
                var excluded=covering.SelectMany(p=>p.Exact??Array.Empty<ExactNearRegions.Component>())
                    .Concat(plate.Role=="Base"?siteRegions:Array.Empty<ExactNearRegions.Component>()).ToArray();
                var exact=SubtractCovered(material,excluded,exactNear);
                var profiles=new PathsD(exact.SelectMany(c=>c.Sample()));
                ContourExposure? exposure=profiles.Count==0?null:new(plate.Role,profiles,source,plate.Bottom,plate.Top)
                    {Exact=exact,Excluded=excluded,Regions=regions,Boundary=exactNear};
                next.Add(plate,new(covering,exposure));if(exposure is not null)result.Add(exposure);
            }
        }
        previous=next;
        return result.OrderBy(e=>e.Top).ThenBy(e=>e.Role,StringComparer.Ordinal).ToArray();
    }
    internal static IReadOnlyList<ExactNearRegions.Component> SubtractCovered(
        IReadOnlyList<ExactNearRegions.Component> material,IReadOnlyList<ExactNearRegions.Component> covers,ExactNearRegions boundary)
    {
        var result=new List<ExactNearRegions.Component>();
        foreach(var component in material)
        {
            var box=component.Bounds;
            var masks=covers.Where(c=>c.Bounds.Intersects(box)).ToArray();
            if(masks.Length==0)result.Add(component);
            else
            {
                // This rectangle only bounds the arrangement. The included
                // component supplies all actual finite edges and holes.
                var domain=ContourPaths.Rectangle(box.MinX-1,box.MinY-1,box.MaxX+1,box.MaxY+1);
                result.AddRange(boundary.Split(domain,masks,included:component,includeFar:false).Near);
            }
        }
        return result;
    }

}
