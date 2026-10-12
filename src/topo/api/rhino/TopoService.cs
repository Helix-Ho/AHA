using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Runtime;
using Aha.Setting;
using Rhino;

namespace Aha.Topo;

/// <summary>The panel and MCP share detached preparation and explicit document creation.</summary>
public static class TopoService
{
    public static string RequireKey()
    {
        var key = AhaSettings.GetVWorldKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("AHA Setting에서 VWorld 인증키를 설정하세요.");
        return key;
    }

    public static GeoPoint SelectionCentre(IReadOnlyList<GeoFeature> parcels)
    {
        var points = parcels.SelectMany(p => p.Polygons).SelectMany(p => p).SelectMany(r => r).ToArray();
        if (points.Length == 0) throw new ArgumentException("대상 필지를 하나 이상 선택하세요.");
        return new((points.Min(p => p.Lon) + points.Max(p => p.Lon)) / 2,
            (points.Min(p => p.Lat) + points.Max(p => p.Lat)) / 2);
    }

    public static GeoBounds BoundsFor(GeoPoint centre, double widthMetres, double depthMetres)
    {
        if (!double.IsFinite(widthMetres) || !double.IsFinite(depthMetres) ||
            widthMetres < 50 || widthMetres > 3000 || depthMetres < 50 || depthMetres > 3000)
            throw new ArgumentException("모델 가로·세로는 각각 50~3,000 m여야 합니다.");
        var projection = new LocalProjection(centre);
        var sw = projection.ToGeo(-widthMetres / 2, -depthMetres / 2);
        var ne = projection.ToGeo(widthMetres / 2, depthMetres / 2);
        return new(sw.Lon, sw.Lat, ne.Lon, ne.Lat);
    }

    private static readonly TopoWorkflow Workflow = new(() => new VWorldTopoSource(RequireKey()));
    public static JsonObject PrepareStart(RhinoDoc doc, IReadOnlyList<GeoFeature> targets,
        GeoPoint origin, GeoBounds bounds, int terrainLevel, ModelOptions options)
        => Workflow.Start(doc, targets, origin, bounds, terrainLevel, options);
    public static JsonObject PrepareByPnusStart(RhinoDoc doc, string[] pnus, double widthMetres,
        double depthMetres, int level, ModelOptions options)
        => Workflow.StartByPnus(doc, pnus, widthMetres, depthMetres, level, options);
    public static JsonObject PrepareXYByPnusStart(RhinoDoc doc, string[] pnus, double widthMetres,
        double depthMetres, bool includeRoads, double minimumBuildingArea)
        => Workflow.StartXY(doc, pnus, widthMetres, depthMetres, includeRoads, minimumBuildingArea);
    public static JsonObject Create(string id) => Jobs.Create(id);
    public static JsonObject? Current(uint document) => Jobs.CurrentPrepared(document);
    public static JsonObject Cancel(string id) => Jobs.Cancel(id);

}

internal interface ITopoSource : IDisposable
{
    Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct);
    Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct);
    Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct);
    Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress);
}

internal sealed class VWorldTopoSource : ITopoSource
{
    private readonly VWorldClient features;
    private readonly TerrainClient terrain;
    internal VWorldTopoSource(string key) { features = new VWorldClient(key); terrain = new TerrainClient(key); }
    public Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct) => features.ParcelByPnuAsync(pnu, ct);
    public Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct) => features.ParcelsAsync(bounds, ct);
    public Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct) => features.BuildingsAsync(bounds, ct);
    public Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress)
        => terrain.FetchAsync(bounds, level, ct, progress);
    public void Dispose() { features.Dispose(); terrain.Dispose(); }
}

internal sealed class TopoWorkflow(Func<ITopoSource> source)
{
    private static readonly SemaphoreSlim GeometrySlot = new(1, 1);

    internal JsonObject Start(RhinoDoc doc, IReadOnlyList<GeoFeature> targets, GeoPoint origin,
        GeoBounds bounds, int terrainLevel, ModelOptions options)
    {
        var context = Capture(doc); var serial = doc.RuntimeSerialNumber;
        var snapshot = JsonSerializer.Deserialize<GeoFeature[]>(JsonSerializer.Serialize(targets))!;
        BuildingRules.Validate(options); bounds.Validate(); TerrainClient.Plan(bounds, terrainLevel);
        var projection = new LocalProjection(origin);
        var sw = projection.ToLocal(new(bounds.West, bounds.South));
        var ne = projection.ToLocal(new(bounds.East, bounds.North));
        return Jobs.StartPrepared(serial, "topo", async (ct, progress) =>
            new TopoPreparedJob<ModelResult>(serial, await PrepareAsync(context, snapshot, origin, bounds,
                terrainLevel, options, ct, progress).ConfigureAwait(false), ModelSummary),
            Input("model", snapshot.Select(item => item.Pnu).ToArray(), ne.X-sw.X, ne.Y-sw.Y, terrainLevel, options));
    }

    internal JsonObject StartByPnus(RhinoDoc doc, string[] pnus, double width, double depth, int level, ModelOptions options)
    {
        var context = Capture(doc); var serial = doc.RuntimeSerialNumber; pnus = pnus.ToArray();
        ValidatePnuModel(pnus, width, depth, level, options);
        return Jobs.StartPrepared(serial, "topo", async (ct, progress) =>
            new TopoPreparedJob<ModelResult>(serial, await PrepareByPnusAsync(context, pnus, width, depth, level,
                options, ct, progress).ConfigureAwait(false), ModelSummary), Input("model", pnus, width, depth, level, options));
    }

    internal JsonObject StartXY(RhinoDoc doc, string[] pnus, double width, double depth, bool roads, double area)
    {
        var context = Capture(doc); var serial = doc.RuntimeSerialNumber; pnus = pnus.ToArray();
        ValidateXY(pnus, width, depth, area);
        return Jobs.StartPrepared(serial, "topo_xy", async (ct, progress) =>
            new TopoPreparedJob<XYResult>(serial, await PrepareXYByPnusAsync(context, pnus, width, depth, roads,
                area, ct, progress).ConfigureAwait(false), XYModelSummary),
            Input("xy", pnus, width, depth, null, new { IncludeRoads = roads, MinArea = area }));
    }

    private static ModelContext Capture(RhinoDoc doc)
    {
        if (RhinoApp.InvokeRequired) throw new InvalidOperationException("Topo 입력은 Rhino 화면 스레드에서 확정해야 합니다.");
        DocumentAccess.Get(doc.RuntimeSerialNumber);
        return ModelContext.Capture(doc);
    }

    private static JsonObject Input(string mode, string[] pnus, double width, double depth, int? level, object options)
        => new() { ["mode"] = mode, ["pnus"] = JsonSerializer.SerializeToNode(pnus), ["width_m"] = width,
            ["depth_m"] = depth, ["terrain_level"] = level, ["options"] = JsonSerializer.SerializeToNode(options) };

    private static JsonNode ModelSummary(PreparedModel<ModelResult> prepared)
    {
        var value = prepared.Details;
        return JsonSerializer.SerializeToNode(new { mode = "model", applied_settings = value.AppliedSettings,
            terrain_level = value.TerrainLevel, bounds = value.Bounds,
            coordinates = new { origin = value.Origin, document_units = value.DocumentUnits, document_units_per_metre = value.DocumentUnitsPerMetre },
            prepared_object_count = prepared.ObjectCount, object_counts = prepared.ObjectCounts,
            selected_parcels = value.SelectedParcels, site_components = value.SiteComponents, warnings = value.Warnings })!;
    }

    private static JsonNode XYModelSummary(PreparedModel<XYResult> prepared)
    {
        var value = prepared.Details;
        return JsonSerializer.SerializeToNode(new { mode = "xy", applied_settings = value.AppliedSettings, bounds = value.Bounds,
            coordinates = value.Coordinates, prepared_object_count = prepared.ObjectCount, object_counts = value.ObjectCounts,
            selected_parcels = value.SelectedParcels, site_components = value.SiteComponents, warnings = value.Warnings })!;
    }

    private static async Task<T> Calculate<T>(Func<T> calculate, CancellationToken ct, IProgress<string>? progress)
    {
        progress?.Report("형상 계산 순서를 기다리고 있습니다…");
        await GeometrySlot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("지형·건물 형상을 계산하고 있습니다…");
                ct.ThrowIfCancellationRequested();
                return calculate();
            }, ct).ConfigureAwait(false);
        }
        finally { GeometrySlot.Release(); }
    }

    internal Task<PreparedModel<ModelResult>> PrepareAsync(ModelContext context, IReadOnlyList<GeoFeature> targets,
        GeoPoint origin, GeoBounds bounds, int terrainLevel, ModelOptions options,
        CancellationToken ct, IProgress<string>? progress = null)
        => PrepareCoreAsync(context, targets, origin, bounds, terrainLevel, options, ct, progress, null);

    private async Task<PreparedModel<ModelResult>> PrepareCoreAsync(ModelContext context, IReadOnlyList<GeoFeature> targets,
        GeoPoint origin, GeoBounds bounds, int terrainLevel, ModelOptions options, CancellationToken ct,
        IProgress<string>? progress, ITopoSource? supplied)
    {
        var selection = ValidateModel(targets, origin, bounds, terrainLevel, options);
        var projection = new LocalProjection(origin);
        ct.ThrowIfCancellationRequested();
        progress?.Report("필지·건물 데이터를 수집하고 있습니다…");
        using var owned = supplied is null ? source() : null;
        var client = supplied ?? owned!;
        var buildings = await client.BuildingsAsync(bounds, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var calculationBounds = TerrainCoverage.For(selection, projection, bounds, buildings, options);
        TerrainClient.Plan(calculationBounds, terrainLevel);
        ct.ThrowIfCancellationRequested();
        // Query buildings only in the chosen output. Extra ground must not introduce
        // another generation of buildings and repeatedly grow the acquisition region.
        var parcels = client.ParcelsAsync(calculationBounds, ct);
        var terrain = client.TerrainAsync(calculationBounds, terrainLevel, ct, progress);
        await Task.WhenAll(parcels, terrain).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Calculate(() =>
        {
            var field = TerrainField.FromGrid(calculationBounds, projection, terrain.Result);
            var result = RhinoModel.Prepare(context, origin, bounds, targets, parcels.Result, buildings, field, options, ct, progress);
            result.Details.TerrainLevel = terrainLevel;
            return result;
        }, ct, progress).ConfigureAwait(false);
    }

    internal async Task<PreparedModel<ModelResult>> PrepareByPnusAsync(ModelContext context, string[] pnus, double widthMetres,
        double depthMetres, int level, ModelOptions options, CancellationToken ct, IProgress<string>? progress = null)
    {
        ValidatePnuModel(pnus, widthMetres, depthMetres, level, options);
        ct.ThrowIfCancellationRequested();
        using var client = source();
        var targets = await ReadTargetsAsync(client, pnus, ct).ConfigureAwait(false);
        var origin = TopoService.SelectionCentre(targets);
        return await PrepareCoreAsync(context, targets, origin, TopoService.BoundsFor(origin, widthMetres, depthMetres), level, options, ct, progress, client).ConfigureAwait(false);
    }

    internal async Task<PreparedModel<XYResult>> PrepareXYByPnusAsync(ModelContext context, string[] pnus, double widthMetres,
        double depthMetres, bool includeRoads, double minimumBuildingArea, CancellationToken ct, IProgress<string>? progress = null)
    {
        ValidateXY(pnus, widthMetres, depthMetres, minimumBuildingArea);
        ct.ThrowIfCancellationRequested();
        progress?.Report("선택 필지의 경계를 수집하고 있습니다…");
        using var client = source();
        var targets = await ReadTargetsAsync(client, pnus, ct).ConfigureAwait(false);
        var origin = TopoService.SelectionCentre(targets);
        var bounds = TopoService.BoundsFor(origin, widthMetres, depthMetres);
        ValidateSelection(targets, origin, bounds);
        ct.ThrowIfCancellationRequested();
        progress?.Report("필지·건물 데이터를 수집하고 있습니다…");
        var parcels = client.ParcelsAsync(bounds, ct);
        var buildings = client.BuildingsAsync(bounds, ct);
        await Task.WhenAll(parcels, buildings).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Calculate(() => XYModel.Prepare(context, origin, bounds, targets, parcels.Result, buildings.Result,
            includeRoads, minimumBuildingArea, ct), ct, progress).ConfigureAwait(false);
    }

    private static SiteSelection ValidateModel(IReadOnlyList<GeoFeature> targets, GeoPoint origin, GeoBounds bounds, int level, ModelOptions options)
    {
        BuildingRules.Validate(options);
        var selection = ValidateSelection(targets, origin, bounds);
        if (options.UseNearRegion) EditRegion.Create(selection, new LocalProjection(origin), bounds, options.NearRatio);
        TerrainClient.Plan(bounds, level);
        return selection;
    }
    private static void ValidatePnuModel(string[] pnus, double width, double depth, int level, ModelOptions options)
    {
        TopoService.BoundsFor(new GeoPoint(127, 37.5), width, depth);
        BuildingRules.Validate(options); TerrainTile.ValidateLevel(level);
        if (level < 3) throw new ArgumentException("지형 레벨은 3~15여야 합니다.");
        ValidatePnus(pnus);
    }
    private static void ValidateXY(string[] pnus, double width, double depth, double area)
    {
        TopoService.BoundsFor(new GeoPoint(127, 37.5), width, depth);
        PlanarReferences.ValidateMinimumArea(area); ValidatePnus(pnus);
    }

    private static SiteSelection ValidateSelection(IReadOnlyList<GeoFeature> targets, GeoPoint origin, GeoBounds bounds)
    {
        bounds.Validate();
        var projection = new LocalProjection(origin);
        var sw = projection.ToLocal(new(bounds.West, bounds.South));
        var ne = projection.ToLocal(new(bounds.East, bounds.North));
        if (ne.X - sw.X > 3000.001 || ne.Y - sw.Y > 3000.001 || ne.X - sw.X < 49.999 || ne.Y - sw.Y < 49.999)
            throw new ArgumentException("모델 가로·세로는 각각 50~3,000 m여야 합니다.");
        var selection = SiteSelection.Create(targets, projection);
        if (targets.SelectMany(p => p.Polygons).SelectMany(p => p).SelectMany(r => r)
            .Any(p => p.Lon < bounds.West || p.Lon > bounds.East || p.Lat < bounds.South || p.Lat > bounds.North))
            throw new ArgumentException("모델 범위가 선택한 모든 필지를 포함해야 합니다. 범위를 넓히세요.");
        return selection;
    }

    private static void ValidatePnus(string[] pnus)
    {
        ArgumentNullException.ThrowIfNull(pnus);
        if (pnus.Any(p => p is null || p.Length != 19 || p.Any(c => c < '0' || c > '9')))
            throw new ArgumentException("19자리 PNU를 입력하세요.");
        if (pnus.Length == 0 || pnus.Length > 100 || pnus.Distinct().Count() != pnus.Length)
            throw new ArgumentException("중복 없는 대상 PNU를 1~100개 지정하세요.");
    }

    private static async Task<List<GeoFeature>> ReadTargetsAsync(ITopoSource client, string[] pnus, CancellationToken ct)
    {
        var targets = new List<GeoFeature>();
        foreach (var pnu in pnus)
        {
            ct.ThrowIfCancellationRequested();
            targets.Add(await client.ParcelByPnuAsync(pnu, ct).ConfigureAwait(false));
        }
        return targets;
    }
}

internal sealed class TopoPreparedJob<T>(uint document, PreparedModel<T> model, Func<PreparedModel<T>, JsonNode> summary) : PreparedJob
{
    public override JsonNode Summary => summary(model);
    public override Task<JsonNode?> CreateAsync() => RhinoDispatch.InvokeAsync(() =>
    {
        var doc = DocumentAccess.Get(document);
        if (!model.Context.Matches(doc))
            throw new InvalidOperationException("연산 이후 대상 Rhino 문서의 단위 또는 허용오차가 변경되었습니다. 다시 연산하세요.");
        if (doc.InCommand(false) > 0)
            throw new CreateDeferredException("대상 문서에서 Rhino 명령이 진행 중입니다. 명령 종료 후 생성을 다시 요청하세요.");
        try
        {
            using var lease = DocumentAccess.Enter(doc);
            return JsonSerializer.SerializeToNode(model.Commit(doc));
        }
        catch (DocumentBusyException error) { throw new CreateDeferredException(error.Message); }
    });
    public override void Dispose() => model.Dispose();
}
