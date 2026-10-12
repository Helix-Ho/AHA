using Aha.Info;
using Rhino;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class XYChecks
{
    internal static Dictionary<string, object?> Run()
    {
        var checks = new Dictionary<string, object?>();
        foreach (var units in new[] { UnitSystem.Meters, UnitSystem.Millimeters })
        {
            using var doc = RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem = units;
            doc.ModelAbsoluteTolerance = units == UnitSystem.Meters ? .001 : 1;
            doc.UndoRecordingEnabled = true;
            var marker = doc.Objects.AddPoint(new Point3d(100, 100, 100));
            var originalLayers = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).ToHashSet();
            var projection = new LocalProjection(new GeoPoint(127, 37.5));
            var bounds = projection.Bounds(80);
            var targets = new[] { Parcel("1", 0, 0, 10, 10), Parcel("2", 10, 0, 20, 10), Parcel("3", 40, 0, 60, 20) };
            targets[2].Polygons[0].Add(Ring(44, 4, 46, 6));
            var road = new GeoFeature { Id = "road", Pnu = "9900000000100040000", LandCategoryCode = ParcelLandCategory.Road,
                Address = "Road address", Polygons = new() { new() { Ring(-5,-90, -4.98, 90) } } };
            var parcels = targets.Append(road).ToArray();
            var buildings = new[] { Building("inside", -20,-20,-10,-10), Building("crossing",70,40,110,50),
                Building("small",-30,-30,-29,-29), Building("underground",-30,-40,-29,-39, true),
                Building("outside",100,100,120,120) };
            var invalid = new GeoFeature { Id = "invalid", Polygons = new() { new() { new() {
                projection.ToGeo(0,-50),projection.ToGeo(10,-40),projection.ToGeo(0,-40),projection.ToGeo(10,-50),projection.ToGeo(0,-50) } } } };
            buildings = buildings.Append(invalid).ToArray();
            uint before = doc.NextUndoRecordSerialNumber;
            using var prepared = XYModel.Prepare(ModelContext.Capture(doc), projection.Origin, bounds, targets, parcels, buildings);
            var preparation = prepared.Details;
            checks[units + "_prepared_metadata_without_created_identifiers"] = preparation.ParentLayerId == Guid.Empty
                && preparation.CreatedCount == 0 && preparation.ObjectCounts.Values.Sum() == prepared.ObjectCount
                && preparation.SelectedParcels == 3 && preparation.SiteComponents == 2
                && preparation.InvalidBuildingIds.SequenceEqual(new[] { "invalid" });
            var result = prepared.Commit(doc);
            var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
            var actualCounts = objects.GroupBy(obj => obj.Attributes.GetUserString("AHA.Role"))
                .ToDictionary(group => group.Key, group => group.Count());
            checks[units + "_creation_preserves_prepared_metadata_and_detached_identifiers"] = preparation.ParentLayerId == Guid.Empty
                && preparation.ObjectIds.Count == 0 && result.ParentLayerId != Guid.Empty && result.CreatedCount == prepared.ObjectCount
                && result.Coordinates == preparation.Coordinates && result.AppliedSettings == preparation.AppliedSettings
                && result.Warnings.SequenceEqual(preparation.Warnings) && result.InvalidBuildingIds.SequenceEqual(preparation.InvalidBuildingIds)
                && result.ObjectCounts.Count == actualCounts.Count && result.ObjectCounts.All(pair => actualCounts.GetValueOrDefault(pair.Key) == pair.Value);
            double scale = units == UnitSystem.Meters ? 1 : 1000;
            var targetObjects = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") == "TargetBoundary").ToArray();
            double targetArea = 0;
            foreach (var obj in targetObjects)
            {
                using var area = AreaMassProperties.Compute((Curve)obj.Geometry);
                var info = InfoData.Parse(obj.Attributes.GetUserString(InfoData.Key)!);
                targetArea += area.Area * (info["site"]!.GetValue<bool>() ? 1 : -1);
            }
            checks[units + "_union_holes_metadata_units_area"] = targetObjects.Length == 3 && result.SiteComponents == 2
                && result.SelectedParcels == 3 && Math.Abs(targetArea / (scale * scale) - 596) < 1e-5
                && result.Coordinates.Origin == projection.Origin && result.Coordinates.DocumentUnitsPerMetre == scale
                && doc.ModelUnitSystem == units && objects.All(obj => obj.Geometry is Curve { IsValid: true }
                    && obj.Geometry.GetBoundingBox(true).Min.Z == 0 && obj.Geometry.GetBoundingBox(true).Max.Z == 0)
                && targetObjects.All(obj =>
                {
                    var info = InfoData.Parse(obj.Attributes.GetUserString(InfoData.Key)!);
                    return info["data"]!["parcels"]!.AsArray().All(parcel => parcel!["address"] is not null && parcel["area_m2"] is null);
                });
            var xy = doc.Layers.First(layer => !layer.IsDeleted && layer.ParentLayerId == result.ParentLayerId);
            checks[units + "_visible_xy_only_branch"] = xy.Name == "XY" && xy.IsVisible
                && objects.All(obj => doc.Layers[obj.Attributes.LayerIndex].ParentLayerId == xy.Id)
                && doc.Layers.Where(layer => !layer.IsDeleted && !originalLayers.Contains(layer.Id)).Count() == 6
                && doc.Layers.First(layer => layer.Id == result.ParentLayerId).IsExpanded && !xy.IsExpanded;
            var footprints = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") == "BuildingFootprint").ToArray();
            checks[units + "_valid_crossing_and_underground_references_without_model_height"] = footprints.Length == 3
                && footprints.Select(obj => obj.Attributes.GetUserString("AHA.FeatureId")).ToHashSet().SetEquals(new[] { "inside", "crossing", "underground" })
                && footprints.All(obj => InfoData.Parse(obj.Attributes.GetUserString(InfoData.Key)!)["data"]!["height_m"] is null)
                && result.InvalidBuildingIds.SequenceEqual(new[] { "invalid" });
            var crossing = footprints.Single(obj => obj.Attributes.GetUserString("AHA.FeatureId") == "crossing");
            checks[units + "_source_clipping_does_not_close_extent"] = crossing.Geometry is Curve { IsClosed: false }
                && Math.Abs(crossing.Geometry.GetBoundingBox(true).Max.X / scale - 80) < 1e-6;
            var roads = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") == "RoadBoundary").ToArray();
            checks[units + "_road_source_not_model_cleanup"] = roads.Length == 2
                && roads.All(obj => obj.Geometry is Curve { IsClosed: false })
                && roads.SelectMany(obj => new[] { obj.Geometry.GetBoundingBox(true).Min.X / scale, obj.Geometry.GetBoundingBox(true).Max.X / scale })
                    .Any(x => Math.Abs(x + 4.98) < 1e-6);

            uint after = doc.NextUndoRecordSerialNumber;
            bool undone = doc.Undo();
            bool removed = result.ObjectIds.All(id => doc.Objects.FindId(id) is null or { IsDeleted: true });
            bool originalPreserved = doc.Objects.FindId(marker) is { IsDeleted: false }
                && originalLayers.SetEquals(doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id));
            bool endUndo = TopoWorkflowChecks.EndHostRecord(doc);
            bool redone = doc.Redo();
            bool restored = result.ObjectIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false });
            bool endRedo = TopoWorkflowChecks.EndHostRecord(doc);
            checks[units + "_single_undo_redo_preserves_existing_content"] = after == before + 1
                && undone && removed && originalPreserved && endUndo && redone && restored && endRedo;

            var terrain = TerrainField.FromSamples(bounds, projection, 2, 2, Enumerable.Repeat(0d, 9).ToArray());
            var context = CheckModel.Build(doc, projection.Origin, bounds, targets, parcels, buildings, terrain,
                new ModelOptions { UseNearRegion = false });
            var fullXY = context.ObjectIds.Select(id => doc.Objects.FindId(id)!).Where(obj =>
                obj.Attributes.GetUserString("AHA.CurveSpace") == "XY").ToArray();
            checks[units + "_same_planar_context_as_full_topo"] = fullXY.Length == result.ObjectIds.Count && result.ObjectIds.All(id =>
            {
                var obj = doc.Objects.FindId(id)!;
                return fullXY.Any(other => other.Attributes.Name == obj.Attributes.Name
                    && Curve.GetDistancesBetweenCurves((Curve)obj.Geometry, (Curve)other.Geometry, .000001 * scale,
                        out var max, out _, out _, out _, out _, out _) && max < .000002 * scale);
            });
            var objectIds = ModelGeometryChecks.AllObjects(doc).Select(obj => obj.Id).ToHashSet();
            var layerIds = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).ToHashSet();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool rejected = false;
            try { CheckModel.XY(doc, projection.Origin, bounds, targets, parcels, buildings, ct: cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            checks[units + "_cancelled_xy_has_no_partial_output"] = rejected
                && objectIds.SetEquals(ModelGeometryChecks.AllObjects(doc).Select(obj => obj.Id))
                && layerIds.SetEquals(doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id));
            var withoutRoads = CheckModel.XY(doc, projection.Origin, bounds, targets, parcels, buildings, includeRoads: false);
            checks[units + "_roads_off_preserves_parcel_source"] = !withoutRoads.ObjectCounts.ContainsKey("RoadBoundary")
                && withoutRoads.ObjectCounts["ParcelBoundary"] == result.ObjectCounts["ParcelBoundary"];

            var edge = projection.ToGeo(50.0000008, 50.0000008);
            var edgeBounds = new GeoBounds(projection.ToGeo(-50.0000008, 0).Lon, projection.ToGeo(0, -50.0000008).Lat, edge.Lon, edge.Lat);
            var edgeTarget = Parcel("9", 30, -10, 50.00000079, 10);
            var exactTarget = CheckModel.XY(doc, projection.Origin, edgeBounds, [edgeTarget], [], []);
            checks[units + "_quantized_target_edge_stays_closed"] = exactTarget.ObjectIds.Select(id => doc.Objects.FindId(id)!).All(obj =>
                obj.Geometry is Curve { IsClosed: true, IsValid: true } && InfoData.Parse(obj.Attributes.GetUserString(InfoData.Key)!)["site"]!.GetValue<bool>());
            var countBeforeInvalidTarget = doc.Objects.Count;
            bool targetClippingRejected = false;
            try { CheckModel.XY(doc, projection.Origin, edgeBounds, [Parcel("8", 30, -10, 51, 10)], [], []); }
            catch (ArgumentException) { targetClippingRejected = true; }
            checks[units + "_direct_xy_rejects_outside_target_without_partial_result"] = targetClippingRejected && doc.Objects.Count == countBeforeInvalidTarget;

            GeoFeature Parcel(string id, double x0, double y0, double x1, double y1) => new()
            { Id = id, Pnu = "411111300010840009" + id, Address = "Fixed parcel " + id, LandCategoryCode = 8,
                Polygons = new() { new() { Ring(x0, y0, x1, y1) } } };
            GeoFeature Building(string id, double x0, double y0, double x1, double y1, bool underground = false) => new()
            { Id = id, Floors = 2, BuildingPositionCode = underground ? "1" : "0", BuildingClassification = BuildingClassificationStatus.Verified,
                Polygons = new() { new() { Ring(x0,y0,x1,y1) } } };
            List<GeoPoint> Ring(double x0, double y0, double x1, double y1) => new()
            { projection.ToGeo(x0,y0), projection.ToGeo(x1,y0), projection.ToGeo(x1,y1), projection.ToGeo(x0,y1), projection.ToGeo(x0,y0) };
        }
        checks["geometry_checks_passed"] = checks.Values.All(value => value is true);
        return checks;
    }
}
