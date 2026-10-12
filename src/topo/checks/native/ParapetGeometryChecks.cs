using System.Globalization;
using System.Text.Json.Nodes;
using Clipper2Lib;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class ParapetGeometryChecks
{
    internal static Dictionary<string, object?> Run()
    {
        int examples = 0;
        foreach (var units in new[] { UnitSystem.Meters, UnitSystem.Millimeters })
        {
            double scale = RhinoMath.UnitScale(UnitSystem.Meters, units);
            foreach (var example in ParapetProfileChecks.Cases())
            {
                var geometries = BuildingGeometry.CreateEditable(ParapetProfiles.Create(example.Roof, example.Width), 110, .3, scale, .001 * scale);
                try
                {
                    double volume = 0;
                    foreach (var geometry in geometries)
                    {
                        Require(geometry is Extrusion { IsValid: true, IsSolid: true }, "actual Extrusion type");
                        var extrusion = (Extrusion)geometry;
                        Require(ModelPersistenceChecks.ClosedExtrusion(extrusion), "closed manifold analytical boundary");
                        using var mass = VolumeMassProperties.Compute(extrusion);
                        volume += mass!.Volume / Math.Pow(scale, 3);
                        var box = geometry.GetBoundingBox(true);
                        Require(Math.Abs(box.Min.Z / scale - 110) < 1e-8 && Math.Abs(box.Max.Z / scale - 110.3) < 1e-8, "roof and parapet top");
                    }
                    Require(Math.Abs(volume - example.Area * .3) < 1e-6, example.Name + " independent volume");
                    examples++;
                }
                finally { foreach (var geometry in geometries) geometry.Dispose(); }
            }
            var projection = new LocalProjection(new GeoPoint(127, 37.5));
            var bounds = projection.Bounds(50);
            var site = Feature("site", -5, -5, 5, 5);
            var buildings = new[] { Feature("near", -3, 7, 3, 11), Feature("touching", 13, -1, 16, 1), Feature("far", 25, -2, 30, 2) };
            var field = TerrainField.FromSamples(bounds, projection, 2, 2, Enumerable.Repeat(100.3, 9).ToArray());
            foreach (bool near in new[] { false, true })
            foreach (bool contour in near ? new[] { false, true } : new[] { false })
            foreach (bool separate in new[] { false, true })
            {
                var options = new ModelOptions(UseNearRegion: near, IncludeRoads: false, Contour: contour, SeparateFoundations: separate);
                using var off = Document(); using var on = Document();
                var without = CheckModel.Build(off, projection.Origin, bounds, site, new[] { site }, buildings, field, options with { Parapets = false });
                using var prepared = RhinoModel.Prepare(ModelContext.Capture(on), projection.Origin, bounds, site, new[] { site }, buildings, field, options);
                Require(!ModelGeometryChecks.AllObjects(on).Any(), "preparation leaves the document empty");
                int preparedCount = prepared.ObjectCount;
                var with = prepared.Commit(on);
                var objects = with.ObjectIds.Select(id => on.Objects.FindId(id)!).ToArray();
                Require(Audit(on, objects, options, scale), "generated parapets meet the common model audit");
                Require(with.AppliedSettings.Parapets == near && with.NearBuildings == without.NearBuildings
                    && with.FarBuildings == without.FarBuildings && with.NearSolidCount == without.NearSolidCount, "applied settings and existing count meanings");
                var original = without.ObjectIds.Select(id => off.Objects.FindId(id)!).ToArray();
                var retained = objects.Where(o => Role(o) != "Parapet").ToArray();
                Require(original.Length == retained.Length && original.Zip(retained).All(pair =>
                    pair.First.Attributes.Name == pair.Second.Attributes.Name
                    && pair.First.Attributes.GetUserString(Aha.Info.InfoData.Key) == pair.Second.Attributes.GetUserString(Aha.Info.InfoData.Key)
                    && GeometryBase.GeometryEquals(pair.First.Geometry, pair.Second.Geometry)), "parapet switch preserves all existing geometry and Info");
                Require(preparedCount == with.CreatedCount && with.CreatedCount == without.CreatedCount + objects.Count(o => Role(o) == "Parapet"), "prepared/created counts include separate parapets");
                Require(prepared.Commit(on).ObjectIds.SequenceEqual(with.ObjectIds), "repeated commit preserves the same objects");
                if (near && !contour && !separate)
                {
                    var parapet = objects.Single(o => Role(o) == "Parapet");
                    using var wrong = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
                    Require(on.Objects.Replace(parapet.Id, wrong) && !Audit(on, objects.Select(o => on.Objects.FindId(o.Id)!).ToArray(), options, scale), "a damaged parapet fails the shared audit");
                }
                examples++;
            }

            RhinoDoc Document() { var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = units; doc.ModelAbsoluteTolerance = .001 * scale; return doc; }
            GeoFeature Feature(string id, double x0, double y0, double x1, double y1) => new()
            {
                Id = id, Height = 10, LandCategoryCode = 8,
                Polygons = new() { new() { new() { projection.ToGeo(x0, y0), projection.ToGeo(x1, y0), projection.ToGeo(x1, y1), projection.ToGeo(x0, y1) } } }
            };
        }
        return new() { ["geometry_checks_passed"] = true, ["parapet_cases"] = examples };
    }

    // Every model replay uses this audit, including local regional fixtures.
    // Read actual body/profile geometry; do not rebuild the product offset as an oracle.
    internal static bool Audit(RhinoDoc doc, IReadOnlyList<RhinoObject> objects, ModelOptions options, double scale)
    {
        var parapets = objects.Where(o => Role(o) == "Parapet").ToArray();
        var bodies = objects.Where(o => Role(o) == "Building" && o.Attributes.GetUserString("AHA.Zone") == "Near")
            .ToLookup(o => o.Attributes.GetUserString("AHA.FeatureId")!);
        if (!options.EffectiveParapets) return parapets.Length == 0;
        if (!bodies.Select(g => g.Key).ToHashSet().SetEquals(parapets.Select(o => o.Attributes.GetUserString("AHA.FeatureId")))) return false;
        foreach (var group in parapets.GroupBy(o => o.Attributes.GetUserString("AHA.FeatureId")!))
        {
            var roof = new PathsD(bodies[group.Key].SelectMany(o => Profiles((Extrusion)o.Geometry, scale)));
            var band = new PathsD();
            double roofZ = bodies[group.Key].First().Geometry.GetBoundingBox(true).Max.Z / scale;
            foreach (var obj in group)
            {
                if (obj.Geometry is not Extrusion { IsValid: true, IsSolid: true } extrusion || !ModelPersistenceChecks.ClosedExtrusion(extrusion)
                    || obj.Attributes.GetUserString("AHA.Zone") != "Near" || !obj.Attributes.Name.StartsWith("Parapet_", StringComparison.Ordinal)
                    || !doc.Layers[obj.Attributes.LayerIndex].FullPath.EndsWith("::Near::Buildings::Parapets", StringComparison.Ordinal)) return false;
                var info = JsonNode.Parse(obj.Attributes.GetUserString(Aha.Info.InfoData.Key)!)!;
                if (info["kind"]!.GetValue<string>() != "parapet" || info["site"]!.GetValue<bool>()) return false;
                double Tag(string key) => double.Parse(obj.Attributes.GetUserString(key)!, CultureInfo.InvariantCulture);
                var box = extrusion.GetBoundingBox(true);
                if (Math.Abs(box.Min.Z / scale - roofZ) > 1e-7 || Math.Abs(box.Max.Z / scale - roofZ - options.ParapetHeightMetres) > 1e-7
                    || Tag("AHA.ParapetWidthMetres") != options.ParapetWidthMetres || Tag("AHA.ParapetHeightMetres") != options.ParapetHeightMetres
                    || Math.Abs(Tag("AHA.GroundMetres") - roofZ) > 1e-7
                    || Math.Abs(Tag("AHA.TopMetres") - roofZ - options.ParapetHeightMetres) > 1e-7) return false;
                double area = 0;
                for (int i = 0; i < extrusion.ProfileCount; i++)
                {
                    using var curve = extrusion.Profile3d(i, 0);
                    using var mass = AreaMassProperties.Compute(curve);
                    if (mass is null || mass.Area <= 0) return false;
                    area += (i == 0 ? 1 : -1) * mass.Area / (scale * scale);
                }
                using var volume = VolumeMassProperties.Compute(extrusion);
                if (area <= 0 || volume is null || Math.Abs(volume.Volume / Math.Pow(scale, 3) - area * options.ParapetHeightMetres) > Math.Max(1e-7, area * options.ParapetHeightMetres * 1e-6)) return false;
                band.AddRange(Profiles(extrusion, scale));
            }
            if (ContourPaths.Area(ContourPaths.Difference(band, roof)) > 1e-8) return false;
            // All original roof edges must remain covered, including courtyard edges.
            foreach (var path in roof)
            foreach (var point in path)
                if (!band.Any(ring => Clipper.PointInPolygon(point, ring, EditRegion.ClipPrecision) != PointInPolygonResult.IsOutside)) return false;
        }
        return true;
    }

    private static IEnumerable<PathD> Profiles(Extrusion extrusion, double scale)
    {
        for (int i = 0; i < extrusion.ProfileCount; i++)
        {
            using var profile = extrusion.Profile3d(i, 0);
            if (!profile.TryGetPolyline(out var line)) throw new InvalidOperationException("Expected a polygonal roof profile");
            var path = new PathD(line.Take(line.Count - 1).Select(p => new PointD(p.X / scale, p.Y / scale)));
            if ((Clipper.Area(path) > 0) != (i == 0)) path.Reverse();
            yield return path;
        }
    }
    private static string Role(RhinoObject obj) => obj.Attributes.GetUserString("AHA.Role") ?? "";
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException("Parapet: " + label); }
}
