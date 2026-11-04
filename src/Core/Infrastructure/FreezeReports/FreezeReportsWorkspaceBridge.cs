using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.FreezeReports;

// The resource owns one stable identity. Each native work scope borrows and
// pins it locally; no borrowed region survives the callback's return.
internal sealed unsafe class FreezeReportsWorkspaceBridge : IDisposable
{
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly IComputeScratch scratch;
    private GCHandle callbackRoot;
    private int disposed;

    internal FreezeReportsWorkspaceBridge(ResourceManagerLedgerOwner ledger)
    {
        scratch = ledger.CreateScratch(ledger.NativePageSize);
        try
        {
            ComputeScratchNames.Name(scratch, "freeze-reports", "native work bytes (high water)");
            callbackRoot = GCHandle.Alloc(this);
        }
        catch { scratch.Dispose(); throw; }
    }

    internal ScratchCallbacks Callbacks => new()
    {
        Context = GCHandle.ToIntPtr(callbackRoot),
        Run = &Run
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private static int Run(nint context, nuint requested,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> work, nint operation)
    {
        try
        {
            if (requested == 0 || work == null) return 503;
            var self = (FreezeReportsWorkspaceBridge)GCHandle.FromIntPtr(context).Target!;
            self.execution.Wait();
            try
            {
                if (Volatile.Read(ref self.disposed) != 0) return 503;
                using var lease = self.scratch.Acquire();
                if (lease.Length < requested)
                {
                    // Grow before pinning, preserving the same scratch UID.
                    if (lease.IsLoading) lease.PublishAndRetain();
                    lease.Grow(requested);
                }
                int result;
                using (var view = new ComputeScratchView(lease))
                    result = work(operation, (byte*)view.AlignedRegion(0, requested, 1), checked((nuint)lease.Length));
                lease.Complete();
                return result;
            }
            finally { self.execution.Release(); }
        }
        catch { return 503; }
    }

    // Native destruction drains all source callbacks before this is called.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        execution.Wait();
        try
        {
            scratch.Dispose();
            callbackRoot.Free();
        }
        finally { execution.Release(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScratchCallbacks
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, nuint,
            delegate* unmanaged[Cdecl]<nint, byte*, nuint, int>, nint, int> Run;
    }
}
