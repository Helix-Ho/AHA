using Clipper2Lib;
using System.Globalization;
using Rhino;
using Rhino.Geometry;

namespace Aha.Topo;

public sealed class ModelResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TerrainField SurfaceField { get; internal set; } = null!;
    [System.Text.Json.Serialization.JsonIgnore]
    public RoadGradingResult RoadGrading { get; internal set; } = null!;
    public ModelOptions AppliedSettings { get; internal set; } = new();
    public int? TerrainLevel { get; internal set; }
    public GeoBounds Bounds { get; internal set; } = null!;
    public GeoPoint Origin { get; internal set; } = null!;
    public string DocumentUnits { get; internal set; } = "";
    public double DocumentUnitsPerMetre { get; internal set; }
    public int SelectedParcels { get; internal set; }
    public int SiteComponents { get; internal set; }
    public int CreatedCount { get; internal set; }
    public int NearSolidCount { get; internal set; }
    public double? NearBottomElevationMetres { get; internal set; }
    public Guid ParentLayerId { get; internal set; }
    public int TerrainVertices { get; internal set; }
    public int SurfaceCount { get; internal set; }
    public int NearSurfaces { get; internal set; }
    public int FarSurfaces { get; internal set; }
    public int DisplayTriangles { get; internal set; }
    public int NearBuildings { get; internal set; }
    public int FarBuildings { get; internal set; }
    public int UnzonedBuildings { get; internal set; }
    public double SurfacePlanAreaResidualSquareMetres { get; internal set; }
    public double MaximumBoundaryChordErrorMetres { get; internal set; }
    public int DroppedZeroAreaBranches { get; internal set; }
    public int MergedPatchBlocks { get; internal set; }
    public int KnownBuildings { get; internal set; }
    public int FloorEstimatedBuildings { get; internal set; }
    public int DefaultEstimatedBuildings { get; internal set; }
    public int FilteredBuildings { get; internal set; }
    public int OutsideBuildings { get; internal set; }
    public int InvalidBuildings { get; internal set; }
    public int UndergroundBuildings { get; internal set; }
    public int UnclassifiedBuildings { get; internal set; }
    public int ClippedParcelRings { get; internal set; }
    public int RoadParcels { get; internal set; }
    public int RoadSurfaces { get; internal set; }
    public RoadGeometryDiagnostics RoadDiagnostics { get; internal set; } = RoadGeometryDiagnostics.Empty;
    public IReadOnlyList<ModelPlanDiagnostics> PlanCleanup { get; internal set; } = Array.Empty<ModelPlanDiagnostics>();
    public double RoadAreaSquareMetres { get; internal set; }
    public double MaximumDisplayHeightErrorMetres { get; internal set; }
    public string LayerName { get; internal set; } = "";
    public List<Guid> ObjectIds { get; } = new();
    public List<Guid> SiteObjectIds { get; } = new();
    public int ContourPlateCount { get; internal set; }
    public double ContourOmittedAreaSquareMetres { get; internal set; }
    public double ContourOverlayReassignedAreaSquareMetres { get; internal set; }
    public double PartitionReassignedAreaSquareMetres { get; internal set; }
    public List<string> Warnings { get; } = new();
    public List<string> InvalidBuildingIds { get; } = new();
}

/// <summary>Calculates detached model geometry from acquired data and copied document units.</summary>
public static class RhinoModel
{
    private sealed record Pending(GeometryBase Geometry, string Layer, string Name, Dictionary<string, string> Tags, SolidExpectation? ExpectedSolid = null);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PreparedModel<ModelResult> Prepare(ModelContext context, GeoPoint origin, GeoBounds bounds,
        GeoFeature target, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        TerrainGrid terrain, ModelOptions options, CancellationToken ct = default)
        => Prepare(context, origin, bounds, new[] { target }, parcels, buildings, terrain, options, ct);

    public static PreparedModel<ModelResult> Prepare(ModelContext context, GeoPoint origin, GeoBounds bounds,
        IReadOnlyList<GeoFeature> targets, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        TerrainGrid terrain, ModelOptions options, CancellationToken ct = default)
    {
        var projection = new LocalProjection(origin);
        var coverage = TerrainCoverage.For(SiteSelection.Create(targets, projection), projection, bounds, buildings, options);
        return Prepare(context, origin, bounds, targets, parcels, buildings,
            TerrainField.FromGrid(coverage, projection, terrain), options, ct);
    }

    public static PreparedModel<ModelResult> Prepare(ModelContext context, GeoPoint origin, GeoBounds bounds,
        GeoFeature target, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        TerrainField terrain, ModelOptions options, CancellationToken ct = default)
        => Prepare(context, origin, bounds, new[] { target }, parcels, buildings, terrain, options, ct);

    public static PreparedModel<ModelResult> Prepare(ModelContext context, GeoPoint origin, GeoBounds bounds,
        IReadOnlyList<GeoFeature> targets, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        TerrainField terrain, ModelOptions options, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("도로·지반 기준을 계산하고 있습니다…");
        BuildingRules.Validate(options);
        var projection = new LocalProjection(origin);
        var selection = SiteSelection.Create(targets, projection);
        var near = options.UseNearRegion
            ? EditRegion.Create(selection, projection, bounds, options.NearRatio) : null;
        var area = RoadGeometry.Build(options.IncludeRoads ? parcels : Array.Empty<GeoFeature>(),
            projection, terrain.Bounds, bounds, near, ct);
        ct.ThrowIfCancellationRequested();
        return PrepareCore(context, origin, bounds, selection, near, parcels, buildings,
            RoadGrading.Apply(terrain, area, ct), options, area, ct, progress);
    }

    private static PreparedModel<ModelResult> PrepareCore(ModelContext context, GeoPoint origin, GeoBounds bounds,
        SiteSelection selection, EditRegion? near, IReadOnlyList<GeoFeature> parcels, IReadOnlyList<GeoFeature> buildings,
        RoadGradingResult grading, ModelOptions options, RoadArea roadArea,
        CancellationToken ct, IProgress<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var terrain = grading.Field;
        using var supports = new TerrainSupportSurfaces(terrain);
        ArgumentNullException.ThrowIfNull(context);
        BuildingRules.Validate(options); bounds.Validate();
        var scale = context.UnitsPerMetre;
        var projection = new LocalProjection(origin);
        if (terrain.Projection.Origin != origin || terrain.Bounds.West > bounds.West || terrain.Bounds.South > bounds.South ||
            terrain.Bounds.East < bounds.East || terrain.Bounds.North < bounds.North)
            throw new ArgumentException("지형 자료는 같은 원점에서 모델 출력 범위를 포함해야 합니다.");
        var result = new ModelResult { LayerName = ModelCoordinates.NewLayerName(), AppliedSettings = options with
            { NearRatio = options.UseNearRegion ? options.NearRatio : 2, NearDepthMetres = options.UseNearRegion ? options.NearDepthMetres : 30,
                RoadDropMetres = options.EffectiveRoadDrop, Contour = options.EffectiveContour, ContourIntervalMetres = options.EffectiveContour ? options.ContourIntervalMetres : 1,
                Parapets = options.EffectiveParapets },
            Bounds = bounds, Origin = origin, DocumentUnits = context.Units.ToString(), DocumentUnitsPerMetre = scale };
        result.SelectedParcels = selection.Parcels.Count; result.SiteComponents = selection.Components.Count;
        var pending = new List<Pending>();
        var ownedSurfaces = new List<GeometryBase>();
        bool transferred = false;
        var tolerance = Math.Max(context.AbsoluteTolerance, .000001 * scale);
        result.RoadGrading = grading;
        result.SurfaceField = result.RoadGrading.Field;
        try
        {
            var references = PlanarReferences.Prepare(selection, parcels, buildings, projection, bounds,
                options.IncludeRoads, options.MinArea, feature => near?.Intersects(feature, projection) == true);
            result.ClippedParcelRings = references.ClippedParcelRings;
            result.InvalidBuildingIds.AddRange(references.InvalidBuildingIds);
            result.InvalidBuildings = references.InvalidBuildingIds.Count;
            result.Warnings.AddRange(references.Warnings);
            var buildingInfo = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidates = new List<GeoFeature>();
            foreach (var building in references.EligibleBuildings)
            {
                if (BuildingRules.IsUnderground(building)) { result.UndergroundBuildings++; continue; }
                if (TerrainCoverage.IsGenerated(building, projection, bounds, options.MinArea)) candidates.Add(building);
                else result.OutsideBuildings++;
            }
            var eligibleIds = references.EligibleBuildings.Select(building => building.Id).ToHashSet(StringComparer.Ordinal);
            var invalidIds = references.InvalidBuildingIds.ToHashSet(StringComparer.Ordinal);
            foreach (var building in buildings.Where(building => !eligibleIds.Contains(building.Id) && !invalidIds.Contains(building.Id)))
            {
                if (!BuildingRules.IsUnderground(building) && Geo.Area(building, projection) < options.MinArea) result.FilteredBuildings++;
                else result.OutsideBuildings++;
            }
            var sitePaths = SiteSelection.FeaturePaths(selection.Boundary, projection);
            var surface = TerrainSurface.Prepare(result.SurfaceField, roadArea, options.EffectiveRoadDrop,
                options.TerrainAsNurbs, options.SeparateRoads,
                near, options.NearDepthMetres, bounds, includeNear: !options.EffectiveContour);
            roadArea = surface.GroundRoadArea;
            ct.ThrowIfCancellationRequested();
            var placementRoads = options.EffectiveContour ? roadArea with
                { Outlines = Clipper.Difference(roadArea.Outlines, sitePaths, FillRule.NonZero, EditRegion.ClipPrecision) } : roadArea;
            var placement = BuildingPlacement.Prepare(terrain, placementRoads, options.EffectiveRoadDrop,
                options.SeparateRoads, candidates);
            foreach (var rejection in placement.Rejections) RejectBuilding(rejection.FeatureId, rejection.Reason);
            var originals = candidates.ToDictionary(feature => feature.Id, StringComparer.Ordinal);
            bool IsNear(BuildingPlacementResult item) => near is not null &&
                near.ContainsStrict(originals[item.SourceFeatureId], projection) && near.ContainsStrict(item.Footprint);
            var nearIds = placement.Placements.Where(IsNear).Select(item => item.SourceFeatureId).ToHashSet(StringComparer.Ordinal);
            var overlapGroups = BuildingOverlapGroups.Create(placement.Placements
                .Where(item => nearIds.Contains(item.SourceFeatureId)).Select(item => (item.SourceFeatureId, item.Footprint)));
            var placements = BuildingPlacement.ShareLowestFloors(placement.Placements, overlapGroups);
            ContourPlan? contour = null;
            if (options.EffectiveContour)
            {
                progress?.Report("등고 단면·도로·기초 판을 계산하고 있습니다…");
                var siteArea = PlanarTriangulation.ToGridGeometry(sitePaths);
                contour = ContourPlan.Build(terrain, roadArea.Outlines, near!, sitePaths,
                    options.ContourIntervalMetres, options.EffectiveRoadDrop, options.NearDepthMetres, options.SeparateRoads,
                    placements.Where(item => nearIds.Contains(item.SourceFeatureId)).Select(item =>
                        new ContourBuilding(item.SourceFeatureId, item.Footprint, item.BaseElevationMetres,
                            !siteArea.Intersects(PlanarTriangulation.ToGridGeometry(item.Footprint)) &&
                            !siteArea.Intersects(PlanarTriangulation.ToGridGeometry(SiteSelection.FeaturePaths(originals[item.SourceFeatureId], projection))))).ToArray(), roadArea.PlanarNearBoundary, overlapGroups, ct, progress);

                result.ContourOmittedAreaSquareMetres = Math.Abs(Clipper.Area(contour.RemovedDetail));
                result.ContourOverlayReassignedAreaSquareMetres = ContourPaths.Area(contour.OverlayRoleChanges);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report("Rhino 형상과 표시망을 준비하고 있습니다…");
            surface.Materialize(scale, Math.Min(.001, tolerance / scale), supports);
            ownedSurfaces.AddRange(surface.Pieces.Select(piece => piece.Geometry));
            foreach (var item in placements)
            {
                ct.ThrowIfCancellationRequested();
                double floor = item.BaseElevationMetres;
                if (contour is not null)
                {
                    if (contour.BuildingBottoms.TryGetValue(item.SourceFeatureId, out double contourFloor)) floor = contourFloor;
                    else if (contour.MinimumGroundElevation(item.Footprint) is { } steppedMinimum)
                        floor = Math.Min(floor, steppedMinimum - 1);
                }
                AddBuilding(item.Feature, nearIds.Contains(item.SourceFeatureId), floor, item.MeanElevationMetres);
            }
            var expectedBuildings = placements.Select(item => item.SourceFeatureId).ToHashSet(StringComparer.Ordinal);
            var actualBuildings = pending.Where(item => item.Tags.GetValueOrDefault("AHA.Role") == "Building")
                .Select(item => item.Tags["AHA.FeatureId"]).ToHashSet(StringComparer.Ordinal);
            if (!actualBuildings.SetEquals(expectedBuildings))
                throw new InvalidOperationException("생성 대상 건물과 실제 출력 건물의 식별자가 일치하지 않습니다.");
            var allSurfaces = surface.Pieces.ToList();
            if (contour is not null)
            {
                var plates = ContourGeometry.Create(contour, terrain, scale, Math.Min(.001, tolerance / scale));
                ownedSurfaces.AddRange(plates.Select(piece => piece.Geometry)); allSurfaces.AddRange(plates);
                result.ContourPlateCount = plates.Count(piece => piece.Geometry is Extrusion);
                foreach (var site in selection.Components)
                foreach (var solid in SiteGeometry.Build(terrain, site, contour.BaseTop, scale, Math.Min(.001, tolerance / scale), supports))
                    pending.Add(new(solid, "Site", site.Boundary.Id, new()
                    {
                        [Aha.Info.InfoData.Key] = TopoObjectInfo.Parcel(site.Parcels, site: true, detailed: true),
                        ["AHA.Role"] = "Site", ["AHA.Source"] = "Selected cadastral parcels and continuous graded terrain",
                        ["AHA.GroundMetres"] = contour.BaseTop.ToString("R", Inv)
                    }));
            }
            roadArea = surface.ConsumedRoadArea;
            foreach (var piece in allSurfaces)
                pending.Add(new(piece.Geometry, piece.Layer, piece.Layer, new()
                {
                    [Aha.Info.InfoData.Key] = TopoObjectInfo.Kind(piece.Role is "Road" or "RoadSide" ? "road" : "land"),
                    ["AHA.Role"] = piece.Role, ["AHA.Zone"] = piece.Zone,
                    ["AHA.Source"] = piece.Role is "Road" or "RoadSide" ? "VWorld dt_d002 road-category parcels + SDK DEM" : "VWorld 3D SDK DEM",
                    ["AHA.RoadBoundaryMethod"] = "Cadastral road parcels; shared modelling regions cleaned independently of original reference curves",
                    ["AHA.ModelPlanRevision"] = ModelPlanPolicy.Revision.ToString(Inv),
                    ["AHA.MinimumModelWidthMetres"] = ModelPlanPolicy.Default.MinimumWidthMetres.ToString("R", Inv),
                    ["AHA.MinimumModelAreaSquareMetres"] = ModelPlanPolicy.Default.MinimumAreaSquareMetres.ToString("R", Inv),
                    ["AHA.SurfaceMethod"] = contour is not null && piece.Zone == "Near"
                        ? piece.Geometry is Extrusion ? "Contour profiles and height intervals; direct Extrusion"
                            : piece.Role == "Connection" ? "Open NURBS outer wall from continuous Far boundary to contour base top" : "Planar road overlay on contour plates"
                        : "Common graded field; shared planar partition; direct Mesh or NURBS",
                    ["AHA.TerrainAsNurbs"] = options.TerrainAsNurbs.ToString(),
                    ["AHA.SeparateRoads"] = options.SeparateRoads.ToString(),
                    ["AHA.RoadDropMetres"] = options.EffectiveRoadDrop.ToString("R", Inv),
                    ["AHA.CellLongitudeDegrees"] = terrain.CellLongitudeDegrees.ToString("R", Inv),
                    ["AHA.CellLatitudeDegrees"] = terrain.CellLatitudeDegrees.ToString("R", Inv),
                    ["AHA.DisplayMeshHeightToleranceMetres"] = "0.05", ["AHA.BoundaryToleranceMetres"] = "0.001",
                    ["AHA.PlanarPrecisionMetres"] = "0.000001",
                    ["AHA.DroppedZeroAreaBranches"] = surface.DroppedZeroAreaBranches.ToString(Inv),
                    ["AHA.Elevation"] = result.RoadGrading.CrossSectionSamples == 0 ? "Source elevation field" : "Source elevation field with estimated road grading",
                    ["AHA.RoadGradingMethod"] = "Opposing cadastral boundary cross sections; reference heights retained with bounded DEM-node correction",
                    ["AHA.RoadGradingMaximumChangeMetres"] = result.RoadGrading.MaximumAdjustmentMetres.ToString("R", Inv),
                    ["AHA.RoadGradingConverged"] = result.RoadGrading.Converged.ToString()
                }));
            result.TerrainVertices = terrain.Heights.Length;
            result.SurfaceCount = allSurfaces.Count(piece => piece.Role != "RoadSide");
            result.NearSurfaces = allSurfaces.Count(piece => piece.Zone == "Near" && piece.Role != "RoadSide");
            result.FarSurfaces = allSurfaces.Count(piece => piece.Zone == "Far" && piece.Role != "RoadSide");
            result.NearSolidCount = contour is null ? surface.NearSolidCount : allSurfaces.Count(piece => piece.Geometry is Extrusion);
            result.NearBottomElevationMetres = contour?.BaseBottom ?? surface.BottomElevationMetres;
            result.DisplayTriangles = surface.DisplayTriangles;
            result.SurfacePlanAreaResidualSquareMetres = surface.PlanAreaResidualSquareMetres;
            result.MaximumBoundaryChordErrorMetres = surface.MaximumBoundaryChordErrorMetres;
            result.MaximumDisplayHeightErrorMetres = surface.MaximumDisplayHeightErrorMetres;
            result.DroppedZeroAreaBranches = surface.DroppedZeroAreaBranches;
            result.MergedPatchBlocks = surface.MergedPatchBlocks;
            result.PartitionReassignedAreaSquareMetres = surface.ReassignedRoleAreaSquareMetres;
            result.RoadParcels = roadArea.Diagnostics.RoadParcelCount;
            result.RoadDiagnostics = roadArea.Diagnostics;
            result.PlanCleanup = roadArea.Diagnostics.ModelCleanup is { } cleanup ? new[] { cleanup } : Array.Empty<ModelPlanDiagnostics>();
            result.RoadAreaSquareMetres = roadArea.AreaSquareMetres;
            result.RoadSurfaces = allSurfaces.Count(p => p.Role == "Road");
            foreach (var item in PlanarReferences.CreateObjects(references, projection, scale, buildingInfo))
                pending.Add(new(item.Geometry, item.Path, item.Name, new(item.Tags)));
            if (contour is null)
            foreach (var reference in references.Paths.Where(reference => reference.Layer == "TargetBoundary"))
            {
                var tags = new Dictionary<string, string>(reference.Tags)
                {
                    ["AHA.CurveSpace"] = "Projected",
                    ["AHA.ElevationMethod"] = "Continuous graded land/road surface, including step crossings",
                    [Aha.Info.InfoData.Key] = TopoObjectInfo.WithSite(reference.Tags[Aha.Info.InfoData.Key], reference.IsTargetOuter)
                };
                pending.Add(new(new PolylineCurve(surface.Drape(reference.Path, scale)), "TargetBoundary", reference.Name, tags));
            }
            if (result.UnclassifiedBuildings > 0)
                result.Warnings.Add($"건물 {result.UnclassifiedBuildings}개의 지상·지하 구분을 확인하지 못했습니다. 해당 객체에 미확인 상태를 기록하고 기존 높이 규칙을 적용했습니다.");
            if (result.NearBuildings + result.FarBuildings + result.UnzonedBuildings > 0)
                result.Warnings.Add("건물 지붕은 연속 지형의 면적 가중평균에 원천·추정 높이를 더합니다. 기초는 모델링 표현이며 실제 기초·지하층 정보가 아닙니다.");
            if (result.RoadParcels > 0) result.Warnings.Add("도로 경계는 지적도의 도로 부지입니다. 포장면·보도 구분과 단차·교량·터널 높이는 실측값이 아닙니다.");
            if (result.RoadParcels > 0)
                result.Warnings.Add($"모델 면은 {ModelPlanPolicy.Default.MinimumWidthMetres * 1000:N0}mm 기준으로 미세 형상을 정리했습니다. 원본 필지선과 대지경계선은 보존합니다.");
            if (result.RoadDiagnostics.UnknownCategoryCount > 0)
                result.Warnings.Add($"지목 미확인 필지 {result.RoadDiagnostics.UnknownCategoryCount}개는 도로로 분류하지 않았습니다.");
            if (result.RoadDiagnostics.RepairedParcelCount > 0)
                result.Warnings.Add($"원천 필지 {result.RoadDiagnostics.RepairedParcelCount}개의 교차 경계를 정리해 면을 구성했습니다. 원본 필지는 보존합니다.");
            if (result.RoadDiagnostics.ConflictAreaSquareMetres > 0)
                result.Warnings.Add($"원천에서 도로와 다른 필지가 겹치는 {result.RoadDiagnostics.ConflictAreaSquareMetres:N2}m²는 도로 분류에서 제외했습니다.");
            var objects = pending.Select(item =>
            {
                string role = item.Tags.GetValueOrDefault("AHA.Role", "");
                string leaf = role switch
                {
                    "Land" => "Terrain", "Road" => "Roads", "RoadSide" => "RoadSides",
                    "Base" => "Base", "Foundation" => "Foundations", "Parapet" => "Parapets", "Connection" => "Connections",
                    "Building" => item.Tags.GetValueOrDefault("AHA.HeightSource") switch
                    { "source_height" => "SourceHeight", "floor_estimate" => "FloorEstimate", _ => "DefaultEstimate" },
                    _ => item.Layer
                };
                bool reference = item.Geometry is Curve;
                string zone = item.Tags.GetValueOrDefault("AHA.Zone", "All");
                string prefix = zone is "Near" or "Far" ? zone + "::" : "";
                string path = role == "Site" ? "Site" : reference ? item.Layer
                    : prefix + (role is "Building" or "Foundation" or "Parapet" ? "Buildings::" : "Surfaces::") + leaf;
                return new ModelDocumentObject(item.Geometry, path, item.Name, item.Tags, item.ExpectedSolid);
            }).ToArray();
            ct.ThrowIfCancellationRequested();
            progress?.Report("준비 형상을 검증하고 있습니다…");
            ct.ThrowIfCancellationRequested();
            var initialPaths = contour is not null
                ? new[] { "Site", "Near::Surfaces", "Near::Buildings", "Far::Surfaces", "Far::Buildings", "XY" }
                : options.UseNearRegion
                    ? new[] { "TargetBoundary", "Near::Surfaces", "Near::Buildings", "Far::Surfaces", "Far::Buildings", "XY" }
                    : new[] { "TargetBoundary", "Surfaces", "Buildings", "XY" };
            var prepared = new PreparedModel<ModelResult>(context, result, objects, initialPaths, new Dictionary<string, string>
            {
                ["AHA.UseNearRegion"] = options.UseNearRegion.ToString(),
                ["AHA.Contour"] = options.EffectiveContour.ToString(),
                ["AHA.ContourIntervalMetres"] = options.ContourIntervalMetres.ToString("R", Inv),
                ["AHA.SeparateFoundations"] = options.SeparateFoundations.ToString(),
                ["AHA.Parapets"] = options.EffectiveParapets.ToString(),
                ["AHA.ParapetWidthMetres"] = options.ParapetWidthMetres.ToString("R", Inv),
                ["AHA.ParapetHeightMetres"] = options.ParapetHeightMetres.ToString("R", Inv),
                ["AHA.NearRatio"] = options.UseNearRegion ? options.NearRatio.ToString("R", Inv) : "",
                ["AHA.NearDepthMetres"] = options.UseNearRegion ? options.NearDepthMetres.ToString("R", Inv) : "",
                ["AHA.UndergroundBuildingsExcluded"] = result.UndergroundBuildings.ToString(Inv),
                ["AHA.UnclassifiedBuildings"] = result.UnclassifiedBuildings.ToString(Inv),
                ["AHA.OriginLongitude"] = origin.Lon.ToString("R", Inv),
                ["AHA.OriginLatitude"] = origin.Lat.ToString("R", Inv),
                ["AHA.Coordinates"] = "Local XY from WGS84 origin; elevation Z; document units",
                ["AHA.BoundsWGS84"] = string.Join(",", new[] { bounds.West, bounds.South, bounds.East, bounds.North }.Select(v => v.ToString("R", Inv)))
            }, value => value, ct: ct, progress: progress);
            transferred = true;
            return prepared;

            void AddBuilding(GeoFeature building, bool isNear, double ground, double referenceMean)
            {
                var built = new List<(GeometryBase Geometry, string Role, SolidExpectation? ExpectedSolid)>();
                try
                {
                    var decision = BuildingRules.Resolve(building, options);
                    double roof = referenceMean + decision.Height;
                    var footprint = BuildingGeometry.PrepareFootprint(building, projection);
                    double footprintArea = Geo.Area(building, projection);
                    string info = TopoObjectInfo.Building(building, decision.Height, decision.Source, detailed: isNear);
                    if (options.SeparateFoundations)
                    {
                        Create(referenceMean, roof, false);
                        Create(ground, referenceMean, true);
                    }
                    else Create(ground, roof, false);
                    if (isNear && options.EffectiveParapets)
                        built.AddRange(BuildingGeometry.PrepareEditable(ParapetProfiles.Create(footprint.Roof, options.ParapetWidthMetres),
                            roof, options.ParapetHeightMetres, scale, tolerance).Select(item => ((GeometryBase)item.Geometry, "Parapet", (SolidExpectation?)item.Expectation)));
                    string zone = near is null ? "All" : isNear ? "Near" : "Far";
                    foreach (var item in built)
                    {
                        bool foundation = item.Role == "Foundation", parapet = item.Role == "Parapet";
                        double objectBottom = parapet ? roof : foundation || !options.SeparateFoundations ? ground : referenceMean;
                        double objectTop = parapet ? roof + options.ParapetHeightMetres : foundation ? referenceMean : roof;
                        var tags = new Dictionary<string, string>
                        {
                            ["AHA.Role"] = item.Role, ["AHA.Zone"] = zone,
                            ["AHA.Source"] = "VWorld " + building.SourceLayer, ["AHA.SourceLayer"] = building.SourceLayer,
                            ["AHA.FeatureId"] = building.Id, [Aha.Info.InfoData.Key] = parapet ? TopoObjectInfo.Kind("parapet") : info,
                            ["AHA.BuildingPositionCode"] = building.BuildingPositionCode,
                            ["AHA.BuildingClassification"] = building.BuildingClassification.ToString(),
                            ["AHA.RawUndergroundFloors"] = building.UndergroundFloors?.ToString("R", Inv) ?? "missing",
                            ["AHA.HeightMetres"] = decision.Height.ToString("R", Inv), ["AHA.HeightSource"] = decision.Source,
                            ["AHA.RawSourceHeight"] = building.Height?.ToString("R", Inv) ?? "missing",
                            ["AHA.RawSourceFloors"] = building.Floors?.ToString("R", Inv) ?? "missing",
                            ["AHA.DefaultHeightSettingMetres"] = options.UnknownHeight.ToString("R", Inv),
                            ["AHA.FloorHeightSettingMetres"] = options.FloorHeight.ToString("R", Inv),
                            ["AHA.MinAreaSettingSquareMetres"] = options.MinArea.ToString("R", Inv),
                            ["AHA.GroundMetres"] = objectBottom.ToString("R", Inv),
                            ["AHA.FoundationBottomMetres"] = ground.ToString("R", Inv),
                            ["AHA.ReferenceMeanElevationMetres"] = referenceMean.ToString("R", Inv),
                            ["AHA.ModelHeightMetres"] = (objectTop - objectBottom).ToString("R", Inv),
                            ["AHA.RoofElevationMetres"] = roof.ToString("R", Inv),
                            ["AHA.GroundMethod"] = parapet ? "Roof-edge inset band above the existing flat roof"
                                : "Whole-footprint minimum minus 1m, or exposed contour-plate bottom; roof uses pre-contour area-weighted mean plus source/estimated height",
                            ["AHA.FootprintAreaSquareMetres"] = footprintArea.ToString("R", Inv),
                            ["AHA.SelectionRule"] = isNear ? "Original and model footprint strictly inside Near" : "Whole-feature centroid inside output; full footprint retained"
                        };
                        if (parapet)
                        {
                            tags["AHA.ParapetWidthMetres"] = options.ParapetWidthMetres.ToString("R", Inv);
                            tags["AHA.ParapetHeightMetres"] = options.ParapetHeightMetres.ToString("R", Inv);
                            tags["AHA.TopMetres"] = objectTop.ToString("R", Inv);
                        }
                        pending.Add(new(item.Geometry, "Buildings", item.Role + "_" + building.Id, tags, item.ExpectedSolid));
                    }
                    built.Clear(); buildingInfo[building.Id] = info;
                    if (!BuildingRules.HasKnownPosition(building)) result.UnclassifiedBuildings++;
                    if (near is null) result.UnzonedBuildings++; else if (isNear) result.NearBuildings++; else result.FarBuildings++;
                    if (decision.Source == "source_height") result.KnownBuildings++;
                    else if (decision.Source == "floor_estimate") result.FloorEstimatedBuildings++; else result.DefaultEstimatedBuildings++;

                    void Create(double bottom, double top, bool foundation)
                    {
                        if (isNear || options.BuildingsAsNurbs)
                            built.AddRange(BuildingGeometry.PrepareEditable(footprint.Profiles, bottom, top - bottom, scale, tolerance)
                                .Select(item => ((GeometryBase)item.Geometry, foundation ? "Foundation" : "Building", (SolidExpectation?)item.Expectation)));
                        else built.Add((BuildingGeometry.CreateMesh(footprint, bottom, top - bottom, scale), foundation ? "Foundation" : "Building", null));
                    }
                }
                catch
                {
                    foreach (var item in built) item.Geometry.Dispose();
                    throw;
                }
            }
            void RejectBuilding(string id, string reason)
            {
                result.InvalidBuildings++; result.InvalidBuildingIds.Add(id);
                result.Warnings.Add("건물 " + id + "의 유효하지 않은 모델 윤곽 제외: " + reason + " 원본 XY는 유지합니다.");
            }

        }
        finally
        {
            var retained = transferred ? pending.Select(item => item.Geometry).ToHashSet() : new HashSet<GeometryBase>();
            foreach (var geometry in pending.Select(item => item.Geometry).Concat(ownedSurfaces).Distinct())
                if (!retained.Contains(geometry)) geometry.Dispose();
        }
    }

}
