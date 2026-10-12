using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using Aha.Runtime;
using Aha.Topo;
using Aha.Mcp;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;

namespace Aha.Topo.Checks;

/// <summary>Controlled acquisition checks; calls the installed product and confines all writes to owned headless documents.</summary>
public static class TopoServiceChecks
{
    private const string PublicPnu = "9900000000100010000";
    private static int running;
    private sealed record ObjectState(uint Geometry, string Attributes, bool Selected);
    private sealed record DocumentState(bool Modified, Dictionary<Guid, ObjectState> Objects, Dictionary<Guid, string> Layers);

    /// <summary>Call on the UI thread. Returns a report path immediately; never wait synchronously for completion.</summary>
    public static string Start(string outputDirectory)
    {
        if (RhinoApp.InvokeRequired) throw new InvalidOperationException("Start must be called on Rhino's UI thread.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Topo lifecycle checks are already running.");
        try
        {
            var directory = Path.Combine(Path.GetFullPath(outputDirectory), "topo-lifecycle-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
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
        var cases = new List<object>(); bool failed = false;
        var report = new Dictionary<string, object?> { ["status"]="running", ["success"]=false, ["cases"]=cases };
        void Save() { File.WriteAllText(path+".tmp", JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})); File.Move(path+".tmp",path,true); }
        void Check(string name,bool pass) { cases.Add(new{name,success=pass}); failed |= !pass; Save(); }
        RhinoDoc? first=null, second=null;
        Dictionary<uint,DocumentState>? baseline=null;
        uint? active=null; uint firstId=0,secondId=0;
        TaskCompletionSource<bool>? hold=null;
        var jobIds=new List<string>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct=deadline.Token;
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var parcel=new GeoFeature{Id="fixture",Pnu=PublicPnu,LandCategoryCode=8,
            Polygons=new(){new(){new(){projection.ToGeo(-5,-5),projection.ToGeo(5,-5),projection.ToGeo(5,5),projection.ToGeo(-5,5),projection.ToGeo(-5,-5)}}}};
        int opened=0;
        var workflow=new TopoWorkflow(()=>{Interlocked.Increment(ref opened);return new FixedSource(parcel,()=>hold?.Task??Task.CompletedTask);});
        var options=McpTools.ReadTopoOptions(new JsonObject { ["parapets"] = true, ["parapet_width_m"] = .25, ["parapet_height_m"] = .45 });
        var bounds=TopoService.BoundsFor(projection.Origin,200,200);
        string Track(System.Text.Json.Nodes.JsonObject state) { var id=state["job_id"]!.GetValue<string>();jobIds.Add(id);return id; }
        async Task<System.Text.Json.Nodes.JsonObject> Wait(string id, params string[] expected)
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();var state=Jobs.Status(id);string value=state["state"]!.GetValue<string>();
                if(expected.Contains(value))return state;
                if(value is "failed" or "cancelled" or "completed")throw new Exception("Unexpected job state: "+state);
                await Task.Delay(20,ct).ConfigureAwait(false);
            }
        }
        async Task<string> Start() => Track(await RhinoDispatch.InvokeAsync(()=>workflow.Start(first!,new[]{parcel},projection.Origin,bounds,12,options)));
        try
        {
            var product=typeof(TopoService).Assembly;
            report["production_assembly_path"]=product.Location;report["production_module_id"]=product.ManifestModule.ModuleVersionId;
            report["production_assembly_version"]=product.GetName().Version?.ToString();
            await RhinoDispatch.InvokeAsync(()=>{baseline=Snapshot();active=RhinoDoc.ActiveDoc?.RuntimeSerialNumber;
                first=CreateFixture();firstId=first.RuntimeSerialNumber;second=CreateFixture();secondId=second.RuntimeSerialNumber;return true;});
            foreach(double width in new[]{49d,3001d,double.NaN,double.PositiveInfinity})
            {
                int before=opened;bool rejected=false;
                try{await RhinoDispatch.InvokeAsync(()=>workflow.StartByPnus(first!,new[]{PublicPnu},width,200,12,options));}
                catch(ArgumentException){rejected=true;}
                Check("invalid_size_precedes_source",rejected&&opened==before);
            }
            hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
            string id=await Start();bool duplicate=false;
            try{await Start();}catch(InvalidOperationException){duplicate=true;}
            Check("one_preparation_per_document",duplicate&&Jobs.Status(id)["state"]!.GetValue<string>()=="running");
            Guid edited=await RhinoDispatch.InvokeAsync(()=>{using var lease=DocumentAccess.Enter(first!);return first!.Objects.AddPoint(1,2,3);});
            Check("preparation_does_not_lock_user_or_other_tools",edited!=Guid.Empty);
            Jobs.CancelTransient();Check("mcp_stop_keeps_topo",Jobs.Status(id)["state"]!.GetValue<string>()=="running");
            hold.SetResult(true);hold=null;
            var ready=await Wait(id,"ready");
            Check("ready_has_summary_without_result_guids",ready["summary"]?["prepared_object_count"]?.GetValue<int>()>0&&ready["result"] is null);
            Check("parapet_input_and_ready_summary",options.Parapets&&options.ParapetWidthMetres==.25&&options.ParapetHeightMetres==.45
                && ready["summary"]?["object_counts"]?["Parapet"]?.GetValue<int>()==1
                && ready["summary"]?["applied_settings"]?["ParapetWidthMetres"]?.GetValue<double>()==.25
                && ready["summary"]?["applied_settings"]?["ParapetHeightMetres"]?.GetValue<double>()==.45);
            Check("preparation_does_not_mutate_document",await RhinoDispatch.InvokeAsync(()=>LiveObjectIds(first!).SetEquals(new[]{edited})&&first!.Layers.Count(l=>!l.IsDeleted)==1));
            IDisposable? busy=await RhinoDispatch.InvokeAsync(()=>DocumentAccess.Enter(first!));
            TopoService.Create(id);await Wait(id,"ready");busy.Dispose();
            Check("busy_create_retains_prepared_result",Jobs.Status(id)["summary"] is not null);
            Check("panel_creates_ready_geometry_without_key",await TopoPanelChecks.CreatePrepared(first!));
            TopoService.Create(id);
            var completed=await Wait(id,"completed");var result=completed["result"]!;
            int createdCount=result["CreatedCount"]!.GetValue<int>();var objectIds=result["ObjectIds"]!.Deserialize<Guid[]>()!;
            Check("create_commits_to_original_document_once",await RhinoDispatch.InvokeAsync(()=>createdCount>0&&createdCount==objectIds.Length&&LiveObjectIds(first!).SetEquals(objectIds.Append(edited))));
            Check("panel_created_parapet_is_a_separate_roof_extrusion",await RhinoDispatch.InvokeAsync(()=>
            {
                var parapet=objectIds.Select(g=>first!.Objects.FindId(g)!).Single(o=>o.Attributes.GetUserString("AHA.Role")=="Parapet");
                var box=parapet.Geometry.GetBoundingBox(true);
                return parapet.Geometry is Rhino.Geometry.Extrusion {IsValid:true,IsSolid:true}
                    &&first!.Layers[parapet.Attributes.LayerIndex].FullPath.EndsWith("::Near::Buildings::Parapets",StringComparison.Ordinal)
                    &&Math.Abs(box.Min.Z-26000)<1e-5&&Math.Abs(box.Max.Z-26450)<1e-5;
            }));
            string committed=completed["result"]!.ToJsonString();
            Check("repeated_create_returns_same_result",TopoService.Create(id)["result"]!.ToJsonString()==committed);
            var undone=await RhinoDispatch.InvokeAsync(()=>
            {
                bool undo=first!.Undo();
                // Rhino's headless Undo leaves its redo record active until explicitly ended.
                if(first.UndoRecordingIsActive)Require(first.EndUndoRecord(first.CurrentUndoRecordSerialNumber),"Could not end the host Undo record.");
                return new{undo,prior_preserved=LiveObjectIds(first).SetEquals(new[]{edited}),model_removed=objectIds.All(g=>first.Objects.FindId(g)==null),layers_removed=first.Layers.Count(l=>!l.IsDeleted)==1};
            });
            report["undo_observation"]=undone;
            Check("one_undo_preserves_prior_user_edit",undone.undo&&undone.prior_preserved&&undone.model_removed&&undone.layers_removed);
            Require(undone.undo&&undone.prior_preserved&&undone.model_removed&&undone.layers_removed,"Topo Undo did not restore the fixture document.");
            TopoService.Create(id);Check("create_after_undo_does_not_recreate",await RhinoDispatch.InvokeAsync(()=>LiveObjectIds(first!).SetEquals(new[]{edited})));
            string wire=await Start();await Wait(wire,"ready");
            await McpTools.CallAsync("aha_topo_create",new JsonObject{["job_id"]=wire});
            var wireResult=(await Wait(wire,"completed"))["result"]!;
            Check("mcp_create_preserves_parapet_options_and_objects",wireResult["AppliedSettings"]?["Parapets"]?.GetValue<bool>()==true
                &&wireResult["AppliedSettings"]?["ParapetWidthMetres"]?.GetValue<double>()==.25
                &&await RhinoDispatch.InvokeAsync(()=>wireResult["ObjectIds"]!.Deserialize<Guid[]>()!.Select(g=>first!.Objects.FindId(g)!)
                    .Count(o=>o.Attributes.GetUserString("AHA.Role")=="Parapet")==1));
            await RhinoDispatch.InvokeAsync(()=>{if(!first!.Undo())throw new Exception("Cannot undo wire-created fixture");
                if(first.UndoRecordingIsActive)first.EndUndoRecord(first.CurrentUndoRecordSerialNumber);return true;});
            string discard=await Start();await Wait(discard,"ready");TopoService.Cancel(discard);await Wait(discard,"cancelled");
            Check("ready_discard_releases_reservation",TopoService.Current(firstId)==null);
            string changed=await Start();await Wait(changed,"ready");
            await RhinoDispatch.InvokeAsync(()=>{first!.ModelAbsoluteTolerance=2;return true;});
            TopoService.Create(changed);await Wait(changed,"failed");
            Check("changed_document_basis_rejects_creation",await RhinoDispatch.InvokeAsync(()=>LiveObjectIds(first!).SetEquals(new[]{edited})&&first!.Layers.Count(l=>!l.IsDeleted)==1));
            await RhinoDispatch.InvokeAsync(()=>{first!.ModelAbsoluteTolerance=1;return true;});
            hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
            string cancelled=await Start();TopoService.Cancel(cancelled);await Wait(cancelled,"cancelled");
            Check("acquisition_cancel_releases_reservation",TopoService.Current(firstId)==null);hold.TrySetResult(true);hold=null;
            string closing=Track(await RhinoDispatch.InvokeAsync(()=>workflow.Start(second!,new[]{parcel},projection.Origin,bounds,12,options)));
            await Wait(closing,"ready");
            await RhinoDispatch.InvokeAsync(()=>
            {
                second!.Dispose();
                Require(RhinoDoc.FromRuntimeSerialNumber(secondId)==null,"The headless fixture remained open.");
                // Rhino 8 suppresses static document events for headless docs. Exercise the installed handler explicitly.
                var args=(DocumentEventArgs)Activator.CreateInstance(typeof(DocumentEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{secondId},null)!;
                typeof(AhaPlugin).GetMethod("DocumentClosed",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{null,args});
                return true;
            });
            await Wait(closing,"cancelled");Check("document_close_handler_discards_ready",TopoService.Current(secondId)==null);

            // A synchronous fixture still calculates geometry on a worker, with no document lease.
            var context=await RhinoDispatch.InvokeAsync(()=>ModelContext.Capture(first!));bool geometryOffThread=false;
            using(var prepared=await workflow.PrepareAsync(context,new[]{parcel},projection.Origin,bounds,12,options,ct,
                new InlineProgress(message=>{if(message.StartsWith("Rhino 형상",StringComparison.Ordinal))geometryOffThread=RhinoApp.InvokeRequired;})))
                Check("geometry_calculation_runs_off_ui_thread",geometryOffThread&&prepared.ObjectCount>0);
            using(var releaseGeometry=new ManualResetEventSlim())
            using(var cancelQueued=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<PreparedModel<ModelResult>>? calculating=null,queued=null;
                try
                {
                    calculating=workflow.PrepareAsync(context,new[]{parcel},projection.Origin,bounds,12,options,ct,
                        new InlineProgress(message=>{if(message.StartsWith("Rhino 형상",StringComparison.Ordinal)){entered.TrySetResult(true);releaseGeometry.Wait(ct);}}));
                    await entered.Task.WaitAsync(ct);
                    queued=workflow.PrepareAsync(context,new[]{parcel},projection.Origin,bounds,12,options,cancelQueued.Token,
                        new InlineProgress(message=>{if(message.StartsWith("형상 계산 순서",StringComparison.Ordinal))waiting.TrySetResult(true);}));
                    await waiting.Task.WaitAsync(ct);
                    Check("ui_dispatch_remains_available_during_geometry",await RhinoDispatch.InvokeAsync(()=>first!.Objects.FindId(edited)!=null));
                    cancelQueued.Cancel();bool stopped=false;
                    try{using var ignored=await queued;}catch(OperationCanceledException){stopped=true;}
                    Check("queued_geometry_can_cancel_without_entering_slot",stopped&&!calculating.IsCompleted);
                }
                finally
                {
                    releaseGeometry.Set();cancelQueued.Cancel();
                    if(calculating!=null)try{(await calculating).Dispose();}catch{}
                    if(queued!=null)try{(await queued).Dispose();}catch{}
                }
            }
            await XYServiceChecks.RunAsync(Check,ct);
            await TerrainCoverageServiceChecks.RunAsync(Check,ct);
            Check("panel_input_and_job_lifecycle",await TopoPanelChecks.RunAsync(first!));
        }
        catch(Exception error){failed=true;report["error"]=error.ToString();}
        finally
        {
            foreach(string id in jobIds)try{TopoService.Cancel(id);}catch{}
            hold?.TrySetResult(true);
            await RhinoDispatch.InvokeAsync(()=>{second?.Dispose();first?.Dispose();return true;});
            var after=await RhinoDispatch.InvokeAsync(()=>Snapshot(firstId,secondId));
            bool preserved=baseline!=null&&baseline.Count==after.Count&&baseline.All(pair=>after.TryGetValue(pair.Key,out var value)
                &&pair.Value.Objects.Count==value.Objects.Count&&pair.Value.Objects.All(obj=>value.Objects.TryGetValue(obj.Key,out var current)&&current==obj.Value)
                &&pair.Value.Layers.Count==value.Layers.Count&&pair.Value.Layers.All(layer=>value.Layers.GetValueOrDefault(layer.Key)==layer.Value));
            Check("user_geometry_layers_selection_preserved",preserved&&await RhinoDispatch.InvokeAsync(()=>RhinoDoc.ActiveDoc?.RuntimeSerialNumber==active));
            report["status"]=failed?"failed":"passed";report["success"]=!failed;report["finished_at"]=DateTimeOffset.UtcNow;
            try{Save();}finally{Interlocked.Exchange(ref running,0);}
        }
    }

    private static RhinoDoc CreateFixture()
    {
        var doc = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException("Could not create headless fixture.");
        doc.ModelUnitSystem = UnitSystem.Millimeters;
        doc.ModelAbsoluteTolerance = 1;
        doc.UndoRecordingEnabled = true;
        return doc;
    }
    private static HashSet<Guid> LiveObjectIds(RhinoDoc doc) => doc.Objects.GetObjectList(
        new ObjectEnumeratorSettings{ActiveObjects=true,HiddenObjects=true,LockedObjects=true,DeletedObjects=false})
        .Select(obj=>obj.Id).ToHashSet();
    private static Dictionary<uint, DocumentState> Snapshot(params uint[] excluded) => RhinoDoc.OpenDocuments(true)
        .Where(doc => !excluded.Contains(doc.RuntimeSerialNumber))
        .ToDictionary(doc => doc.RuntimeSerialNumber, doc => new DocumentState(doc.Modified,
            doc.Objects.GetObjectList(new ObjectEnumeratorSettings { ActiveObjects = true, HiddenObjects = true, LockedObjects = true, DeletedObjects = false })
                .ToDictionary(obj => obj.Id, obj => new ObjectState(obj.Geometry.DataCRC(0), obj.Attributes.ToJSON(new SerializationOptions()), obj.IsSelected(false) != 0)),
            doc.Layers.Where(layer => !layer.IsDeleted).ToDictionary(layer => layer.Id, layer => layer.ToJSON(new SerializationOptions()))));
    private sealed class FixedSource(GeoFeature parcel, Func<Task> wait) : ITopoSource
    {
        public Task<GeoFeature> ParcelByPnuAsync(string pnu, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(parcel); }
        public async Task<List<GeoFeature>> ParcelsAsync(GeoBounds bounds, CancellationToken ct)
        { await wait().WaitAsync(ct); ct.ThrowIfCancellationRequested(); return new() { parcel }; }
        public Task<List<GeoFeature>> BuildingsAsync(GeoBounds bounds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var projection=new LocalProjection(new GeoPoint(127,37.5));
            return Task.FromResult(new List<GeoFeature>{new(){Id="near-building",Polygons=new(){new(){new(){
                projection.ToGeo(-3,7),projection.ToGeo(3,7),projection.ToGeo(3,11),projection.ToGeo(-3,11)}}}}});
        }
        public Task<TerrainGrid> TerrainAsync(GeoBounds bounds, int level, CancellationToken ct, IProgress<string>? progress)
        {
            ct.ThrowIfCancellationRequested();
            var plan = TerrainClient.Plan(bounds, level);
            var tiles = new List<TerrainTile>();
            for (int y = plan.MinY; y <= plan.MaxY; y++) for (int x = plan.MinX; x <= plan.MaxX; x++)
                tiles.Add(new TerrainTile(level, x, y, Enumerable.Repeat(20f, TerrainTile.ValueCount).ToArray()));
            return Task.FromResult(new TerrainGrid(tiles));
        }
        public void Dispose() { }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
