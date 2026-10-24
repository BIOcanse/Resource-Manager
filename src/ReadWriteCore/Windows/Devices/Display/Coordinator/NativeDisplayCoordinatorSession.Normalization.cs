using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeDisplayCoordinatorSession
{
    private readonly INativeDisplayNormalizationScratch? normalizationScratch;
    private NativeDisplayNormalizationLease? normalizationUse;
    private MemoryHandle normalizationPin;
    private bool normalizationPinned;

    private unsafe void BeginNormalization()
    {
        if (normalizationScratch is null) return; // Explicit independent-library ordinary-create route.
        EndNormalization(); // Retry a prior failed endUse before allowing another operation.
        normalizationUse = normalizationScratch.Acquire();
        try
        {
            normalizationPin = normalizationUse.Pin();
            normalizationPinned = true;
            var status = (NativeDisplayCoordinatorStatus)NativeMethods.BindNormalizationScratch(
                RequireHandle(), (IntPtr)normalizationPin.Pointer, normalizationScratch.Length);
            if (status != NativeDisplayCoordinatorStatus.Ok)
                throw new InvalidOperationException($"Display normalization bind failed with {status}.");
        }
        catch (Exception error)
        {
            try { EndNormalization(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private void EndNormalization()
    {
        if (normalizationUse is null) return;
        var status = (NativeDisplayCoordinatorStatus)NativeMethods.BindNormalizationScratch(RequireHandle(), IntPtr.Zero, 0);
        if (status != NativeDisplayCoordinatorStatus.Ok)
            throw new InvalidOperationException($"Display normalization detach failed with {status}.");
        if (normalizationPinned)
        {
            normalizationPin.Dispose();
            normalizationPinned = false;
        }
        normalizationUse.Complete();
        normalizationUse.Dispose();
        normalizationUse = null;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_create_external")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CreateExternal(in NativeDisplayCoordinatorConfiguration configuration, out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_bind_normalization_scratch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BindNormalizationScratch(IntPtr handle, IntPtr pointer, ulong length);
    }
}
