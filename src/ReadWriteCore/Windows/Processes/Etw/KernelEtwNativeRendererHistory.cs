using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Reads one process stamp from the immutable native renderer publication.</summary>
internal static class KernelEtwNativeRendererHistory
{
    internal readonly record struct Revision(ulong Epoch, ulong EventsLost, ulong Value);

    internal static Revision? Read(KernelEtwEventSource source, int processId)
    {
        if (processId <= 0 || source.NativeSession is not { } session) return null;
        using var owner = session.AcquireReadLease();
        var status = Query(owner.OwnerHandle, unchecked((uint)processId), out var stamp, 24);
        if (status == 3) return null;
        KernelEtwNativeReaders.Check(status, "query renderer publication stamp");
        return new(stamp.Epoch, stamp.EventsLost, stamp.Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Stamp { public ulong Epoch, EventsLost, Value; }

    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_renderer_stamp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Query(nint owner, uint processId, out Stamp stamp, uint size);
}
