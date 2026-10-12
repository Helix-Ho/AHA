using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class BuildingClassificationChecks
{
    internal static Dictionary<string, object?> Run(string directory)
    {
        var results = new List<object>();
        bool passed = true;
        foreach (var unit in new[] { UnitSystem.Meters, UnitSystem.Millimeters })
        foreach (bool near in new[] { false, true })
        {
            var origin = new GeoPoint(127, 37.5);
            var projection = new LocalProjection(origin);
            var bounds = projection.Bounds(100);
            double scale = RhinoMath.UnitScale(UnitSystem.Meters, unit);
            GeoFeature Feature(string id, double x, string code, BuildingClassificationStatus status, double floors = 0)
                => new()
                {
                    Id = id, SourceLayer = VWorldClient.BuildingSourceLayer, BuildingId = "identity-" + id,
                    Pnu = "9900000000100020000", Address = "확보한 주소 " + id,
                    BuildingDetails = new BuildingDetails(id, "", "", "가상시", "종로구", "", "테스트로", "1"),
                    Floors = floors, BuildingPositionCode = code, BuildingClassification = status,
                    Polygons = new() { new() { new()
                    {
                        projection.ToGeo(x - 3, -3), projection.ToGeo(x + 3, -3),
                        projection.ToGeo(x + 3, 3), projection.ToGeo(x - 3, 3), projection.ToGeo(x - 3, -3)
                    } } }
                };
            var target = Feature("target", 0, "", BuildingClassificationStatus.NotRequested);
            var buildings = new[]
            {
                Feature("above-zero-floor", 0, "0", BuildingClassificationStatus.Verified),
                Feature("below-zero-floor", -25, "1", BuildingClassificationStatus.Verified),
                Feature("below-positive-floor", 25, "1", BuildingClassificationStatus.Verified, 2),
                Feature("unknown-position", 45, "", BuildingClassificationStatus.NotFound, 2)
            };
            var parcels = new[] { Feature("near-parcel", 4, "", BuildingClassificationStatus.NotRequested),
                Feature("far-parcel", 45, "", BuildingClassificationStatus.NotRequested) };
            var terrain = TerrainField.FromSamples(bounds, projection, 4, 4, new double[25]);
            using var doc = RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem = unit; doc.ModelAbsoluteTolerance = .001 * scale;
            var result = CheckModel.Build(doc, origin, bounds, target, parcels, buildings,
                terrain, new ModelOptions(UseNearRegion: near, IncludeRoads: false));
            var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
            var models = objects.Where(o => o.Attributes.GetUserString("AHA.Role") == "Building").ToArray();
            var modelsById = models.ToDictionary(o => o.Attributes.GetUserString("AHA.FeatureId")!);
            bool aboveKept = modelsById.Keys.ToHashSet().SetEquals(new[] { "above-zero-floor", "unknown-position" })
                && modelsById["above-zero-floor"].Attributes.GetUserString("AHA.HeightSource") == "default_estimate";
            bool sourceReferences = buildings.All(building =>
            {
                var xy = objects.Single(o => o.Attributes.GetUserString("AHA.Role") == "BuildingFootprint"
                    && o.Attributes.GetUserString("AHA.CurveSpace") == "XY"
                    && o.Attributes.GetUserString("AHA.FeatureId") == building.Id);
                if (xy.Geometry is not Curve curve || !curve.TryGetPolyline(out var line)) return false;
                var ring = building.Polygons[0][0];
                return line.Count == ring.Count && line.Select((p, i) =>
                {
                    var original = projection.ToLocal(ring[i]);
                    return p.DistanceTo(new Point3d(original.X * scale, original.Y * scale, 0)) < 1e-8 * scale;
                }).All(x => x);
            });
            bool metadata = buildings.All(building =>
            {
                var xy = objects.Single(o => o.Attributes.GetUserString("AHA.Role") == "BuildingFootprint"
                    && o.Attributes.GetUserString("AHA.FeatureId") == building.Id);
                string raw = xy.Attributes.GetUserString(Aha.Info.InfoData.Key)!;
                var data = Aha.Info.InfoData.Parse(raw)["data"]!;
                bool detailed = near && building.Id == "above-zero-floor";
                return (data["building_name"] is not null) == detailed
                    && (data["parcels"]![0]!["address"] is not null) == detailed
                    && (!modelsById.TryGetValue(building.Id, out var model) || raw == model.Attributes.GetUserString(Aha.Info.InfoData.Key));
            }) && parcels.All(parcel =>
            {
                var xy = objects.Single(o => o.Attributes.GetUserString("AHA.Role") == "ParcelBoundary"
                    && o.Attributes.GetUserString("AHA.FeatureId") == parcel.Id);
                var data = Aha.Info.InfoData.Parse(xy.Attributes.GetUserString(Aha.Info.InfoData.Key)!)["data"]!;
                return (data["parcels"]![0]!["address"] is not null) == (near && parcel.Id == "near-parcel");
            }) && objects.Where(o => o.Attributes.GetUserString("AHA.Role") == "TargetBoundary").All(obj =>
            {
                var info = Aha.Info.InfoData.Parse(obj.Attributes.GetUserString(Aha.Info.InfoData.Key)!);
                return info["data"]!["parcels"]![0]!["address"]!.GetValue<string>() == target.Address
                    && info["site"]!.GetValue<bool>() == (obj.Attributes.GetUserString("AHA.CurveSpace") == "Projected");
            });
            bool counts = result.UndergroundBuildings == 2 && result.UnclassifiedBuildings == 1
                && result.FloorEstimatedBuildings == 1 && result.DefaultEstimatedBuildings == 1;
            string path = Path.Combine(directory, $"building-classification-{unit}-{near}.3dm");
            bool write = doc.Write3dmFile(path, new FileWriteOptions { FileVersion = 8, SuppressDialogBoxes = true, IncludeRenderMeshes = true });
            using var saved = write ? File3dm.Read(path) : null;
            bool persisted = saved is not null && saved.Objects.Count(o => o.Attributes.GetUserString("AHA.ExcludedUnderground") == "True"
                && o.Attributes.GetUserString("AHA.CurveSpace") == "XY") == 2
                && saved.Objects.Count(o => o.Attributes.GetUserString("AHA.Role") == "Building") == 2;
            bool casePassed = aboveKept && sourceReferences && metadata && TopoWorkflowChecks.LayerStructure(doc, result.ParentLayerId, near) && counts && persisted && objects.All(o => o.Geometry.IsValid);
            passed &= casePassed;
            results.Add(new { unit = unit.ToString(), near, aboveKept, sourceReferences, metadata, counts, persisted, passed = casePassed });
        }
        return new() { ["geometry_checks_passed"] = passed, ["building_classification_output_checks"] = results };
    }
}
