using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Native.Pdh;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Host memory/concurrency adapter. The identity is stable, each lease/pin is
// call-local, and no result reference may escape consume. Caps are injected by
// the caller; this platform adapter has no settings or scheduling authority.
internal sealed unsafe class PdhGpuProjectionOwner : IDisposable
{
    private readonly object gate = new();
    private readonly IComputeScratch scratch;
    private bool disposed;
    internal ulong PeakWorkspaceBytes { get; private set; }
    internal ulong WorkspaceBytes => scratch.Length;
    internal ulong PeakRegionBytes { get; private set; }
    internal PdhGpuProjectionOwner(ResourceManagerLedgerOwner ledger)
    {
        scratch = ledger.CreateScratch(ledger.NativePageSize);
        ComputeScratchNames.Name(scratch, "pdh-gpu-projection", "GPU projection workspace");
    }
    internal delegate void Consume(in NativePdhGpuOwner.Output result);
    internal delegate void ConsumeShared(in NativePdhGpuOwner.SharedResult result);
    internal void QueryShared(in NativePdhGpuOwner.QueryHost api, in NativePdhGpuOwner.QueryRequest request,
        bool windows, ConsumeShared consume)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var call = new SharedCall(this, consume);
            var root = GCHandle.Alloc(call);
            uint status;
            try
            {
                var host = api;
                host.WorkspaceContext = host.ConsumeContext = GCHandle.ToIntPtr(root);
                host.WithRegion = &WithSharedRegion; host.Consume = &AcceptShared;
                status = windows ? NativePdhGpuOwner.SharedQueryWindows(host, request) : NativePdhGpuOwner.SharedQuery(host, request);
            }
            finally { root.Free(); }
            call.Failure?.Throw();
            if (status != 0) throw new InvalidOperationException($"PDH shared query ABI failed ({status}).");
        }
    }
    private sealed class SharedCall(PdhGpuProjectionOwner owner, ConsumeShared consume)
    {
        internal readonly PdhGpuProjectionOwner Owner = owner;
        internal readonly ConsumeShared Consume = consume;
        internal ExceptionDispatchInfo? Failure;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private static uint WithSharedRegion(nint context, nuint required,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, uint> work, nint operation)
    {
        var call = (SharedCall)GCHandle.FromIntPtr(context).Target!;
        try
        {
            using var lease = call.Owner.scratch.Acquire();
            if (lease.Length < (ulong)required)
            {
                if (lease.IsLoading) lease.PublishAndRetain();
                lease.Grow((ulong)required);
            }
            uint result;
            call.Owner.PeakRegionBytes = Math.Max(call.Owner.PeakRegionBytes, lease.Length);
            using (var view = new ComputeScratchView(lease))
                result = work(operation, (byte*)view.AlignedRegion(0, (ulong)required, 1), required);
            lease.Complete();
            return result;
        }
        catch (Exception exception) { call.Failure = ExceptionDispatchInfo.Capture(exception); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AcceptShared(nint context, NativePdhGpuOwner.SharedResult* result, nuint used)
    {
        var call = (SharedCall)GCHandle.FromIntPtr(context).Target!;
        if (call.Failure is not null) return;
        try
        {
            call.Owner.PeakWorkspaceBytes = Math.Max(call.Owner.PeakWorkspaceBytes, (ulong)used);
            call.Consume(in *result);
        }
        catch (Exception exception) { call.Failure = ExceptionDispatchInfo.Capture(exception); }
    }
    internal void Project(in NativePdhGpuOwner.Input input, nuint maximum, Consume consume)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var required = NativePdhGpuOwner.Workspace(input);
            if (required == 0 || required > maximum) throw new InvalidOperationException("PDH GPU projection exceeds its declared workspace.");
            using var lease = scratch.Acquire();
            if (lease.Length < (ulong)required)
            {
                if (lease.IsLoading) lease.PublishAndRetain();
                lease.Grow((ulong)required);
            }
            using (var view = new ComputeScratchView(lease))
            {
                PeakRegionBytes = Math.Max(PeakRegionBytes, lease.Length);
                var address = (byte*)view.AlignedRegion(0, (ulong)required, 1);
                var status = NativePdhGpuOwner.Project(input, address, required, maximum, out var output);
                if (status != 0) throw new InvalidOperationException($"PDH GPU projection failed ({status}).");
                PeakWorkspaceBytes = Math.Max(PeakWorkspaceBytes, (ulong)output.WorkspaceUsed);
                consume(in output);
            }
            lease.Complete();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            scratch.Dispose();
            disposed = true;
        }
    }
}
