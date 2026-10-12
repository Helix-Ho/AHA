using System.Globalization;
using System.Collections.ObjectModel;
using Rhino;
using Rhino.Geometry;

namespace Aha.Topo;

public sealed record XYCoordinates(GeoPoint Origin, string DocumentUnits, double DocumentUnitsPerMetre)
{
    public string SourceCrs => "EPSG:4326";
    public string XAxis => "East";
    public string YAxis => "North";
    public double ElevationMetres => 0;
    public string Method => "Local WGS84 origin approximation, not a survey coordinate system; Z=0, no terrain elevation.";
}

public sealed record XYAppliedSettings(double WidthMetres, double DepthMetres, bool IncludeRoads, double MinimumBuildingAreaSquareMetres);

public sealed record XYResult(Guid ParentLayerId, string LayerName, IReadOnlyList<Guid> ObjectIds,
    int SelectedParcels, int SiteComponents, XYCoordinates Coordinates, GeoBounds Bounds,
    IReadOnlyDictionary<string, int> ObjectCounts, XYAppliedSettings AppliedSettings,
    IReadOnlyList<string> InvalidBuildingIds, IReadOnlyList<string> Warnings)
{
    public int CreatedCount => ObjectIds.Count;
}

/// <summary>Source XY context, without terrain acquisition, height estimation or 3D construction.</summary>
public static class XYModel
{
    public static PreparedModel<XYResult> Prepare(ModelContext context, GeoPoint origin, GeoBounds bounds,
        IReadOnlyList<GeoFeature> targets, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        bool includeRoads = true, double minimumBuildingArea = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scale = context.UnitsPerMetre;
        var projection = new LocalProjection(origin);
        var selection = SiteSelection.Create(targets, projection);
        var references = PlanarReferences.Prepare(selection, parcels, buildings, projection, bounds,
            includeRoads, minimumBuildingArea, targetSite: true);
        var committed = new ModelResult { LayerName = ModelCoordinates.NewLayerName() };
        var objects = PlanarReferences.CreateObjects(references, projection, scale);
        bool transferred = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            var sw = projection.ToLocal(new(bounds.West, bounds.South));
            var ne = projection.ToLocal(new(bounds.East, bounds.North));
            int selectedParcels = targets.Count, siteComponents = selection.Components.Count;
            var coordinates = new XYCoordinates(origin, context.Units.ToString(), scale);
            var settings = new XYAppliedSettings(ne.X - sw.X, ne.Y - sw.Y, includeRoads, minimumBuildingArea);
            var counts = new ReadOnlyDictionary<string, int>(objects.GroupBy(obj => obj.Tags["AHA.Role"])
                .ToDictionary(group => group.Key, group => group.Count()));
            var invalidBuildingIds = Array.AsReadOnly(references.InvalidBuildingIds.ToArray());
            var warnings = Array.AsReadOnly(references.Warnings.ToArray());
            XYResult Result(ModelResult value) => new(value.ParentLayerId, value.LayerName, value.ObjectIds.ToArray(),
                selectedParcels, siteComponents, coordinates, bounds, counts, settings, invalidBuildingIds, warnings);
            var prepared = new PreparedModel<XYResult>(context, committed, objects, new[] { "XY", "XY::TargetBoundary", "XY::ParcelBoundary",
                "XY::RoadBoundary", "XY::BuildingFootprints" }.Where(path => includeRoads || path != "XY::RoadBoundary").ToArray(),
                new Dictionary<string, string>
                {
                    ["AHA.OriginLongitude"] = origin.Lon.ToString("R", CultureInfo.InvariantCulture),
                    ["AHA.OriginLatitude"] = origin.Lat.ToString("R", CultureInfo.InvariantCulture),
                    ["AHA.Coordinates"] = "Local east/north XY from WGS84 origin; Z=0; document units",
                    ["AHA.BoundsWGS84"] = string.Join(",", new[] { bounds.West, bounds.South, bounds.East, bounds.North }.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))
                }, Result, hideXY: false, ct: ct);
            transferred = true;
            return prepared;
        }
        finally { if (!transferred) foreach (var item in objects) item.Geometry.Dispose(); }
    }
}

internal static class ModelCoordinates
{
    internal static double MetresToDocumentUnits(RhinoDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits)
            throw new InvalidOperationException("Rhino 문서의 길이 단위를 설정해 주세요.");
        double scale = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        if (!double.IsFinite(scale) || scale <= 0) throw new InvalidOperationException("문서 단위를 변환할 수 없습니다.");
        return scale;
    }

    internal static string NewLayerName() => "aha_topo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];

    internal static PolylineCurve XYCurve(IEnumerable<GeoPoint> path, LocalProjection projection, double scale)
        => new(path.Select(point => { var local = projection.ToLocal(point); return new Point3d(local.X * scale, local.Y * scale, 0); }));
}
