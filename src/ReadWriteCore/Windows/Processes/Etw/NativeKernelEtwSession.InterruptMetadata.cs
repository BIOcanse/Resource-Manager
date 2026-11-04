using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>The immutable module inventory identity captured by the native event owner.</summary>
public readonly record struct KernelEtwInterruptMetadata(long SessionGeneration, ulong OwnerSerial,
    ulong ActiveModules, ulong ModuleRevision);

public sealed unsafe partial class NativeKernelEtwSession
{
    /// <summary>Reads published module inventory facts without taking a kernel-session hold.</summary>
    /// <remarks>The serial identifies the module owner and remains stable across duration freeze/thaw.
    /// A new module owner has a new serial, even in the same physical session generation.</remarks>
    public KernelEtwInterruptMetadata? QueryInterruptMetadata()
    {
        using var lease = AcquireReadLease();
        var result = InterruptMetadataNative.Query(lease.OwnerHandle, out var value,
            checked((uint)sizeof(NativeInterruptMetadata)));
        if (result == 3) return null;
        Check(result, "read published interrupt module metadata");
        if (value.OwnerSerial == 0)
            throw new InvalidDataException("Native interrupt module metadata has no owner identity.");
        return new(value.SessionGeneration, value.OwnerSerial, value.ActiveModules, value.ModuleRevision);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInterruptMetadata
    {
        public long SessionGeneration;
        public ulong OwnerSerial, ActiveModules, ModuleRevision;
    }

    private static class InterruptMetadataNative
    {
        [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_interrupt_metadata", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Query(nint owner, out NativeInterruptMetadata value, uint size);
    }
}
