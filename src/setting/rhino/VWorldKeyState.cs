namespace Aha.Setting;

public enum VWorldKeyStatus { Unknown, Missing, Registered, Validated, ValidationFailed }

/// <summary>Process-local status only. No credential, hash, or persistent value is retained.</summary>
internal sealed class VWorldKeyStateTracker
{
    private readonly object gate = new();
    private VWorldKeyStatus state;
    private long revision;
    public event Action? Changed;
    public VWorldKeyStatus State { get { lock (gate) return state; } }
    public long Revision { get { lock (gate) return revision; } }

    public void Observe(bool registered)
    {
        bool changed;
        lock (gate)
        {
            var next = registered
                ? state is VWorldKeyStatus.Unknown or VWorldKeyStatus.Missing ? VWorldKeyStatus.Registered : state
                : VWorldKeyStatus.Missing;
            changed = next != state;
            if (changed) { state = next; revision++; }
        }
        if (changed) Notify();
    }

    public void Saved() => Replace(VWorldKeyStatus.Registered);
    public void Deleted() => Replace(VWorldKeyStatus.Missing);

    private void Replace(VWorldKeyStatus next)
    {
        lock (gate) { state = next; revision++; }
        Notify();
    }

    public bool ReportValidation(long validatedRevision, bool success)
    {
        bool changed;
        lock (gate)
        {
            if (revision != validatedRevision || state is VWorldKeyStatus.Unknown or VWorldKeyStatus.Missing) return false;
            var next = success ? VWorldKeyStatus.Validated : VWorldKeyStatus.ValidationFailed;
            changed = state != next;
            state = next;
        }
        if (changed) Notify();
        return true;
    }

    private void Notify()
    {
        // A UI observer must not turn a successful credential operation into a reported failure.
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { observer(); } catch { }
    }
}
