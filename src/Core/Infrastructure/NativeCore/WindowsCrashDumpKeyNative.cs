using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Synchronous C ABI transport. Fixed small structs and raw registry values are borrowed only in each call.
public sealed partial class WindowsCrashDumpKeyNative : ICrashDumpKeyNative
{
    private const int SlotCount = 7;
    private const int MaximumValueBytes = 256;
    private const int NativeValueSize = 12 + MaximumValueBytes;
    private const int SnapshotBytes = NativeValueSize * SlotCount;

    public CrashDumpKeyNativePlan Plan(AppCrashDumpKeySettings previous, AppCrashDumpKeySettings desired, bool hasSnapshot)
    {
        var before = RequestOf(previous);
        var request = RequestOf(desired);
        Check(PlanNative(in before, in request, hasSnapshot ? 1U : 0U, out var plan));
        return new(plan.Operation, plan.CapturePrevious != 0, Reason(plan.Reason));
    }

    public unsafe CrashDumpKeySnapshot Capture()
    {
        Check(CaptureNative(out var snapshot, out var result));
        if (Reason(result.Reason) is { } reason)
            throw new CrashDumpKeySettingException(reason, result.Detail, result.FailedSlot);
        var values = new CrashDumpKeyRegistryValue[SlotCount];
        var pointer = (byte*)&snapshot;
        var index = 0;
        while (index < SlotCount)
        {
            var value = (NativeValue*)(pointer + index * NativeValueSize);
            var data = new ReadOnlySpan<byte>(value->Data, checked((int)value->Length)).ToArray();
            values[index++] = new(value->Present != 0, value->ValueType, data);
        }
        return new(values);
    }

    public unsafe CrashDumpKeyNativeResult Apply(AppCrashDumpKeySettings desired, CrashDumpKeySnapshot baseline)
    {
        var snapshot = SnapshotOf(baseline);
        var request = RequestOf(desired);
        Check(ApplyNative(in request, in snapshot, out var result));
        return new(Reason(result.Reason), result.Detail, result.FailedSlot, result.RollbackDetail,
            result.Reason == 0 ? result.KernelDumpConfigured != 0 : null);
    }

    public unsafe CrashDumpKeyNativeResult Inspect(AppCrashDumpKeySettings desired)
    {
        var request = RequestOf(desired);
        var snapshot = desired.PreviousValues is null ? new NativeSnapshot() : SnapshotOf(desired.PreviousValues);
        Check(InspectNative(in request, desired.PreviousValues is null ? null : &snapshot, out var result));
        return new(Reason(result.Reason), result.Detail, result.FailedSlot, result.RollbackDetail,
            result.Reason is 0 or 9 ? result.KernelDumpConfigured != 0 : null);
    }

    private static unsafe NativeSnapshot SnapshotOf(CrashDumpKeySnapshot baseline)
    {
        if (baseline.Values is not { Count: SlotCount }) throw new InvalidDataException("Invalid crash key pre-image slot count.");
        var snapshot = new NativeSnapshot();
        var pointer = (byte*)&snapshot;
        var index = 0;
        while (index < SlotCount)
        {
            var input = baseline.Values[index] ?? throw new InvalidDataException("Invalid crash key pre-image value.");
            if (input.Data is null || input.Data.Length > MaximumValueBytes) throw new InvalidDataException("Crash key pre-image value exceeds ABI bound.");
            var output = (NativeValue*)(pointer + index++ * NativeValueSize);
            output->Present = input.Present ? 1U : 0U;
            output->ValueType = input.ValueType;
            output->Length = checked((uint)input.Data.Length);
            input.Data.AsSpan().CopyTo(new Span<byte>(output->Data, MaximumValueBytes));
        }
        return snapshot;
    }

    private static NativeRequest RequestOf(AppCrashDumpKeySettings settings) => new()
    {
        Enabled = settings.Enabled ? 1U : 0U,
        Custom = settings.Dump1Keys is null && settings.Dump2Key is null ? 0U
            : settings.Dump1Keys.HasValue && settings.Dump2Key.HasValue ? 1U : 2U,
        Dump1Keys = settings.Dump1Keys ?? 0,
        Dump2Key = settings.Dump2Key ?? 0
    };

    private static string? Reason(uint reason) => reason switch
    {
        0 => null,
        1 => "invalid-crash-dump-keys",
        2 => "crash-dump-keys-read-failed",
        3 => "crash-dump-keys-write-failed",
        4 => "crash-dump-keys-verify-failed",
        5 => "crash-dump-keys-restore-pending",
        6 => "invalid-crash-dump-key-snapshot",
        7 => "crash-dump-keys-access-denied",
        8 => "crash-dump-key-value-too-large",
        9 => "crash-dump-keys-configuration-mismatch",
        _ => "crash-dump-keys-unknown-result"
    };

    private static void Check(int result)
    { if (result != 0) throw new InvalidOperationException($"Invalid crash key native ABI call ({result})."); }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRequest { internal uint Enabled, Custom, Dump1Keys, Dump2Key; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlan { internal uint Operation, CapturePrevious, Reason; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult { internal uint Reason, Detail, FailedSlot, RollbackDetail, RequiresRestart, KernelDumpConfigured; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeSnapshot { internal uint FirstWord; internal fixed byte Rest[SnapshotBytes - sizeof(uint)]; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeValue
    {
        internal uint Present, ValueType, Length;
        internal fixed byte Data[MaximumValueBytes];
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_dump_keys_plan")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PlanNative(in NativeRequest previous, in NativeRequest desired, uint hasSnapshot, out NativePlan plan);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_dump_keys_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CaptureNative(out NativeSnapshot snapshot, out NativeResult result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_dump_keys_apply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ApplyNative(in NativeRequest desired, in NativeSnapshot baseline, out NativeResult result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_dump_keys_inspect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int InspectNative(in NativeRequest desired, NativeSnapshot* baseline, out NativeResult result);
}
