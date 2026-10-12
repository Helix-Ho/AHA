using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class TransactionChecks
{
    internal static Dictionary<string, object?> Run()
    {
        var preparedGeometry = PreparedGeometryChecks.Run();
        bool persistenceControls=ModelPersistenceChecks.Controls();
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = UnitSystem.Meters;
        doc.ModelAbsoluteTolerance = .001;
        doc.UndoRecordingEnabled = true;
        var original = doc.Objects.AddPoint(Point3d.Origin);
        var layers = doc.Layers.Where(l => !l.IsDeleted).Select(l => l.Id).ToHashSet();
        var order = doc.Layers.GetSorted();
        int current = doc.Layers.CurrentLayerIndex;
        bool Unchanged() => ModelGeometryChecks.AllObjects(doc).Select(o => o.Id).ToHashSet().SetEquals(new[] { original })
            && layers.SetEquals(doc.Layers.Where(l => !l.IsDeleted).Select(l => l.Id))
            && doc.Layers.CurrentLayerIndex == current && order.SequenceEqual(doc.Layers.GetSorted());

        var tags = new Dictionary<string, string>();
        using var valid = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
        using var unsupported = new Point(Point3d.Origin);
        var result = new ModelResult { LayerName = "aha_topo_rollback_check" };
        bool rejected = false;
        try
        {
            ModelDocumentWriter.Commit(doc, result, new[]
            {
                new ModelDocumentObject(valid, "XY::ParcelBoundary", "first addition", tags),
                new ModelDocumentObject(unsupported, "XY::ParcelBoundary", "unsupported geometry", tags)
            }, new[] { "XY" });
        }
        catch (InvalidOperationException) { rejected = true; }
        bool preflight = rejected && result.ObjectIds.Count == 0 && Unchanged();
        var interrupted = new InterruptedObjects(new(valid, "XY::ParcelBoundary", "first addition", tags),
            () => doc.Objects.Count > 1);
        bool interruptedCommit = false;
        try { ModelDocumentWriter.Commit(doc, result, interrupted, new[] { "XY" }); }
        catch (IOException) { interruptedCommit = true; }
        bool rollback = interruptedCommit && interrupted.AddedBeforeFailure && result.ObjectIds.Count == 0 && Unchanged();

        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(50);
        var terrain = TerrainField.FromSamples(bounds, projection, 2, 2, Enumerable.Repeat(12d, 9).ToArray());
        var target = new GeoFeature { Id = "target", Polygons = new() { new() { new()
        { projection.ToGeo(-5,-5), projection.ToGeo(5,-5), projection.ToGeo(5,5), projection.ToGeo(-5,5), projection.ToGeo(-5,-5) } } } };
        bool undoIsolation;
        using(var isolated=RhinoDoc.CreateHeadless(null))using(var other=RhinoDoc.CreateHeadless(null))
        {
            isolated.ModelUnitSystem=other.ModelUnitSystem=UnitSystem.Meters;
            isolated.UndoRecordingEnabled=other.UndoRecordingEnabled=true;
            uint serial=isolated.NextUndoRecordSerialNumber;
            uint otherRecord=other.BeginUndoRecord("independent document");
            other.Objects.AddPoint(Point3d.Origin);other.EndUndoRecord(otherRecord);
            var model=CheckModel.Build(isolated,projection.Origin,bounds,target,new[]{target},Array.Empty<GeoFeature>(),terrain,new ModelOptions());
            undoIsolation=(bool)TopoWorkflowChecks.Transaction(isolated,model,true,serial,
                new Dictionary<Guid,(uint Geometry,string Attributes)>())["one_pass_transaction_passed"]!;
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { CheckModel.Build(doc, projection.Origin, bounds, target, new[] { target }, Array.Empty<GeoFeature>(), terrain, new ModelOptions(), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        bool cancellationPreserved = cancelled && Unchanged();
        using var duringCalculation = new CancellationTokenSource();
        // Cancel while enumerating source buildings without a timer or a product hook.
        var observedBuildings = new ObservedList<GeoFeature>(new[] { target }, duringCalculation.Cancel);
        bool computationCancelled = false;
        try { CheckModel.Build(doc, projection.Origin, bounds, target, new[] { target }, observedBuildings, terrain, new ModelOptions(), duringCalculation.Token); }
        catch (OperationCanceledException) { computationCancelled = true; }
        bool computedCancellationPreserved = observedBuildings.Enumerated && computationCancelled && Unchanged();
        bool preparedCancellationPreserved=true;
        foreach(bool contour in new[]{false,true})
        {
            using var beforeCommit=new CancellationTokenSource();bool reached=false,accepted=false;
            var progress=new InlineProgress(message=>{if(message.StartsWith("준비 형상",StringComparison.Ordinal)){reached=true;beforeCommit.Cancel();}});
            try
            {
                CheckModel.Build(doc,projection.Origin,bounds,new[]{target},new[]{target},Array.Empty<GeoFeature>(),terrain,
                    new ModelOptions(Contour:contour),beforeCommit.Token,progress);
            }
            catch(OperationCanceledException){accepted=true;}
            preparedCancellationPreserved &= reached&&accepted&&Unchanged();
        }
        var retry = CheckModel.Build(doc, projection.Origin, bounds, target, new[] { target }, Array.Empty<GeoFeature>(), terrain, new ModelOptions());
        bool recovered = retry.CreatedCount > 0 && retry.ObjectIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false });
        var repeated = Enumerable.Range(0, 2).Select(_ => CheckModel.Build(doc, projection.Origin, bounds, target,
            new[] { target }, Array.Empty<GeoFeature>(), terrain, new ModelOptions())).Prepend(retry).ToArray();
        bool independent = repeated.Select(r => r.ParentLayerId).Distinct().Count() == 3
            && repeated.SelectMany(r => r.ObjectIds).Distinct().Count() == repeated.Sum(r => r.ObjectIds.Count);
        var targetObjects = repeated.Select(r => r.ObjectIds.Select(id => doc.Objects.FindId(id)!).Single(obj =>
            obj.Attributes.GetUserString("AHA.Role") == "TargetBoundary" && obj.Attributes.GetUserString("AHA.CurveSpace") == "Projected")).ToArray();
        var originals = targetObjects.Select(obj => obj.Attributes.GetUserString(Aha.Info.InfoData.Key)).ToArray();
        using (var edited = targetObjects[1].Attributes.Duplicate())
        {
            var info = Aha.Info.InfoData.Parse(originals[1]!);
            info["data"]!["notes"] = "second generated site only";
            edited.SetUserString(Aha.Info.InfoData.Key, info.ToJsonString());
            independent &= doc.Objects.ModifyAttributes(targetObjects[1].Id, edited, true);
        }
        independent &= doc.Objects.FindId(targetObjects[0].Id)!.Attributes.GetUserString(Aha.Info.InfoData.Key) == originals[0]
            && doc.Objects.FindId(targetObjects[2].Id)!.Attributes.GetUserString(Aha.Info.InfoData.Key) == originals[2]
            && doc.Objects.FindId(targetObjects[1].Id)!.Attributes.GetUserString(Aha.Info.InfoData.Key) != originals[1];
        return new()
        {
            ["persistence_semantic_controls"] = persistenceControls,
            ["geometry_checks_passed"] = undoIsolation && persistenceControls && (bool)preparedGeometry["geometry_checks_passed"]! && preflight && rollback && cancellationPreserved && computedCancellationPreserved && preparedCancellationPreserved && recovered && independent,
            ["one_undo_independent_of_other_document_records"] = undoIsolation,
            ["prepared_display_geometry"] = preparedGeometry,
            ["unsupported_geometry_rejected_before_change"] = preflight,
            ["failed_partial_commit_preserves_objects_layers_order"] = rollback,
            ["cancelled_build_preserves_objects_layers_order"] = cancellationPreserved,
            ["cancellation_during_building_preparation_preserves_document"] = computedCancellationPreserved,
            ["continuous_and_contour_precommit_cancellation_preserves_document"] = preparedCancellationPreserved,
            ["normal_retry_after_failure_and_cancel"] = recovered,
            ["three_generations_have_independent_layers_objects_and_info"] = independent
        };
    }

    private sealed class InlineProgress(Action<string> report):IProgress<string>
    {public void Report(string value)=>report(value);}

    // Interrupt the prepared stream only after validation and one real Rhino
    // addition. This exercises rollback without changing the product's writer.
    private sealed class InterruptedObjects(ModelDocumentObject item, Func<bool> added) : IReadOnlyList<ModelDocumentObject>
    {
        private int enumerations;
        public bool AddedBeforeFailure { get; private set; }
        public int Count => 1;
        public ModelDocumentObject this[int index] => index == 0 ? item : throw new IndexOutOfRangeException();
        public IEnumerator<ModelDocumentObject> GetEnumerator()
        {
            int pass = ++enumerations;
            yield return item;
            if (pass == 2)
            {
                AddedBeforeFailure = added();
                throw new IOException("Interrupted prepared object stream");
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ObservedList<T>(IReadOnlyList<T> source, Action onEnumeration) : IReadOnlyList<T>
    {
        public bool Enumerated { get; private set; }
        public int Count => source.Count;
        public T this[int index] => source[index];
        public IEnumerator<T> GetEnumerator()
        {
            Enumerated = true;
            onEnumeration();
            return source.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
