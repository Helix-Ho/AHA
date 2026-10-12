using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Info;
using Aha.Runtime;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Checks;

/// <summary>UI-thread checks against the loaded Aha plug-in; never uses the active document as a fixture.</summary>
public static class RuntimeNativeChecks
{
    public static string RunSafe()
    {
        var cases = new List<object>();
        var report = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["scope"] = "Installed Aha services, document targeting and mutation gate; not concurrent jobs or MCP transport.",
            ["cases"] = cases
        };
        RhinoDoc? first = null, second = null;
        uint firstSerial = 0, secondSerial = 0;
        uint? activeBefore = null;
        Dictionary<uint, (int Count, bool Modified)>? documentsBefore = null;
        var failed = false;

        void Check(string name, Action assertion)
        {
            try { assertion(); cases.Add(new { name, success = true }); }
            catch (Exception error)
            {
                failed = true;
                cases.Add(new { name, success = false, error = error.ToString() });
            }
        }
        void Reject(string name, Action action) => Check(name, () =>
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected a closed/missing/busy target rejection.");
        });

        try
        {
            if (RhinoApp.InvokeRequired) throw new InvalidOperationException("RunSafe must be called on Rhino's UI thread.");
            var assembly = typeof(InfoService).Assembly;
            report["production_assembly_path"] = assembly.Location;
            report["production_assembly_version"] = assembly.GetName().Version?.ToString();
            report["production_module_id"] = assembly.ManifestModule.ModuleVersionId.ToString();
            Require(assembly.GetName().Name == "Aha" && typeof(DocumentAccess).Assembly == assembly,
                "Info and the document gate must come from the same loaded Aha product assembly.");
            report["rhino_version"] = RhinoApp.Version.ToString();
            activeBefore = RhinoDoc.ActiveDoc?.RuntimeSerialNumber;
            documentsBefore = SnapshotDocuments();

            first = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException("Could not create first headless fixture.");
            firstSerial = first.RuntimeSerialNumber;
            second = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException("Could not create second headless fixture.");
            secondSerial = second.RuntimeSerialNumber;
            report["fixture_documents"] = new[]
            {
                new { document_id = firstSerial, name = first.Name, path = first.Path },
                new { document_id = secondSerial, name = second.Name, path = second.Path }
            };
            Check("two_unsaved_headless_documents_have_same_name_and_distinct_targets", () =>
            {
                Require(first.IsHeadless && second.IsHeadless, "Fixtures must be headless.");
                Require(string.IsNullOrEmpty(first.Path) && string.IsNullOrEmpty(second.Path), "Fixtures must remain unsaved.");
                Require(string.Equals(first.Name ?? "", second.Name ?? "", StringComparison.Ordinal), "Unsaved fixture names must be identical.");
                Require(firstSerial != secondSerial, "Document targets must differ despite matching names.");
                Require(DocumentAccess.Get(firstSerial) == first && DocumentAccess.Get(secondSerial) == second, "Serial numbers must resolve to their own documents.");
            });

            var firstId = AddCurve(first);
            var secondId = AddCurve(second);
            Require(firstId != secondId, "Fixture object IDs must differ for wrong-document checks.");
            var originalGeometry = first.Objects.FindId(firstId)!.Geometry.DataCRC(0);
            Check("info_create_read_update_delete_in_explicit_document", () =>
            {
                Require(InfoService.Read(first, firstId) is null, "New fixture must have no Info.");
                InfoService.Update(first, firstId, Patch("created"), "");
                Require(InfoService.Read(first, firstId)?["data"]?["notes"]?.GetValue<string>() == "created", "Create/read must return the new value.");
                var revision = Raw(first, firstId);
                InfoService.Update(first, firstId, Patch("updated"), revision);
                Require(InfoService.Read(first, firstId)?["data"]?["notes"]?.GetValue<string>() == "updated", "Update/read must return the changed value.");
                InfoService.Delete(first, firstId, Raw(first, firstId));
                Require(InfoService.Read(first, firstId) is null, "Delete must remove only Info.");
                Require(first.Objects.FindId(firstId)!.Attributes.GetUserString("aha.check.keep") == "untouched", "Other UserText must survive CRUD.");
                Require(first.Objects.FindId(firstId)!.Geometry.DataCRC(0) == originalGeometry, "CRUD must preserve geometry.");
            });
            InfoService.Update(first, firstId, Patch("first"));
            InfoService.Update(second, secondId, Patch("second"));
            var firstExpected = Raw(first, firstId);
            var secondExpected = Raw(second, secondId);

            Reject("wrong_document_read_rejected", () => InfoService.Read(second, firstId));
            Reject("wrong_document_update_rejected", () => InfoService.Update(second, firstId, Patch("wrong")));
            Reject("wrong_document_delete_rejected", () => InfoService.Delete(second, firstId));
            Check("wrong_document_calls_leave_both_fixtures_unchanged", () =>
                Require(Raw(first, firstId) == firstExpected && Raw(second, secondId) == secondExpected, "Wrong-document calls must not change either fixture."));

            using (DocumentAccess.Enter(first))
            {
                Reject("same_document_gate_reentry_rejected", () => { using var unexpected = DocumentAccess.Enter(first); });
                Reject("info_update_obeys_document_gate", () => InfoService.Update(first, firstId, Patch("busy")));
                Reject("info_delete_obeys_document_gate", () => InfoService.Delete(first, firstId));
                Check("different_document_gate_can_enter", () => { using var independent = DocumentAccess.Enter(second); });
            }
            Check("released_document_gate_can_enter_again", () => { using var reacquired = DocumentAccess.Enter(first); });
            Check("busy_rejections_preserve_info", () => Require(Raw(first, firstId) == firstExpected, "Busy rejections must not write attributes."));

            Require(first.Objects.Delete(firstId, true), "Fixture object deletion failed.");
            Reject("deleted_object_read_rejected", () => InfoService.Read(first, firstId));
            Reject("deleted_object_update_rejected", () => InfoService.Update(first, firstId, Patch("deleted")));
            Reject("deleted_object_info_delete_rejected", () => InfoService.Delete(first, firstId));

            second.Dispose();
            Reject("closed_document_lookup_rejected", () => DocumentAccess.Get(secondSerial));
            Reject("closed_document_gate_rejected", () => { using var unexpected = DocumentAccess.Enter(second); });
            Reject("closed_document_info_read_rejected", () => InfoService.Read(second, secondId));
            Reject("closed_document_info_update_rejected", () => InfoService.Update(second, secondId, Patch("closed")));
            Reject("closed_document_info_delete_rejected", () => InfoService.Delete(second, secondId));
        }
        catch (Exception error) { failed = true; report["error"] = error.ToString(); }
        finally
        {
            if (second is not null) Check("second_fixture_disposed", () => second.Dispose());
            if (first is not null) Check("first_fixture_disposed", () => first.Dispose());
            if (firstSerial != 0) Check("first_fixture_no_longer_open", () => Require(RhinoDoc.FromRuntimeSerialNumber(firstSerial) is null, "First fixture leaked."));
            if (secondSerial != 0) Check("second_fixture_no_longer_open", () => Require(RhinoDoc.FromRuntimeSerialNumber(secondSerial) is null, "Second fixture leaked."));
            if (documentsBefore is not null)
            {
                Check("active_document_unchanged", () => Require(RhinoDoc.ActiveDoc?.RuntimeSerialNumber == activeBefore, "The active document changed."));
                Check("preexisting_documents_preserved", () =>
                {
                    var after = SnapshotDocuments();
                    Require(documentsBefore.Count == after.Count && documentsBefore.All(pair => after.TryGetValue(pair.Key, out var state) && state == pair.Value),
                        "Open document IDs, object counts or modified flags changed outside the fixtures.");
                });
            }
        }
        report["success"] = !failed;
        report["assertions"] = cases.Count;
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<uint, (int Count, bool Modified)> SnapshotDocuments() => RhinoDoc.OpenDocuments(true)
        .ToDictionary(doc => doc.RuntimeSerialNumber, doc => (doc.Objects.Count, doc.Modified));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonObject Patch(string notes) => new() { ["data"] = new JsonObject { ["notes"] = notes } };
    private static string? Raw(RhinoDoc doc, Guid id) => doc.Objects.FindId(id)?.Attributes.GetUserString(InfoData.Key);
    private static Guid AddCurve(RhinoDoc doc)
    {
        using var curve = new Circle(Plane.WorldXY, 10).ToNurbsCurve();
        using var attributes = new ObjectAttributes { Name = "AHA runtime check fixture" };
        attributes.SetUserString("aha.check.keep", "untouched");
        var id = doc.Objects.AddCurve(curve, attributes);
        Require(id != Guid.Empty, "Could not create fixture curve.");
        return id;
    }
}
