using Clipper2Lib;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class PlanBoundarySourcesChecks
{
    internal static int Run()
    {
        int count = 0;
        var witness = FixtureData.Witness("plan_boundary");
        static PointD Point(JsonElement p) => new(p[0].GetDouble(), p[1].GetDouble());
        var corridorOrigin = Point(witness.GetProperty("corridor"));
        Check(PlanBoundarySources.WithinGraphCorridor(corridorOrigin,new(corridorOrigin.x + .000002,corridorOrigin.y)),
            "an exact two-grid-unit displacement is inside the existing corridor");
        Check(!PlanBoundarySources.WithinGraphCorridor(corridorOrigin,new(corridorOrigin.x + .000003,corridorOrigin.y)),
            "three grid units cannot be accepted by arithmetic correction");
        Check(!PlanBoundarySources.WithinGraphCorridor(corridorOrigin,new(corridorOrigin.x + .0000021,corridorOrigin.y)),
            "a non-grid displacement is not snapped into the corridor");
        // Graph-only nodes on a finite source side must not become source corners.
        var footprint = new PathD(witness.GetProperty("footprint").EnumerateArray().Select(Point));
        var inserted = witness.GetProperty("graph_insert").EnumerateArray().Select(Point).ToArray();
        var graph = new PathD(footprint.Take(8).Concat(inserted).Append(footprint[^1]));
        foreach (bool reverse in new[] { false, true })
        foreach (var translation in new[] { (X: 0d, Y: 0d, Rotate: false),
            (X: 137.123456, Y: -98.765432, Rotate: true), (X: 10000d, Y: -20000d, Rotate: false) })
        {
            PointD Map(PointD p) => new((translation.Rotate ? -p.y : p.x) + translation.X,
                (translation.Rotate ? p.x : p.y) + translation.Y);
            var sourcePath = new PathD((reverse ? footprint.AsEnumerable().Reverse() : footprint).Select(Map));
            var graphPath = new PathD((reverse ? graph.AsEnumerable().Reverse() : graph).Select(Map));
            var sources = new PlanBoundarySources();
            var originalOwners = Enumerable.Range(0, footprint.Count)
                .Select(i => sources.Add(Map(footprint[i]), Map(footprint[(i + 1) % footprint.Count]))).ToArray();
            for (int i = 0; i < graph.Count; i++)
                sources.AddGraphSegment(Map(graph[i]), Map(graph[(i + 1) % graph.Count]),
                    originalOwners[i < 7 ? i : i < 10 ? 7 : 8]);
            var restored = sources.Restore(new PathsD { graphPath }).Single();
            Check(restored.Count == sourcePath.Count, "graph-only Near nodes do not create source corners");
            foreach (var point in sourcePath)
                Check(restored.Any(p => Distance(p, point) < 1e-9), "original finite source corners survive reconstruction");
            var firstOwner = sources.Owner(Map(inserted[0]), Map(inserted[1]));
            var sideOwner = sources.Owner(Map(inserted[1]), Map(footprint[^1]));
            Check(firstOwner.Id == sideOwner.Id, "the short graph recrossing retains the source side owner");
        }
        var provenance = new PlanBoundarySources();
        var original = provenance.Add(new(0, 0), new(10, 0));
        var closeTangent = provenance.Add(new(5, -.000001), new(5, .1));
        var nearChord = provenance.Add(new(5, -.000001), new(6, .1), PlanBoundarySources.SourceKind.Near);
        provenance.AddGraphSegment(new(5, -.000001), new(5.000001, -.000001), original);
        provenance.AddGraphSegment(new(5, -.000001), new(5, .1), closeTangent);
        provenance.AddGraphSegment(new(5, -.000001), new(5.000001, -.000001), nearChord);
        Check(provenance.Owner(new(5, -.000001), new(5.000001, -.000001)).Id == original.Id,
            "noded source context survives a nearer adjacent tangent and overlapping Near chord");
        Check(provenance.Owner(new(5.00000025, -.000001), new(5.00000075, -.000001)).Id == original.Id,
            "mesh refinement inherits its containing noded source edge");
        bool missing = false;
        try { provenance.Owner(new(5, -.000001), new(5.01, -.000001)); }
        catch (InvalidOperationException) { missing = true; }
        Check(missing, "an unowned edge cannot borrow a nearby original source");
        var shared = new PlanBoundarySources();
        var grid = shared.Add(new(0, 0), new(10, 0), PlanBoundarySources.SourceKind.SamplingGrid);
        var footprintSide = shared.Add(new(0, 0), new(10, .000001));
        shared.AddGraphSegment(new(0, 0), new(5, 0), grid);
        shared.AddGraphSegment(new(0, 0), new(5, 0), footprintSide);
        Check(shared.Owner(new(0, 0), new(5, 0)).Id == footprintSide.Id,
            "a snapped DEM sampling edge retains the actual source side as its boundary");
        var ambiguous = new PlanBoundarySources();
        var lowerParallel = ambiguous.Add(new(0, 0), new(10, 0));
        var upperParallel = ambiguous.Add(new(0, .000001), new(10, .000001));
        ambiguous.AddGraphSegment(new(0, 0), new(10, 0), lowerParallel);
        ambiguous.AddGraphSegment(new(0, 0), new(10, 0), upperParallel);
        bool conflict = false;
        try { ambiguous.Owner(new(2, 0), new(4, 0)); }
        catch (InvalidOperationException) { conflict = true; }
        Check(conflict, "conflicting retained supports fail instead of silently selecting one");
        // A one-micrometre source connector retains ownership through graph noding.
        foreach (bool reverse in new[] { false, true })
        foreach (var transform in new[] { (X: 0d, Y: 0d, Rotate: false),
            (X: 10000d, Y: -20000d, Rotate: true) })
        {
            PointD Map(PointD p) => new((transform.Rotate ? -p.y : p.x) + transform.X,
                (transform.Rotate ? p.x : p.y) + transform.Y);
            var connectorPoints = witness.GetProperty("connector").EnumerateArray().Select(p => Map(Point(p))).ToArray();
            var a = connectorPoints[0]; var b = connectorPoints[1];
            var c = connectorPoints[2]; var d = connectorPoints[3];
            var connectorSources = new PlanBoundarySources();
            var previous = connectorSources.Add(a, b, PlanBoundarySources.SourceKind.Road);
            var connector = connectorSources.Add(b, c, PlanBoundarySources.SourceKind.Road);
            var next = connectorSources.Add(c, d, PlanBoundarySources.SourceKind.Road);
            connectorSources.AddGraphSegment(c, b, previous);
            connectorSources.AddGraphSegment(b, c, connector);
            connectorSources.AddGraphSegment(c, b, next);
            var firstCorner = PlanBoundarySources.Intersection(previous, connector)!.Value;
            var lastCorner = PlanBoundarySources.Intersection(connector, next)!.Value;
            Check(firstCorner.x == b.x && firstCorner.y == b.y, "the first connector corner keeps its exact source coordinate");
            Check(lastCorner.x == c.x && lastCorner.y == c.y, "the last connector corner keeps its exact source coordinate");
            Check(connectorSources.Owner(reverse ? c : b, reverse ? b : c).Id == connector.Id,
                "the original finite connector owns the shared hot-pixel span");
            var firstQuarter = new PointD(b.x + (c.x - b.x) / 4, b.y + (c.y - b.y) / 4);
            var lastQuarter = new PointD(b.x + 3 * (c.x - b.x) / 4, b.y + 3 * (c.y - b.y) / 4);
            Check(connectorSources.Owner(reverse ? lastQuarter : firstQuarter, reverse ? firstQuarter : lastQuarter).Id == connector.Id,
                "connector ownership survives direct display refinement");
        }
        var axes = new PlanBoundarySources();
        var vertical = axes.Add(new(0, -20), new(0, 20), PlanBoundarySources.SourceKind.SamplingGrid);
        var slant = axes.Add(new(-1.372166, -8.562893), new(.002862, -7.630953));
        var forwardIntersection = PlanBoundarySources.Intersection(vertical, slant)!.Value;
        var reverseIntersection = PlanBoundarySources.Intersection(slant, vertical)!.Value;
        Check(forwardIntersection.x == 0 && reverseIntersection.x == 0,
            "an original axis coordinate remains exactly zero at a shallow source intersection");
        Check(forwardIntersection.x == reverseIntersection.x && forwardIntersection.y == reverseIntersection.y,
            "both incident faces use the identical canonical source intersection");
        return count;

        void Check(bool passed, string message)
        {
            count++;
            if (!passed) throw new InvalidOperationException("Plan boundary sources: " + message);
        }
        static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2));
    }
}
