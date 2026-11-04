namespace ResourceManager.App.Domain.Settings;

// PreviousValues is backend-owned. A save always replaces client-supplied values with the stored pre-image.
// It is committed before touching the registry and survives a failed apply or restore.
public sealed record AppCrashDumpKeySettings(
    bool Enabled = false,
    uint? Dump1Keys = null,
    uint? Dump2Key = null,
    CrashDumpKeySnapshot? PreviousValues = null);

public sealed record CrashDumpKeySnapshot(IReadOnlyList<CrashDumpKeyRegistryValue> Values);
public sealed record CrashDumpKeyRegistryValue(bool Present, uint ValueType, byte[] Data);

public sealed record CrashDumpKeyDiagnostics(
    string State,
    string KeyCombination,
    uint? Dump1Keys,
    uint? Dump2Key,
    bool TakesEffectAfterRestart,
    bool? KernelDumpConfigured = null,
    string? Reason = null,
    uint Detail = 0,
    uint FailedSlot = 0,
    uint RollbackDetail = 0);
