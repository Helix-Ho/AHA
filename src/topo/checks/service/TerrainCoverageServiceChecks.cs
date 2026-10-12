using Aha.Runtime;
using Rhino;
using System.Globalization;

namespace Aha.Topo.Checks;

internal static class TerrainCoverageServiceChecks
{
    internal static async Task RunAsync(Action<string, bool> check, CancellationToken ct)
    {
        var origin = new GeoPoint(127, 37.5);
        var projection = new LocalProjection(origin);
        var output = TopoService.BoundsFor(origin, 100, 100);
        var parcel = Rectangle("parcel", -5, -5, 5, 5, "9900000000100010000");
        var buildings = new[] { Rectangle("east", 10, -4, 80, 4), Rectangle("north", -4, 10, 4, 90) };
        var expected = Bounds(-50, -50, 80, 90);
        var options = new ModelOptions(IncludeRoads: false);
        var sources = new List<CoverageSource>();
        Func<CoverageSource> makeSource = () => new(parcel, buildings);
        var workflow = new TopoWorkflow(() => { var source = makeSource(); sources.Add(source); return source; });
        var doc = await RhinoDispatch.InvokeAsync(() =>
        {
            var fixture = RhinoDoc.CreateHeadless(null);
            fixture.ModelUnitSystem = UnitSystem.Meters; fixture.ModelAbsoluteTolerance = .001;
            return fixture;
        }).ConfigureAwait(false);
        var context = await RhinoDispatch.InvokeAsync(() => ModelContext.Capture(doc));
        async Task<ModelResult> Generate(IReadOnlyList<GeoFeature> targets, GeoPoint origin, GeoBounds bounds,
            int level, ModelOptions options, CancellationToken token)
        {
            using var prepared = await workflow.PrepareAsync(context, targets, origin, bounds, level, options, token);
            token.ThrowIfCancellationRequested();
            return await RhinoDispatch.InvokeAsync(() => prepared.Commit(doc));
        }
        async Task<XYResult> GenerateXY(string[] pnus,double width,double depth,bool roads,double area,CancellationToken token)
        {
            using var prepared=await workflow.PrepareXYByPnusAsync(context,pnus,width,depth,roads,area,token);
            token.ThrowIfCancellationRequested();return await RhinoDispatch.InvokeAsync(()=>prepared.Commit(doc));
        }
        try
        {
            var result = await Generate( new[] { parcel }, origin, output, 12, options, ct).ConfigureAwait(false);
            var request = sources[^1];
            check("whole_ground_acquired_once_for_all_generated_buildings", request.BuildingBounds.SequenceEqual(new[] { output })
                && request.ParcelBounds.SequenceEqual(new[] { expected }) && request.TerrainBounds.SequenceEqual(new[] { expected }));
            check("ground_expansion_preserves_output_and_full_far_buildings", result.Bounds == output && result.SurfaceField.Bounds == expected
                && result.NearBuildings == 0 && result.FarBuildings == 2 && result.UnzonedBuildings == 0
                && result.InvalidBuildings == 0 && !result.AppliedSettings.Contour);
            check("full_far_buildings_use_acquired_ground_for_foundations_and_roofs", await RhinoDispatch.InvokeAsync(() =>
            {
                var models = result.ObjectIds.Select(id => doc.Objects.FindId(id))
                    .Where(obj => obj.Attributes.GetUserString("AHA.Role") == "Building").ToArray();
                var footprints = new Dictionary<string, (double X0, double Y0, double X1, double Y1)>
                { ["east"] = (10, -4, 80, 4), ["north"] = (-4, 10, 4, 90) };
                if (models.Length != footprints.Count || models.Select(obj => obj.Attributes.GetUserString("AHA.FeatureId"))
                    .Distinct().Count() != footprints.Count) return false;
                return models.All(obj =>
                {
                    if (!footprints.TryGetValue(obj.Attributes.GetUserString("AHA.FeatureId") ?? "", out var footprint)
                        || obj.Attributes.GetUserString("AHA.Zone") != "Far") return false;
                    var box = obj.Geometry.GetBoundingBox(true);
                    // Both full footprints cross Near and output. Their ground is
                    // the fixed 20m plane, so min−1=19 and mean+6=26 everywhere.
                    return obj.Geometry is Rhino.Geometry.Mesh { IsClosed: true, IsValid: true }
                        && Close(box.Min.X, footprint.X0) && Close(box.Min.Y, footprint.Y0)
                        && Close(box.Max.X, footprint.X1) && Close(box.Max.Y, footprint.Y1)
                        && Close(box.Min.Z, 19) && Close(box.Max.Z, 26)
                        && Number("AHA.FoundationBottomMetres", 19)
                        && Number("AHA.ReferenceMeanElevationMetres", 20)
                        && Number("AHA.RoofElevationMetres", 26);
                    bool Number(string key, double value) => double.TryParse(obj.Attributes.GetUserString(key),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var actual) && Close(actual, value);
                });
                static bool Close(double actual, double expectedValue) => double.IsFinite(actual) && Math.Abs(actual - expectedValue) < 1e-6;
            }).ConfigureAwait(false));
            check("expanded_ground_does_not_expand_land_or_xy_output", await RhinoDispatch.InvokeAsync(() =>
            {
                var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)).ToArray();
                var limited = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") is "Land" or "Road"
                    || obj.Attributes.GetUserString("AHA.CurveSpace") == "XY").ToArray();
                return limited.Length > 0 && limited.All(obj =>
                {
                    var box = obj.Geometry.GetBoundingBox(true);
                    return box.Min.X >= -50.001 && box.Min.Y >= -50.001 && box.Max.X <= 50.001 && box.Max.Y <= 50.001;
                });
            }).ConfigureAwait(false));
            check("coverage_source_disposed_after_success", request.Disposed);

            await Generate( new[] { parcel }, origin, output, 12,
                options with { UseNearRegion = false }, ct).ConfigureAwait(false);
            request = sources[^1];
            check("near_off_still_acquires_whole_building_ground", request.BuildingBounds.SequenceEqual(new[] { output })
                && request.ParcelBounds.SequenceEqual(new[] { expected }) && request.TerrainBounds.SequenceEqual(new[] { expected }));
            await GenerateXY( new[] { parcel.Pnu }, 100, 100, true, 10, ct).ConfigureAwait(false);
            request = sources[^1];
            check("xy_keeps_output_queries_and_never_requests_terrain", request.BuildingBounds.SequenceEqual(new[] { output })
                && request.ParcelBounds.SequenceEqual(new[] { output }) && request.TerrainBounds.Count == 0);

            var before = await RhinoDispatch.InvokeAsync(() => (Objects: doc.Objects.Count,
                Layers: doc.Layers.Count(layer => !layer.IsDeleted))).ConfigureAwait(false);
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                makeSource = () => new(parcel, buildings) { AfterBuildings = cancelled.Cancel };
                bool stopped = false;
                try { await Generate( new[] { parcel }, origin, output, 12, options, cancelled.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { stopped = true; }
                request = sources[^1];
                check("cancel_after_buildings_prevents_expanded_acquisition", stopped && request.BuildingBounds.Count == 1
                    && request.ParcelBounds.Count == 0 && request.TerrainBounds.Count == 0 && request.Disposed);
            }

            makeSource = () => new(parcel, new[] { Rectangle("oversize", -30000, -4, 30000, 4) });
            bool limited = false;
            try { await Generate( new[] { parcel }, origin, output, 15, options, ct).ConfigureAwait(false); }
            catch (ArgumentException error) when (error.Message.Contains("한도", StringComparison.Ordinal) || error.Message.Contains("100만", StringComparison.Ordinal))
            { limited = true; }
            request = sources[^1];
            check("expanded_terrain_limits_checked_before_parcel_or_dem_fetch", limited && request.BuildingBounds.Count == 1
                && request.ParcelBounds.Count == 0 && request.TerrainBounds.Count == 0 && request.Disposed);

            makeSource = () => new(parcel, buildings) { FailTerrain = true };
            bool failed = false;
            try { await Generate( new[] { parcel }, origin, output, 12, options, ct).ConfigureAwait(false); }
            catch (InvalidDataException error) when (error.Message == "fixed missing terrain") { failed = true; }
            request = sources[^1];
            check("expanded_ground_failure_does_not_substitute_height", failed && request.TerrainBounds.SequenceEqual(new[] { expected }) && request.Disposed);
            check("coverage_cancel_limit_and_fetch_failure_leave_no_partial_model", await RhinoDispatch.InvokeAsync(() =>
                doc.Objects.Count == before.Objects && doc.Layers.Count(layer => !layer.IsDeleted) == before.Layers).ConfigureAwait(false));
            check("coverage_failures_release_document_gate", await RhinoDispatch.InvokeAsync(() =>
            { using var lease = DocumentAccess.Enter(doc); return true; }).ConfigureAwait(false));
        }
        finally { await RhinoDispatch.InvokeAsync(() => { doc.Dispose(); return true; }).ConfigureAwait(false); }

        GeoBounds Bounds(double x0, double y0, double x1, double y1)
        { var a = projection.ToGeo(x0,y0); var b = projection.ToGeo(x1,y1); return new(a.Lon,a.Lat,b.Lon,b.Lat); }
        GeoFeature Rectangle(string id, double x0, double y0, double x1, double y1, string pnu = "") => new()
        { Id = id, Pnu = pnu, Height = 6, Polygons = new() { new() { new() { projection.ToGeo(x0,y0), projection.ToGeo(x1,y0),
            projection.ToGeo(x1,y1), projection.ToGeo(x0,y1), projection.ToGeo(x0,y0) } } } };
    }

    private sealed class CoverageSource(GeoFeature parcel, IReadOnlyList<GeoFeature> buildings) : ITopoSource
    {
        internal List<GeoBounds> BuildingBounds { get; } = new();
        internal List<GeoBounds> ParcelBounds { get; } = new();
        internal List<GeoBounds> TerrainBounds { get; } = new();
        internal bool Disposed { get; private set; }
        internal bool FailTerrain { get; init; }
        internal Action? AfterBuildings { get; init; }
        public Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(parcel); }
        public Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); BuildingBounds.Add(bounds); AfterBuildings?.Invoke(); return Task.FromResult(buildings.ToList()); }
        public Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); ParcelBounds.Add(bounds); return Task.FromResult(new List<GeoFeature> { parcel }); }
        public Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress)
        {
            ct.ThrowIfCancellationRequested(); TerrainBounds.Add(bounds);
            if (FailTerrain) return Task.FromException<TerrainGrid>(new InvalidDataException("fixed missing terrain"));
            var plan = TerrainClient.Plan(bounds, level);
            var tiles = new List<TerrainTile>();
            for (int y = plan.MinY; y <= plan.MaxY; y++) for (int x = plan.MinX; x <= plan.MaxX; x++)
                tiles.Add(new(level, x, y, Enumerable.Repeat(20f, TerrainTile.ValueCount).ToArray()));
            return Task.FromResult(new TerrainGrid(tiles));
        }
        public void Dispose() => Disposed = true;
    }
}
