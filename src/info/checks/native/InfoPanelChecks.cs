using System.Reflection;
using System.Text.Json.Nodes;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
namespace Aha.Info.Checks;
internal static class InfoPanelChecks
{
    internal static int Run()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        var first = doc.Objects.AddCircle(new Circle(Plane.WorldXY, 10));
        var second = doc.Objects.AddPoint(Point3d.Origin);
        InfoService.Update(doc, first, JsonNode.Parse("""{"data":{"parcels":[{"address":"one","custom":42},{"address":"two"}]}}""")!.AsObject());
        using var panel = new InfoPanel(doc.RuntimeSerialNumber, () => doc.RuntimeSerialNumber);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(InfoPanel).GetField(name, hidden)!.GetValue(panel)!;
        void Call(string name, params object[] args) => typeof(InfoPanel).GetMethod(name, hidden)!.Invoke(panel, args);
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
        Call("LoadInfo", first);
        Field<TextBox>("parcelAddress").Text = "edited";
        Field<TextArea>("notes").Text = "pinned";
        doc.Objects.UnselectAll(); doc.Objects.Select(second); Call("Poll");
        Check(Field<Guid>("objectId") == first, "dirty editor remains bound to original object");
        Call("Save");
        var data = InfoService.Read(doc, first)!["data"]!;
        Check(data["parcels"]![0]!["address"]!.GetValue<string>() == "edited" && data["parcels"]![0]!["custom"]!.GetValue<int>() == 42
            && data["parcels"]![1]!["address"]!.GetValue<string>() == "two", "save commits parcel editor and preserves other slots/extensions");
        Check(InfoService.Read(doc, second) is null, "selection changes never redirect writes");
        Field<TextArea>("notes").Text = "stale edit";
        InfoService.Update(doc, first, JsonNode.Parse("""{"data":{"notes":"external"}}""")!.AsObject());
        try { Call("Save"); throw new Exception("stale panel write accepted"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { checks++; }
        Check(InfoService.Read(doc, first)!["data"]!["notes"]!.GetValue<string>() == "external", "stale edit leaves external value intact");
        foreach (var (field, value) in new[] { ("max_floors", "9007199254740992.1"),
            ("max_floors", "1e-400"), ("setback_m", "1e-400"), ("setback_m", "-1e-400") })
        {
            Call("LoadInfo", first);
            string before = doc.Objects.FindId(first)!.Attributes.GetUserString(InfoData.Key);
            Field<Dictionary<string, TextBox>>("numbers")[field].Text = value;
            try { Call("Save"); throw new Exception("panel rounded an invalid numeric token before validation: " + field); }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { checks++; }
            Check(doc.Objects.FindId(first)!.Attributes.GetUserString(InfoData.Key) == before,
                "rejected numeric input leaves stored information unchanged");
        }
        InfoService.Update(doc, first, JsonNode.Parse("""{"data":{"max_floors":9007199254740993,"setback_m":0.1234567890123456789}}""")!.AsObject());
        Call("LoadInfo", first);
        Field<TextArea>("notes").Text = "edit beside exact numbers";
        Call("Save");
        var precise = InfoService.Read(doc, first)!["data"]!;
        Check(precise["max_floors"]!.GetValue<long>() == 9007199254740993L
            && precise["setback_m"]!.ToJsonString() == "0.1234567890123456789",
            "editing another field preserves untouched numeric precision");
        Call("LoadInfo", first);
        Field<Dictionary<string, TextBox>>("numbers")["max_floors"].Text = "9007199254740995";
        Call("Save");
        Check(InfoService.Read(doc, first)!["data"]!["max_floors"]!.GetValue<long>() == 9007199254740995L,
            "entered integer digits reach common validation without double rounding");
        Field<TextBox>("fieldName").Text = "custom_number";
        Field<DropDown>("fieldType").SelectedIndex = 1;
        Field<TextArea>("fieldValue").Text = "9007199254740997";
        Call("Save");
        Check(InfoService.Read(doc, first)!["data"]!["custom_number"]!.GetValue<long>() == 9007199254740997L,
            "additional numeric fields also preserve entered digits");
        using (var nullable = doc.Objects.FindId(first)!.Attributes.Duplicate())
        {
            var info = InfoService.Read(doc, first)!;
            info["data"]!["bcr_percent"] = null; info["data"]!["notes"] = null;
            nullable.SetUserString(InfoData.Key, info.ToJsonString());
            doc.Objects.ModifyAttributes(first, nullable, true);
        }
        Call("LoadInfo", first);
        Field<TextBox>("kind").Text = "parcel";
        Call("Save");
        var nullableData = InfoService.Read(doc, first)!["data"]!.AsObject();
        Check(nullableData.ContainsKey("bcr_percent") && nullableData["bcr_percent"] is null
            && nullableData.ContainsKey("notes") && nullableData["notes"] is null,
            "unrelated panel edits preserve explicit unknown standard values");
        using (var malformed = doc.Objects.FindId(first)!.Attributes.Duplicate())
        {
            var info = InfoService.Read(doc, first)!;
            info["data"]!["parcels"] = new JsonObject { ["unexpected"] = true };
            malformed.SetUserString(InfoData.Key, info.ToJsonString());
            doc.Objects.ModifyAttributes(first, malformed, true);
        }
        Call("LoadInfo", first);
        Field<TextArea>("notes").Text = "pending note survives advanced repair";
        var malformedRaw = doc.Objects.FindId(first)!.Attributes.GetUserString(InfoData.Key);
        Call("CommitInputs", false);
        Check(Field<JsonObject>("working")["data"]!["parcels"] is JsonObject
            && doc.Objects.FindId(first)!.Attributes.GetUserString(InfoData.Key) == malformedRaw,
            "advanced preparation accepts repairable stored fields without writing");
        try { Call("ApplyAdvanced", new JsonObject()); throw new Exception("advanced editor accepted unrepaired fields"); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { checks++; }
        Call("ApplyAdvanced", JsonNode.Parse("""{"data":{"parcels":[{"address":"repaired"}]}}""")!.AsObject());
        Call("Save");
        var repaired = InfoService.Read(doc, first)!["data"]!;
        Check(repaired["parcels"]![0]!["address"]!.GetValue<string>() == "repaired"
            && repaired["notes"]!.GetValue<string>() == "pending note survives advanced repair",
            "advanced repair preserves pending form inputs and saves a fully validated result");
        int parent = doc.Layers.Add(new Layer { Name = "locked parent" });
        int child = doc.Layers.Add(new Layer { Name = "child", ParentLayerId = doc.Layers[parent].Id });
        using var attributes = doc.Objects.FindId(first)!.Attributes.Duplicate(); attributes.LayerIndex = child; doc.Objects.ModifyAttributes(first, attributes, true);
        doc.Layers[parent].IsLocked = true; doc.Layers[parent].CommitChanges();
        Check(!InfoService.CanEdit(doc, doc.Objects.FindId(first)!), "parent lock blocks metadata edits");
        try { InfoService.Delete(doc, first); throw new Exception("parent lock ignored"); }
        catch (InvalidOperationException) { checks++; }
        return checks;
    }
}
