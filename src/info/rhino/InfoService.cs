using System.Text.Json.Nodes;
using Aha.Runtime;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Info;

/// <summary>Call on Rhino's UI thread. Changes only the selected object's aha.info attribute.</summary>
public static class InfoService
{
    public static JsonObject? Read(RhinoDoc doc, Guid objectId)
    {
        var obj = Find(doc, objectId, false);
        var json = obj.Attributes.GetUserString(InfoData.Key);
        return json is null ? null : InfoData.Parse(json, requireSupportedVersion: false, validateFields: false);
    }

    public static JsonObject Update(RhinoDoc doc, Guid objectId, JsonObject patch, string? expectedJson = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        using var access = DocumentAccess.Enter(doc);
        var obj = Find(doc, objectId, true);
        var previous = obj.Attributes.GetUserString(InfoData.Key);
        CheckExpected(previous, expectedJson);
        var current = previous is null ? InfoData.Create("object", false, new JsonObject()) : InfoData.Parse(previous, validateFields: false);
        var result = InfoData.Merge(current, patch);
        if (result["site"]!.GetValue<bool>() && !CanBeSite(obj))
            throw new ArgumentException("대지는 유효한 닫힌 Curve, Brep 또는 Extrusion으로 지정할 수 있습니다.");
        // An unchanged edit creates no Undo record.
        if (previous is not null && JsonNode.DeepEquals(JsonNode.Parse(previous), result)) return result;
        var json = result.ToJsonString();
        if (!string.Equals(previous, json, StringComparison.Ordinal)) Write(doc, obj, json, "AHA Info 저장");
        return result;
    }

    public static void Delete(RhinoDoc doc, Guid objectId, string? expectedJson = null)
    {
        using var access = DocumentAccess.Enter(doc);
        var obj = Find(doc, objectId, true);
        var previous = obj.Attributes.GetUserString(InfoData.Key);
        CheckExpected(previous, expectedJson);
        if (previous is null) return;
        InfoData.Parse(previous, validateFields: false); // Never erase a document from an unsupported version.
        Write(doc, obj, null, "AHA Info 삭제");
    }

    public static bool CanBeSite(RhinoObject obj) => obj.Geometry.IsValid && (obj.Geometry switch
    {
        Curve curve => curve.IsClosed,
        Brep brep => brep.IsSolid,
        Extrusion extrusion => extrusion.IsSolid,
        _ => false
    });

    public static bool CanEdit(RhinoDoc doc, RhinoObject obj)
    {
        if (doc.IsReadOnly || obj.IsDeleted || obj.IsLocked || obj.IsReference || obj.IsInstanceDefinitionGeometry) return false;
        var layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
        while (layer is not null)
        {
            if (layer.IsLocked || layer.IsReference) return false;
            if (layer.ParentLayerId == Guid.Empty) break;
            layer = doc.Layers.FindId(layer.ParentLayerId);
        }
        return true;
    }

    private static RhinoObject Find(RhinoDoc doc, Guid id, bool writable)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (RhinoDoc.FromRuntimeSerialNumber(doc.RuntimeSerialNumber) != doc)
            throw new InvalidOperationException("대상 Rhino 문서가 닫혔습니다.");
        var obj = doc.Objects.FindId(id);
        if (obj is null || obj.IsDeleted) throw new InvalidOperationException("대상 객체가 삭제되었거나 이 문서에 없습니다.");
        if (writable && !CanEdit(doc, obj))
            throw new InvalidOperationException("읽기 전용 문서, 잠긴 객체 또는 참조 객체의 정보는 수정할 수 없습니다.");
        return obj;
    }

    private static void CheckExpected(string? actual, string? expected)
    {
        if (expected is not null && !string.Equals(actual ?? "", expected, StringComparison.Ordinal))
            throw new InvalidOperationException("편집 중 객체 정보가 변경되었습니다. 새로 읽은 뒤 다시 편집하세요.");
    }

    private static void Write(RhinoDoc doc, RhinoObject obj, string? json, string label)
    {
        using var attributes = obj.Attributes.Duplicate();
        attributes.SetUserString(InfoData.Key, json);
        var undo = doc.BeginUndoRecord(label);
        try
        {
            if (!doc.Objects.ModifyAttributes(obj.Id, attributes, true))
                throw new InvalidOperationException("객체 정보를 저장하지 못했습니다.");
        }
        finally
        {
            if (undo != 0) doc.EndUndoRecord(undo);
        }
        doc.Views.Redraw();
    }
}
