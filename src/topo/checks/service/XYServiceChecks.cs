using Aha.Runtime;
using Rhino;

namespace Aha.Topo.Checks;

internal static class XYServiceChecks
{
    internal static async Task RunAsync(Action<string, bool> check, CancellationToken ct)
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var targets = new[] { Parcel("9900000000100010000", 0), Parcel("9900000000100020000", 10) };
        var pnus = targets.Select(parcel => parcel.Pnu).ToArray();
        TaskCompletionSource<bool>? hold = null;
        Action? afterContext = null;
        int sources = 0, requests = 0;
        var workflow = new TopoWorkflow(() => { sources++; return new XYSource(targets, () => hold?.Task ?? Task.CompletedTask, () => requests++, () => afterContext?.Invoke()); });
        RhinoDoc? doc = null;
        Task<PreparedModel<XYResult>>? pending = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            doc = await RhinoDispatch.InvokeAsync(() =>
            {
                var fixture = RhinoDoc.CreateHeadless(null);
                fixture.ModelUnitSystem = UnitSystem.Millimeters; fixture.ModelAbsoluteTolerance = 1;
                fixture.UndoRecordingEnabled = true;
                return fixture;
            }).ConfigureAwait(false);
            var context = await RhinoDispatch.InvokeAsync(() => ModelContext.Capture(doc));
            foreach (var invalid in new[] { Array.Empty<string>(), new[] { pnus[0], pnus[0] }, new[] { "bad-pnu" } })
            {
                int before = sources; bool rejected = false;
                try { await workflow.PrepareXYByPnusAsync(context, invalid, 100, 100, true, 10, ct).ConfigureAwait(false); }
                catch (ArgumentException) { rejected = true; }
                check("xy_invalid_pnu_rejected_before_acquisition", rejected && sources == before);
            }
            foreach (var invalid in new[] { (49d, 100d, 10d), (100d, 3001d, 10d), (100d, 100d, -1d), (100d, 100d, double.NaN) })
            {
                int before = sources; bool rejected = false;
                try { await workflow.PrepareXYByPnusAsync(context, pnus, invalid.Item1, invalid.Item2, true, invalid.Item3, ct).ConfigureAwait(false); }
                catch (ArgumentException) { rejected = true; }
                check("xy_invalid_extent_or_area_rejected_before_acquisition", rejected && sources == before);
            }
            hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = workflow.PrepareXYByPnusAsync(context, pnus, 100, 100, true, 10, cancellation.Token);
            check("xy_acquisition_pending", !pending.IsCompleted);
            check("xy_preparation_allows_other_document_edits", await RhinoDispatch.InvokeAsync(() =>
            { using var lease = DocumentAccess.Enter(doc); return true; }));
            cancellation.Cancel();
            bool cancelled = false;
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; }
            check("xy_acquisition_cancellation_preserves_document", cancelled && await RhinoDispatch.InvokeAsync(() =>
                !doc.Objects.Any() && doc.Layers.Count(layer => !layer.IsDeleted) == 1).ConfigureAwait(false));

            hold = null;
            int beforeRetry = requests;
            using var ready = await workflow.PrepareXYByPnusAsync(context, pnus, 100, 100, true, 10, ct).ConfigureAwait(false);
            check("xy_ready_does_not_add_objects", await RhinoDispatch.InvokeAsync(() => doc.Objects.Count == 0));
            var result = await RhinoDispatch.InvokeAsync(() => ready.Commit(doc));
            check("xy_retries_with_context_and_without_terrain_requests", requests - beforeRetry == pnus.Length + 2
                && result.CreatedCount == 3 && result.SiteComponents == 1 && result.SelectedParcels == 2);
            check("xy_uses_shared_centre_preserves_units_and_commits_to_target", await RhinoDispatch.InvokeAsync(() =>
                result.Coordinates.Origin == TopoService.SelectionCentre(targets)
                && result.Coordinates.DocumentUnitsPerMetre == 1000 && doc.ModelUnitSystem == UnitSystem.Millimeters
                && result.ObjectIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false })).ConfigureAwait(false));

            using var beforeCommit = new CancellationTokenSource();
            bool reachedContext = false, cancelledBeforeCommit = false;
            afterContext = () => { reachedContext = true; beforeCommit.Cancel(); };
            try { await workflow.PrepareXYByPnusAsync(context, pnus, 100, 100, true, 10, beforeCommit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelledBeforeCommit = true; }
            finally { afterContext = null; }
            check("xy_cancellation_after_context_acquisition_has_no_partial_commit", reachedContext && cancelledBeforeCommit
                && await RhinoDispatch.InvokeAsync(() => doc.Objects.Count == result.CreatedCount
                    && doc.Layers.Count(layer => !layer.IsDeleted) == 7).ConfigureAwait(false));


        }
        finally
        {
            cancellation.Cancel(); hold?.TrySetResult(true);
            if (pending is not null) { try { (await pending.ConfigureAwait(false)).Dispose(); } catch { } }
            await RhinoDispatch.InvokeAsync(() => { doc?.Dispose(); return true; }).ConfigureAwait(false);
        }

        GeoFeature Parcel(string pnu, double x) => new() { Id = pnu, Pnu = pnu, Address = "Fixed parcel " + pnu,
            Polygons = new() { new() { new() { projection.ToGeo(x,0), projection.ToGeo(x+10,0), projection.ToGeo(x+10,10), projection.ToGeo(x,10), projection.ToGeo(x,0) } } } };
    }

    private sealed class XYSource(IReadOnlyList<GeoFeature> targets, Func<Task> wait, Action requested, Action afterContext) : ITopoSource
    {
        public async Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct)
        { requested(); await wait().WaitAsync(ct).ConfigureAwait(false); return targets.Single(parcel => parcel.Pnu == pnu); }
        public Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct) { requested(); return Task.FromResult(targets.ToList()); }
        public Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct)
        { requested(); afterContext(); return Task.FromResult(new List<GeoFeature>()); }
        public Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress) => throw new InvalidOperationException("XY generation requested terrain.");
        public void Dispose() { }
    }

}
