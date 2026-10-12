using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Aha.Info;
using Aha.Runtime;
using Rhino;

namespace Aha.EumCrawl;

public static class EumService
{
    internal static readonly EumWorkflow Workflow = new(async (pnu, ct) =>
    {
        using var client = new EumClient();
        return await client.LookupAsync(pnu, ct).ConfigureAwait(false);
    }, () => DateTimeOffset.UtcNow);

    public static Task<JsonObject> PreviewAsync(RhinoDoc doc, Guid? objectId, string? pnu, int parcelIndex, CancellationToken ct)
        => Workflow.PreviewAsync(doc, objectId, pnu, parcelIndex, ct);
    public static Task<JsonObject> ReadAsync(RhinoDoc doc, string pnu, string[]? sections, CancellationToken ct)
        => ReadAsync(doc, pnu, sections, ct, async (query, selected, cancel) =>
        {
            using var client = new EumClient();
            return await client.ReadEvidenceAsync(query, selected, cancel).ConfigureAwait(false);
        });

    internal static async Task<JsonObject> ReadAsync(RhinoDoc doc, string pnu, string[]? sections, CancellationToken ct,
        Func<string, string[]?, CancellationToken, Task<JsonObject>> read)
    {
        if (!EumParser.IsPnu(pnu)) throw new ArgumentException("조회할 19자리 PNU를 입력하세요.");
        var selected = EumEvidence.ValidateSections(sections);
        var serial = await RhinoDispatch.InvokeAsync(() => DocumentAccess.Get(doc.RuntimeSerialNumber).RuntimeSerialNumber);
        ct.ThrowIfCancellationRequested();
        var result = await read(pnu, selected, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await RhinoDispatch.InvokeAsync(() => DocumentAccess.Get(serial));
        ct.ThrowIfCancellationRequested();
        result["document_id"] = serial;
        return result;
    }

    public static void Discard(JsonObject? preview) => Workflow.Discard(preview);
    public static JsonObject Apply(RhinoDoc doc, Guid objectId, JsonObject preview, IEnumerable<string> fields)
        => Workflow.Apply(doc, objectId, preview, fields);
}

// Lookup and time are the only external inputs; panel and MCP use the same workflow.
internal sealed class EumWorkflow(Func<string, CancellationToken, Task<JsonObject>> lookup, Func<DateTimeOffset> clock)
{
    private readonly object cacheGate = new();
    private sealed record PreviewEntry(uint Document, Guid Object, string ExpectedJson, JsonObject Result, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, PreviewEntry> Previews = new();

    /// <summary>A single PNU lookup, optionally bound to one parcel slot of an object. Never writes Info.</summary>
    public async Task<JsonObject> PreviewAsync(RhinoDoc doc, Guid? objectId, string? pnu, int parcelIndex, CancellationToken ct)
    {
        var snapshot = await RhinoDispatch.InvokeAsync(() =>
        {
            DocumentAccess.Get(doc.RuntimeSerialNumber);
            var data = new JsonObject();
            var raw = "";
            var writable = false;
            if (objectId is Guid id)
            {
                var obj = doc.Objects.FindId(id);
                if (obj is null || obj.IsDeleted) throw new InvalidOperationException("대상 객체가 없습니다.");
                var info = InfoService.Read(doc, id);
                if (info is not null)
                {
                    InfoData.Validate(info);
                    data = (JsonObject)info["data"]!.DeepClone();
                }
                raw = obj.Attributes.GetUserString(InfoData.Key) ?? "";
                writable = InfoService.CanEdit(doc, obj);
            }
            var count = (data["parcels"] as JsonArray)?.Count ?? 0;
            if (parcelIndex < 0 || parcelIndex >= Math.Max(1, count)) throw new ArgumentException("조회할 원필지 항목을 다시 선택하세요.");
            var query = pnu?.Trim();
            if (string.IsNullOrEmpty(query) && count > 0) query = data["parcels"]![parcelIndex]?["pnu"]?.GetValue<string>();
            if (!EumParser.IsPnu(query ?? "")) throw new ArgumentException("조회할 19자리 PNU를 입력하세요. 주소만으로 검색하는 기능은 아직 제공하지 않습니다.");
            return (Data: data, Raw: raw, Serial: doc.RuntimeSerialNumber, Pnu: query!, Writable: writable);
        });
        ct.ThrowIfCancellationRequested();
        var parcel = await lookup(snapshot.Pnu, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await RhinoDispatch.InvokeAsync(() => DocumentAccess.Get(snapshot.Serial));
        ct.ThrowIfCancellationRequested();
        var result = EumApply.CreatePreview(parcel, snapshot.Data, parcelIndex);
        result["document_id"] = snapshot.Serial;
        result["object_id"] = objectId?.ToString();
        result["can_apply"] = snapshot.Writable;
        if (objectId is Guid target)
        {
            lock (cacheGate)
            {
            foreach (var expired in Previews.Where(p => p.Value.Expires <= clock()).Select(p => p.Key)) Previews.TryRemove(expired, out _);
            if (Previews.Count >= 100) throw new InvalidOperationException("조회 미리보기가 많습니다. 기존 결과를 적용하거나 잠시 뒤 다시 조회하세요.");
            var id = Guid.NewGuid().ToString("N");
            result["preview_id"] = id;
            Previews[id] = new PreviewEntry(snapshot.Serial, target, snapshot.Raw, (JsonObject)result.DeepClone(), clock().AddMinutes(30));
            }
        }
        return result;
    }

    public void Discard(JsonObject? preview)
    {
        var id = preview?["preview_id"]?.GetValue<string>();
        if (id is not null) Previews.TryRemove(id, out _);
    }

    /// <summary>UI thread only. Values come from the cached result; the caller selects field names only.</summary>
    public JsonObject Apply(RhinoDoc doc, Guid objectId, JsonObject preview, IEnumerable<string> fields)
    {
        var id = preview["preview_id"]?.GetValue<string>() ?? "";
        if (!Previews.TryGetValue(id, out var cached) || cached.Expires <= clock() ||
            cached.Document != doc.RuntimeSerialNumber || cached.Object != objectId)
            throw new InvalidOperationException("이 객체의 유효한 조회 결과가 없습니다. 다시 검색하세요.");
        var selected = fields.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new ArgumentException("반영할 항목을 선택하세요.");
        var current = InfoService.Read(doc, objectId);
        var data = current?["data"] as JsonObject ?? new JsonObject();
        var patchData = EumApply.DataPatch(cached.Result, data, selected);
        // Source material belongs to the query result. Only selected business properties are stored.
        var updated = InfoService.Update(doc, objectId, new JsonObject { ["data"] = patchData }, cached.ExpectedJson);
        Previews.TryRemove(id, out _);
        return updated;
    }
}
