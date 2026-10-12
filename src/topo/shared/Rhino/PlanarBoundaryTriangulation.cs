using Clipper2Lib;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

/// <summary>Triangulates a plane using only its boundary, retaining every shared boundary vertex.</summary>
internal static class PlanarBoundaryTriangulation
{
    internal static PathsD Build(PathsD outlines)
    {
        var triangles = PlanarTriangulation.TriangulateSource(outlines);
        // Polygon triangulators may omit a collinear boundary vertex. Restore
        // those exact points so the cap and existing wall edges remain atomic
        // counterparts, rather than accepting visually coincident T-junctions.
        var vertices = new STRtree<Coordinate>();
        foreach (var p in outlines.SelectMany(path => path).DistinctBy(p => (p.x, p.y)))
        {
            var point = new Coordinate(p.x, p.y);
            vertices.Insert(new Envelope(point), point);
        }
        vertices.Build();
        var result = new PathsD();
        foreach (var triangle in triangles)
        {
            var boundary = new PathD();
            for (int edge = 0; edge < 3; edge++)
            {
                var a = triangle[edge]; var b = triangle[(edge + 1) % 3];
                var from = new Coordinate(a.x, a.y); var to = new Coordinate(b.x, b.y);
                var points = vertices.Query(new Envelope(from, to))
                    .Where(p => Orientation.Index(from, to, p) == OrientationIndex.Collinear)
                    .OrderBy(p => a.x != b.x ? (p.X - a.x) / (b.x - a.x) : (p.Y - a.y) / (b.y - a.y));
                foreach (var p in points)
                    if (!p.Equals2D(to)) boundary.Add(new(p.X, p.Y));
            }
            if (boundary.Count == 3) result.Add(triangle);
            else
            {
                // Only triangles with omitted edge points need one interior
                // vertex. No terrain-grid interior vertices are copied.
                var center = new PointD(triangle.Average(p => p.x), triangle.Average(p => p.y));
                for (int i = 0; i < boundary.Count; i++)
                    result.Add(new() { center, boundary[i], boundary[(i + 1) % boundary.Count] });
            }
        }
        return result;
    }
}
