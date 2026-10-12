namespace Aha.Setting;

// Status changes follow successful storage operations. No credential is retained here.
internal sealed class CredentialAccess(Func<string> read, Action<string> write, Action delete, Func<bool> contains, bool supported)
{
    private readonly VWorldKeyStateTracker state = new();
    internal bool Supported => supported;
    internal VWorldKeyStatus State => state.State;
    internal long Revision => state.Revision;
    internal event Action? Changed { add => state.Changed += value; remove => state.Changed -= value; }
    internal string Read() { var value = read(); state.Observe(!string.IsNullOrEmpty(value)); return value; }
    internal bool Contains() { bool value = contains(); state.Observe(value); return value; }
    internal void Save(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("저장할 VWorld KEY를 입력하세요.");
        write(value.Trim()); state.Saved();
    }
    internal void Delete() { delete(); state.Deleted(); }
    internal bool ReportValidation(long revision, bool success) => state.ReportValidation(revision, success);
}
