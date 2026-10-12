using System.Text.Json;
using System.Text.Json.Nodes;
using Aha.Info;
using Aha.Runtime;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Aha.EumCrawl.Checks;

/// <summary>Fixed-response checks against the loaded Aha plug-in. All writes stay in owned headless documents.</summary>
public static class EumNativeChecks
{
    private static string FixturePnu => FixtureData.Facts.GetProperty("pnu").GetString()!;
    private static int running;
    private sealed record ObjectState(uint Geometry, string Attributes, bool Selected);
    private sealed record DocumentState(bool Modified, Dictionary<Guid, ObjectState> Objects, Dictionary<Guid, string> Layers);

    /// <summary>Call on Rhino's UI thread. Returns immediately; read report.json instead of blocking the UI.</summary>
    public static string Start(string outputDirectory)
    {
        if (RhinoApp.InvokeRequired) throw new InvalidOperationException("Start must run on Rhino's UI thread.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Eum native checks are already running.");
        try
        {
            _ = FixtureData.Facts; // Fail before scheduling an unobservable background task.
            var directory = Path.Combine(Path.GetFullPath(outputDirectory), "eum-native-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "report.json");
            File.WriteAllText(path, "{\"status\":\"starting\",\"success\":false}");
            _ = Task.Run(() => RunAsync(path));
            return path;
        }
        catch { Interlocked.Exchange(ref running, 0); throw; }
    }

    private static async Task RunAsync(string path)
    {
        var cases = new List<object>();
        var lookups = new List<object>();
        var report = new Dictionary<string, object?>
        {
            ["status"] = "running", ["success"] = false, ["cases"] = cases, ["lookups"] = lookups,
            ["pnu"] = FixturePnu, ["started_at"] = DateTimeOffset.UtcNow,
            ["execution_condition"] = "Run with an empty active test document and leave existing documents and selections unchanged until completion.",
            ["headless_undo_host_effect"] = "Rhino 8 headless Undo can mark the active document Modified. Full preservation is checked before Undo; final content preservation and Modified flags are reported separately.",
            ["scope"] = "Installed EumService PreviewAsync/Apply with fixed HTTP lookups of one parcel; standalone, new Info, explicit parcel slot, identity, raw revision, document/object binding and Undo. Not a panel or MCP transport check."
        };
        var previews = new List<JsonObject>();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var client = new EumClient(new FixtureHandler(FixtureData.DirectoryPath));
        var workflow = new EumWorkflow(client.LookupAsync, () => now);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        RhinoDoc? first = null, second = null;
        Guid firstId = Guid.Empty, otherId = Guid.Empty, secondId = Guid.Empty;
        uint firstSerial = 0, secondSerial = 0;
        uint? activeBefore = null;
        Dictionary<uint, DocumentState>? baseline = null;
        string? outcome = null;
        bool fetching = false;

        void Save()
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        void Stage(string value) { report["stage"] = value; Save(); }
        void Check(string name, bool value)
        {
            cases.Add(new { name, success = value });
            if (!value) throw new InvalidOperationException(name);
        }
        void Reject(string name, Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(message, StringComparison.Ordinal))
            { Check(name, true); return; }
            Check(name, false);
        }
        async Task<JsonObject> Lookup(Guid? objectId, int index)
        {
            fetching = true;
            var preview = await workflow.PreviewAsync(first!, objectId, FixturePnu, index, cancellation.Token).ConfigureAwait(false);
            fetching = false;
            previews.Add(preview);
            lookups.Add(new
            {
                bound = objectId.HasValue, parcel_index = index,
                fields = preview["fields"]!.AsObject().ToDictionary(p => p.Key, p => p.Value?["status"]?.GetValue<string>()),
                warnings = preview["warnings"]?.DeepClone()
            });
            return preview;
        }

        try
        {
            var product = typeof(EumService).Assembly;
            report["production_assembly_path"] = product.Location;
            report["production_assembly_version"] = product.GetName().Version?.ToString();
            report["production_module_id"] = product.ManifestModule.ModuleVersionId.ToString();
            Require(product.GetName().Name == "Aha" && typeof(InfoService).Assembly == product && typeof(RhinoDispatch).Assembly == product,
                "The installed Aha product assembly is required.");
            await RhinoDispatch.InvokeAsync(() =>
            {
                report["rhino_version"] = RhinoApp.Version.ToString();
                activeBefore = RhinoDoc.ActiveDoc?.RuntimeSerialNumber;
                Require(activeBefore is not null, "Open an empty test document first; headless Undo can change its Modified flag.");
                baseline = Snapshot();
                first = CreateFixture(); firstSerial = first.RuntimeSerialNumber;
                second = CreateFixture(); secondSerial = second.RuntimeSerialNumber;
                firstId = AddPoint(first);
                otherId = AddPoint(first);
                secondId = AddPoint(second, firstId);
                Check("fixture_documents_are_headless_and_inactive", first.IsHeadless && second.IsHeadless
                    && RhinoDoc.ActiveDoc?.RuntimeSerialNumber == activeBefore);
                Check("same_object_id_exists_in_distinct_documents", firstId == secondId && firstSerial != secondSerial);
                return true;
            }).ConfigureAwait(false);

            Stage("read_source_without_preview");
            var evidence = await EumService.ReadAsync(first!, FixturePnu, ["bcr_far"], cancellation.Token,
                client.ReadEvidenceAsync).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                Check("evidence_has_no_apply_token_or_candidate_values", evidence["preview_id"] is null && evidence["fields"] is null
                    && evidence["document_id"]!.GetValue<uint>() == firstSerial && evidence["sections"] is JsonArray { Count: 1 });
                Check("evidence_does_not_write_or_select_objects", InfoService.Read(first!, firstId) is null
                    && first!.Objects.FindId(firstId).IsSelected(false) == 0);
                return true;
            }).ConfigureAwait(false);
            var delayedRead = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closingDoc = await RhinoDispatch.InvokeAsync(CreateFixture).ConfigureAwait(false);
            var pendingRead = EumService.ReadAsync(closingDoc, FixturePnu, ["parcel"], cancellation.Token,
                (_, _, _) => { startedRead.TrySetResult(true); return delayedRead.Task; });
            await startedRead.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() => { closingDoc.Dispose(); return true; }).ConfigureAwait(false);
            delayedRead.SetResult((JsonObject)evidence.DeepClone());
            try { await pendingRead.ConfigureAwait(false); throw new Exception("Closed document accepted source evidence"); }
            catch (InvalidOperationException) { Check("evidence_rejects_closed_document_after_response", true); }

            Stage("standalone_lookup");
            var standalone = await Lookup(null, 0).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                Check("standalone_has_no_apply_id", standalone["preview_id"] is null && standalone["object_id"] is null
                    && standalone["can_apply"]?.GetValue<bool>() == false);
                Check("standalone_keeps_info_empty", InfoService.Read(first!, firstId) is null);
                Reject("standalone_cannot_apply", () => workflow.Apply(first!, firstId, standalone, new[] { "parcels.identity" }), "유효한 조회 결과가 없습니다");
                Check("rejected_standalone_did_not_create_info", InfoService.Read(first!, firstId) is null);
                return true;
            }).ConfigureAwait(false);

            Stage("bound_lookup_without_info");
            var createdPreview = await Lookup(firstId, 0).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                Check("bound_preview_does_not_write_info", InfoService.Read(first!, firstId) is null
                    && createdPreview["can_apply"]?.GetValue<bool>() == true && createdPreview["preview_id"] is not null);
                var expectedArea = Candidate(createdPreview, "parcels.area_m2");
                var expectedCategory = Candidate(createdPreview, "parcels.land_category");
                var expectedBcr = Candidate(createdPreview, "bcr_percent");
                var expectedFar = Candidate(createdPreview, "far_percent");
                Check("ratio_candidates_disclose_minimum_policy", new[] { "bcr_percent", "far_percent" }.All(field =>
                    createdPreview["fields"]?[field]?["method"]?.GetValue<string>() == "minimum_percentage"
                    && !string.IsNullOrWhiteSpace(createdPreview["fields"]?[field]?["notice"]?.GetValue<string>())
                    && createdPreview["fields"]?[field]?["observed_percentages"] is JsonArray values
                    && values.Count > 0 && values.Min(value => value!.GetValue<double>()) == Candidate(createdPreview, field).GetValue<double>()));
                Reject("preview_rejects_another_object", () => workflow.Apply(first!, otherId, createdPreview, new[] { "parcels.identity" }), "유효한 조회 결과가 없습니다");
                Reject("preview_rejects_same_guid_in_another_document", () => workflow.Apply(second!, secondId, createdPreview, new[] { "parcels.identity" }), "유효한 조회 결과가 없습니다");
                Check("wrong_targets_stay_without_info", InfoService.Read(first!, otherId) is null && InfoService.Read(second!, secondId) is null);
                var geometry = first!.Objects.FindId(firstId)!.Geometry.DataCRC(0);
                // The public response can be edited by a caller, but Apply must still use the cache.
                createdPreview["fields"]!["parcels.area_m2"]!["value"] = -123;
                var result = workflow.Apply(first, firstId, createdPreview,
                    new[] { "parcels.identity", "parcels.land_category", "parcels.area_m2", "bcr_percent", "far_percent", "notes" });
                var entry = result["data"]!["parcels"]![0]!;
                Check("apply_creates_info_on_an_arbitrary_object", result["site"]?.GetValue<bool>() == false
                    && entry["pnu"]?.GetValue<string>() == FixturePnu && !string.IsNullOrWhiteSpace(entry["address"]?.GetValue<string>())
                    && JsonNode.DeepEquals(entry["land_category"], expectedCategory));
                Check("apply_uses_cached_values_not_caller_preview", JsonNode.DeepEquals(entry["area_m2"], expectedArea));
                Check("ratio_minima_persist_as_numbers_and_notes_explain_method",
                    JsonNode.DeepEquals(result["data"]?["bcr_percent"], expectedBcr)
                    && JsonNode.DeepEquals(result["data"]?["far_percent"], expectedFar)
                    && result["data"]?["notes"]?.GetValue<string>().Contains(EumParser.RatioPolicyNotice, StringComparison.Ordinal) == true);
                Check("apply_keeps_geometry_and_unrelated_usertext", first.Objects.FindId(firstId)!.Geometry.DataCRC(0) == geometry
                    && first.Objects.FindId(firstId)!.Attributes.GetUserString("aha.check.keep") == "untouched");
                Check("apply_does_not_persist_raw_sources", result["data"]?["sources"] is null);
                Check("preexisting_documents_fully_preserved_before_headless_undo", SameDocuments(baseline!, Snapshot(firstSerial, secondSerial)));
                // Rhino's headless Undo itself can mark the active document Modified, even without AHA.
                // The check above includes Modified; after Undo we still require all document contents to be unchanged.
                Check("new_info_apply_can_undo", first.Undo());
                EndHostRecord(first);
                Check("undo_restores_absent_info", InfoService.Read(first, firstId) is null && first.Objects.FindId(firstId)!.Geometry.DataCRC(0) == geometry);
                Check("new_info_apply_can_redo", first.Redo());
                EndHostRecord(first);
                Check("redo_restores_applied_info", JsonNode.DeepEquals(InfoService.Read(first, firstId), result));
                Reject("applied_preview_is_consumed", () => workflow.Apply(first, firstId, createdPreview, new[] { "parcels.identity" }), "유효한 조회 결과가 없습니다");
                return true;
            }).ConfigureAwait(false);

            var original = JsonNode.Parse("""{"version":1,"kind":"point","site":false,"extension":{"keep":true},"data":{"notes":"keep native memo","max_height_m":77,"sources":[{"custom":"preserve existing evidence"}],"parcels":[{"pnu":"__SOURCE__","address":"untouched first parcel","area_m2":11},{"pnu":"__OTHER__","address":"different target address","land_category":"전","area_m2":7,"custom":{"keep":1},"land_use_zones":{"national_plan":"old plan","other_laws":"old laws","extension":"keep"}},{"pnu":"__SOURCE__","address":"untouched repeated PNU","area_m2":13}]}}"""
                .Replace("__SOURCE__", FixturePnu).Replace("__OTHER__", FixtureData.Facts.GetProperty("other_pnu").GetString()))!.AsObject();
            await RhinoDispatch.InvokeAsync(() =>
            {
                InfoService.Update(first!, firstId, original, Raw(first!, firstId));
                return true;
            }).ConfigureAwait(false);
            Stage("mismatched_identity_and_raw_revision");
            var slotPreview = await Lookup(firstId, 1).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                var originalRaw = Raw(first!, firstId)!;
                var normalizedBefore = InfoService.Read(first!, firstId)!;
                Check("explicit_query_and_target_identity_are_different", slotPreview["parcels"]![0]!["pnu"]?.GetValue<string>() == FixturePnu
                    && slotPreview["current_data"]!["parcels"]![1]!["pnu"]?.GetValue<string>() == FixtureData.Facts.GetProperty("other_pnu").GetString());
                WriteRaw(first!, firstId, " \n" + originalRaw);
                Check("raw_revision_fixture_preserves_json_values", JsonNode.DeepEquals(InfoService.Read(first!, firstId), normalizedBefore));
                Reject("raw_revision_change_rejects_apply", () => workflow.Apply(first!, firstId, slotPreview, new[] { "parcels.area_m2" }), "편집 중 객체 정보가 변경되었습니다");
                Check("revision_rejection_does_not_write", Raw(first!, firstId) == " \n" + originalRaw);
                WriteRaw(first!, firstId, originalRaw);
                var expectedArea = Candidate(slotPreview, "parcels.area_m2");
                var expectedCategory = Candidate(slotPreview, "parcels.land_category");
                var expectedZones = Candidate(slotPreview, "parcels.land_use_zones");
                var result = workflow.Apply(first!, firstId, slotPreview,
                    new[] { "parcels.land_category", "parcels.area_m2", "parcels.land_use_zones" });
                var data = result["data"]!.AsObject();
                var target = data["parcels"]![1]!;
                Check("unchecked_identity_survives_mismatched_lookup", target["pnu"]?.GetValue<string>() == FixtureData.Facts.GetProperty("other_pnu").GetString()
                    && target["address"]?.GetValue<string>() == "different target address");
                Check("only_explicit_slot_receives_lookup_values", JsonNode.DeepEquals(target["area_m2"], expectedArea)
                    && JsonNode.DeepEquals(target["land_category"], expectedCategory)
                    && JsonNode.DeepEquals(data["parcels"]![0], original["data"]!["parcels"]![0])
                    && JsonNode.DeepEquals(data["parcels"]![2], original["data"]!["parcels"]![2]));
                Check("known_zone_groups_apply_to_the_target", expectedZones.AsObject().All(p => JsonNode.DeepEquals(target["land_use_zones"]?[p.Key], p.Value)));
                Check("unknown_and_unselected_values_survive", target["custom"]?["keep"]?.GetValue<int>() == 1
                    && target["land_use_zones"]?["extension"]?.GetValue<string>() == "keep"
                    && data["notes"]?.GetValue<string>() == "keep native memo" && data["max_height_m"]?.GetValue<int>() == 77
                    && result["extension"]?["keep"]?.GetValue<bool>() == true
                    && JsonNode.DeepEquals(data["sources"], original["data"]!["sources"]));
                Check("slot_apply_can_undo", first!.Undo());
                EndHostRecord(first!);
                Check("slot_apply_undo_restores_original_info", JsonNode.DeepEquals(InfoService.Read(first!, firstId), normalizedBefore));
                return true;
            }).ConfigureAwait(false);
            Stage("expired_preview");
            var expiredPreview = await Lookup(firstId, 0).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                now = now.AddMinutes(30);
                var raw = Raw(first!, firstId);
                Reject("expired_preview_cannot_apply", () => workflow.Apply(first!, firstId, expiredPreview, new[] { "bcr_percent" }), "유효한 조회 결과가 없습니다");
                Check("expired_preview_keeps_object_unchanged", Raw(first!, firstId) == raw);
                return true;
            }).ConfigureAwait(false);
            Stage("site_solid_selected_fields");
            var siteId = await RhinoDispatch.InvokeAsync(() =>
            {
                using var solid = new BoundingBox(Point3d.Origin, new Point3d(10, 10, 5)).ToBrep();
                var id = first!.Objects.AddBrep(solid);
                InfoService.Update(first, id, InfoData.Create("parcel", true, new JsonObject
                {
                    ["notes"] = "site note", ["parcels"] = new JsonArray(new JsonObject { ["pnu"] = FixturePnu })
                }), "");
                return id;
            }).ConfigureAwait(false);
            var sitePreview = await Lookup(siteId, 0).ConfigureAwait(false);
            await RhinoDispatch.InvokeAsync(() =>
            {
                var before = InfoService.Read(first!, siteId)!;
                var geometry = first!.Objects.FindId(siteId)!.Geometry.DataCRC(0);
                var applied = workflow.Apply(first, siteId, sitePreview, new[] { "bcr_percent", "far_percent" });
                Check("site_solid_apply_preserves_site_geometry_and_unselected_data", applied["site"]!.GetValue<bool>()
                    && JsonNode.DeepEquals(applied["data"]!["parcels"], before["data"]!["parcels"])
                    && applied["data"]!["notes"]!.GetValue<string>() == "site note"
                    && first.Objects.FindId(siteId)!.Geometry.DataCRC(0) == geometry);
                Check("site_solid_receives_selected_ratio_candidates", JsonNode.DeepEquals(applied["data"]!["bcr_percent"], Candidate(sitePreview, "bcr_percent"))
                    && JsonNode.DeepEquals(applied["data"]!["far_percent"], Candidate(sitePreview, "far_percent")));
                Check("site_solid_apply_can_undo", first.Undo()); EndHostRecord(first);
                Check("site_solid_undo_restores_info", JsonNode.DeepEquals(InfoService.Read(first, siteId), before));
                return true;
            }).ConfigureAwait(false);
            var fixedParcel = await client.LookupAsync(FixturePnu, cancellation.Token);
            var capacity = new EumWorkflow((_, _) => Task.FromResult((JsonObject)fixedParcel.DeepClone()), () => now);
            var attempts = Enumerable.Range(0, 110).Select(async _ =>
            {
                try { return await capacity.PreviewAsync(first!, firstId, FixturePnu, 0, cancellation.Token); }
                catch (InvalidOperationException error) when (error.Message.Contains("미리보기가 많습니다")) { return null; }
            }).ToArray();
            var accepted = (await Task.WhenAll(attempts)).Where(p => p is not null).ToArray();
            Check("concurrent_preview_capacity_is_exact", accepted.Length == 100);
            foreach (var item in accepted) capacity.Discard(item);
            var afterDiscard = await capacity.PreviewAsync(first!, firstId, FixturePnu, 0, cancellation.Token);
            Check("discard_releases_capacity", afterDiscard["preview_id"] is not null);
            capacity.Discard(afterDiscard);
            Check("panel_discards_delayed_responses", await EumPanelChecks.RunAsync(first!, firstId, otherId, client));
            outcome = "passed";
        }
        catch (Exception error)
        {
            outcome = fetching && (error is HttpRequestException or OperationCanceledException) ? "network_error"
                : error is InvalidDataException ? "source_error" : "failed";
            report["error"] = error.ToString();
        }
        finally
        {
            try
            {
                await RhinoDispatch.InvokeAsync(() =>
                {
                    foreach (var preview in previews) workflow.Discard(preview);
                    try { second?.Dispose(); }
                    finally { first?.Dispose(); }
                    if (firstSerial != 0) Check("first_fixture_disposed", RhinoDoc.FromRuntimeSerialNumber(firstSerial) is null);
                    if (secondSerial != 0) Check("second_fixture_disposed", RhinoDoc.FromRuntimeSerialNumber(secondSerial) is null);
                    if (baseline is not null)
                    {
                        Check("active_document_is_unchanged", RhinoDoc.ActiveDoc?.RuntimeSerialNumber == activeBefore);
                        var after = Snapshot();
                        report["preexisting_document_modified_flags"] = baseline.Select(p => new
                        {
                            document_id = p.Key, before = p.Value.Modified,
                            after = after.GetValueOrDefault(p.Key)?.Modified
                        }).ToArray();
                        Check("preexisting_documents_objects_attributes_selection_and_layers_unchanged", SameDocumentContents(baseline, after));
                    }
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception error) { outcome = "failed"; report["cleanup_error"] = error.ToString(); }
            report["status"] = outcome ?? "failed";
            report["success"] = outcome == "passed";
            report["assertions"] = cases.Count;
            report["finished_at"] = DateTimeOffset.UtcNow;
            try { Save(); } finally { Interlocked.Exchange(ref running, 0); }
        }
    }

    private static JsonNode Candidate(JsonObject preview, string field)
    {
        var value = preview["fields"]?[field];
        if (value?["status"]?.GetValue<string>() != "candidate" || value["value"] is null)
            throw new InvalidDataException("Fixture did not provide a required candidate: " + field);
        return value["value"]!.DeepClone();
    }
    private static RhinoDoc CreateFixture()
    {
        var doc = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException("Could not create a headless fixture.");
        doc.UndoRecordingEnabled = true;
        return doc;
    }
    private static Guid AddPoint(RhinoDoc doc, Guid? objectId = null)
    {
        using var attributes = new ObjectAttributes { Name = "AHA Eum native check fixture" };
        if (objectId is Guid id) attributes.ObjectId = id;
        attributes.SetUserString("aha.check.keep", "untouched");
        var result = doc.Objects.AddPoint(Point3d.Origin, attributes);
        Require(result != Guid.Empty, "Could not add fixture point.");
        return result;
    }
    private static void WriteRaw(RhinoDoc doc, Guid id, string raw)
    {
        using var attributes = doc.Objects.FindId(id)!.Attributes.Duplicate();
        attributes.SetUserString(InfoData.Key, raw);
        var undo = doc.BeginUndoRecord("AHA Eum native revision fixture");
        try { Require(doc.Objects.ModifyAttributes(id, attributes, true), "Could not write raw revision fixture."); }
        finally { if (undo != 0) doc.EndUndoRecord(undo); }
    }
    private static string? Raw(RhinoDoc doc, Guid id) => doc.Objects.FindId(id)!.Attributes.GetUserString(InfoData.Key);
    private static void EndHostRecord(RhinoDoc doc)
    { if (doc.UndoRecordingIsActive) doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber); }
    private static Dictionary<uint, DocumentState> Snapshot(params uint[] excluded) => RhinoDoc.OpenDocuments(true)
        .Where(doc => !excluded.Contains(doc.RuntimeSerialNumber)).ToDictionary(
        doc => doc.RuntimeSerialNumber, doc => new DocumentState(doc.Modified,
            doc.Objects.GetObjectList(new ObjectEnumeratorSettings { ActiveObjects = true, HiddenObjects = true, LockedObjects = true, DeletedObjects = false })
                .ToDictionary(obj => obj.Id, obj => new ObjectState(obj.Geometry.DataCRC(0), obj.Attributes.ToJSON(new SerializationOptions()), obj.IsSelected(false) != 0)),
            doc.Layers.Where(layer => !layer.IsDeleted).ToDictionary(layer => layer.Id, layer => layer.ToJSON(new SerializationOptions()))));
    private static bool SameDocuments(Dictionary<uint, DocumentState> before, Dictionary<uint, DocumentState> after) =>
        SameDocumentContents(before, after) && before.All(p => p.Value.Modified == after[p.Key].Modified);
    private static bool SameDocumentContents(Dictionary<uint, DocumentState> before, Dictionary<uint, DocumentState> after) =>
        before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var current)
            && p.Value.Layers.Count == current.Layers.Count
            && p.Value.Layers.All(layer => current.Layers.TryGetValue(layer.Key, out var state) && state == layer.Value)
            && p.Value.Objects.Count == current.Objects.Count && p.Value.Objects.All(obj => current.Objects.TryGetValue(obj.Key, out var state) && state == obj.Value));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
