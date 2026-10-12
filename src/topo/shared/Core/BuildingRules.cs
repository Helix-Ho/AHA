using Clipper2Lib;

namespace Aha.Topo;

public sealed record ModelOptions(double UnknownHeight = 6, double FloorHeight = 3, double MinArea = 10,
    double NearRatio = 2, double RoadDropMetres = 0.2,
    bool UseNearRegion = true, double NearDepthMetres = 30,
    bool TerrainAsNurbs = false, bool BuildingsAsNurbs = false,
    bool SeparateRoads = true, bool LowerRoads = true, bool IncludeRoads = true,
    bool Contour = false, double ContourIntervalMetres = 1, bool SeparateFoundations = false,
    bool Parapets = true, double ParapetWidthMetres = .3, double ParapetHeightMetres = .3)
{
    public double EffectiveRoadDrop => IncludeRoads && SeparateRoads && LowerRoads ? RoadDropMetres : 0;
    public bool EffectiveContour => UseNearRegion && Contour;
    public bool EffectiveParapets => UseNearRegion && Parapets;
}

public sealed record HeightDecision(double Height, string Source);
public sealed class BuildingSourceException(string message, Exception? inner = null) : ArgumentException(message, inner);

/// <summary>Height fallback and area settings; no Rhino dependency.</summary>
public static class BuildingRules
{
    public static bool HasKnownPosition(GeoFeature feature) =>
        feature.BuildingClassification == BuildingClassificationStatus.Verified &&
        feature.BuildingPositionCode is "0" or "1";

    public static bool IsUnderground(GeoFeature feature) =>
        HasKnownPosition(feature) && feature.BuildingPositionCode == "1";

    // Both context output and ground acquisition use the same whole-footprint rules.
    // Invalid source geometry is reported by the output path rather than expanded into a query.
    public static bool IsEligibleFootprint(GeoFeature feature, LocalProjection projection,
        GeoBounds bounds, double minimumArea)
    {
        double area = ValidateFootprint(feature, projection);
        if (!IsUnderground(feature) && area < minimumArea) return false;
        var sw = projection.ToLocal(new(bounds.West, bounds.South));
        var ne = projection.ToLocal(new(bounds.East, bounds.North));
        var box = new PathsD { new PathD { new(sw.X, sw.Y), new(ne.X, sw.Y), new(ne.X, ne.Y), new(sw.X, ne.Y) } };
        return Math.Abs(Clipper.Area(Clipper.Intersect(SiteSelection.FeaturePaths(feature, projection), box,
            FillRule.NonZero, EditRegion.ClipPrecision))) > 1e-12;
    }

    internal static double ValidateFootprint(GeoFeature feature, LocalProjection projection)
    {
        try { SiteSelection.ValidateFeature(feature, projection); }
        catch (ArgumentException error) { throw new BuildingSourceException(error.Message, error); }
        double area = Geo.Area(feature, projection);
        if (!double.IsFinite(area) || area <= 0) throw new BuildingSourceException("invalid_footprint_area");
        return area;
    }

    public static void Validate(ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.ParapetWidthMetres) || options.ParapetWidthMetres <= 0 ||
            !double.IsFinite(options.ParapetHeightMetres) || options.ParapetHeightMetres <= 0)
            throw new ArgumentException("파라펫 폭과 높이는 양의 유한값이어야 합니다.");
        if (!double.IsFinite(options.UnknownHeight) || options.UnknownHeight <= 0 ||
            !double.IsFinite(options.FloorHeight) || options.FloorHeight <= 0 ||
            !double.IsFinite(options.MinArea) || options.MinArea < 0)
            throw new ArgumentException("높이 설정은 양수, 최소 면적은 0 이상이어야 합니다.");
        if (options.UseNearRegion && (!double.IsFinite(options.NearRatio) || options.NearRatio < SiteSelection.MinimumNearRatio))
            throw new ArgumentException("근접 영역을 지정할 때 배수는 1.5 이상의 유한한 값이어야 합니다.");
        if (options.UseNearRegion && (!double.IsFinite(options.NearDepthMetres) || options.NearDepthMetres <= 0))
            throw new ArgumentException("근접 영역 깊이는 0보다 큰 유한한 값이어야 합니다.");
        if (options.Contour && !options.UseNearRegion)
            throw new ArgumentException("Contour는 근접 영역을 켠 상태에서 사용할 수 있습니다.");
        if (options.Contour && (!double.IsFinite(options.ContourIntervalMetres) || options.ContourIntervalMetres <= 0))
            throw new ArgumentException("Contour 간격은 0보다 큰 유한한 값이어야 합니다.");
        if (options.Contour && options.NearDepthMetres / options.ContourIntervalMetres <= 2)
            throw new ArgumentException("Contour의 입체 깊이는 간격의 2배보다 커야 합니다.");
        if (options.IncludeRoads && options.SeparateRoads && options.LowerRoads &&
            (!double.IsFinite(options.RoadDropMetres) || options.RoadDropMetres < 0))
            throw new ArgumentException("도로 단차는 0 이상의 유한한 값이어야 합니다.");
    }

    public static HeightDecision Resolve(GeoFeature feature, ModelOptions options)
    {
        Validate(options);
        if (feature.Height is > 0 && double.IsFinite(feature.Height.Value))
            return new HeightDecision(feature.Height.Value, "source_height");
        if (feature.Floors is > 0 && double.IsFinite(feature.Floors.Value))
        {
            var height = feature.Floors.Value * options.FloorHeight;
            if (!double.IsFinite(height)) throw new ArgumentException("층수 기반 높이를 계산할 수 없습니다.");
            return new HeightDecision(height, "floor_estimate");
        }
        return new HeightDecision(options.UnknownHeight, "default_estimate");
    }
}
