using System.Diagnostics;
using Clipper2Lib;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>Runs production geometry from saved normalized inputs without network or active-document changes.</summary>
public static class SurfaceCheckRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static volatile bool running;

    private static Dictionary<string, object?> RecordedContour()
    {
        return RecordedContourGeometryChecks.Run();
    }

    public static string Start(string selection = "All", string? outputDirectory = null)
    {
        if (running) throw new InvalidOperationException("Surface check is already running.");
        if ((selection is "Recorded" or "Replay" or "RecordedContour") && !FixtureData.Recorded)
            throw new InvalidOperationException("Recorded regional inputs require the local fixture check scope.");
        string directory = Path.Combine(Path.GetFullPath(outputDirectory ?? Path.Combine(Path.GetTempPath(), "aha-topo-checks")), DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "../latest.txt"), directory);
        running = true;
        try { _ = Task.Run(() => Run(directory, selection)); }
        catch { running = false; throw; }
        return directory;
    }

    private static async Task Run(string directory, string selection)
    {
        var records = new List<Dictionary<string, object?>>();
        var report = new Dictionary<string, object?>
        {
            ["status"] = "running", ["fixture_directory"] = RegionalFixtures.DirectoryPath, ["output_directory"] = directory,
            ["rhino"] = RhinoApp.Version.ToString(), ["assembly"] = typeof(SurfaceCheckRunner).Assembly.GetName().Name,
            ["active_document_changed"] = false, ["cases"] = records,
            ["fixture_scope"] = FixtureData.Recorded ? "recorded-local" : "public-synthetic",
            ["replay_scope"] = FixtureData.Recorded
                ? "Normalized regional features and DEM samples are loaded from the local fixture manifest with checksum and dimension checks."
                : "Synthetic geometry is generated without regional source data or network requests."
        };
        void Save() => File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, Json));
        void Log(string text) { File.AppendAllText(Path.Combine(directory, "progress.log"), DateTimeOffset.UtcNow.ToString("O") + " " + text + "\n"); Save(); }
        try
        {
            Save();
            var tests=selection is "All" or "Synthetic"?new[]{"NearRoadCleanup","Coverage","Contour","Parapets","XY","Transaction","HullGeometry","Helpers","BuildingClassification","MeshEfficiency","Synthetic-m","Synthetic-mm","Synthetic-disabled-unused-settings",
                "Synthetic-ratio-below-minimum","Synthetic-near-overrun","Synthetic-noninteger-grid-m","Synthetic-noninteger-grid-mm",
                "Synthetic-multi-site-m","Synthetic-multi-site-mm"}:Array.Empty<string>();
            if(tests.Length>0||selection=="Matrix")
            {
                var matrix=from near in new[]{"off","on"} from surface in new[]{"MeshTerrain","NurbsTerrain"}
                    from road in new[]{"Split","Overlay"} from unit in new[]{"m","mm"} select $"Synthetic-{near}-{surface}-{road}-{unit}";
                tests=tests.Concat(matrix).ToArray();
                if(selection!="Matrix")tests=tests.Concat(new[]{"Synthetic-on-MeshTerrain-NurbsBuildings-m","Synthetic-off-NurbsTerrain-NurbsBuildings-mm",
                    "Synthetic-on-no-lowering-m","Synthetic-on-drop-value-mm","Synthetic-on-drop-small-m","Synthetic-on-drop-small-mm",
                    "Synthetic-off-drop-small-m","Synthetic-on-drop-zero-m","Synthetic-on-minimum-ratio-m","Synthetic-on-depth-mm",
                    "Synthetic-on-target-adjacent-m","Synthetic-on-target-adjacent-mm","Synthetic-on-target-disjoint-m","Synthetic-on-target-disjoint-mm",
                    "Synthetic-on-foundations-m","Synthetic-on-foundations-mm",
                    "Synthetic-on-contour-m","Synthetic-on-contour-mm","Synthetic-on-contour-foundations-m",
                    "Synthetic-on-contour-Overlay-m","Synthetic-on-contour-NurbsTerrain-mm",
                    "Synthetic-on-roads-disabled-m","Synthetic-off-roads-disabled-mm"}).ToArray();
            }
            if(selection=="Recorded")tests=new[]{"BoundaryModes","RecordedContour","SeongsuContact-near","SeongsuContact-contour","JongnoJunction-near","JongnoJunction-contour","Helpers"};
            bool singleSynthetic=selection is "NearRoadCleanup" or "Coverage" or "Contour" or "Parapets" or "RecordedContour" or "BoundaryModes" or "XY" or "Transaction" or "Helpers" or "HullGeometry" or "BuildingClassification" or "MeshEfficiency"||selection.StartsWith("Synthetic-",StringComparison.Ordinal);
            if(singleSynthetic)tests=new[]{selection};
            if(selection=="TargetBoundaries")tests=new[]{"Synthetic-on-target-adjacent-m","Synthetic-on-target-adjacent-mm",
                "Synthetic-on-target-disjoint-m","Synthetic-on-target-disjoint-mm","Synthetic-multi-site-m","Synthetic-multi-site-mm"};
            if(selection=="LayerLayout")tests=new[]{"Synthetic-off-MeshTerrain-Split-m","Synthetic-on-MeshTerrain-Split-m",
                "Synthetic-off-MeshTerrain-Split-mm","Synthetic-on-MeshTerrain-Split-mm"};
            var areas = selection is "Recorded" or "Replay" ? new[] { "Gamcheon", "Gamcheon-near", "Gamcheon-contour", "Jeongneung", "Jeongneung-near", "Jeongneung-contour", "Dodong", "Dodong-near", "SeoulBroad", "SeoulBroad-near", "GunpoMulti", "GunpoMulti-near", "Bulgwang-near", "Bulgwang-contour" }
                : selection is "All" or "Synthetic" or "TargetBoundaries" or "LayerLayout" or "Matrix" || singleSynthetic ? Array.Empty<string>() : new[] { selection };
            foreach (var name in tests.Concat(areas))
            {
                var record = new Dictionary<string, object?> { ["id"] = name, ["status"] = "running" };
                records.Add(record); Log(name + " started");
                var timer = Stopwatch.StartNew();
                try
                {
                    var result=await OnUi(()=>name=="NearRoadCleanup"?NearRoadCleanupChecks.Run():name=="Coverage"?CoverageGeometryChecks.Run():name=="Contour"?ContourGeometryChecks.Run():name=="RecordedContour"?RecordedContour():name=="BoundaryModes"?BoundaryModes(directory,stage=>{record["stage"]=stage;Log(name+" "+stage);}):name=="XY"?XYChecks.Run():name=="Transaction"?TransactionChecks.Run():name=="HullGeometry"?HullGeometryChecks.Run():name=="Helpers"?SurfaceHelperChecks.Run(directory)
                        :name=="BuildingClassification"?BuildingClassificationChecks.Run(directory)
                        :name=="MeshEfficiency"?MeshEfficiencyChecks.Check():name=="Parapets"?ParapetGeometryChecks.Run()
                        :name.StartsWith("Synthetic",StringComparison.Ordinal)?Synthetic(name,directory):Replay(name,directory,
                            stage=>{record["stage"]=stage;Log(name+" "+stage);}));
                    foreach (var item in result) record[item.Key] = item.Value;
                    record["status"] = (bool)record["geometry_checks_passed"]! ? "passed" : "failed_checks";
                }
                catch (Exception error)
                {
                    record["status"] = "failed"; record["error"] = error.ToString();
                }
                record["elapsed_seconds"] = timer.Elapsed.TotalSeconds;
                Log(name + " " + record["status"]);
            }
            foreach (var record in records.Where(r => ((string)r["id"]!).EndsWith("-near", StringComparison.Ordinal)||((string)r["id"]!).Contains("-on-",StringComparison.Ordinal)))
            {
                string id=(string)record["id"]!;
                string baselineId=id.EndsWith("-near",StringComparison.Ordinal)?id[..^5]:id.Replace("-on-","-off-",StringComparison.Ordinal);
                var baseline = records.SingleOrDefault(r => (string)r["id"]! == baselineId);
                if (baseline is null) continue;
                // A Near cut may legitimately require additional sub-detail road cleanup.
                // Each run is still audited against its resolved plan and graded field.
                if (record.TryGetValue("near_plan_adjusted", out var adjusted) && (bool)adjusted!)
                {
                    record["off_baseline_comparison"] = "near_boundary_changes_road_partition";
                    continue;
                }
                bool invariant = record.TryGetValue("graded_field_sha256",out var actual) && baseline.TryGetValue("graded_field_sha256",out var expected)
                    && (string)actual! == (string)expected!
                    && record.TryGetValue("model_plan_cleanup",out var plan)&&baseline.TryGetValue("model_plan_cleanup",out var expectedPlan)
                    &&JsonSerializer.Serialize(plan,Json)==JsonSerializer.Serialize(expectedPlan,Json);
                record["one_field_and_plan_match_off_baseline"] = invariant;
                if (!invariant && (string)record["status"]! == "passed") record["status"] = "failed_checks";
            }
            report["status"] = records.All(c => (string)c["status"]! == "passed") ? "passed" : "completed_with_failures";
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        finally
        {
            if ((string)report["status"]! == "passed")
                foreach (var file in Directory.EnumerateFiles(directory, "*.3dm")) File.Delete(file);
            report["finished_at"] = DateTimeOffset.UtcNow; Save(); running = false;
        }
    }

    private static Dictionary<string, object?> BoundaryModes(string directory, Action<string> progress)
    {
        // The source building crosses the exact Near boundary and must remain Far
        // in both terrain representations, with its whole-footprint height statistics.
        var cases = new List<Dictionary<string, object?>>();
        foreach (var units in new[] { UnitSystem.Meters, UnitSystem.Millimeters })
        foreach (bool contour in new[] { false, true })
        {
            string id = $"BoundaryModes-{(contour ? "contour" : "continuous")}-{(units == UnitSystem.Meters ? "m" : "mm")}";
            progress(id);
            var result = Replay("JongnoBoundary-near", directory, stage => progress(id + " " + stage), units, contour, id);
            bool buildingPreserved = (int)result["far_building_objects"]! == 1
                && (int)result["checked_building_features"]! == 1;
            result["id"] = id;
            result["contour_enabled"] = contour;
            result["crossing_building_preserved"] = buildingPreserved;
            result["geometry_checks_passed"] = (bool)result["geometry_checks_passed"]! && buildingPreserved;
            cases.Add(result);
        }
        return new() { ["geometry_checks_passed"] = cases.All(result => (bool)result["geometry_checks_passed"]!),
            ["cases"] = cases };
    }

    private static Dictionary<string, object?> Replay(string name, string directory, Action<string> progress,
        UnitSystem? units = null, bool? contour = null, string? outputName = null)
    {
        bool contourName = name.EndsWith("-contour", StringComparison.Ordinal);
        bool useNear = contourName || name.EndsWith("-near", StringComparison.Ordinal);
        string sourceName = contourName ? name[..^8] : useNear ? name[..^5] : name;
        progress("reading normalized regional fixture");
        var inputTimer = Stopwatch.StartNew();
        var input = RegionalFixtures.Load(sourceName);
        var origin = input.Origin; var bounds = input.Bounds;
        var projection = new LocalProjection(origin);
        var field = TerrainField.FromSamples(input.CalculationBounds ?? bounds, projection, input.Terrain.Columns, input.Terrain.Rows, input.Terrain.Heights);
        var targets = input.Targets;
        var buildings = input.Buildings; var parcels = input.Parcels;
        double inputSeconds = inputTimer.Elapsed.TotalSeconds;
        var options = (input.Options ?? new ModelOptions()) with { UseNearRegion = useNear };
        options = options with { Contour = contour ?? contourName };
        var selection = SiteSelection.Create(targets, projection);
        if (input.CalculationBounds is null) bounds = CoveredOutput(field, selection, buildings, options);
        var roads = RoadGeometry.Build(options.IncludeRoads ? parcels : Array.Empty<GeoFeature>(), projection, field.Bounds, bounds,
            useNear ? EditRegion.Create(selection, projection, bounds, options.NearRatio) : null);
        var originalHeights = field.Heights.ToArray();
        progress("computing independent road-grading reference");
        var grading = RoadGrading.Apply(field, roads);
        progress("building production geometry");
        var timer = Stopwatch.StartNew();
        using var fixture=BuildFixture(outputName??name,directory,units??Enum.Parse<UnitSystem>(input.Units),origin,bounds,targets,parcels,buildings,field,options,useNear,input.DocumentToleranceMetres);
        var doc=fixture.Doc; var result=fixture.Result;
        double buildSeconds = fixture.BuildSeconds;
        timer.Restart();
        progress("auditing actual output geometry");
        var output = AuditFixture(fixture,bounds,projection,grading.Field,buildings,options,parcels,progress);
        output["geometry_audit_seconds"] = timer.Elapsed.TotalSeconds;
        AddFixtureChecks(output,fixture);
        output["saved_input_read_and_prepare_seconds"] = inputSeconds;
        output["geometry_build_seconds"] = buildSeconds;
        output["performance_scope"] = "Geometry build excludes saved-source reading, native geometry auditing, 3dm export, and exported-file readback. Output bytes include terrain, roads, road sides, buildings, and parcel/target curves with render meshes.";
        output["source_grid_columns"] = input.Terrain.Columns; output["source_grid_rows"] = input.Terrain.Rows;
        output["input_buildings"] = buildings.Count; output["recovered_road_parcels"] = parcels.Count(p => p.LandCategoryCode == 14);
        output["fixture_checksum_dimensions_and_counts_passed"] = true;
        output["document_absolute_tolerance_metres"] = input.DocumentToleranceMetres;
        if (!options.EffectiveContour) AddSelectionChecks(output, doc, selection, projection, result.ObjectIds, parcels);
        AddGradingChecks(output,result,field,originalHeights,grading);
        WriteModel(doc, Path.Combine(directory, (outputName??name) + ".3dm"), output, progress);
        progress("finished");
        return output;
    }

    private static GeoBounds CoveredOutput(TerrainField field, SiteSelection selection, IReadOnlyList<GeoFeature> buildings, ModelOptions options)
    {
        // Fixed samples model an acquisition envelope, not a promise that every
        // source building at its outer edge has complete ground. Select an inset
        // output window while retaining the full source list and exact samples.
        for(int cells=0; cells<Math.Min(field.Columns,field.Rows)/2; cells++)
        {
            var sw=field.Projection.ToGeo(field.X0+cells*field.Dx,field.Y0+cells*field.Dy);
            var ne=field.Projection.ToGeo(field.X1-cells*field.Dx,field.Y1-cells*field.Dy);
            var candidate=new GeoBounds(sw.Lon,sw.Lat,ne.Lon,ne.Lat);
            if(selection.Boundary.Polygons.SelectMany(p=>p).SelectMany(r=>r).Any(p=>
                p.Lon<candidate.West||p.Lon>candidate.East||p.Lat<candidate.South||p.Lat>candidate.North))break;
            GeoBounds coverage;
            try{coverage=TerrainCoverage.For(selection,field.Projection,candidate,buildings,options);}
            catch(ArgumentException){break;}
            if(coverage.West>=field.Bounds.West-1e-10&&coverage.South>=field.Bounds.South-1e-10
                &&coverage.East<=field.Bounds.East+1e-10&&coverage.North<=field.Bounds.North+1e-10)return candidate;
        }
        throw new InvalidDataException("Registered terrain samples cannot cover every generated building while containing the selected Near region.");
    }

    private static Dictionary<string, object?> Synthetic(string name, string directory)
    {
        var origin = new GeoPoint(127, 37.5); var projection = new LocalProjection(origin); var bounds = projection.Bounds(100);
        bool nonintegerGrid = name.StartsWith("Synthetic-noninteger-grid", StringComparison.Ordinal);
        int columns = nonintegerGrid ? 7 : 8, rows = nonintegerGrid ? 9 : 8;
        var heights = Enumerable.Range(0, (columns + 1) * (rows + 1)).Select(i =>
        {
            double x = -100 + (i % (columns + 1)) * 200.0 / columns, y = -100 + (i / (columns + 1)) * 200.0 / rows;
            // Quadratic node heights produce real slope breaks between bilinear
            // cells, unlike the globally bilinear baseline used by older checks.
            return 20 + 0.03 * x + 0.07 * y + 0.001 * x * y + (nonintegerGrid ? 0.003 * x * x + 0.0005 * y * y : 0);
        }).ToArray();
        var field = TerrainField.FromSamples(bounds, projection, columns, rows, heights);
        // Keep synthetic identities separate from schema-valid parcel numbers.
        int parcelNumber = 0;
        GeoFeature Feature(string id, double x0, double y0, double x1, double y1) => new()
        {
            Id = id, Pnu = $"99000000001{++parcelNumber:D4}0000", SourceLayer = VWorldClient.BuildingSourceLayer, Floors = 3,
            ParcelLabel = id + " 대", LandCategoryCode = 8,
            Polygons = new() { new() { new() { projection.ToGeo(x0, y0), projection.ToGeo(x1, y0), projection.ToGeo(x1, y1), projection.ToGeo(x0, y1), projection.ToGeo(x0, y0) } } }
        };
        var target = Feature("target", -10, -10, 10, 10);
        bool multiple = name.StartsWith("Synthetic-multi-site", StringComparison.Ordinal);
        var targets = multiple ? new[] { Feature("selected-a",-45,-5,-35,5), Feature("selected-b",-35,-5,-25,5),
            Feature("selected-c",-5,-5,5,5), Feature("selected-d",45,-5,55,5) }
            : name.Contains("target-adjacent",StringComparison.Ordinal) ? new[]{Feature("selected-left",-10,-10,0,10),Feature("selected-right",0,-10,10,10)}
            : name.Contains("target-disjoint",StringComparison.Ordinal) ? new[]{Feature("selected-left",-35,-10,-15,10),Feature("selected-right",15,-10,35,10)} : new[] { target };
        var selection = SiteSelection.Create(targets,projection);
        // Place this footprint across the rightmost analytic circle boundary,
        // including translated/multiple sites rather than assuming centre X=0.
        double edgeX = (multiple ? 50 : name.Contains("target-disjoint",StringComparison.Ordinal) ? 25 : 0)
            + (name.Contains("minimum-ratio",StringComparison.Ordinal) ? 1.5 : 2) * Math.Sqrt(multiple ? 50 : 200);
        var buildings = new[] { name.Contains("contour",StringComparison.Ordinal) ? Feature("near", 15, -3, 21, 3) : Feature("near", -3, -3, 3, 3), Feature("crossing", edgeX-1, 0, edgeX+4, 5), Feature("far", 80, 60, 90, 70) };
        var roadParcel = new GeoFeature { Id = "crossing-road-parcel", Pnu = "9900000000199990000",
            SourceLayer = "dt_d002", ParcelLabel = "1 도", LandCategoryCode = 14,
            Polygons = new() { new() { new() { projection.ToGeo(-100, 5), projection.ToGeo(100, 5),
                projection.ToGeo(100, 11), projection.ToGeo(-100, 11), projection.ToGeo(-100, 5) } } } };
        var parcels = (nonintegerGrid ? new[]
        {
            Feature("crossing-grid-road-near", -15, -90, 15, 90),
            new GeoFeature { Id = "crossing-aoi-diagonal", Pnu = "9900000000199980000", Polygons = new()
                { new() { new() { projection.ToGeo(-130,-80), projection.ToGeo(70,-40), projection.ToGeo(120,90), projection.ToGeo(-80,65), projection.ToGeo(-130,-80) } } } }
        } : new[] { Feature("context-parcel",-95,-90,-85,-80) }).Concat(targets).Append(roadParcel).ToArray();
        var unit = name.EndsWith("-mm", StringComparison.Ordinal) ? UnitSystem.Millimeters : UnitSystem.Meters;
        bool useNear=name.Contains("-on-",StringComparison.Ordinal)||multiple;
        var options=new ModelOptions(MinArea:0,NearRatio:name=="Synthetic-disabled-unused-settings" ? 0
            : name.Contains("minimum-ratio",StringComparison.Ordinal) ? 1.5 : 2,
            NearDepthMetres:name.Contains("-depth",StringComparison.Ordinal) ? 7.5 : 30,
            RoadDropMetres:name.Contains("drop-value",StringComparison.Ordinal) ? .35
                : name.Contains("drop-small",StringComparison.Ordinal) ? .05 : name.Contains("drop-zero",StringComparison.Ordinal) ? 0 : .2,
            UseNearRegion:useNear,TerrainAsNurbs:name.Contains("NurbsTerrain",StringComparison.Ordinal),
            BuildingsAsNurbs:name.Contains("NurbsBuildings",StringComparison.Ordinal),
            SeparateRoads:!name.Contains("Overlay",StringComparison.Ordinal),LowerRoads:!name.Contains("no-lowering",StringComparison.Ordinal),
            IncludeRoads:!name.Contains("roads-disabled",StringComparison.Ordinal),
            Contour:name.Contains("contour",StringComparison.Ordinal), SeparateFoundations:name.Contains("foundations",StringComparison.Ordinal));
        if(name is "Synthetic-near-overrun" or "Synthetic-ratio-below-minimum")
        {
            using var rejectedDoc=NewDocument(unit);
            var ids=ModelGeometryChecks.AllObjects(rejectedDoc).Select(o=>o.Id).ToHashSet();
            bool rejected=false;string? reason=null;
            try{CheckModel.Build(rejectedDoc,origin,bounds,targets,parcels,buildings,field,options with{UseNearRegion=true,NearRatio=name=="Synthetic-near-overrun"?20:1.49});}
            catch(Exception error) when(error is InvalidOperationException or ArgumentException){rejected=true;reason=error.Message;}
            bool unchanged=ids.SetEquals(ModelGeometryChecks.AllObjects(rejectedDoc).Select(o=>o.Id)) && TopoWorkflowChecks.Roots(rejectedDoc).Length==0;
            return new(){["geometry_checks_passed"]=rejected&&unchanged,["invalid_region_rejected"]=rejected,["source_unchanged"]=unchanged,["reason"]=reason};
        }
        var roads = RoadGeometry.Build(options.IncludeRoads ? parcels : Array.Empty<GeoFeature>(), projection, bounds, near: useNear
            ? EditRegion.Create(selection, projection, bounds, options.NearRatio) : null);
        var originalHeights = field.Heights.ToArray();
        var grading = RoadGrading.Apply(field, roads);
        var timer = Stopwatch.StartNew();
        using var fixture=BuildFixture(name,directory,unit,origin,bounds,targets,parcels,buildings,field,options,useNear);
        var doc=fixture.Doc; var result=fixture.Result;
        double buildSeconds = fixture.BuildSeconds;
        timer.Restart();
        var output = AuditFixture(fixture,bounds,projection,grading.Field,buildings,options,parcels);
        output["geometry_build_seconds"] = buildSeconds;
        output["geometry_audit_seconds"] = timer.Elapsed.TotalSeconds;
        AddFixtureChecks(output,fixture);
        var objectsById = result.ObjectIds.Select(id=>doc.Objects.FindId(id)!).Where(o => o.Attributes.Name.StartsWith("Building_", StringComparison.Ordinal))
            .GroupBy(o => o.Attributes.GetUserString("AHA.FeatureId")!).ToDictionary(g => g.Key, g => g.ToArray());
        string crossingZone = objectsById["crossing"].First().Attributes.GetUserString("AHA.Zone");
        bool crossedNear = crossingZone == (useNear ? "Far" : "All");
        output["whole_building_zone_classification_passed"] = crossedNear;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && crossedNear;
        bool includedRoads=ModelGeometryChecks.AllObjects(doc).Any(o=>o.Attributes.GetUserString("AHA.Role")=="Road");
        output["include_roads_setting_passed"] = includedRoads == options.IncludeRoads;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && includedRoads == options.IncludeRoads;
        bool settings = result.AppliedSettings.Contour == options.EffectiveContour
            && result.AppliedSettings.SeparateFoundations == options.SeparateFoundations
            && doc.Layers.FindId(result.ParentLayerId)!.GetUserString("AHA.Contour") == options.EffectiveContour.ToString();
        output["applied_settings_and_layer_tags_passed"] = settings;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && settings;
        output["document_units"] = unit.ToString();
        output["source_grid_columns"] = columns; output["source_grid_rows"] = rows;
        if (!options.EffectiveContour) AddSelectionChecks(output, doc, selection, projection, result.ObjectIds, parcels);
        AddGradingChecks(output,result,field,originalHeights,grading);
        var sideNormals=CheckSyntheticSideNormals(doc,roads);
        output["synthetic_road_side_brep_normals_checked"] = sideNormals.Checked;
        output["synthetic_inward_road_side_brep_normals"] = sideNormals.Inward;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && sideNormals.Inward == 0
            && (fixture.IsNear || options.EffectiveRoadDrop == 0 || !options.TerrainAsNurbs || sideNormals.Checked > 0);
        WriteModel(doc, Path.Combine(directory, name + ".3dm"), output);
        return output;
    }

    private sealed class ModelFixture : IDisposable
    {
        public RhinoDoc Doc {get;init;}=null!;
        public ModelResult Result {get;set;}=null!;
        public Dictionary<string,object?> Checks {get;}=new();
        public double BuildSeconds {get;set;}
        public bool IsNear {get;init;}
        public IReadOnlyList<GeoFeature> Targets {get;init;}=Array.Empty<GeoFeature>();
        public void Dispose()=>Doc.Dispose();
    }
    private static ModelFixture BuildFixture(string name,string directory,UnitSystem unit,GeoPoint origin,GeoBounds bounds,
        IReadOnlyList<GeoFeature> targets,IReadOnlyList<GeoFeature> parcels,IReadOnlyList<GeoFeature> buildings,TerrainField field,
        ModelOptions options,bool near,double documentToleranceMetres=.001)
    {
        var fixture=new ModelFixture{Doc=NewDocument(unit,documentToleranceMetres),IsNear=near,Targets=targets};
        try
        {
            using(var curve=new LineCurve(new Point3d(-1,-1,0),new Point3d(1,-1,0)))fixture.Doc.Objects.AddCurve(curve);
            var existing=ModelGeometryChecks.AllObjects(fixture.Doc).ToDictionary(o=>o.Id,o=>(o.Geometry.DataCRC(0),o.Attributes.ToJSON(new SerializationOptions())));
            fixture.Doc.UndoRecordingEnabled=true;uint serial=fixture.Doc.NextUndoRecordSerialNumber;
            string rawBefore=JsonSerializer.Serialize(new{targets,parcels,buildings,field.Heights});
            var timer=Stopwatch.StartNew();fixture.Result=CheckModel.Build(fixture.Doc,origin,bounds,targets,parcels,buildings,field,options with{UseNearRegion=near});
            fixture.BuildSeconds=timer.Elapsed.TotalSeconds;
            bool rawUnchanged=rawBefore==JsonSerializer.Serialize(new{targets,parcels,buildings,field.Heights});
            fixture.Checks["raw_input_geometry_and_heights_unchanged"]=rawUnchanged;
            fixture.Checks["single_model_plan_cleanup_record"]=fixture.Result.PlanCleanup.Count==1;
            foreach(var pair in TopoWorkflowChecks.Transaction(fixture.Doc,fixture.Result,near,serial,existing))fixture.Checks[pair.Key]=pair.Value;
            return fixture;
        }
        catch{fixture.Dispose();throw;}
    }
    private static Dictionary<string,object?> AuditFixture(ModelFixture fixture,GeoBounds bounds,LocalProjection projection,
        TerrainField field,IReadOnlyList<GeoFeature> buildings,ModelOptions options,IReadOnlyList<GeoFeature> parcels,Action<string>? progress=null)
    {
        var near=fixture.IsNear?EditRegion.Create(SiteSelection.Create(fixture.Targets,projection),projection,bounds,options.NearRatio):null;
        var output=options.EffectiveContour
            ? ContourModelChecks.Run(fixture.Doc,fixture.Result,SiteSelection.Create(fixture.Targets,projection),near!,field,buildings,parcels,options)
            : ModelGeometryChecks.Run(fixture.Doc,fixture.Result,bounds,projection,field,buildings,options,progress,parcels,near);
        if(near is not null && !options.EffectiveContour)
        {
            var check=NearGeometryChecks.Run(fixture.Doc,fixture.Result,near,field,options,buildings,parcels);
            foreach(var pair in check)output[pair.Key]=pair.Value;
            output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&(bool)check["near_geometry_checks_passed"]!;
        }
        var references=SourceReferenceChecks.Run(fixture.Doc,fixture.Result,projection,bounds,parcels,buildings,options.IncludeRoads);
        foreach(var pair in references)output[pair.Key]=pair.Value;
        output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&(bool)references["raw_xy_source_geometry_passed"]!;
        return output;
    }

    private static void AddFixtureChecks(Dictionary<string,object?> output,ModelFixture fixture)
    {
        foreach(var check in fixture.Checks)output[check.Key]=check.Value;
        output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&(bool)fixture.Checks["raw_input_geometry_and_heights_unchanged"]!
            &&(bool)fixture.Checks["single_model_plan_cleanup_record"]!;
        if(fixture.Checks.TryGetValue("one_pass_transaction_passed",out var passed))
            output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]! && (bool)passed!;
        {
            bool structure=TopoWorkflowChecks.LayerStructure(fixture.Doc,fixture.Result.ParentLayerId,fixture.IsNear);
            output["topography_layer_structure_passed"]=structure;
            output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&structure;
        }
        if(!fixture.Result.AppliedSettings.EffectiveContour)
        {
            var modelSurfaces=fixture.Result.ObjectIds.Select(id=>fixture.Doc.Objects.FindId(id)!)
                .Where(o=>o.Attributes.GetUserString("AHA.Role") is "Land" or "Road")
                .Select(o=>new SurfacePiece(o.Geometry,"",o.Attributes.GetUserString("AHA.Role")!,o.Attributes.GetUserString("AHA.Zone")!));
            var quality=ModelSurfaceQualityChecks.Check(modelSurfaces,RhinoMath.UnitScale(UnitSystem.Meters,fixture.Doc.ModelUnitSystem));
            output["model_surface_minimum_size"]=quality;
            output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&quality.Passed;
        }
        var curves=CheckPlanCurves(fixture.Doc);
        foreach(var pair in curves)output[pair.Key]=pair.Value;
        output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]! && (bool)curves["xy_reference_curves_passed"]!;
    }

    private static Dictionary<string,object?> CheckPlanCurves(RhinoDoc doc)
    {
        double scale=RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem);
        // Every source reference stays XY; only the target boundary has a projected pair.
        var objects=ModelGeometryChecks.AllObjects(doc).ToArray();
        var xy=objects.Where(o=>o.Attributes.GetUserString("AHA.CurveSpace")=="XY").ToArray();
        var projected=objects.Where(o=>o.Attributes.GetUserString("AHA.CurveSpace")=="Projected")
            .ToLookup(o=>o.Attributes.Name);
        var flatGroups=xy.Where(o=>o.Attributes.Name.StartsWith("XY_",StringComparison.Ordinal)
            &&o.Attributes.GetUserString("AHA.Role")=="TargetBoundary").ToLookup(o=>o.Attributes.Name[3..]);
        int projectedCount=projected.Sum(g=>g.Count());
        int invalid=xy.Count(o=>!o.Attributes.Name.StartsWith("XY_",StringComparison.Ordinal))
            +projected.Sum(g=>g.Count(o=>o.Attributes.GetUserString("AHA.Role")!="TargetBoundary")),matched=0; double maxZ=0;
        bool contour=objects.Any(o=>o.Attributes.GetUserString("AHA.Role")=="Site");
        if(contour ? projectedCount!=0 : !flatGroups.Select(g=>g.Key).ToHashSet(StringComparer.Ordinal).SetEquals(projected.Select(g=>g.Key)))invalid++;
        foreach(var group in contour ? Enumerable.Empty<IGrouping<string,RhinoObject>>() : flatGroups)
        {
            var flat=Segments(group);
            var lifted=Segments(projected[group.Key]);
            if(group.Count()!=projected[group.Key].Count() || flat.Count==0 || !ModelGeometryChecks.BoundaryCoverage(flat,lifted)
                || !ModelGeometryChecks.BoundaryCoverage(lifted,flat))invalid++;
            else matched++;
        }
        foreach(var obj in xy)
        {
            if(obj.Geometry is not Curve curve || !curve.TryGetPolyline(out var line)){invalid++;continue;}
            maxZ=Math.Max(maxZ,line.Max(p=>Math.Abs(p.Z/scale)));
            bool hidden=false;var layer=doc.Layers[obj.Attributes.LayerIndex];
            while(layer is not null)
            { if(!layer.IsVisible)hidden=true; if(layer.ParentLayerId==Guid.Empty)break;layer=doc.Layers.FindId(layer.ParentLayerId); }
            if(!hidden)invalid++;
        }
        return new() { ["xy_reference_curves_passed"]=xy.Length>0 && invalid==0 && maxZ<=1e-12,
            ["xy_curve_objects"]=xy.Length,["projected_curve_objects"]=projectedCount,
            ["xy_matched_projected_groups"]=matched,["xy_curve_errors"]=invalid,["xy_maximum_z_metres"]=maxZ };
        List<(Point3d A,Point3d B)> Segments(IEnumerable<RhinoObject> values)
        {
            var result=new List<(Point3d,Point3d)>();
            foreach(var item in values)
            {
                if(item.Geometry is not Curve curve || !curve.TryGetPolyline(out var line))continue;
                for(int i=0;i+1<line.Count;i++)
                {
                    var a=new Point3d(line[i].X/scale,line[i].Y/scale,0); var b=new Point3d(line[i+1].X/scale,line[i+1].Y/scale,0);
                    if(a.DistanceTo(b)>1e-10)result.Add((a,b));
                }
            }
            return result;
        }
    }

    private static void AddSelectionChecks(Dictionary<string, object?> output, RhinoDoc doc,
        SiteSelection selection, LocalProjection projection,IReadOnlyList<Guid> objectIds,IReadOnlyList<GeoFeature> sourceParcels)
    {
        double scale = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var objects = objectIds.Select(id=>doc.Objects.FindId(id)!).ToArray();
        var targets = objects.Where(o => o.Attributes.GetUserString("AHA.Role")=="TargetBoundary"&&o.Attributes.GetUserString("AHA.CurveSpace")=="Projected").ToArray();
        var selectedIds = selection.Parcels.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var selectedPnus = selection.Parcels.Select(p => p.Pnu).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        bool metadata = targets.Length > 0;
        double maximumError = 0; int componentsChecked = 0;
        foreach (var component in selection.Components)
        {
            var actual = targets.Where(o => o.Attributes.GetUserString("AHA.FeatureId") == component.Boundary.Id).ToArray();
            metadata &= actual.Length > 0;
            var expectedIds = component.Parcels.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var expectedPnus = component.Parcels.Select(p => p.Pnu).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var obj in actual)
            {
                var info = Aha.Info.InfoData.Parse(obj.Attributes.GetUserString(Aha.Info.InfoData.Key)!);
                var parcelInfo = info["data"]!["parcels"]!.AsArray();
                metadata &= parcelInfo.Select(p=>p?["pnu"]?.GetValue<string>()??"").Where(p=>p.Length>0).ToHashSet().SetEquals(expectedPnus);
                metadata &= component.Parcels.All(p=>parcelInfo.Any(q=>q?["pnu"]?.GetValue<string>()==p.Pnu && (q?["address"]?.GetValue<string>()??"")==p.Address));
            }
            var expectedSegments = component.Boundary.Polygons.SelectMany(p => p).SelectMany(r =>
                Enumerable.Range(0, r.Count - 1).Select(i => (A: Point(r[i]), B: Point(r[i + 1])))).ToArray();
            var actualSegments = new List<(Point2d A, Point2d B)>();
            foreach (var obj in actual)
            {
                if (obj.Geometry is not Curve curve || !curve.TryGetPolyline(out var line))
                    throw new InvalidDataException("Target boundary is not a polyline.");
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    var a = new Point2d(line[i].X / scale, line[i].Y / scale);
                    var b = new Point2d(line[i + 1].X / scale, line[i + 1].Y / scale);
                    if (a.DistanceTo(b) > 1e-10) actualSegments.Add((a,b));
                }
            }
            if (actualSegments.Count == 0) { metadata = false; continue; }
            Measure(expectedSegments, actualSegments); Measure(actualSegments, expectedSegments);
            componentsChecked++;
        }
        metadata &= targets.Select(o => o.Attributes.GetUserString("AHA.FeatureId")).Distinct().Count() == selection.Components.Count;
        var individualCurves=objects.Where(o=>o.Attributes.GetUserString("AHA.Role")=="ParcelBoundary"
            &&o.Attributes.GetUserString("AHA.CurveSpace")=="XY").ToArray();
        var selectedOriginals=individualCurves.Where(o=>selectedIds.Contains(o.Attributes.GetUserString("AHA.FeatureId")??"")
            ||(System.Text.Json.Nodes.JsonNode.Parse(o.Attributes.GetUserString(Aha.Info.InfoData.Key)??"{}")?["data"]?["parcels"]?.AsArray()
                .Any(p=>selectedPnus.Contains(p?["pnu"]?.GetValue<string>()??""))??false)).ToArray();
        bool references=sourceParcels.Where(p=>selectedIds.Contains(p.Id)||selectedPnus.Contains(p.Pnu))
            .All(p=>selectedOriginals.Any(o=>o.Attributes.GetUserString("AHA.FeatureId")==p.Id));
        bool passed=metadata&&references&&maximumError<=1e-6&&componentsChecked==selection.Components.Count;
        output["selected_parcel_count"] = selection.Parcels.Count;
        output["site_component_count"] = selection.Components.Count;
        output["target_boundary_object_count"] = targets.Length;
        output["target_source_metadata_complete"] = metadata;
        output["selected_original_parcel_curves"] = selectedOriginals.Length;
        output["original_selected_parcel_references_preserved"] = references;
        output["target_union_boundary_max_xy_error_m"] = maximumError;
        output["target_union_checks_passed"] = passed;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && passed;

        Point2d Point(GeoPoint p) { var xy = projection.ToLocal(p); return new(xy.X, xy.Y); }
        void Measure(IEnumerable<(Point2d A, Point2d B)> source, IEnumerable<(Point2d A, Point2d B)> target)
        {
            var edges = target.ToArray();
            foreach (var edge in source)
            foreach (var p in new[] { edge.A, new Point2d((edge.A.X+edge.B.X)/2, (edge.A.Y+edge.B.Y)/2), edge.B })
                maximumError = Math.Max(maximumError, edges.Min(e => Distance(p,e.A,e.B)));
        }
        static double Distance(Point2d p, Point2d a, Point2d b)
        {
            double dx=b.X-a.X, dy=b.Y-a.Y, length=dx*dx+dy*dy;
            double t=length == 0 ? 0 : Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
            return p.DistanceTo(new(a.X+t*dx,a.Y+t*dy));
        }
    }

    private static void AddGradingChecks(Dictionary<string, object?> output, ModelResult result,
        TerrainField rawField, double[] originalHeights, RoadGradingResult reference)
    {
        bool unchanged = originalHeights.SequenceEqual(rawField.Heights);
        bool sameGrid = result.SurfaceField.Columns == rawField.Columns && result.SurfaceField.Rows == rawField.Rows;
        double fieldError = result.SurfaceField.Heights.Length == reference.Field.Heights.Length
            ? result.SurfaceField.Heights.Zip(reference.Field.Heights, (a,b) => Math.Abs(a-b)).Max() : double.PositiveInfinity;
        var adjustments = result.SurfaceField.Heights.Zip(originalHeights, (a,b) => Math.Abs(a-b)).ToArray();
        double actualMaximumAdjustment = adjustments.Max();
        double actualRmsAdjustment = Math.Sqrt(adjustments.Average(value => value*value));
        bool bounded = actualMaximumAdjustment <= RoadGrading.MaximumNodeAdjustmentMetres + 1e-10;
        bool adjustmentMetrics = Math.Abs(actualMaximumAdjustment-reference.MaximumAdjustmentMetres) <= 1e-10
            && Math.Abs(actualRmsAdjustment-reference.RmsAdjustmentMetres) <= 1e-10;
        bool finite = reference.Field.Heights.All(double.IsFinite);
        bool passed = unchanged && sameGrid && fieldError <= 1e-12 && finite && reference.Converged && bounded && adjustmentMetrics;
        output["model_plan_cleanup"] = result.PlanCleanup;
        output["near_plan_adjusted"] = result.RoadDiagnostics.ModelCleanup?.NearResolvedComponents > 0;
        output["graded_field_sha256"] = Convert.ToHexString(SHA256.HashData(result.SurfaceField.Heights.SelectMany(BitConverter.GetBytes).ToArray()));
        output["road_grading"] = new Dictionary<string,object?>
        {
            ["cross_section_samples"] = reference.CrossSectionSamples, ["adjusted_nodes"] = reference.AdjustedNodes,
            ["limited_nodes"] = reference.LimitedNodes,
            ["iterations"] = reference.Iterations, ["converged"] = reference.Converged,
            ["reference_solve_seconds"] = reference.ElapsedSeconds,
            ["maximum_adjustment_m"] = reference.MaximumAdjustmentMetres, ["rms_adjustment_m"] = reference.RmsAdjustmentMetres,
            ["cross_slope_rms_before"] = reference.CrossSlopeRmsBefore, ["cross_slope_rms_after"] = reference.CrossSlopeRmsAfter,
            ["reference_height_rms_m"] = reference.ReferenceHeightRmsMetres, ["influence_distance_m"] = reference.InfluenceDistanceMetres,
            ["allowed_node_adjustment_m"] = RoadGrading.MaximumNodeAdjustmentMetres,
            ["actual_maximum_node_adjustment_m"] = actualMaximumAdjustment, ["actual_rms_node_adjustment_m"] = actualRmsAdjustment,
            ["all_nodes_within_adjustment_bound"] = bounded, ["reported_adjustments_match_actual_nodes"] = adjustmentMetrics,
            ["raw_source_unchanged"] = unchanged, ["grid_unchanged"] = sameGrid,
            ["production_vs_independent_reference_max_error_m"] = fieldError,
            ["checks_passed"] = passed,
            ["scope"] = "Independent single application to raw DEM; all output surfaces audited against this corrected field, separately from source elevation adjustments."
        };
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && passed;
    }

    private static (int Checked, int Inward) CheckSyntheticSideNormals(RhinoDoc doc, RoadArea roads)
    {
        double scale = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        int count = 0, inward = 0;
        foreach (var obj in ModelGeometryChecks.AllObjects(doc))
        {
            if (obj.Attributes.GetUserString("AHA.Role") != "RoadSide" || obj.Geometry is not Brep brep) continue;
            foreach (var face in brep.Faces)
            {
                double u = face.Domain(0).Mid, v = face.Domain(1).Mid;
                var point = face.PointAt(u,v); var normal = face.UnderlyingSurface().NormalAt(u,v);
                if (face.OrientationIsReversed) normal.Reverse();
                // Sample both sides of the actual cadastral road boundary,
                // including conflicts removed by non-road parcels.
                var planNormal = new Vector2d(normal.X, normal.Y);
                if (!planNormal.Unitize()) { inward++; count++; continue; }
                double x = point.X / scale, y = point.Y / scale;
                if (!Inside(x + planNormal.X * .001, y + planNormal.Y * .001)
                    || Inside(x - planNormal.X * .001, y - planNormal.Y * .001)) inward++;
                count++;
            }
        }
        return (count,inward);
        bool Inside(double x, double y)
        {
            int winding = 0;
            foreach (var path in roads.Outlines)
            {
                var position = Clipper.PointInPolygon(new PointD(x, y), path, RoadGeometry.ClipPrecision);
                if (position == PointInPolygonResult.IsOn) return true;
                if (position == PointInPolygonResult.IsInside) winding += Clipper.Area(path) > 0 ? 1 : -1;
            }
            return winding != 0;
        }
    }

    private static RhinoDoc NewDocument(UnitSystem unit,double toleranceMetres=.001)
    { var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = unit; doc.ModelAbsoluteTolerance = toleranceMetres * RhinoMath.UnitScale(UnitSystem.Meters, unit); return doc; }

    private static void WriteModel(RhinoDoc doc, string path, Dictionary<string, object?> output, Action<string>? progress = null)
    {
        // Persistence is orthogonal to geometry: cover units, representation and complex Near once.
        if (!new[] { "Synthetic-m", "Synthetic-mm", "Synthetic-on-MeshTerrain-Split-m", "Synthetic-on-NurbsTerrain-Overlay-mm", "GunpoMulti-near",
                "Synthetic-on-contour-foundations-m", "Synthetic-on-contour-mm", "Gamcheon-near", "Gamcheon-contour" }
            .Contains(Path.GetFileNameWithoutExtension(path)))
        { output["persistence_checked"] = false; return; }
        output["persistence_checked"] = true;
        using var beforeSave=ModelPersistenceChecks.Capture(doc);
        progress?.Invoke("writing 3dm with render meshes");
        var timer = Stopwatch.StartNew(); bool saved = doc.Write3dmFile(path, new FileWriteOptions { FileVersion = 8, SuppressDialogBoxes = true, IncludeRenderMeshes = true });
        output["write_seconds"] = timer.Elapsed.TotalSeconds; output["export_success"] = saved; output["model_path"] = path;
        output["output_bytes"] = saved ? new FileInfo(path).Length : 0;
        output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && saved;
        using(var afterSave=ModelPersistenceChecks.Capture(doc))
            output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]! && ModelPersistenceChecks.Differences(beforeSave,afterSave).Count==0;
        if (saved)
        {
            progress?.Invoke("reading exported model and checking render caches");
            timer.Restart();
            using var read = File3dm.Read(path) ?? throw new InvalidDataException("Exported model could not be read back.");
            int missing = read.Objects.Where(o => o.Attributes.GetUserString("AHA.Role") is "Land" or "Road" or "RoadSide" or "Site")
                .Sum(o => o.Geometry is Brep brep ? brep.Faces.Count(f => f.GetMesh(MeshType.Render) is not { IsValid: true }) : o.Geometry is Mesh or Extrusion ? 0 : 1);
            var storedLayers=doc.Layers.Where(l=>!l.IsDeleted&&l.GetUserString("AHA.Tool")=="Topo").ToArray();
            bool contracts=storedLayers.All(layer=>read.AllLayers.Any(savedLayer=>savedLayer.Id==layer.Id
                &&savedLayer.GetUserString("AHA.Tool")==layer.GetUserString("AHA.Tool")&&savedLayer.IsVisible==layer.IsVisible));
            bool near=doc.Layers.Any(l=>!l.IsDeleted&&storedLayers.Any(root=>root.Id==l.ParentLayerId)&&l.Name=="Near");
            bool curveVisibility=storedLayers.All(root=>
            {
                var refs=read.AllLayers.Single(l=>l.ParentLayerId==root.Id&&l.Name=="XY");
                return !refs.IsVisible&&!refs.GetPersistentVisibility()&&read.AllLayers.Where(l=>l.ParentLayerId==refs.Id).All(l=>l.GetPersistentVisibility());
            });
            output["roundtrip_reference_visibility_passed"]=curveVisibility;
            var savedObjects=read.Objects.ToDictionary(o=>o.Attributes.ObjectId);
            var actualIds=savedObjects.Keys.ToHashSet();
            bool identities=actualIds.SetEquals(ModelGeometryChecks.AllObjects(doc).Select(o=>o.Id));
            output["roundtrip_model_contracts_and_visibility_passed"]=contracts;
            output["roundtrip_object_identities_passed"]=identities;
            var liveExtrusions=ModelGeometryChecks.AllObjects(doc).Where(o=>o.Geometry is Extrusion).ToArray();
            bool extrusionTypes=liveExtrusions.All(original=>savedObjects.TryGetValue(original.Id,out var savedObject)
                &&savedObject.Geometry is Extrusion {IsValid:true,IsSolid:true}
                &&GeometryBase.GeometryEquals(savedObject.Geometry,original.Geometry)
                &&ModelPersistenceChecks.ClosedExtrusion((Extrusion)savedObject.Geometry)
                &&savedObject.Attributes.GetUserString(Aha.Info.InfoData.Key)==original.Attributes.GetUserString(Aha.Info.InfoData.Key));
            output["roundtrip_extrusions_and_info_preserved"]=extrusionTypes;
            var referenceObjects=ModelGeometryChecks.AllObjects(doc).Where(o=>o.Geometry is Curve
                &&o.Attributes.GetUserString("AHA.CurveSpace") is "Projected" or "XY").ToArray();
            bool savedReferences=referenceObjects.All(original=>savedObjects.TryGetValue(original.Id,out var savedReference)
                &&GeometryBase.GeometryEquals(savedReference.Geometry,original.Geometry));
            output["roundtrip_reference_curve_geometry_passed"]=savedReferences;
            var targetObjects=ModelGeometryChecks.AllObjects(doc).Where(o=>o.Attributes.GetUserString("AHA.Role")=="TargetBoundary"
                &&o.Attributes.GetUserString("AHA.CurveSpace")=="Projected").ToArray();
            var archivedTargets=read.Objects.Where(o=>targetObjects.Any(live=>live.Id==o.Attributes.ObjectId)).ToArray();
            bool savedTargets=archivedTargets.Length==targetObjects.Length&&archivedTargets.All(o=>o.Geometry is Curve
                && GeometryBase.GeometryEquals(targetObjects.Single(live=>live.Id==o.Attributes.ObjectId).Geometry,o.Geometry)
                && o.Attributes.GetUserString("AHA.CurveSpace")=="Projected"
                && read.AllLayers.Any(l=>l.Index==o.Attributes.LayerIndex&&l.Name=="TargetBoundary"
                    &&storedLayers.Any(root=>root.Id==l.ParentLayerId)&&l.IsVisible&&l.GetPersistentVisibility()));
            output["roundtrip_target_curves_passed"]=savedTargets;
            var solidIds=ModelGeometryChecks.AllObjects(doc).Where(o=>o.Geometry is Brep{IsSolid:true}
                && (o.Attributes.GetUserString("AHA.Role")=="Site" || o.Attributes.GetUserString("AHA.Zone")=="Near"
                    && o.Attributes.GetUserString("AHA.Role") is "Land" or "Road")).Select(o=>o.Id).ToHashSet();
            double scale=RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem);
            var savedSolids=read.Objects.Where(o=>solidIds.Contains(o.Attributes.ObjectId)).ToArray();
            var savedDisplays=savedSolids.Where(o=>o.Geometry is Brep{IsSolid:true}).Select(o=>SolidDisplayChecks.Check((Brep)o.Geometry,scale)).ToArray();
            bool savedClosed=savedDisplays.Length==solidIds.Count&&savedDisplays.All(a=>a.Passed);
            bool reopenedClosed=true;
            bool reopenedTargets=true;
            bool reopenedReferences=true;
            bool reopenedExtrusions=true;
            {
                // File3dm checks the archive; opening a fresh document also checks
                // that Rhino's document import preserves every near face cache.
                using var reopened=RhinoDoc.OpenHeadless(path)??throw new InvalidDataException("Saved Topo model could not be reopened.");
                using var reopenedState=ModelPersistenceChecks.Capture(reopened);
                var semantic=ModelPersistenceChecks.Differences(beforeSave,reopenedState);
                output["saved_model_semantic_differences"]=semantic;
                output["geometry_checks_passed"]=(bool)output["geometry_checks_passed"]!&&semantic.Count==0;
                var restored=solidIds.Select(id=>reopened.Objects.FindId(id)?.Geometry as Brep).ToArray();
                var displays=restored.Where(b=>b is {IsSolid:true}).Select(b=>SolidDisplayChecks.Check(b!,scale)).ToArray();
                reopenedClosed=displays.Length==solidIds.Count&&displays.All(a=>a.Passed);
                output["reopened_solid_display_meshes"]=displays;
                reopenedTargets=targetObjects.All(original=>reopened.Objects.FindId(original.Id) is {} target&&GeometryBase.GeometryEquals(target.Geometry,original.Geometry))
                    &&TopoWorkflowChecks.Roots(reopened).All(site=>TopoWorkflowChecks.ReopenedVisibilityCycle(reopened,site.Id,near)
                        &&TopoWorkflowChecks.TargetBoundaryVisibleWithFarHidden(reopened,site.Id));
                reopenedReferences=referenceObjects.All(original=>reopened.Objects.FindId(original.Id) is {} reference
                    &&GeometryBase.GeometryEquals(reference.Geometry,original.Geometry));
                reopenedExtrusions=liveExtrusions.All(original=>reopened.Objects.FindId(original.Id) is {Geometry:Extrusion{IsValid:true,IsSolid:true}} restoredExtrusion
                    &&GeometryBase.GeometryEquals(restoredExtrusion.Geometry,original.Geometry)
                    &&ModelPersistenceChecks.ClosedExtrusion((Extrusion)restoredExtrusion.Geometry)
                    &&restoredExtrusion.Attributes.GetUserString(Aha.Info.InfoData.Key)==original.Attributes.GetUserString(Aha.Info.InfoData.Key));
            }
            output["reopened_target_curves_and_visibility_passed"]=reopenedTargets;
            output["reopened_reference_curve_geometry_passed"]=reopenedReferences;
            output["reopened_extrusions_and_info_preserved"]=reopenedExtrusions;
            output["saved_near_solid_count"]=solidIds.Count;
            output["roundtrip_solid_display_meshes"]=savedDisplays;
            output["roundtrip_closed_solid_display_meshes_passed"]=savedClosed;
            output["reopened_closed_solid_display_meshes_passed"]=reopenedClosed;
            output["roundtrip_missing_surface_render_meshes"] = missing;
            output["export_readback_seconds"] = timer.Elapsed.TotalSeconds;
            output["geometry_checks_passed"] = (bool)output["geometry_checks_passed"]! && missing == 0 && contracts && identities && curveVisibility
                &&savedClosed&&reopenedClosed&&savedTargets&&reopenedTargets&&savedReferences&&reopenedReferences&&extrusionTypes&&reopenedExtrusions;
        }
    }

    private static Task<T> OnUi<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RhinoApp.InvokeOnUiThread(new Action(() => { try { completion.SetResult(action()); } catch (Exception error) { completion.SetException(error); } }));
        return completion.Task;
    }
}
