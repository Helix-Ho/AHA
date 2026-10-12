using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class TopoWorkflowChecks
{
    internal static Layer[] Roots(RhinoDoc doc)=>doc.Layers.Where(l=>!l.IsDeleted&&l.GetUserString("AHA.Tool")=="Topo").ToArray();
    internal static bool LayerStructure(RhinoDoc doc,Guid root,bool near)
    {
        var parent=doc.Layers.FindId(root);if(parent is null||!parent.Name.StartsWith("aha_topo_",StringComparison.Ordinal)||parent.GetUserString("AHA.Tool")!="Topo")return false;
        var branch=doc.Layers.GetSorted().Select(i=>doc.Layers[i]).Where(l=>!l.IsDeleted&&Within(doc,l,root)).ToArray();
        var direct=branch.Where(l=>l.ParentLayerId==root).ToArray();
        bool contour=direct.Any(layer=>layer.Name=="Site");
        if(!direct.Select(l=>l.Name).SequenceEqual(contour?new[]{"Site","Near","Far","XY"}:near?new[]{"TargetBoundary","Near","Far","XY"}:new[]{"TargetBoundary","Surfaces","Buildings","XY"}))return false;
        var target=direct.Single(l=>l.Name==(contour?"Site":"TargetBoundary"));var targetChildren=branch.Where(l=>l.ParentLayerId==target.Id).ToArray();
        if(!target.IsVisible||!target.GetPersistentVisibility()||targetChildren.Length!=0)return false;
        if(near&&!direct.Where(l=>l.Name is "Near" or "Far").All(zone=>
            branch.Where(l=>l.ParentLayerId==zone.Id).Select(l=>l.Name).SequenceEqual(new[]{"Surfaces","Buildings"})))return false;
        var xy=direct.Single(l=>l.Name=="XY");var children=branch.Where(l=>l.ParentLayerId==xy.Id).ToArray();
        if(xy.IsVisible||xy.GetPersistentVisibility()||!children.All(l=>l.GetPersistentVisibility())
            ||branch.Any(l=>l.Name is "Curves" or "Projected"))return false;
        var xyTarget=children.SingleOrDefault(l=>l.Name=="TargetBoundary");
        if(xyTarget is null)return false;
        var objects=ModelGeometryChecks.AllObjects(doc).Where(o=>Within(doc,doc.Layers[o.Attributes.LayerIndex],root)).ToArray();
        var targetObjects=objects.Where(o=>o.Attributes.LayerIndex==target.Index).ToArray();
        if(targetObjects.Length==0||targetObjects.Any(o=>contour
            ? o.Geometry is not Brep { IsValid:true, IsSolid:true } || o.Attributes.GetUserString("AHA.Role")!="Site"
            : o.Geometry is not Curve || o.Attributes.GetUserString("AHA.Role")!="TargetBoundary"
                || o.Attributes.GetUserString("AHA.CurveSpace")!="Projected"))return false;
        return objects.Where(o=>o.Geometry is Curve).All(o=>
        {
            bool isTarget=o.Attributes.GetUserString("AHA.Role")=="TargetBoundary";
            var layer=doc.Layers[o.Attributes.LayerIndex];
            return o.Attributes.GetUserString("AHA.CurveSpace") switch
            {
                "Projected"=>!contour&&isTarget&&layer.Id==target.Id,
                "XY"=>isTarget?layer.Id==xyTarget.Id:Within(doc,layer,xy.Id),
                _=>false
            };
        });
    }
    internal static Dictionary<string,object?> Transaction(RhinoDoc doc,ModelResult result,bool near,uint before,
        IReadOnlyDictionary<Guid,(uint Geometry,string Attributes)> existing)
    {
        bool OriginalUnchanged()=>existing.All(pair=>doc.Objects.FindId(pair.Key) is {} obj&&obj.Geometry.DataCRC(0)==pair.Value.Geometry
            &&obj.Attributes.ToJSON(new SerializationOptions())==pair.Value.Attributes);
        bool exact=Roots(doc).Length==1&&Roots(doc)[0].Id==result.ParentLayerId&&result.ObjectIds.Count==result.CreatedCount
            &&ModelGeometryChecks.AllObjects(doc).Select(o=>o.Id).ToHashSet().SetEquals(existing.Keys.Concat(result.ObjectIds));
        bool layer=LayerStructure(doc,result.ParentLayerId,near);bool unchanged=OriginalUnchanged();
        // Initial tree presentation is a creation requirement. Rhino's Undo/Redo
        // manages subsequent UI expansion separately from the model structure.
        bool expanded=doc.Layers.FindId(result.ParentLayerId)!.IsExpanded
            &&doc.Layers.Where(l=>!l.IsDeleted&&l.Id!=result.ParentLayerId&&Within(doc,l,result.ParentLayerId)).All(l=>!l.IsExpanded);
        uint after=doc.NextUndoRecordSerialNumber;bool undoReturn=doc.Undo();
        // Serial allocation is shared across Rhino documents. One Undo is
        // judged by its actual effect, not by unrelated documents' record IDs.
        bool undo=undoReturn&&Roots(doc).Length==0&&OriginalUnchanged()
            &&ModelGeometryChecks.AllObjects(doc).Select(o=>o.Id).ToHashSet().SetEquals(existing.Keys);
        bool ended=EndHostRecord(doc);bool redoReturn=doc.Redo();
        bool redo=redoReturn&&OriginalUnchanged()&&Roots(doc).Length==1&&result.ObjectIds.All(id=>doc.Objects.FindId(id) is {IsDeleted:false});
        ended &= EndHostRecord(doc);
        return new(){["one_pass_transaction_passed"]=exact&&layer&&expanded&&unchanged&&undo&&redo&&ended,
            ["one_final_root_without_intermediate_model"]=exact,["final_layer_structure_passed"]=layer,
            ["initial_layer_expansion_passed"]=expanded,
            ["unrelated_existing_geometry_unchanged"]=unchanged,["single_undo_removes_generated_model"]=undo,
            ["redo_restores_same_final_objects"]=redo,["undo_diagnostic"]=new{before,after,undoReturn,redoReturn,ended}};
    }
    internal static bool TargetBoundaryVisibleWithFarHidden(RhinoDoc doc,Guid root)
    {
        var targets=ModelGeometryChecks.AllObjects(doc).Where(o=>(o.Attributes.GetUserString("AHA.Role")=="Site"
            || o.Attributes.GetUserString("AHA.Role")=="TargetBoundary" && o.Attributes.GetUserString("AHA.CurveSpace")=="Projected")
            &&Within(doc,doc.Layers[o.Attributes.LayerIndex],root)).ToArray();
        var far=doc.Layers.FirstOrDefault(l=>!l.IsDeleted&&l.ParentLayerId==root&&l.Name=="Far");
        if(far is null)return targets.Length>0&&targets.All(o=>o.Visible&&!o.IsHidden);
        using var saved=new Layer{Name=far.Name,ParentLayerId=far.ParentLayerId};saved.CopyAttributesFrom(far);
        foreach(var key in far.GetUserStrings().AllKeys)if(key is not null)saved.SetUserString(key,far.GetUserString(key));
        bool recording=doc.UndoRecordingEnabled;doc.UndoRecordingEnabled=false;
        try
        {
            using var hidden=new Layer{Name=far.Name,ParentLayerId=far.ParentLayerId};hidden.CopyAttributesFrom(saved);
            hidden.IsVisible=false;hidden.SetPersistentVisibility(false);
            return doc.Layers.Modify(hidden,far.Id,true)&&targets.Length>0&&targets.All(o=>o.Visible&&!o.IsHidden);
        }
        finally{doc.Layers.Modify(saved,far.Id,true);doc.UndoRecordingEnabled=recording;}
    }
    internal static bool ReopenedVisibilityCycle(RhinoDoc doc,Guid root,bool near)
    {
        bool ok=LayerStructure(doc,root,near);
        foreach(bool visible in new[]{false,true})
        {
            var original=doc.Layers.FindId(root)!;using var copy=new Layer{Name=original.Name,ParentLayerId=original.ParentLayerId};copy.CopyAttributesFrom(original);
            foreach(var key in original.GetUserStrings().AllKeys)if(key is not null)copy.SetUserString(key,original.GetUserString(key));copy.IsVisible=visible;
            ok &= doc.Layers.Modify(copy,root,true);
        }
        return ok&&LayerStructure(doc,root,near)&&doc.Layers.FindId(root)!.IsVisible;
    }
    internal static bool EndHostRecord(RhinoDoc doc)=>!doc.UndoRecordingIsActive||doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);
    internal static bool Within(RhinoDoc doc,Layer layer,Guid root)
    {
        while(layer.Id!=root){if(layer.ParentLayerId==Guid.Empty)return false;layer=doc.Layers.FindId(layer.ParentLayerId);if(layer is null)return false;}return true;
    }
}
