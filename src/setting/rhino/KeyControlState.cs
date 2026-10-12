namespace Aha.Setting;

/// <summary>UI permissions only; this model never contains the credential itself.</summary>
internal readonly record struct KeyControlState(
    bool InputEnabled, bool InputReadOnly, bool CanSave, bool CanDelete, bool CanValidate, bool CanReveal)
{
    public static KeyControlState From(bool supported, bool known, bool stored, bool busy, bool hasInput)
    {
        if (!supported || !known || busy) return new(false, true, false, false, false, false);
        return new(true, stored, !stored, stored, stored || hasInput, stored || hasInput);
    }
}
