using System.Text.Json;
using Aha.Setting;

int passed = 0;
void Check(bool success, string description)
{
    if (!success) throw new Exception(description);
    passed++;
    Console.WriteLine("PASS " + description);
}
void Reject(Action action, string description, string? forbiddenText = null)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
    {
        Check(forbiddenText is null || !ex.Message.Contains(forbiddenText, StringComparison.Ordinal), description);
        return;
    }
    throw new Exception("Expected rejection: " + description);
}

const string sentinel = "DUMMY-KEY-DO-NOT-LOG";
foreach (string localUrl in new[] { "http://127.0.0.1:18940/mcp/", "http://localhost:18940/mcp/", "http://[::1]:18940/mcp/" })
{
    McpAddress.Validate(localUrl);
    Check(true, "local HTTP endpoint can be copied");
}
foreach (string invalidUrl in new[] { "", "https://example.org/mcp", "http://example.org/mcp", "file:///tmp/mcp",
    "http://user:pass@localhost/mcp", "http://localhost/mcp?key=" + sentinel, "http://localhost/mcp#secret" })
    Reject(() => McpAddress.Validate(invalidUrl), "nonlocal or credential-bearing endpoint rejected", sentinel);


var noKey = KeyControlState.From(true, true, false, false, false);
Check(noKey.InputEnabled && !noKey.InputReadOnly && noKey.CanSave && !noKey.CanDelete,
    "empty key store permits entry and save but not delete");
Check(!noKey.CanValidate && !noKey.CanReveal, "empty input cannot validate or reveal");
var enteredKey = KeyControlState.From(true, true, false, false, true);
Check(enteredKey.CanSave && enteredKey.CanValidate && enteredKey.CanReveal && !enteredKey.CanDelete,
    "unsaved input permits validation and reveal without enabling delete");
var storedKey = KeyControlState.From(true, true, true, false, true);
Check(storedKey.InputEnabled && storedKey.InputReadOnly && !storedKey.CanSave && storedKey.CanDelete,
    "stored key remains readable but cannot be edited or replaced");
Check(storedKey.CanValidate && storedKey.CanReveal, "stored key permits validation and reveal");
Check(KeyControlState.From(true, true, false, false, false) == noKey,
    "deleting a stored key returns to editable registration state");
foreach (var state in new[] {
    KeyControlState.From(false, true, true, false, true),
    KeyControlState.From(true, false, false, false, false),
    KeyControlState.From(true, true, true, true, true),
    KeyControlState.From(true, true, false, true, true) })
    Check(!state.InputEnabled && state.InputReadOnly && !state.CanSave && !state.CanDelete && !state.CanValidate && !state.CanReveal,
        "unsupported unknown or busy key state disables all key actions");

var keyState = new VWorldKeyStateTracker();
int keyEvents = 0;
keyState.Changed += () => keyEvents++;
Check(keyState.State == VWorldKeyStatus.Unknown, "key status starts unknown without reading credentials");
keyState.Observe(false);
Check(keyState.State == VWorldKeyStatus.Missing && keyEvents == 1, "missing key observation publishes registration state");
keyState.Saved();
long validationRevision = keyState.Revision;
Check(keyState.State == VWorldKeyStatus.Registered, "saving requires fresh validation");
Check(keyState.ReportValidation(validationRevision, true) && keyState.State == VWorldKeyStatus.Validated,
    "successful explicit validation is shared in memory");
keyState.Observe(true);
Check(keyState.State == VWorldKeyStatus.Validated && keyState.Revision == validationRevision,
    "rereading an existing key preserves the explicit validation result");
Check(keyState.ReportValidation(validationRevision, false) && keyState.State == VWorldKeyStatus.ValidationFailed,
    "failed explicit validation is distinguished from missing key");
keyState.Deleted();
Check(keyState.State == VWorldKeyStatus.Missing && !keyState.ReportValidation(validationRevision, true),
    "deleting a key rejects an older in-flight validation result");
keyState.Saved();
long previousKey = keyState.Revision;
keyState.Saved();
Check(!keyState.ReportValidation(previousKey, true) && keyState.State == VWorldKeyStatus.Registered,
    "replacing a key rejects the preceding key validation");
var noRegisteredKey = new VWorldKeyStateTracker();
Check(!noRegisteredKey.ReportValidation(noRegisteredKey.Revision, true) && noRegisteredKey.State == VWorldKeyStatus.Unknown,
    "validating unsaved input cannot mark an unknown stored key as valid");
keyState.Changed += () => throw new InvalidOperationException("fixture observer failure");
keyState.Deleted();
Check(keyState.State == VWorldKeyStatus.Missing, "an observer failure cannot turn credential success into failure");

var defaults = SettingsCodec.Decode("{}");
Check(!defaults.McpEnabled && defaults.McpPort == 18940, "missing preference fields default to disabled and port 18940");
string encoded = SettingsCodec.Encode(new GeneralSettings(true, 19000));
using (var json = JsonDocument.Parse(encoded))
{
    Check(json.RootElement.EnumerateObject().Count() == 2 && json.RootElement.TryGetProperty("mcpEnabled", out _)
        && json.RootElement.TryGetProperty("mcpPort", out _), "general preference format has no credential field");
}
Check(SettingsCodec.Decode(encoded) == new GeneralSettings(true, 19000), "general preferences round-trip");
string stripped = SettingsCodec.Encode(SettingsCodec.Decode("{\"mcpEnabled\":false,\"vworldKey\":\"" + sentinel + "\"}"));
Check(!stripped.Contains(sentinel, StringComparison.Ordinal) && !stripped.Contains("vworld", StringComparison.OrdinalIgnoreCase),
    "unknown credential-like JSON fields are never propagated into saved preferences");
foreach (string invalid in new[] { "null", "[]", "{", "{\"mcpEnabled\":\"yes\"}", "{\"mcpPort\":80}",
    "{\"mcpPort\":65536}", "{\"mcpPort\":999999999999999999}", "{\"mcpPort\":\"18940\"}" })
    Reject(() => SettingsCodec.Decode(invalid), "invalid preference value is rejected");
Reject(() => SettingsCodec.Encode(new GeneralSettings(true, 0)), "invalid port cannot be persisted");

string directory = Path.Combine(Path.GetTempPath(), "aha-setting-checks-" + Guid.NewGuid().ToString("N"));
try
{
    string path = Path.Combine(directory, "settings.json");
    var store = new SettingsStore(path);
    Check(store.Read() == new GeneralSettings(), "fresh install needs no settings file");
    store.Write(new GeneralSettings(true, 19001));
    Check(OperatingSystem.IsWindows() || File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "settings file is owner-only");
    string prior = File.ReadAllText(path);
    try { store.Write(new GeneralSettings(true, 1)); throw new Exception("bad port accepted"); }
    catch (ArgumentException) { Check(File.ReadAllText(path) == prior, "invalid write preserves prior settings"); }
    var collision = Path.Combine(directory, "collision"); Directory.CreateDirectory(collision);
    try { new SettingsStore(collision).Write(new GeneralSettings()); throw new Exception("directory overwritten"); }
    catch (IOException) { Check(!Directory.GetFiles(directory).Any(f => f.EndsWith(".tmp")), "failed replacement cleans temporary file"); }
    File.WriteAllText(path, "{");
    try { store.Read(); throw new Exception("corrupt settings accepted"); }
    catch (InvalidDataException) { Check(File.ReadAllText(path) == "{", "reading corrupt settings does not erase source"); }
    store.Write(new GeneralSettings(true, 19001));
    Directory.Delete(collision);
    var reopened = new SettingsStore(path);
    Check(reopened.Read() == new GeneralSettings(true, 19001), "preferences survive reopening the store");
    reopened.Write(new GeneralSettings(false, 18940));
    Check(store.Read() == new GeneralSettings(false, 18940) && Directory.GetFiles(directory).Length == 1,
        "replacement leaves only the settings file");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

// A failed storage operation must never announce a successful state transition.
string isolatedKey = "";
bool failWrite = false, failRead = false, failDelete = false;
var access = new CredentialAccess(() => failRead ? throw new IOException("fixture") : isolatedKey,
    value => { if (failWrite) throw new IOException("fixture"); isolatedKey = value; },
    () => { if (failDelete) throw new IOException("fixture"); isolatedKey = ""; }, () => isolatedKey.Length > 0, true);
Check(access.Read() == "" && access.State == VWorldKeyStatus.Missing, "isolated empty store is missing");
access.Save("  TEST-KEY  ");
Check(isolatedKey == "TEST-KEY" && access.Contains(), "credential operations use the injected store and trim input");
long storedRevision = access.Revision;
foreach (string operation in new[] { "read", "write", "delete" })
{
    failRead = operation == "read"; failWrite = operation == "write"; failDelete = operation == "delete";
    try { if (failRead) access.Read(); else if (failWrite) access.Save("replacement"); else access.Delete(); throw new Exception("storage failure ignored"); }
    catch (IOException) { Check(access.Revision == storedRevision && isolatedKey == "TEST-KEY", "failed " + operation + " preserves credential and revision"); }
}
failRead = failWrite = failDelete = false;
access.Delete();
Check(!access.Contains() && access.Read() == "", "delete empties isolated store");

// Compile the real SecItem adapter, but do not access the user's Keychain, settings, or network.
Console.WriteLine($"{passed} checks passed; no OS credentials or network accessed.");
