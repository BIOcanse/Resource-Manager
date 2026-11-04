using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Settings;

// Thin host adapter: native owns validation, transition selection and every registry operation.
// JsonAppSettingsStore owns the durable settings image; no startup callback writes system keys.
public sealed class CrashDumpKeySettings(ICrashDumpKeyNative native)
{
    private CrashDumpKeyDiagnostics? diagnostics;
    public CrashDumpKeyDiagnostics? Diagnostics => Volatile.Read(ref diagnostics);

    internal CrashDumpKeyPrepared Prepare(AppSettings previous, AppSettings candidate)
    {
        var before = previous.SystemIntegration.CrashDumpKeys;
        var requested = candidate.SystemIntegration.CrashDumpKeys;
        var plan = native.Plan(before, requested, before.PreviousValues is not null);
        if (plan.Reason is not null) throw new CrashDumpKeySettingException(plan.Reason);
        // A client cannot overwrite or discard the backend-owned pre-image (including by PUT).
        var baseline = before.PreviousValues;
        if (plan.CapturePrevious) baseline = native.Capture();
        var owned = requested with { PreviousValues = baseline };
        return new(candidate with { SystemIntegration = candidate.SystemIntegration with { CrashDumpKeys = owned } }, plan.Operation, baseline);
    }

    internal bool ApplyCommitted(CrashDumpKeyPrepared prepared)
    {
        var desired = prepared.Settings.SystemIntegration.CrashDumpKeys;
        if (prepared.Operation == 0) return false;
        CrashDumpKeyNativeResult result;
        try { result = native.Apply(desired, prepared.Baseline ?? throw new InvalidDataException("Missing crash key pre-image.")); }
        catch (Exception)
        {
            Volatile.Write(ref diagnostics, new("failed", Combination(desired), desired.Dump1Keys, desired.Dump2Key,
                true, Reason: "crash-dump-keys-bridge-failed"));
            return false;
        }
        Volatile.Write(ref diagnostics, new(result.Reason is null ? "takesEffectAfterRestart" : "failed",
            Combination(desired), desired.Dump1Keys, desired.Dump2Key, true,
            result.KernelDumpConfigured, result.Reason, result.Detail, result.FailedSlot, result.RollbackDetail));
        return prepared.Operation == 2 && result.Reason is null;
    }

    public CrashDumpKeyDiagnostics Describe(AppCrashDumpKeySettings settings)
    {
        CrashDumpKeyNativeResult observed;
        try { observed = native.Inspect(settings); }
        catch (Exception)
        { return new("failed", Combination(settings), settings.Dump1Keys, settings.Dump2Key, true, Reason: "crash-dump-keys-bridge-failed"); }
        var previousFailure = Diagnostics is { State: "failed" } failure ? failure : null;
        return new(observed.Reason is null ? (settings.Enabled ? "takesEffectAfterRestart" : settings.PreviousValues is null ? "off" : "restorePending")
                : "configurationMismatch", Combination(settings), settings.Dump1Keys, settings.Dump2Key, true,
            observed.KernelDumpConfigured, observed.Reason ?? previousFailure?.Reason,
            observed.Detail, observed.FailedSlot, observed.RollbackDetail);
    }

    private static string Combination(AppCrashDumpKeySettings settings)
        => settings.Dump1Keys is null && settings.Dump2Key is null ? "rightCtrlScrollLockTwice" : "custom";
}

internal sealed record CrashDumpKeyPrepared(AppSettings Settings, uint Operation, CrashDumpKeySnapshot? Baseline);
internal sealed class CrashDumpKeySettingException(string reason, uint detail = 0, uint failedSlot = 0) : ArgumentException(reason)
{
    internal string Reason { get; } = reason;
    internal uint Detail { get; } = detail;
    internal uint FailedSlot { get; } = failedSlot;
}

public interface ICrashDumpKeyNative
{
    CrashDumpKeyNativePlan Plan(AppCrashDumpKeySettings previous, AppCrashDumpKeySettings desired, bool hasSnapshot);
    CrashDumpKeySnapshot Capture();
    CrashDumpKeyNativeResult Apply(AppCrashDumpKeySettings desired, CrashDumpKeySnapshot baseline);
    CrashDumpKeyNativeResult Inspect(AppCrashDumpKeySettings desired);
}

public sealed record CrashDumpKeyNativePlan(uint Operation, bool CapturePrevious, string? Reason);
public sealed record CrashDumpKeyNativeResult(string? Reason, uint Detail, uint FailedSlot, uint RollbackDetail, bool? KernelDumpConfigured);
