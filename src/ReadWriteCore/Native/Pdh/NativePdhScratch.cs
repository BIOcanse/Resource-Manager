using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Pdh;

// Internal storage boundary: the independent package has no Ledger policy.
internal abstract class NativePdhScratch : IDisposable
{
    internal abstract void BeginSample();
    internal abstract NativePdhScratchRegion Supply(uint requiredBytes);
    internal abstract void EndSample();
    internal abstract void RetryReturn();
    public abstract void Dispose();
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePdhScratchRegion
{
    internal IntPtr Address;
    internal uint Length;
}

internal interface INativePdhExternalScratchApi
{
    NativePdhResultCode CreateExternal(in NativePdhConfig config, out IntPtr handle);
    NativePdhResultCode SampleExternal(IntPtr handle, ref NativePdhFrameHeader header, NativePdhScratch scratch);
}

internal static partial class NativePdhLibrary
{
    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_create_external_scratch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateExternalPdhCollector(in NativePdhConfig config, out IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_sample_external_scratch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int SampleExternalPdhCollector(IntPtr handle,
        ref NativePdhFrameHeader header, in NativePdhScratchProvider provider);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePdhScratchProvider
{
    internal IntPtr Context;
    internal delegate* unmanaged[Cdecl]<IntPtr, uint, NativePdhScratchRegion*, int> Supply;
}

public sealed partial class NativePdhApi : INativePdhExternalScratchApi
{
    NativePdhResultCode INativePdhExternalScratchApi.CreateExternal(in NativePdhConfig config, out IntPtr handle)
        => (NativePdhResultCode)NativePdhLibrary.CreateExternalPdhCollector(config, out handle);

    unsafe NativePdhResultCode INativePdhExternalScratchApi.SampleExternal(IntPtr handle,
        ref NativePdhFrameHeader header, NativePdhScratch scratch)
    {
        var call = new ScratchCall(scratch);
        var context = GCHandle.Alloc(call);
        try
        {
            var provider = new NativePdhScratchProvider { Context = GCHandle.ToIntPtr(context), Supply = &SupplyScratch };
            var result = (NativePdhResultCode)NativePdhLibrary.SampleExternalPdhCollector(handle, ref header, provider);
            call.Failure?.Throw();
            return result;
        }
        finally { context.Free(); }
    }

    private sealed class ScratchCall(NativePdhScratch scratch)
    {
        internal NativePdhScratch Scratch { get; } = scratch;
        internal ExceptionDispatchInfo? Failure;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int SupplyScratch(IntPtr context, uint required, NativePdhScratchRegion* output)
    {
        var call = (ScratchCall)GCHandle.FromIntPtr(context).Target!;
        *output = default;
        if (call.Failure is not null) return -1;
        try
        {
            *output = call.Scratch.Supply(required);
            return 0;
        }
        catch (Exception exception)
        {
            call.Failure = ExceptionDispatchInfo.Capture(exception);
            return -1;
        }
    }
}
