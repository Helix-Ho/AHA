using System.Text.Json.Nodes;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Aha.Info.Checks;

/// <summary>Invoke on Rhino's UI thread; creates and disposes its own headless document.</summary>
public static class InfoNativeChecks
{
    /// <summary>Returns full exception details across reflection or Python invocation boundaries.</summary>
    public static string RunSafe()
    {
        try
        {
            return JsonSerializer.Serialize(new { success = true, assertions = Run() });
        }
        catch (Exception error)
        {
            return JsonSerializer.Serialize(new { success = false, error = error.ToString() });
        }
    }

    public static int Run()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.UndoRecordingEnabled = true;
        var count = 0;
        void Check(bool value, string message)
        {
            count++;
            if (!value) throw new InvalidOperationException(message);
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            count++;
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException(message);
        }
        JsonObject Patch(string value) => (JsonObject)JsonNode.Parse(value)!;
        using var attributes = new ObjectAttributes { Name = "Info check curve" };
        attributes.SetUserString("other.plugin", "untouched");
        attributes.SetUserString("AHA.Role", "TargetBoundary");
        var id = doc.Objects.AddCurve(new Circle(Plane.WorldXY, 10).ToNurbsCurve(), attributes);
        var geometryBefore = doc.Objects.FindId(id)!.Geometry.DataCRC(0);
        Check(InfoService.Read(doc, id) is null, "New object must have no Info.");
        var document = InfoData.Create("curve", true, Patch("""{"parcels":[{"pnu":"9900000000100020000","address":"가상시 예시동 1","land_category":"대","area_m2":1234.5,"land_use_zones":{"national_plan":"시험 지역(조건 유지)","other_laws":"시험 지구","custom":true}}],"bcr_percent":60}"""));
        document["extension"] = Patch("""{"keep":true}""");
        InfoService.Update(doc, id, document, "");
        var saved = doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key);
        Check(InfoService.Read(doc, id)!["site"]!.GetValue<bool>(), "Closed curve must support site.");
        Check(doc.Objects.FindId(id)!.Attributes.GetUserString("other.plugin") == "untouched", "Unrelated UserText must survive.");
        Check(doc.Objects.FindId(id)!.Attributes.GetUserString("AHA.Role") == "TargetBoundary", "Topo technical tags must survive.");
        Check(doc.Objects.FindId(id)!.Geometry.DataCRC(0) == geometryBefore, "Info save must not replace geometry.");
        Check(InfoService.Read(doc, id)!["data"]!["parcels"]![0]!["area_m2"]!.GetValue<double>() == 1234.5,
            "Stored parcel area is not derived from the circle geometry.");
        Reject<ArgumentException>(() => InfoService.Update(doc, id, Patch("""{"data":{"bcr_percent":"60"}}"""), saved), "Service must reject numeric strings before writing.");
        Reject<ArgumentException>(() => InfoService.Update(doc, id, Patch("""{"data":{"max_floors":2.5}}"""), saved), "Service must reject fractional floor limits.");
        Reject<ArgumentException>(() => InfoService.Update(doc, id, Patch("""{"data":{"parcels":[{"pnu":123}]}}"""), saved), "Service must reject numeric PNU values.");
        Check(doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key) == saved, "Rejected fixed-field patches must leave persisted JSON unchanged.");
        InfoService.Update(doc, id, new JsonObject(), saved);
        Check(doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key) == saved, "No-op keeps exact stored JSON.");
        Check(doc.Undo(), "Info save must create an undo record.");
        Check(InfoService.Read(doc, id) is null, "Undo must remove only created Info.");
        EndHostRecord(doc);
        Check(doc.Redo(), "Info save must support redo.");
        EndHostRecord(doc);
        Check(doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key) == saved, "Redo must restore exact JSON.");
        Reject<InvalidOperationException>(() => InfoService.Update(doc, id, Patch("""{"data":{"notes":"stale"}}"""), ""), "Missing snapshot must not overwrite existing data.");
        InfoService.Update(doc, id, Patch("""{"site":false,"data":{"notes":"updated"}}"""), saved);
        var after = InfoService.Read(doc, id)!;
        Check(after["data"]!["parcels"]!.AsArray().Count == 1 && after["extension"]!["keep"]!.GetValue<bool>(), "Site off and edits must preserve parcel/unknown data.");
        Reject<InvalidOperationException>(() => InfoService.Delete(doc, id, saved), "Stale deletion must be rejected.");
        var copyId = doc.Objects.Duplicate(id);
        Check(copyId != Guid.Empty && copyId != id && JsonNode.DeepEquals(InfoService.Read(doc, copyId), after),
            "Rhino object duplication must preserve metadata under a new object identity.");
        InfoService.Update(doc, copyId, Patch("""{"data":{"notes":"copy only"}}"""));
        Check(InfoService.Read(doc, id)!["data"]!["notes"]!.GetValue<string>() == "updated"
            && InfoService.Read(doc, copyId)!["data"]!["notes"]!.GetValue<string>() == "copy only",
            "Copied objects must have independent Info without automatic synchronization.");

        var point = doc.Objects.AddPoint(Point3d.Origin);
        Reject<ArgumentException>(() => InfoService.Update(doc, point, document, ""), "Point cannot be a site.");
        Check(InfoService.Read(doc, point) is null, "Rejected site write must leave no attributes.");
        var open = doc.Objects.AddLine(Point3d.Origin, new Point3d(1, 0, 0));
        Reject<ArgumentException>(() => InfoService.Update(doc, open, document, ""), "Open curve cannot be a site.");
        InfoService.Update(doc, point, InfoData.Create("point", false, new JsonObject()), "");
        Check(InfoService.Read(doc, point)!["kind"]!.GetValue<string>() == "point", "Non-site metadata works on arbitrary objects.");

        doc.Objects.Lock(point, true);
        Reject<InvalidOperationException>(() => InfoService.Update(doc, point, Patch("""{"data":{"notes":"locked"}}""")), "Locked object edits must fail.");
        doc.Objects.Unlock(point, true);
        doc.Objects.Delete(open, true);
        Reject<InvalidOperationException>(() => InfoService.Read(doc, open), "Deleted object access must fail.");

        using (var attr = doc.Objects.FindId(point)!.Attributes.Duplicate())
        {
            attr.SetUserString(InfoData.Key, """{"version":1,"kind":"point","site":false,"data":{"max_floors":"bad"}}""");
            doc.Objects.ModifyAttributes(point, attr, true);
        }
        Check(InfoService.Read(doc, point)!["data"]!["max_floors"]!.GetValue<string>() == "bad", "Malformed stored fields remain readable for repair.");
        InfoService.Update(doc, point, Patch("""{"data":{"max_floors":0}}"""));
        Check(InfoService.Read(doc, point)!["data"]!["max_floors"]!.GetValue<int>() == 0, "A service patch can repair malformed data and preserve explicit zero.");

        using (var attr = doc.Objects.FindId(point)!.Attributes.Duplicate())
        {
            attr.SetUserString(InfoData.Key, """{"version":2,"kind":"point","site":false,"data":{}}""");
            doc.Objects.ModifyAttributes(point, attr, true);
        }
        Check(InfoService.Read(doc, point)!["version"]!.GetValue<int>() == 2, "Future Info may be read.");
        Reject<NotSupportedException>(() => InfoService.Update(doc, point, Patch("""{"data":{"notes":"edit"}}""")), "Future Info edit must fail.");
        Reject<NotSupportedException>(() => InfoService.Delete(doc, point), "Future Info deletion must fail.");

        count += SolidSiteChecks();
        count += InfoPanelChecks.Run();
        var path = Path.Combine(Path.GetTempPath(), "aha-info-check-" + Guid.NewGuid().ToString("N") + ".3dm");
        var roundtripPassed = false;
        try
        {
            var obj = doc.Objects.FindId(id) ?? throw new InvalidOperationException("Info test object is missing before save.");
            var expectedInfo = obj.Attributes.GetUserString(InfoData.Key);
            // Exercise Rhino's document writer, including layer and object identities.
            Check(doc.Write3dmFile(path, new FileWriteOptions { FileVersion = 8, SuppressDialogBoxes = true }),
                "Info fixture must serialize through the Rhino document writer.");
            using var archive = File3dm.ReadWithLog(path, out var readLog)
                ?? throw new InvalidDataException($"Saved Info archive could not be read. {readLog}");
            // Rhino 8.35's File3dm object table can expose a null indexed slot.
            // Native enumeration avoids the IList fast path used by LINQ First().
            File3dmObject? stored = null;
            foreach (var record in archive.Objects)
            {
                if (record is null) throw new InvalidDataException("Saved Info archive contains a null enumerated object.");
                if (record.Attributes.ObjectId == id) { stored = record; break; }
            }
            Check(stored is not null, "Saved Info archive must contain the original object GUID.");
            Check(stored!.Attributes.GetUserString(InfoData.Key) == expectedInfo, "Info must persist in the saved archive.");
            Check(stored.Attributes.GetUserString("other.plugin") == "untouched" && stored.Geometry.DataCRC(0) == geometryBefore,
                "Archive roundtrip must preserve other UserText and geometry.");
            using var reopened = RhinoDoc.OpenHeadless(path)
                ?? throw new InvalidDataException("Saved Info archive could not be reopened as a Rhino document.");
            Check(JsonNode.DeepEquals(InfoService.Read(reopened, id), InfoData.Parse(expectedInfo!)),
                "Info service must read the same data from a reopened Rhino document.");
            using var imported = RhinoDoc.CreateHeadless(null);
            Check(imported.Import(path), "The saved archive must import into a separate document.");
            var importedObjects = imported.Objects.GetObjectList(ObjectType.AnyObject).ToArray();
            var importedOriginal = importedObjects.Single(obj => obj.Attributes.GetUserString(InfoData.Key) == expectedInfo);
            var importedCopy = importedObjects.Single(obj => obj.Attributes.GetUserString(InfoData.Key)
                == doc.Objects.FindId(copyId)!.Attributes.GetUserString(InfoData.Key));
            Check(importedOriginal.Id != importedCopy.Id && importedOriginal.Geometry.DataCRC(0) == geometryBefore,
                "Import must preserve separate copies and exact geometry.");
            InfoService.Update(imported, importedOriginal.Id, Patch("""{"data":{"notes":"import only"}}"""));
            Check(doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key) == expectedInfo
                && JsonNode.DeepEquals(InfoService.Read(reopened, id), InfoData.Parse(expectedInfo!))
                && InfoService.Read(imported, importedCopy.Id)!["data"]!["notes"]!.GetValue<string>() == "copy only",
                "An imported object's edit must not synchronize to the source, reopened file or sibling copy.");
            roundtripPassed = true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Info archive roundtrip failed. Diagnostic archive: {path}", error);
        }
        finally { if (roundtripPassed && File.Exists(path)) File.Delete(path); }
        var expected = doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key);
        InfoService.Delete(doc, id, expected);
        Check(InfoService.Read(doc, id) is null && doc.Objects.FindId(id)!.Geometry.DataCRC(0) == geometryBefore, "Delete must remove Info only.");
        return count;
    }

    private static int SolidSiteChecks()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.UndoRecordingEnabled = true;
        var count = 0;
        void Check(bool passed, string message)
        {
            count++;
            if (!passed) throw new InvalidOperationException(message);
        }
        using var curve = new Circle(Plane.WorldXY, 2).ToNurbsCurve();
        using var extrusion = Extrusion.Create(curve, 5, true);
        using var brep = new BoundingBox(Point3d.Origin, new Point3d(3, 4, 5)).ToBrep();
        using var openFace = new PlaneSurface(Plane.WorldXY, new Interval(0, 3), new Interval(0, 4)).ToBrep();
        using var openExtrusion = Extrusion.Create(curve, 5, false);
        using var mesh = Mesh.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(3, 4, 5)), 1, 1, 1);
        foreach (var (geometry, allowed) in new (GeometryBase, bool)[] { (brep, true), (extrusion, true), (openFace, false), (openExtrusion, false), (mesh, false) })
        {
            Guid id = geometry switch
            {
                Extrusion item => doc.Objects.AddExtrusion(item),
                Brep item => doc.Objects.AddBrep(item),
                Mesh item => doc.Objects.AddMesh(item),
                _ => throw new InvalidOperationException("Unsupported check geometry")
            };
            var obj = doc.Objects.FindId(id)!;
            Check(id != Guid.Empty && InfoService.CanBeSite(obj) == allowed, "Site eligibility must distinguish closed Brep/Extrusion from open geometry and Mesh.");
            var patch = InfoData.Create("parcel", true, new JsonObject { ["notes"] = geometry.GetType().Name });
            bool rejected = false;
            try { InfoService.Update(doc, id, patch, ""); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected != allowed, "Info uses the same solid site eligibility for writes.");
            if (!allowed) continue;
            Check(InfoService.Read(doc, id)!["site"]!.GetValue<bool>(), "Closed solid Info is readable.");
            InfoService.Update(doc, id, new JsonObject { ["site"] = false });
            Check(!InfoService.Read(doc, id)!["site"]!.GetValue<bool>() && InfoService.Read(doc, id)!["data"]!["notes"]!.GetValue<string>() == geometry.GetType().Name,
                "Solid site designation can be cleared without discarding metadata.");
            Check(doc.Undo(), "Solid site designation supports Undo."); EndHostRecord(doc);
            Check(InfoService.Read(doc, id)!["site"]!.GetValue<bool>(), "Undo restores solid site designation.");
            Check(doc.Redo(), "Solid site designation supports Redo."); EndHostRecord(doc);
            Check(!InfoService.Read(doc, id)!["site"]!.GetValue<bool>(), "Redo clears solid site designation.");
        }
        // Geometry may change independently of Info. Reading and clearing the old designation
        // must remain possible; writing site=true still checks the current geometry.
        var changed = doc.Objects.AddBrep(brep);
        InfoService.Update(doc, changed, InfoData.Create("parcel", true, new JsonObject()), "");
        Check(doc.Objects.Replace(changed, openFace), "A designated solid can be edited into an open surface.");
        Check(InfoService.Read(doc, changed)!["site"]!.GetValue<bool>() && !InfoService.CanBeSite(doc.Objects.FindId(changed)!),
            "Reading preserves the stored designation after an independent geometry edit.");
        InfoService.Update(doc, changed, new JsonObject { ["site"] = false });
        Check(!InfoService.Read(doc, changed)!["site"]!.GetValue<bool>(), "An invalidated designation can be cleared.");
        return count;
    }

    private static void EndHostRecord(RhinoDoc doc)
    {
        if (doc.UndoRecordingIsActive) doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);
    }
}
