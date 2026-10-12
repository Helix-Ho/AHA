using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System.Text.Json;

namespace Aha.Topo.Checks;

/// <summary>Archive meaning, independent of a document's layer table indices.</summary>
internal static class ModelPersistenceChecks
{
    internal sealed record LayerState(Guid Id, Guid Parent, string Name, bool Visible, bool Persistent, string Tags);
    internal sealed record ObjectState(Guid Id, Guid Layer, string Name, string Tags, GeometryBase Geometry);
    internal sealed class Snapshot : IDisposable
    {
        internal required UnitSystem Units;
        internal required Dictionary<Guid, LayerState> Layers;
        internal required Dictionary<Guid, ObjectState> Objects;
        public void Dispose() { foreach (var item in Objects.Values) item.Geometry.Dispose(); }
    }
    internal static Snapshot Capture(RhinoDoc doc) => new()
    {
        Units=doc.ModelUnitSystem,
        Layers=doc.Layers.Where(l=>!l.IsDeleted).ToDictionary(l=>l.Id,l=>new LayerState(l.Id,l.ParentLayerId,l.Name,l.IsVisible,l.GetPersistentVisibility(),Tags(l.GetUserStrings()))),
        Objects=ModelGeometryChecks.AllObjects(doc).ToDictionary(o=>o.Id,o=>new ObjectState(o.Id,doc.Layers[o.Attributes.LayerIndex].Id,
            o.Attributes.Name??"",Tags(o.Attributes.GetUserStrings()),o.Geometry.Duplicate()))
    };
    internal static IReadOnlyList<string> Differences(Snapshot expected,Snapshot actual)
    {
        var changes=new List<string>();
        if(expected.Units!=actual.Units)changes.Add("units");
        foreach(var id in expected.Layers.Keys.Union(actual.Layers.Keys))
            if(!expected.Layers.TryGetValue(id,out var a)||!actual.Layers.TryGetValue(id,out var b)||a!=b)changes.Add("layer:"+id);
        double metres=RhinoMath.UnitScale(expected.Units,UnitSystem.Meters);
        foreach(var id in expected.Objects.Keys.Union(actual.Objects.Keys))
        {
            if(!expected.Objects.TryGetValue(id,out var a)||!actual.Objects.TryGetValue(id,out var b)) { changes.Add("object:"+id);continue; }
            if(a.Layer!=b.Layer||a.Name!=b.Name||a.Tags!=b.Tags)changes.Add("attributes:"+id);
            if(a.Geometry.GetType()!=b.Geometry.GetType()||!GeometryBase.GeometryEquals(a.Geometry,b.Geometry))changes.Add("geometry:"+id);
            var x=a.Geometry.GetBoundingBox(true);var y=b.Geometry.GetBoundingBox(true);
            if(x.Min.DistanceTo(y.Min)*metres>1e-9||x.Max.DistanceTo(y.Max)*metres>1e-9)changes.Add("bounds:"+id);
        }
        return changes;
    }
    private static string Tags(System.Collections.Specialized.NameValueCollection? values) => JsonSerializer.Serialize(
        values?.AllKeys.Where(k=>k is not null).Order(StringComparer.Ordinal).ToDictionary(k=>k!,k=>values[k!])??new());

    internal static bool ClosedExtrusion(Extrusion extrusion)
    {
        using var solid=extrusion.ToBrep();
        if(solid is not {IsValid:true,IsSolid:true,IsManifold:true}||
            solid.Edges.Any(edge=>edge.Valence!=EdgeAdjacency.Interior))return false;
        using var volume=VolumeMassProperties.Compute(solid,true,false,false,false);
        return volume is not null&&double.IsFinite(volume.Volume)&&volume.Volume>0;
    }

    internal static bool Controls()
    {
        using var first=RhinoDoc.CreateHeadless(null);using var second=RhinoDoc.CreateHeadless(null);
        first.ModelUnitSystem=second.ModelUnitSystem=UnitSystem.Meters;
        Guid parent=Guid.NewGuid(),child=Guid.NewGuid();
        using var padding=new Layer{Name="padding"};second.Layers.Add(padding);
        foreach(var doc in new[]{first,second})
        {
            using var a=new Layer{Id=parent,Name="model"};doc.Layers.Add(a);
            using var b=new Layer{Id=child,ParentLayerId=parent,Name="child"};doc.Layers.Add(b);
        }
        // Ignore unrelated default/padding layers; the fixture branch owns its two IDs.
        using var box=new Box(Plane.WorldXY,new Interval(0,2),new Interval(0,3),new Interval(0,4)).ToBrep();
        Guid objectId=Guid.NewGuid();
        foreach(var doc in new[]{first,second})
        {
            using var attr=new ObjectAttributes{ObjectId=objectId,LayerIndex=doc.Layers.FindId(child).Index,Name="fixture"};
            attr.SetUserString("aha.info","{\"version\":1,\"kind\":\"building\"}");
            doc.Objects.AddBrep(box,attr);
        }
        using var baseline=Branch(first);using var same=Branch(second);
        if(first.Layers.FindId(child).Index==second.Layers.FindId(child).Index||Differences(baseline,same).Count!=0)return false;
        var obj=second.Objects.FindId(objectId)!;
        using(var attr=obj.Attributes.Duplicate()) { attr.LayerIndex=second.Layers.FindId(parent).Index;second.Objects.ModifyAttributes(obj,attr,true); }
        using(var changed=Branch(second))if(!Differences(baseline,changed).Any(x=>x.StartsWith("attributes:")))return false;
        using(var attr=obj.Attributes.Duplicate()) { attr.LayerIndex=second.Layers.FindId(child).Index;attr.SetUserString("aha.info","changed");second.Objects.ModifyAttributes(objectId,attr,true); }
        using(var changed=Branch(second))if(!Differences(baseline,changed).Any(x=>x.StartsWith("attributes:")))return false;
        using var moved=box.DuplicateBrep();moved.Translate(1,0,0);second.Objects.Replace(objectId,moved);
        using(var changed=Branch(second))if(!Differences(baseline,changed).Any(x=>x.StartsWith("geometry:")))return false;
        second.Objects.Delete(objectId,true);
        using(var changed=Branch(second))if(!Differences(baseline,changed).Any(x=>x.StartsWith("object:")))return false;
        return true;
        Snapshot Branch(RhinoDoc doc) { var s=Capture(doc);s.Layers=s.Layers.Where(p=>p.Key==parent||p.Key==child).ToDictionary();return s; }
    }
}
