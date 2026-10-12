using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>A continuous design-site body above the contour base; shares terrain patches and closing.</summary>
internal static class SiteGeometry
{
    internal static IReadOnlyList<Brep> Build(TerrainField field, SiteComponent site,
        double bottom, double scale, double tolerance, TerrainSupportSurfaces? supports = null)
    {
        var paths = SiteSelection.FeaturePaths(site.Boundary, field.Projection);
        var bounds = Clipper.GetBounds(paths);
        var low = field.ModelCell(bounds.left, bounds.top);
        var high = field.ModelCell(bounds.right, bounds.bottom);
        var window = new RectD(field.ModelX(low.Column), field.ModelY(low.Row),
            field.ModelX(high.Column + 1), field.ModelY(high.Row + 1));
        var sheets = TerrainSurface.Prepare(field, new RoadArea(new PathsD(), 0), 0,
            useNurbs: true, clip: paths, localBounds: window).Materialize(scale, tolerance, supports);
        try
        {
            var solid = NearTerrainSolid.Build(sheets.Pieces, 1, scale, tolerance, true, bottom);
            return solid.Pieces.Select(piece => (Brep)piece.Geometry).ToArray();
        }
        finally { foreach (var sheet in sheets.Pieces) sheet.Geometry.Dispose(); }
    }
}
