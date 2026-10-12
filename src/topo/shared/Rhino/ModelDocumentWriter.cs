using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo;

internal sealed record SolidExpectation(double Volume, double Tolerance);

internal sealed record ModelDocumentObject(GeometryBase Geometry, string Path, string Name,
    IReadOnlyDictionary<string, string> Tags, SolidExpectation? ExpectedSolid = null);

/// <summary>Write an independent model branch, with one undo record and transactional rollback.</summary>
internal static class ModelDocumentWriter
{
    internal static void Validate(IReadOnlyList<ModelDocumentObject> objects, CancellationToken ct = default)
    {
        foreach (var item in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Geometry is not (Mesh or Curve or Brep or Extrusion))
                throw new InvalidOperationException("Rhino 모델에 추가할 형상이 유효한 지원 형식이 아닙니다: " + item.Name);
            if(!item.Geometry.IsValidWithLog(out var reason))
                throw new InvalidOperationException("준비한 형상의 유효성 검사에 실패했습니다: "+item.Name+"; "+reason);
            if (item.Geometry is Extrusion extrusion) ValidateSolid(extrusion, item.Name, item.ExpectedSolid);
        }
    }

    internal static void ValidateSolid(Extrusion extrusion, string name, SolidExpectation? expected = null)
    {
        // Native flags also accept a capped, retraced zero-area profile.
        // Inspect its analytical boundary once while preparing, before any document writes.
        using var boundary = extrusion.ToBrep();
        if (!extrusion.IsSolid || boundary is not { IsValid: true, IsSolid: true, IsManifold: true }
            || boundary.Edges.Any(edge => edge.Valence != EdgeAdjacency.Interior))
            throw new InvalidOperationException("준비한 Extrusion의 실제 경계가 닫힌 입체가 아닙니다: " + name);
        using var volume = VolumeMassProperties.Compute(boundary,true,false,false,false);
        if (volume is null || !double.IsFinite(volume.Volume) || volume.Volume <= 0)
            throw new InvalidOperationException("준비한 Extrusion의 실제 체적이 양수가 아닙니다: " + name);
        if (expected is not null && (!double.IsFinite(expected.Volume) || expected.Volume <= 0
            || !double.IsFinite(expected.Tolerance) || expected.Tolerance < 0
            || Math.Abs(volume.Volume - expected.Volume) > expected.Tolerance))
            throw new InvalidOperationException("준비한 Extrusion의 체적 또는 내부 구멍이 예상 단면과 다릅니다: " + name);
    }

    internal static void Commit(RhinoDoc doc, ModelResult result, IReadOnlyList<ModelDocumentObject> objects,
        IReadOnlyList<string> initialPaths, IReadOnlyDictionary<string, string>? rootTags = null, bool hideXY = true,
        bool validated = false)
    {
        if (!validated) Validate(objects);
        var created = new List<int>(); var added = new List<Guid>();
        int originalCurrent = doc.Layers.CurrentLayerIndex;
        var originalOrder = doc.Layers.GetSorted();
        var undo = doc.BeginUndoRecord("AHA Topo");
        try
        {
            using var parent = new Layer { Name = result.LayerName, Color = Color.FromArgb(74, 125, 99) };
            parent.SetUserString("AHA.Tool", "Topo");
            if (rootTags is not null) foreach (var tag in rootTags) parent.SetUserString(tag.Key, tag.Value);
            int parentIndex = doc.Layers.Add(parent);
            if (parentIndex < 0) throw new InvalidOperationException("AHA 레이어를 만들 수 없습니다.");
            created.Add(parentIndex); result.ParentLayerId = doc.Layers[parentIndex].Id;
            var paths = new Dictionary<string, int>();
            int LayerFor(string path)
            {
                Guid parentId = result.ParentLayerId; string partial = "";
                foreach (var segment in path.Split("::"))
                {
                    partial = partial.Length == 0 ? segment : partial + "::" + segment;
                    if (!paths.TryGetValue(partial, out var index))
                    {
                        using var layer = new Layer { Name = segment, ParentLayerId = parentId,
                            Color = ColorFor(partial.StartsWith("TargetBoundary", StringComparison.Ordinal) ? "TargetBoundary" : segment) };
                        layer.SetUserString("AHA.GeneratedLayer", "true");
                        index = doc.Layers.Add(layer);
                        if (index < 0) throw new InvalidOperationException("하위 레이어를 만들 수 없습니다.");
                        created.Add(index); paths.Add(partial, index);
                    }
                    parentId = doc.Layers[index].Id;
                }
                return paths[path];
            }
            foreach (var path in initialPaths) LayerFor(path);
            foreach (var item in objects)
            {
                using var attributes = new ObjectAttributes { Name = item.Name };
                attributes.LayerIndex = LayerFor(item.Path);
                foreach (var tag in item.Tags) attributes.SetUserString(tag.Key, tag.Value);
                Guid objectId = item.Geometry switch
                {
                    Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
                    Curve curve => doc.Objects.AddCurve(curve, attributes),
                    Brep brep => doc.Objects.AddBrep(brep, attributes, null, false, false),
                    Extrusion extrusion => doc.Objects.AddExtrusion(extrusion, attributes),
                    _ => Guid.Empty
                };
                if (objectId == Guid.Empty) throw new InvalidOperationException("Rhino 객체를 추가할 수 없습니다.");
                added.Add(objectId); result.ObjectIds.Add(objectId);
                if (item.Tags.GetValueOrDefault("AHA.Role") == "Site") result.SiteObjectIds.Add(objectId);
            }
            if (hideXY && paths.TryGetValue("XY", out int xyIndex))
            {
                using var curves = CopyLayer(doc.Layers[xyIndex]);
                curves.IsVisible = false; curves.SetPersistentVisibility(false);
                if (!doc.Layers.Modify(curves, xyIndex, true)) throw new InvalidOperationException("참조선 묶음을 숨길 수 없습니다.");
            }
            var order = new List<int> { parentIndex }; Append(result.ParentLayerId);
            doc.Layers.Sort(originalOrder.Concat(order).Distinct());
            foreach (int index in created.AsEnumerable().Reverse())
                doc.Layers[index].IsExpanded = index == parentIndex;
            result.CreatedCount = result.ObjectIds.Count;
            void Append(Guid parentId)
            {
                foreach (var layer in created.Select(i => doc.Layers[i]).Where(layer => layer.ParentLayerId == parentId))
                { order.Add(layer.Index); Append(layer.Id); }
            }
        }
        catch (Exception original)
        {
            var failures = new List<string>();
            if (!doc.Layers.SetCurrentLayerIndex(originalCurrent, true)) failures.Add("current layer");
            foreach (var addedId in added)
                if (doc.Objects.FindId(addedId) is not null && !doc.Objects.Delete(addedId, true)) failures.Add("object " + addedId);
            foreach (var index in created.AsEnumerable().Reverse())
                if (!doc.Layers[index].IsDeleted && !doc.Layers.Delete(index, true)) failures.Add("layer " + index);
            doc.Layers.Sort(originalOrder);
            if (added.Any(id => doc.Objects.FindId(id) is not null) || created.Any(i => !doc.Layers[i].IsDeleted))
                failures.Add("remaining model objects/layers");
            if (failures.Count > 0)
                throw new AggregateException("Rhino 모델 추가 실패 후 복구를 완료하지 못했습니다: " + string.Join(", ", failures), original);
            result.ObjectIds.Clear(); result.SiteObjectIds.Clear(); result.ParentLayerId = Guid.Empty; result.CreatedCount = 0;
            throw;
        }
        finally { if (undo != 0) doc.EndUndoRecord(undo); doc.Views.Redraw(); }
    }

    private static Layer CopyLayer(Layer source)
    {
        var layer = new Layer { Name = source.Name, ParentLayerId = source.ParentLayerId };
        layer.CopyAttributesFrom(source);
        var strings = source.GetUserStrings();
        if (strings is not null) foreach (var key in strings.AllKeys)
            if (key is not null) layer.SetUserString(key, strings[key]);
        return layer;
    }

    private static Color ColorFor(string name) => name switch
    {
        "Terrain" or "RoadSides" or "Base" => Color.FromArgb(158, 173, 132),
        "Roads" => Color.FromArgb(80, 85, 90),
        "TargetBoundary" or "Site" => Color.FromArgb(230, 73, 56),
        "ParcelBoundary" => Color.FromArgb(118, 133, 146),
        "SourceHeight" => Color.FromArgb(130, 151, 173),
        "FloorEstimate" => Color.FromArgb(197, 173, 126),
        _ => Color.FromArgb(212, 142, 107)
    };
}
