using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Pdh;

internal static partial class NativePdhLibrary
{
    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_copy_gpu_engine_windows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int CopyGpuEngineWindows(IntPtr handle, ulong sequence,
        NativePdhGpuEngineWindow* rows, uint capacity, uint rowSize, out uint count);
    static NativePdhLibrary() => ReadWriteNativeLibraryResolver.EnsureRegistered();
    private const string LibraryName = "ResourceManager.NativeCore";
    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetPdhCollectorAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreatePdhCollector(in NativePdhConfig config, out IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyPdhCollector(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_request_topology_refresh")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RequestPdhTopologyRefresh(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_sample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SamplePdhCollector(IntPtr handle, ref NativePdhFrameHeader header);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_copy_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int CopyPdhFrame(
        IntPtr handle,
        ulong expectedSequence,
        NativePdhGpuEngineRow* engineRows,
        uint engineCapacity,
        NativePdhGpuMemoryRow* memoryRows,
        uint memoryCapacity,
        NativePdhSystemIo* systemIo);

    [LibraryImport(LibraryName, EntryPoint = "rm_pdh_collector_get_diagnostics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetPdhDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics);

}

public interface INativePdhApi
{
    // Older native assets and existing consumers retain their original row ABI.
    NativePdhResultCode CopyGpuEngineWindows(IntPtr handle, ulong sequence,
        out NativePdhGpuEngineWindow[] windows)
    { windows = []; return NativePdhResultCode.Unavailable; }
    uint GetAbiVersion();

    NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle);

    void Destroy(IntPtr handle);

    NativePdhResultCode RequestTopologyRefresh(IntPtr handle);

    NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header);

    NativePdhResultCode CopyFrame(
        IntPtr handle,
        ulong expectedSequence,
        NativePdhGpuEngineRow[] engineRows,
        NativePdhGpuMemoryRow[] memoryRows,
        out NativePdhSystemIo systemIo);

    NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics);
}

public sealed partial class NativePdhApi : INativePdhApi
{
    public unsafe NativePdhResultCode CopyGpuEngineWindows(IntPtr handle, ulong sequence,
        out NativePdhGpuEngineWindow[] windows)
    {
        windows = [];
        try
        {
            var size = (uint)Marshal.SizeOf<NativePdhGpuEngineWindow>();
            var result = (NativePdhResultCode)NativePdhLibrary.CopyGpuEngineWindows(handle,
                sequence, null, 0, size, out var count);
            if (count > NativeCoreAbi.MaximumFrameRows) return NativePdhResultCode.AbiMismatch;
            if (count == 0) return result;
            if (result != NativePdhResultCode.BufferTooSmall) return result;
            var rows = new NativePdhGpuEngineWindow[count];
            fixed (NativePdhGpuEngineWindow* pointer = rows)
                result = (NativePdhResultCode)NativePdhLibrary.CopyGpuEngineWindows(handle,
                    sequence, pointer, count, size, out var copied);
            if (result == NativePdhResultCode.Ok) windows = rows;
            return result;
        }
        catch (EntryPointNotFoundException) { return NativePdhResultCode.Unavailable; }
    }
    static NativePdhApi() => ReadWriteNativeLibraryResolver.EnsureRegistered();
    public uint GetAbiVersion()
    {
        return NativePdhLibrary.GetPdhCollectorAbiVersion();
    }

    public NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle)
    {
        return (NativePdhResultCode)NativePdhLibrary.CreatePdhCollector(config, out handle);
    }

    public void Destroy(IntPtr handle)
    {
        NativePdhLibrary.DestroyPdhCollector(handle);
    }

    public NativePdhResultCode RequestTopologyRefresh(IntPtr handle)
    {
        return (NativePdhResultCode)NativePdhLibrary.RequestPdhTopologyRefresh(handle);
    }

    public NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header)
    {
        return (NativePdhResultCode)NativePdhLibrary.SamplePdhCollector(handle, ref header);
    }

    public unsafe NativePdhResultCode CopyFrame(
        IntPtr handle,
        ulong expectedSequence,
        NativePdhGpuEngineRow[] engineRows,
        NativePdhGpuMemoryRow[] memoryRows,
        out NativePdhSystemIo systemIo)
    {
        var io = new NativePdhSystemIo
        {
            StructSize = (uint)Marshal.SizeOf<NativePdhSystemIo>()
        };
        int result;
        fixed (NativePdhGpuEngineRow* enginePointer = engineRows)
        fixed (NativePdhGpuMemoryRow* memoryPointer = memoryRows)
        {
            result = NativePdhLibrary.CopyPdhFrame(
                handle,
                expectedSequence,
                enginePointer,
                (uint)engineRows.Length,
                memoryPointer,
                (uint)memoryRows.Length,
                &io);
        }

        systemIo = io;
        return (NativePdhResultCode)result;
    }

    public NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics)
    {
        return (NativePdhResultCode)NativePdhLibrary.GetPdhDiagnostics(handle, ref diagnostics);
    }
}
