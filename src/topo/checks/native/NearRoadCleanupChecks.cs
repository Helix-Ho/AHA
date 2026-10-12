using Clipper2Lib;
using Rhino.Geometry;
using System.Text.Json;

namespace Aha.Topo.Checks;

/// <summary>Near cuts cannot turn valid cadastral roads or land into subminimum standalone objects.</summary>
internal static class NearRoadCleanupChecks
{
    internal static Dictionary<string, object?> Run()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(15);
        var domain = Rectangle(-15, -15, 15, 15);
        var target = Feature("target", Rectangle(-3, -4, 3, 4));
        var near = EditRegion.Create(target, projection, bounds, 2);
        var circle = near.Circles.Single();
        double radius = circle.RadiusMetres;
        var nearBefore = JsonSerializer.Serialize(near.Boundaries);
        var field = TerrainField.FromSamples(bounds, projection, 6, 6, Enumerable.Repeat(20d, 49).ToArray());
        var inputHeights = field.Heights.ToArray();
        var normalRoad = Rectangle(-15, -1, 15, 1);
        var roadCap = Union(new PathsD(normalRoad.Concat(Rectangle(-2, radius - .01, 2, radius + 1))));
        var landCap = Clipper.Difference(domain,
            new PathsD(Rectangle(-2, radius - .01, 2, 15).Concat(Rectangle(-4, -4, 4, 4))), FillRule.NonZero, 6);
        var farCap = Union(new PathsD(normalRoad.Concat(Rectangle(-.5, radius - 1.5, .5, radius + .0001))));
        var fixtures = new[] { (Name: "near-road-cap", Roads: roadCap, RoadFraction: .5),
            (Name: "near-land-cap", Roads: landCap, RoadFraction: 0d),
            (Name: "far-road-cap", Roads: farCap, RoadFraction: .5) };
        var specs = new[] { (Scale: 1d, Nurbs: false), (Scale: 1000d, Nurbs: true) };
        var results = new List<Dictionary<string, object?>>();
        const double drop = .05, depth = 30;

        // Analytic circular segment areas establish the subminimum source cases
        // without relying on the product's sampled Near polygon or size guard.
        double capY = radius - .01;
        double nearCapArea = radius * radius * Math.Acos(capY / radius) - capY * Math.Sqrt(radius * radius - capY * capY);
        double farCapArea = radius + .0001 - (.5 * Math.Sqrt(radius * radius - .25) + radius * radius * Math.Asin(.5 / radius));
        Require(nearCapArea > 0 && nearCapArea < .01 && farCapArea > 0 && farCapArea < .01,
            "Independent circle integrals reproduce subminimum Near and Far caps");

        foreach (var fixture in fixtures)
        {
            var parcels = new[] { Feature(fixture.Name, fixture.Roads, true) };
            string sourceBefore = JsonSerializer.Serialize(parcels);
            var source = RoadGeometry.BuildSource(parcels, projection, bounds);
            var roads = RoadGeometry.Build(parcels, projection, bounds, outputBounds: bounds, near: near);
            var changed = Clipper.Xor(source.Outlines, roads.Outlines, FillRule.NonZero, 6);
            Require(sourceBefore == JsonSerializer.Serialize(parcels) && nearBefore == JsonSerializer.Serialize(near.Boundaries),
                fixture.Name + " preserves cadastral XY and the authoritative Near circle");
            var grading = RoadGrading.Apply(field, roads);
            Require(grading.Field.Heights.All(height => Math.Abs(height - 20) < 1e-10) && field.Heights.SequenceEqual(inputHeights),
                fixture.Name + " keeps the constant terrain and its source samples unchanged");
            double circleArea = Math.PI * radius * radius;
            double stripArea = 2 * (Math.Sqrt(radius * radius - 1) + radius * radius * Math.Asin(1 / radius));
            double expectedRoadArea = fixture.Name == "near-land-cap" ? circleArea - 64 : stripArea;
            if (fixture.Name == "far-road-cap")
            {
                // The tiny Far cap becomes land; the useful Near part reaches
                // the exact circle. Its area is an independent circular-strip integral.
                expectedRoadArea += .5 * Math.Sqrt(radius * radius - .25)
                    + radius * radius * Math.Asin(.5 / radius) - radius + 1.5;
            }
            foreach (var spec in specs)
            {
                TerrainSurface? surface = null;
                var result = new Dictionary<string, object?> { ["case"] = fixture.Name, ["scale"] = spec.Scale,
                    ["far_nurbs"] = spec.Nurbs,
                    ["source_cap_area_m2"] = fixture.Name == "far-road-cap" ? farCapArea : nearCapArea,
                    ["changed_road_area_m2"] = Area(changed) };
                results.Add(result);
                try
                {
                    var building = Feature("building", Rectangle(-2, -2, 2, 2));
                    var placements = BuildingPlacement.Prepare(grading.Field, roads, drop, true, new[] { building });
                    Require(placements.Rejections.Count == 0 && placements.Placements.Count == 1, "Central building placement remains valid");
                    var placement = placements.Placements.Single();
                    double expectedMean = 20 - drop * fixture.RoadFraction;
                    double expectedBase = 20 - (fixture.RoadFraction > 0 ? drop : 0) - 1;
                    Require(Math.Abs(placement.MeanElevationMetres - expectedMean) < 1e-7 &&
                        Math.Abs(placement.BaseElevationMetres - expectedBase) < 1e-7,
                        "Building minimum and area mean use the same resolved road plan, with 50mm vertical drop intact");
                    surface = TerrainSurface.Build(grading.Field, roads, drop, spec.Scale, .001, spec.Nurbs, true, near, depth);
                    var tops = surface.Pieces.Where(piece => piece.Role is "Land" or "Road").ToArray();
                    var nearSolids = tops.Where(piece => piece.Zone == "Near").Select(piece => (Brep)piece.Geometry).ToArray();
                    var quality = ModelSurfaceQualityChecks.Check(tops, spec.Scale);
                    var nearAudit = NearGeometryChecks.Check(surface.Pieces, near, grading.Field, drop, spec.Scale, true, depth,
                        surface.BottomElevationMetres);
                    Require(tops.All(piece => piece.Geometry.IsValid) && nearSolids.All(solid => solid.IsSolid && solid.IsManifold),
                        "Near land and road are valid connected solids");
                    Require(tops.Where(piece => piece.Zone == "Far").All(piece => spec.Nurbs ? piece.Geometry is Brep : piece.Geometry is Mesh),
                        "Far retains the selected surface representation");
                    Require(quality.Passed && (bool)nearAudit["near_geometry_checks_passed"]!,
                        "Actual output has no subminimum zone/role component, preserves the exact Near boundary, and has correct heights");
                    Require(Math.Abs(surface.PlanAreaSquareMetres - 900) < 1e-6 && surface.PlanAreaResidualSquareMetres < 1e-6,
                        "Resolved land and road cover the unchanged output exactly once");
                    Require(Math.Abs(surface.SampleLocal(8, 0) - 19.95) < 1e-8,
                        "50mm road lowering survives horizontal minimum-detail cleanup");
                    var probe = new Point3d(0, 0, (expectedBase + .5) * spec.Scale);
                    Require(nearSolids.Any(solid => solid.IsPointInside(probe, .001 * spec.Scale, true)),
                        "Continuous Near terrain is preserved beneath building foundations");
                    double measuredArea = 0, measuredRoadArea = 0, measuredVolume = 0;
                    foreach (var piece in tops.Where(piece => piece.Zone == "Near"))
                    {
                        using var metric = ((Brep)piece.Geometry).DuplicateBrep();
                        metric.Transform(Transform.Scale(Point3d.Origin, 1 / spec.Scale));
                        using var volume = VolumeMassProperties.Compute(metric, true, false, false, false);
                        Require(volume is not null && double.IsFinite(volume.Volume), "Native volume is available");
                        measuredVolume += volume!.Volume;
                        foreach (var face in metric.Faces)
                        {
                            if (face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid).Z <= .999999) continue;
                            using var horizontal = face.DuplicateFace(false);
                            using var area = AreaMassProperties.Compute(horizontal, true, false, false, false);
                            Require(area is not null && double.IsFinite(area.Area), "Native horizontal face area is available");
                            measuredArea += area!.Area;
                            if (piece.Role == "Road") measuredRoadArea += area.Area;
                        }
                    }
                    double expectedVolume = circleArea * (20 - surface.BottomElevationMetres!.Value) - drop * expectedRoadArea;
                    // These horizontal curves are lifted exactly. Their only
                    // geometric uncertainty is the micrometre plan grid, not
                    // the 1mm budget for fitted non-horizontal 3D boundaries.
                    double grid = ModelPlanPolicy.PrecisionMetres;
                    double circleAreaBudget = 2 * Math.PI * radius * grid + Math.PI * grid * grid;
                    double roadAreaBudget = (2 * Math.PI * radius + Perimeter(roads.Outlines)) * grid;
                    double volumeBudget = circleAreaBudget * (20 - surface.BottomElevationMetres.Value) +
                        drop * roadAreaBudget;
                    Require(Math.Abs(measuredArea - circleArea) <= circleAreaBudget &&
                        Math.Abs(measuredRoadArea - expectedRoadArea) <= roadAreaBudget,
                        "Native horizontal areas agree with the analytic circle and resolved road footprints");
                    Require(Math.Abs(measuredVolume - expectedVolume) <= volumeBudget,
                        "Native solid volume agrees with the analytic continuous flat-ground volume");
                    result["passed"] = true; result["surface_quality"] = quality; result["near_geometry"] = nearAudit;
                    result["near_solids"] = nearSolids.Length;
                    result["native_plan_area_error_m2"] = Math.Abs(measuredArea - circleArea);
                    result["native_road_area_error_m2"] = Math.Abs(measuredRoadArea - expectedRoadArea);
                    result["native_volume_error_m3"] = Math.Abs(measuredVolume - expectedVolume);
                    result["volume_plan_precision_budget_m3"] = volumeBudget;
                }
                catch (Exception error) { result["passed"] = false; result["error"] = error.ToString(); }
                finally { if (surface is not null) foreach (var geometry in surface.Pieces.Select(piece => piece.Geometry).Distinct()) geometry.Dispose(); }
            }
        }
        return new() { ["geometry_checks_passed"] = results.All(result => (bool)result["passed"]!), ["near_road_cleanup_cases"] = results };

        GeoFeature Feature(string id, PathsD paths, bool road = false)
        {
            var tree = new PolyTreeD(); var clipper = new ClipperD(6); clipper.AddSubject(paths);
            if (!clipper.Execute(ClipType.Union, FillRule.NonZero, tree)) throw new InvalidOperationException("Test polygons cannot be joined");
            var feature = new GeoFeature { Id = id, LandCategoryCode = road ? 14 : null };
            Visit(tree); return feature;
            void Visit(PolyPathD parent)
            {
                for (int i = 0; i < parent.Count; i++)
                {
                    var node = parent[i];
                    if (!node.IsHole && node.Polygon is { } shell)
                    {
                        var polygon = new List<List<GeoPoint>> { Ring(shell) };
                        for (int j = 0; j < node.Count; j++) if (node[j].IsHole) polygon.Add(Ring(node[j].Polygon!));
                        feature.Polygons.Add(polygon);
                    }
                    Visit(node);
                }
            }
            List<GeoPoint> Ring(PathD path) => path.Append(path[0]).Select(point => projection.ToGeo(point.x, point.y)).ToList();
        }
    }

    private static PathsD Rectangle(double x0, double y0, double x1, double y1) => new() { new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } };
    private static PathsD Union(PathsD paths) => Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.NonZero, 6);
    private static double Area(PathsD paths) => Math.Abs(Clipper.Area(paths));
    private static double Perimeter(PathsD paths) => paths.Sum(path => Enumerable.Range(0, path.Count).Sum(i =>
        Math.Sqrt(Math.Pow(path[i].x - path[(i + 1) % path.Count].x, 2) + Math.Pow(path[i].y - path[(i + 1) % path.Count].y, 2))));
    private static void Require(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
