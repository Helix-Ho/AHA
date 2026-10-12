using Clipper2Lib;
using NetTopologySuite.Geometries;

namespace Aha.Topo;

public sealed record SiteComponent(GeoFeature Boundary, GeoPoint Centre, double RadiusMetres,
    IReadOnlyList<GeoFeature> Parcels);

/// <summary>Selected source parcels and their dissolved, independently sized design sites.</summary>
public sealed class SiteSelection
{
    public const double MinimumNearRatio = 1.5;
    public IReadOnlyList<GeoFeature> Parcels { get; }
    public GeoFeature Boundary { get; }
    public IReadOnlyList<SiteComponent> Components { get; }

    private SiteSelection(IReadOnlyList<GeoFeature> parcels, GeoFeature boundary,
        IReadOnlyList<SiteComponent> components)
    { Parcels = parcels; Boundary = boundary; Components = components; }

    public static SiteSelection Create(IReadOnlyList<GeoFeature> parcels, LocalProjection projection)
    {
        ArgumentNullException.ThrowIfNull(parcels);
        ArgumentNullException.ThrowIfNull(projection);
        if (parcels.Count == 0) throw new ArgumentException("대상 필지를 하나 이상 선택하세요.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pnus = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<GeoFeature>(ReferenceEqualityComparer.Instance);
        var source = new PathsD();
        var parcelPaths = new List<PathsD>();
        foreach (var parcel in parcels)
        {
            ArgumentNullException.ThrowIfNull(parcel);
            if (!references.Add(parcel) || (!string.IsNullOrEmpty(parcel.Id) && !ids.Add(parcel.Id)) ||
                (!string.IsNullOrEmpty(parcel.Pnu) && !pnus.Add(parcel.Pnu)))
                throw new ArgumentException("동일한 필지가 중복 선택되었습니다.");
            ValidateFeature(parcel, projection);
            var paths = FeaturePaths(parcel, projection);
            parcelPaths.Add(paths); source.AddRange(paths);
        }
        var clipper = new ClipperD(EditRegion.ClipPrecision);
        clipper.AddSubject(source);
        var tree = new PolyTreeD();
        if (!clipper.Execute(ClipType.Union, FillRule.NonZero, tree))
            throw new ArgumentException("선택 필지의 대지 경계를 합칠 수 없습니다.");
        var polygons = new List<PathsD>();
        Visit(tree);
        if (polygons.Count == 0) throw new ArgumentException("선택 필지의 유효한 대지 경계가 없습니다.");
        polygons = polygons.OrderBy(p => p[0].Min(v => v.x)).ThenBy(p => p[0].Min(v => v.y)).ToList();
        var components = new List<SiteComponent>();
        foreach (var paths in polygons)
        {
            var centre = Centroid(paths);
            double radius = paths[0].Max(p => Math.Sqrt(Math.Pow(p.x - centre.X, 2) + Math.Pow(p.y - centre.Y, 2)));
            if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentException("대지의 중심과 크기를 계산할 수 없습니다.");
            var members = parcels.Where((_, i) => Math.Abs(Clipper.Area(
                Clipper.Intersect(paths, parcelPaths[i], FillRule.NonZero, EditRegion.ClipPrecision))) > 1e-12).ToArray();
            var boundary = new GeoFeature { Id = "site-" + (components.Count + 1),
                SourceLayer = "Selected cadastral parcel union", Polygons = new() { GeoRings(paths, projection) } };
            components.Add(new(boundary, projection.ToGeo(centre.X, centre.Y), radius, members));
        }
        return new(parcels.ToArray(), new GeoFeature { Id = "selected-sites", SourceLayer = "Selected cadastral parcel union",
            Polygons = components.SelectMany(c => c.Boundary.Polygons).ToList() }, components);

        void Visit(PolyPathD parent)
        {
            for (int i = 0; i < parent.Count; i++)
            {
                var node = parent[i];
                if (!node.IsHole && node.Polygon is { Count: >= 3 } outer)
                {
                    var paths = new PathsD { outer };
                    for (int j = 0; j < node.Count; j++)
                        if (node[j].Polygon is { Count: >= 3 } hole) paths.Add(hole);
                    polygons.Add(paths);
                }
                Visit(node);
            }
        }
    }

    internal static PathsD FeaturePaths(GeoFeature feature, LocalProjection projection)
    {
        var polygons = new PathsD();
        foreach (var polygon in feature.Polygons)
        {
            if (polygon.Count == 0) continue;
            var outer = RingPath(polygon[0], projection);
            if (outer.Count < 3) continue;
            var holes = new PathsD(polygon.Skip(1).Select(r => RingPath(r, projection)).Where(r => r.Count >= 3));
            polygons.AddRange(holes.Count == 0 ? new PathsD { outer } :
                Clipper.Difference(new PathsD { outer }, holes, FillRule.NonZero, EditRegion.ClipPrecision));
        }
        return Clipper.BooleanOp(ClipType.Union, polygons, null, FillRule.NonZero, EditRegion.ClipPrecision);
    }

    internal static PathD RingPath(IReadOnlyList<GeoPoint> ring, LocalProjection projection)
    {
        var path = new PathD();
        foreach (var point in ring)
        {
            var xy = projection.ToLocal(point);
            var p = new PointD(Math.Round(xy.X, EditRegion.ClipPrecision), Math.Round(xy.Y, EditRegion.ClipPrecision));
            if (path.Count == 0 || p.x != path[^1].x || p.y != path[^1].y) path.Add(p);
        }
        if (path.Count > 1 && path[0].x == path[^1].x && path[0].y == path[^1].y) path.RemoveAt(path.Count - 1);
        if (Clipper.Area(path) < 0) path.Reverse();
        return path;
    }

    internal static void ValidateFeature(GeoFeature feature, LocalProjection projection)
    {
        var factory = new GeometryFactory();
        if (feature.Polygons.Count == 0) throw new ArgumentException("선택 영역에 유효한 경계가 없습니다.");
        foreach (var polygon in feature.Polygons)
        {
            if (polygon.Count == 0) throw new ArgumentException("선택 영역에 유효한 경계가 없습니다.");
            var shell = Ring(polygon[0]);
            var holes = polygon.Skip(1).Select(Ring).ToArray();
            var area = factory.CreatePolygon(shell, holes);
            if (!area.IsValid || area.Area <= 1e-12)
                throw new ArgumentException("경계가 교차하거나 유효한 면적을 이루지 않습니다. 경계를 확인하거나 다시 그리세요.");
        }
        LinearRing Ring(IReadOnlyList<GeoPoint> input)
        {
            var path = RingPath(input, projection);
            if (path.Count < 3) throw new ArgumentException("영역은 서로 다른 세 점 이상의 닫힌 경계여야 합니다.");
            return factory.CreateLinearRing(path.Append(path[0]).Select(p => new Coordinate(p.x, p.y)).ToArray());
        }
    }

    private static List<List<GeoPoint>> GeoRings(PathsD paths, LocalProjection projection) => paths
        .Select(path => path.Append(path[0]).Select(p => projection.ToGeo(p.x, p.y)).ToList()).ToList();

    private static (double X, double Y) Centroid(PathsD paths)
    {
        var anchor = paths[0][0];
        double sum = 0, x = 0, y = 0;
        for (int r = 0; r < paths.Count; r++)
        {
            var path = paths[r]; double twice = 0, cx = 0, cy = 0;
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Count];
                double ax = a.x - anchor.x, ay = a.y - anchor.y, bx = b.x - anchor.x, by = b.y - anchor.y;
                double cross = ax * by - bx * ay;
                twice += cross; cx += (ax + bx) * cross; cy += (ay + by) * cross;
            }
            if (Math.Abs(twice) < 1e-12) continue;
            double weight = Math.Abs(twice) * (r == 0 ? 1 : -1);
            sum += weight; x += cx / (3 * twice) * weight; y += cy / (3 * twice) * weight;
        }
        if (sum <= 0) throw new ArgumentException("대지 면적이 유효하지 않습니다.");
        return (anchor.x + x / sum, anchor.y + y / sum);
    }
}
