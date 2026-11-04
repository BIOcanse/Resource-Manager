using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// All regions and spans are borrowed for the synchronous operation. The caller
// owns the registered Ledger resource and the immutable metadata references;
// this binding contains no authority, collection, allocation or lease field.
internal static unsafe partial class NativeKernelLeakItems
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Key
    {
        internal int Pid;
        internal uint Reserved;
        internal ulong Birth;
        internal fixed byte Text[20];
        internal uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        internal int Pid;
        internal uint State;
        internal ulong Birth;
        internal fixed byte Text[20];
        internal uint Length, Pad;
        internal ulong Current, Growth;
        internal double Seconds, Rate;
        internal uint SampleCount, Reserved;
        internal long First, Last;
        internal nuint Payload;
        internal int FirstOffsetMinutes, LastOffsetMinutes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PlanResult
    {
        internal uint Count, Unrecorded, RecordedSuspects, Changed;
        internal nuint RequiredBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Text
    {
        internal nint Pointer;
        internal nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Metadata
    {
        internal uint ImageStatus, ServicesStatus;
        internal Text Name, Path, Product;
        internal Text* Names;
        internal nuint NamesCount;
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_item_metadata_valid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint MetadataValid(in Metadata metadata);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint Size(uint maximum);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Initialize(nint region, nuint length, uint maximum);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_reserve")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Reserve(nint region, nuint length);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_restore")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Restore(nint region, Row* rows, Metadata* metadata, nuint count, out nuint requiredBytes);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_plan")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Plan(nint region, long ticks, Key* live, nuint liveCount,
        Key* unavailable, nuint unavailableCount, Key* judged, nuint judgedCount,
        Row* suspects, Metadata* metadata, nuint suspectCount, uint capacity, out PlanResult result);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_clear")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Clear(nint region, out PlanResult result);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Snapshot(nint region, uint pending, out Row* rows, out nuint count);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_commit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Commit(nint region);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_metadata")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReadMetadata(nint region, uint pending, nuint index, out Metadata metadata);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_items_service")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReadService(nint region, uint pending, nuint index, nuint nameIndex, out Text name);
}
