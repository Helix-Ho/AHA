using System.Globalization;
using Clipper2Lib;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>Ground acquisition can exceed the fixed output rectangle without truncating generated buildings.</summary>
internal static class CoverageGeometryChecks
{
    internal static Dictionary<string, object?> Run()
    {
        var origin = new GeoPoint(127, 37.5);
        var projection = new LocalProjection(origin);
        var outputBounds = projection.Bounds(50);
        var coverageBounds = projection.Bounds(80);
        var field = TerrainField.FromSamples(coverageBounds, projection, 8, 8,
            Enumerable.Range(0, 81).Select(i => 20 - .1 * (-80 + i % 9 * 20)).ToArray());
        var target = Rectangle("target", -15, -15, 15, 15);
        var building = Rectangle("cross-output", 30, -5, 65, 5);
        var road = Rectangle("road", -80, -2, 80, 2, ParcelLandCategory.Road);
        var metrics = new List<object>();
        foreach (var spec in new[]
        {
            (Near: true, Nurbs: false, Split: true, Units: UnitSystem.Meters),
            (Near: true, Nurbs: true, Split: true, Units: UnitSystem.Millimeters),
            (Near: false, Nurbs: false, Split: true, Units: UnitSystem.Meters),
            (Near: false, Nurbs: true, Split: true, Units: UnitSystem.Millimeters),
            (Near: true, Nurbs: false, Split: false, Units: UnitSystem.Meters)
        })
        {
            double scale = RhinoMath.UnitScale(UnitSystem.Meters, spec.Units);
            using var doc = RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem = spec.Units; doc.ModelAbsoluteTolerance = .001 * scale;
            var options = new ModelOptions(UseNearRegion: spec.Near, BuildingsAsNurbs: spec.Nurbs,
                TerrainAsNurbs: spec.Nurbs, SeparateRoads: spec.Split);
            var result = CheckModel.Build(doc, origin, outputBounds, target, new[] { road }, new[] { building }, field, options);
            var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
            var surfaces = objects.Where(o => o.Attributes.GetUserString("AHA.Role") is "Land" or "Road" or "RoadSide").ToArray();
            Require(result.Bounds == outputBounds && result.Origin == origin, "Output coordinates remain those requested");
            Require(surfaces.Length > 0 && objects.All(o => o.Geometry.IsValid), "All generated geometry is valid");
            Require(surfaces.All(o => InsideOutput(o.Geometry.GetBoundingBox(true), scale)),
                "No terrain, road, or step wall extends into the acquired-only area");
            Require(objects.Where(o => o.Geometry is Curve).All(o => InsideOutput(o.Geometry.GetBoundingBox(true), scale)),
                "Reference curves use output bounds, not acquisition bounds");
            Require(Math.Abs(result.RoadAreaSquareMetres - 400) < 1e-5 &&
                Math.Abs(result.SurfacePlanAreaResidualSquareMetres) < 1e-5,
                "Output road area and terrain coverage are measured inside the requested rectangle");
            Require(result.RoadGrading.MaximumAdjustmentMetres < 1e-6,
                "The longitudinal plane needs no road cross-section correction");
            var model = objects.Single(o => o.Attributes.GetUserString("AHA.Role") == "Building");
            Require(result.NearBuildings == 0 && result.InvalidBuildings == 0 &&
                (spec.Near ? result.FarBuildings == 1 && result.UnzonedBuildings == 0
                    : result.FarBuildings == 0 && result.UnzonedBuildings == 1),
                "A crossing building stays whole: Far with Near enabled, otherwise unzoned");
            var box = model.Geometry.GetBoundingBox(true);
            // z=20−0.1x over x=[30,65]. The road occupies 4/10 of the footprint.
            double mean = 15.25 - (spec.Split ? .08 : 0), minimum = 13.5 - (spec.Split ? .2 : 0);
            double roof = mean + 6, floor = minimum - 1;
            Require(Math.Abs(Number(model,"AHA.ReferenceMeanElevationMetres") - mean) < 1e-6 &&
                Math.Abs(Number(model,"AHA.GroundMetres") - floor) < 1e-6 &&
                Math.Abs(Number(model,"AHA.RoofElevationMetres") - roof) < 1e-6,
                "Far and Near-OFF buildings use the complete terrain minimum and area mean beyond output");
            Require(Math.Abs(box.Max.X/scale - 65) < 1e-6 && Math.Abs(box.Min.Z/scale - floor) < 1e-6 &&
                Math.Abs(box.Max.Z/scale - roof) < 1e-6,
                "Whole building geometry reflects the independent foundation and roof values");
            Require(spec.Nurbs ? model.Geometry is Extrusion { IsSolid:true } : model.Geometry is Mesh { IsClosed:true },
                "Far building format follows the requested direct output type");
            var near = spec.Near ? EditRegion.Create(target,projection,outputBounds,options.NearRatio) : null;
            var audit = ModelGeometryChecks.Run(doc,result,outputBounds,projection,result.SurfaceField,
                new[] {building},options,parcels:new[] {road},nearRegion:near);
            Require((bool)audit["geometry_checks_passed"]!, "Output geometry audit: " + System.Text.Json.JsonSerializer.Serialize(audit));
            metrics.Add(new {spec.Near,spec.Nurbs,spec.Split,units=spec.Units.ToString(),result.CreatedCount,result.FarBuildings});
        }
        // A valid 1m-wide road can intersect output as only a 40mm strip.
        // Output cleanup removes that strip before the shared height/ground plan
        // is used, while preserving the road needed outside output for statistics.
        var rimRoad = Rectangle("output-rim-road", 49.96, -50, 50.96, 50, ParcelLandCategory.Road);
        var flat = TerrainField.FromSamples(coverageBounds, projection, 8, 8, Enumerable.Repeat(20d,81).ToArray());
        foreach (bool nurbs in new[] { false, true })
        {
            using var doc = RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem = UnitSystem.Meters; doc.ModelAbsoluteTolerance = .001;
            var options = new ModelOptions(TerrainAsNurbs: nurbs);
            var result = CheckModel.Build(doc, origin, outputBounds, target, new[] { rimRoad }, new[] { building }, flat, options);
            var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
            var model = objects.Single(o => o.Attributes.GetUserString("AHA.Role") == "Building");
            Require(result.NearBuildings == 0 && result.FarBuildings == 1 && result.InvalidBuildings == 0 &&
                result.RoadAreaSquareMetres == 0 && result.RoadSurfaces == 0 &&
                !objects.Any(o => o.Attributes.GetUserString("AHA.Role") is "Road" or "RoadSide"),
                "Output-rim road below the 100mm detail is removed without rejecting the model");
            const double expectedMean = 20 - .2 * .96 / 35;
            Require(Math.Abs(Number(model,"AHA.ReferenceMeanElevationMetres") - expectedMean) < 1e-6 &&
                Math.Abs(Number(model,"AHA.GroundMetres") - 18.8) < 1e-6 &&
                Math.Abs(Number(model,"AHA.RoofElevationMetres") - (expectedMean+6)) < 1e-6,
                "Only the cleaned output strip is removed; the 960mm road outside still contributes to the full building statistics");
            Require(objects.Any(o => o.Attributes.GetUserString("AHA.Role") == "RoadBoundary" &&
                Math.Abs(o.Geometry.GetBoundingBox(true).Min.X - 49.96) < 1e-6),
                "Original XY road references retain the 40mm strip excluded from modelling");
            var audit = ModelGeometryChecks.Run(doc, result, outputBounds, projection, result.SurfaceField,
                new[] { building }, options, parcels: new[] { rimRoad },
                nearRegion: EditRegion.Create(target, projection, outputBounds, options.NearRatio));
            Require((bool)audit["geometry_checks_passed"]!, "Output-rim cleanup geometry audit: " +
                System.Text.Json.JsonSerializer.Serialize(audit));
            metrics.Add(new { Case = "output-rim-road", nurbs, result.CreatedCount, result.RoadAreaSquareMetres });
        }
        // Final graph noding can expose a sub-detail role fragment after source
        // cleanup. The resolved role must also feed buildings and contour, while
        // roads acquired outside the output remain available for full footprints.
        var rawRoads = new PathsD
        {
            new() { new(-5,-.02),new(5,-.02),new(5,.02),new(-5,.02) },
            new() { new(60,-3),new(70,-3),new(70,3),new(60,3) }
        };
        var resolvedSurface = TerrainSurface.Prepare(flat, new RoadArea(rawRoads, 60.4), 3,
            outputBounds: outputBounds);
        Require(resolvedSurface.Pieces.Count == 0 && resolvedSurface.ReassignedRoleAreaSquareMetres > .39,
            "The common role plan is resolved before any Rhino surfaces are created");
        Require(resolvedSurface.ConsumedRoadArea.AreaSquareMetres == 0 &&
            Math.Abs(resolvedSurface.GroundRoadArea.AreaSquareMetres - 60) < 1e-8,
            "Final role reassignment preserves acquired-only road support");
        var preparedBuildings = BuildingPlacement.Prepare(flat, resolvedSurface.GroundRoadArea, 3, true,
            new[] { Rectangle("inside",-2,-1,2,1), Rectangle("outside",62,-1,68,1) });
        Require(preparedBuildings.Rejections.Count == 0 && preparedBuildings.Placements.Count == 2 &&
            Math.Abs(preparedBuildings.Placements.Single(p=>p.SourceFeatureId=="inside").BaseElevationMetres - 19) < 1e-8 &&
            Math.Abs(preparedBuildings.Placements.Single(p=>p.SourceFeatureId=="outside").BaseElevationMetres - 16) < 1e-8,
            "Building foundations use resolved output roles and full acquired ground");
        resolvedSurface.Materialize(1,.001);
        try
        {
            Require(resolvedSurface.Pieces.All(piece=>piece.Role=="Land") &&
                Math.Abs(resolvedSurface.SampleLocal(0,0)-20) < 1e-8,
                "Materialized terrain uses exactly the role plan used for building statistics");
        }
        finally { foreach(var piece in resolvedSurface.Pieces)piece.Geometry.Dispose(); }
        metrics.Add(new { Case="resolved-ground-role",resolvedSurface.ReassignedRoleAreaSquareMetres });
        return new() { ["geometry_checks_passed"] = true, ["coverage_geometry"] = metrics };

        GeoFeature Rectangle(string id, double x0, double y0, double x1, double y1, int? category = null) => new()
        {
            Id = id, LandCategoryCode = category, Polygons = new() { new() { new()
                { projection.ToGeo(x0, y0), projection.ToGeo(x1, y0), projection.ToGeo(x1, y1), projection.ToGeo(x0, y1) } } }
        };
    }

    private static bool InsideOutput(BoundingBox box, double scale) =>
        box.Min.X / scale >= -50 - 1e-6 && box.Max.X / scale <= 50 + 1e-6 &&
        box.Min.Y / scale >= -50 - 1e-6 && box.Max.Y / scale <= 50 + 1e-6;
    private static double Number(RhinoObject obj, string name) => double.Parse(obj.Attributes.GetUserString(name)!, CultureInfo.InvariantCulture);
    private static double Distance(GeometryBase geometry, Point3d point) => geometry switch
    {
        Brep brep => brep.ClosestPoint(point).DistanceTo(point),
        Mesh mesh => mesh.ClosestPoint(point).DistanceTo(point),
        _ => double.PositiveInfinity
    };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Terrain coverage: " + message); }
}
