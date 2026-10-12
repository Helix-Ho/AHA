using Clipper2Lib;

namespace Aha.Topo;

/// <summary>Roof-edge bands, including courtyard edges, in local metres.</summary>
internal static class ParapetProfiles
{
    internal static IReadOnlyList<PathsD> Create(PathsD roof, double width)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentException("파라펫 폭은 양의 유한값이어야 합니다.");
        var domain = ContourPaths.UnionGeometry(roof);
        var paths = ContourPaths.FromGeometry(domain);
        var bounds = Clipper.GetBounds(paths);
        // An inset this large has no interior. Avoid overflowing the offset
        // engine for a finite width larger than the entire roof.
        var interior = width >= Math.Max(bounds.Width, bounds.Height) ? new PathsD()
            : Clipper.InflatePaths(paths, -width, JoinType.Miter, EndType.Polygon, 2, EditRegion.ClipPrecision);
        var band = interior.Count == 0 ? domain : ContourPaths.OverlayGeometry(domain,
            ContourPaths.UnionGeometry(interior), NetTopologySuite.Operation.Overlay.SpatialFunction.Difference);
        if (band.IsEmpty)
            throw new ArgumentException("파라펫 폭으로 기존 평면 정밀도에서 양의 단면을 만들 수 없습니다.");
        return ContourPaths.Polygons(band).Select(PlanarTriangulation.FromGridPolygon).SelectMany(ProfileContact.Split).ToArray();
    }
}
