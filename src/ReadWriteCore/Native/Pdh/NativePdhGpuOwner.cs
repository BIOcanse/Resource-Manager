using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Pdh;

// Dedicated platform ABI. Every pointer is borrowed for a single synchronous
// operation. Output rows refer to the supplied Ledger region, never native heap.
internal static unsafe partial class NativePdhGpuOwner
{
    static NativePdhGpuOwner() => ReadWriteNativeLibraryResolver.EnsureRegistered();
    [StructLayout(LayoutKind.Sequential)]
    internal struct Identity { internal int Pid; internal uint Reserved; internal long Expected, Current; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        internal ulong Luid, Start;
        internal uint Pid, Physical, Engine, Class, Kind, Reserved;
        internal double Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Size, Version;
        internal NativePdhGpuEngineRow* Engines; internal nuint EngineCount;
        internal NativePdhGpuEngineWindow* Windows; internal nuint WindowCount;
        internal NativePdhGpuMemoryRow* Memory; internal nuint MemoryCount;
        internal Identity* Identities; internal nuint IdentityCount;
        internal uint Flags, Reserved;
        internal static Input Create() => new() { Size = (uint)sizeof(Input), Version = 1 };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Output { internal Row* Rows; internal nuint Count, WorkspaceUsed; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Name { internal char* Text; internal nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Formatted { internal Name Name; internal uint Status, Reserved; internal long Value; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SharedRow { internal ulong Luid; internal uint Pid, Failed; internal ulong Bytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SharedResult
    {
        internal SharedRow* Rows; internal nuint Count;
        internal uint Failures, NativeStatus, Result, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DecoderLimits { internal uint Bytes, Items, NameCharacters, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Adapter { internal ulong Luid; internal int Index; internal uint Software; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryRequest
    {
        internal DecoderLimits Limits;
        internal nuint MaximumWorkspace;
        internal ulong* Requested; internal nuint RequestedCount;
        internal Adapter* Adapters; internal nuint AdapterCount;
        internal uint Process, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryHost
    {
        internal uint Size, Version;
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, nuint*, int> Open;
        internal delegate* unmanaged[Cdecl]<nint, nuint, char*, nuint*, int> Add;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Collect;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, uint*, uint*, byte*, int> Array;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Close;
        internal nint WorkspaceContext;
        internal delegate* unmanaged[Cdecl]<nint, nuint, delegate* unmanaged[Cdecl]<nint, byte*, nuint, uint>, nint, uint> WithRegion;
        internal nint ConsumeContext;
        internal delegate* unmanaged[Cdecl]<nint, SharedResult*, nuint, void> Consume;
        internal static QueryHost Create() => new() { Size = (uint)sizeof(QueryHost), Version = 1 };
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_shared_query")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint SharedQuery(in QueryHost host, in QueryRequest request);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_shared_query_windows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint SharedQueryWindows(in QueryHost host, in QueryRequest request);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_workspace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint Workspace(in Input input);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_project")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Project(in Input input, byte* workspace, nuint length, nuint maximum, out Output result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_parse")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Parse(Name name, uint process, out uint pid, out ulong luid);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_shared")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Shared(Formatted* values, nuint count, ulong* requested, nuint requestedCount,
        uint process, SharedRow* rows, nuint capacity, out SharedResult result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_pdh_gpu_decode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Decode(byte* buffer, uint allocated, uint returned, uint count,
        DecoderLimits limits, Formatted* rows, nuint capacity);
}
