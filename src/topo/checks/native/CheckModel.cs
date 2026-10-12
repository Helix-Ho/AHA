using Aha.Topo;
using Rhino;

namespace Aha.Topo.Checks;

internal static class CheckModel
{
    internal static ModelResult Build(RhinoDoc doc, GeoPoint origin, GeoBounds bounds, GeoFeature target,
        IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings, TerrainGrid terrain, ModelOptions options,
        CancellationToken ct = default) => Build(doc, origin, bounds, new[] { target }, parcels, buildings, terrain, options, ct);
    internal static ModelResult Build(RhinoDoc doc, GeoPoint origin, GeoBounds bounds, IReadOnlyList<GeoFeature> targets,
        IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings, TerrainGrid terrain, ModelOptions options,
        CancellationToken ct = default)
    {
        using var prepared = RhinoModel.Prepare(ModelContext.Capture(doc), origin, bounds, targets, parcels, buildings, terrain, options, ct);
        ct.ThrowIfCancellationRequested(); return prepared.Commit(doc);
    }
    internal static ModelResult Build(RhinoDoc doc, GeoPoint origin, GeoBounds bounds, GeoFeature target,
        IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings, TerrainField terrain, ModelOptions options,
        CancellationToken ct = default) => Build(doc, origin, bounds, new[] { target }, parcels, buildings, terrain, options, ct);
    internal static ModelResult Build(RhinoDoc doc, GeoPoint origin, GeoBounds bounds, IReadOnlyList<GeoFeature> targets,
        IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings, TerrainField terrain, ModelOptions options,
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        using var prepared = RhinoModel.Prepare(ModelContext.Capture(doc), origin, bounds, targets, parcels, buildings, terrain, options, ct, progress);
        ct.ThrowIfCancellationRequested(); return prepared.Commit(doc);
    }
    internal static XYResult XY(RhinoDoc doc, GeoPoint origin, GeoBounds bounds, IReadOnlyList<GeoFeature> targets,
        IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings, bool includeRoads = true,
        double minimumBuildingArea = 10, CancellationToken ct = default)
    {
        using var prepared = XYModel.Prepare(ModelContext.Capture(doc), origin, bounds, targets, parcels, buildings, includeRoads, minimumBuildingArea, ct);
        ct.ThrowIfCancellationRequested(); return prepared.Commit(doc);
    }
}
