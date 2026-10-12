using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>Positive-area footprint groups on the shared model precision grid.</summary>
internal static class BuildingOverlapGroups
{
    internal static IReadOnlyList<string[]> Create(IEnumerable<(string Id, PathsD Footprint)> source)
    {
        var items = source.ToArray();
        var parents = Enumerable.Range(0, items.Length).ToArray();
        var index = new STRtree<int>();
        var bounds = items.Select(item => Clipper.GetBounds(item.Footprint)).ToArray();
        for (int i = 0; i < items.Length; i++)
            index.Insert(new Envelope(bounds[i].left, bounds[i].right, bounds[i].top, bounds[i].bottom), i);
        for (int i = 0; i < items.Length; i++)
        foreach (int j in index.Query(new Envelope(bounds[i].left, bounds[i].right, bounds[i].top, bounds[i].bottom)).Where(j => j < i))
            if (ContourPaths.Area(ContourPaths.Intersect(items[i].Footprint, items[j].Footprint)) > 0)
                parents[Find(i)] = Find(j);
        return Enumerable.Range(0, items.Length).GroupBy(Find)
            .Select(group => group.Select(i => items[i].Id).ToArray()).ToArray();

        int Find(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
    }
}
