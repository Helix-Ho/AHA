using System;
using System.Collections.Generic;
using System.Linq;

namespace Aha.Topo;

public sealed record GeoPoint(double Lon, double Lat);

public sealed record GeoBounds(double West, double South, double East, double North)
{
    public GeoBounds[] Split()
    {
        Validate();
        var x = (West + East) / 2;
        var y = (South + North) / 2;
        return new[]
        {
            new GeoBounds(West, South, x, y), new GeoBounds(x, South, East, y),
            new GeoBounds(West, y, x, North), new GeoBounds(x, y, East, North)
        };
    }

    public void Validate()
    {
        if (!double.IsFinite(West) || !double.IsFinite(South) ||
            !double.IsFinite(East) || !double.IsFinite(North) ||
            West < -180 || East > 180 || South < -90 || North > 90 ||
            West >= East || South >= North)
            throw new ArgumentException("조회 범위의 경도·위도가 올바르지 않습니다.");
    }
}

/// <summary>
/// WGS84 origin-centred east/north coordinates, in metres. Uses the ellipsoid's
/// meridional and prime-vertical radii at the origin. This local approximation
/// is not a survey coordinate system or a registered Korean map CRS.
/// </summary>
public sealed class LocalProjection
{
    private const double Radians = Math.PI / 180;
    private const double SemiMajor = 6378137;
    private const double EccentricitySquared = 0.0066943799901413165;
    private readonly double metresPerLonDegree;
    private readonly double metresPerLatDegree;
    public GeoPoint Origin { get; }

    public LocalProjection(GeoPoint origin)
    {
        Geo.Validate(origin);
        if (Math.Abs(origin.Lat) >= 85)
            throw new ArgumentException("이 지역 좌표 변환은 극지방을 지원하지 않습니다.", nameof(origin));
        Origin = origin;
        var latitude = origin.Lat * Radians;
        var denominator = 1 - EccentricitySquared * Math.Pow(Math.Sin(latitude), 2);
        metresPerLonDegree = SemiMajor / Math.Sqrt(denominator) * Math.Cos(latitude) * Radians;
        metresPerLatDegree = SemiMajor * (1 - EccentricitySquared) /
                             Math.Pow(denominator, 1.5) * Radians;
    }

    public (double X, double Y) ToLocal(GeoPoint point)
    {
        Geo.Validate(point);
        return ((point.Lon - Origin.Lon) * metresPerLonDegree,
                (point.Lat - Origin.Lat) * metresPerLatDegree);
    }

    public GeoPoint ToGeo(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentException("지역 좌표가 유한한 숫자가 아닙니다.");
        var point = new GeoPoint(Origin.Lon + x / metresPerLonDegree,
                                 Origin.Lat + y / metresPerLatDegree);
        Geo.Validate(point);
        return point;
    }

    /// <summary>Square query bounds extending radius metres in each direction.</summary>
    public GeoBounds Bounds(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "조회 반경은 양수여야 합니다.");
        var sw = ToGeo(-radius, -radius);
        var ne = ToGeo(radius, radius);
        var bounds = new GeoBounds(sw.Lon, sw.Lat, ne.Lon, ne.Lat);
        bounds.Validate();
        return bounds;
    }
}

public enum BuildingClassificationStatus
{
    NotRequested, Verified, MissingIdentity, NotFound, UnknownCode
}

/// <summary>Optional descriptive attributes supplied with the building footprint.</summary>
public sealed record BuildingDetails(string Name, string PartName, string EnglishName,
    string Province, string CityDistrict, string Town, string RoadName, string BuildingNumber)
{
    // An incomplete address stays as its supplied components, not a guessed full address.
    public string RoadAddress => string.IsNullOrWhiteSpace(Province) || string.IsNullOrWhiteSpace(CityDistrict) ||
        string.IsNullOrWhiteSpace(RoadName) || string.IsNullOrWhiteSpace(BuildingNumber) ? "" :
        string.Join(" ", new[] { Province, CityDistrict, Town, RoadName, BuildingNumber }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
}

public sealed class GeoFeature
{
    public string Id { get; init; } = "";
    public string SourceLayer { get; init; } = "";
    public string BuildingId { get; init; } = "";
    public BuildingDetails? BuildingDetails { get; init; }
    // Official building-position code; independent of heights and the parcel land category.
    public string BuildingPositionCode { get; init; } = "";
    public BuildingClassificationStatus BuildingClassification { get; init; }
    public double? UndergroundFloors { get; init; }
    public string Pnu { get; init; } = "";
    public string Address { get; init; } = "";
    // Preserve the source label. PNU does not encode the land category.
    public string ParcelLabel { get; init; } = "";
    public int? LandCategoryCode { get; init; }
    // Polygon -> rings (exterior first, then holes) -> longitude/latitude points.
    public List<List<List<GeoPoint>>> Polygons { get; init; } = new();
    // Raw source values: zero is retained, so the caller can label its fallback.
    public double? Height { get; init; }
    public double? Floors { get; init; }

}

public sealed record AddressCandidate(string Label, string Pnu, GeoPoint Location)
{
    public override string ToString() => Label;
}

public static class Geo
{
    public static void Validate(GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.Lon) || !double.IsFinite(point.Lat) ||
            point.Lon < -180 || point.Lon > 180 || point.Lat < -90 || point.Lat > 90)
            throw new ArgumentException("경도·위도가 올바르지 않습니다.");
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> ring)
    {
        if (ring.Count < 3) return 0;
        // Translate before summing to avoid cancellation for large coordinates.
        var origin = ring[0];
        double twiceArea = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            twiceArea += (a.X - origin.X) * (b.Y - origin.Y) -
                         (b.X - origin.X) * (a.Y - origin.Y);
        }
        return twiceArea / 2;
    }

    public static double Area(IReadOnlyList<List<GeoPoint>> polygon, LocalProjection projection)
    {
        if (polygon.Count == 0) return 0;
        var area = Math.Abs(SignedArea(polygon[0].Select(projection.ToLocal).ToArray()));
        for (var i = 1; i < polygon.Count; i++)
            area -= Math.Abs(SignedArea(polygon[i].Select(projection.ToLocal).ToArray()));
        return Math.Max(0, area);
    }

    public static double Area(GeoFeature feature, LocalProjection projection) =>
        feature.Polygons.Sum(polygon => Area(polygon, projection));
}
