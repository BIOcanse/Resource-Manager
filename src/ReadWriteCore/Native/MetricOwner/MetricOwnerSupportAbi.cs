using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.MetricOwner;

// Declarations only. The caller owns mapping/work lifetimes and admission.
public static unsafe partial class MetricOwnerSupportAbi
{
    public const uint CheckpointVersion = 1;
    public const uint CheckpointLayoutFlags = 1; // source access-region fields are present
    public const uint MixedHistoryVersion = 1;
    public const uint KernelHistoryKind = 1, GpuEngineHistoryKind = 2;
    private const string Library = "ResourceManager.NativeCore";

    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_work_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial ulong HistoryWorkSize(uint requirements, uint samples);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_mixed_work_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial ulong HistoryMixedWorkSize(uint requirements, uint samples, uint textBytes);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_configure_mixed")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryConfigureMixed(nint owner, in MetricOwnerMixedHistoryBinding binding, MetricOwnerMixedHistoryRequirement* requirements, uint count, in MetricVectorText text, byte* work, ulong length, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_commit_mixed")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryCommitMixed(nint owner, MetricOwnerHistoryBinding* scalarBindings, uint scalarCount, MetricOwnerMixedHistoryBinding* mixedBindings, uint mixedCount, uint* completed, uint count, ulong token, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_gpu_location")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryGpuLocation(nint owner, in MetricVectorText text, MetricVectorRef address, out MetricOwnerGpuHistoryLocation location, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_gpu_prepared_location")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryGpuPreparedLocation(nint owner, ulong token, in MetricVectorText text, MetricVectorRef address, out MetricOwnerGpuHistoryLocation definition, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_configure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryConfigure(nint owner, in MetricOwnerHistoryBinding binding, MetricOwnerHistoryRequirement* requirements, uint count, byte* work, ulong length, long utcMs, out MetricOwnerFailure failure);
    // Called INSIDE the same publication permission as ordinary Commit.
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_commit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryCommit(nint owner, in MetricOwnerHistoryBinding binding, uint* completed, uint count, ulong token, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_history_commit_grouped")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint HistoryCommitGrouped(nint owner, MetricOwnerHistoryBinding* bindings, uint bindingCount, uint* completed, uint count, ulong token, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_checkpoint_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CheckpointSize(nint owner, out ulong imageSize, out ulong restoreWorkSize, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_checkpoint_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CheckpointEncode(nint owner, byte* destination, ulong capacity, out ulong written, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_checkpoint_prepare")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CheckpointPrepare(nint owner, byte* image, ulong imageLength, byte* work, ulong workLength, out MetricOwnerCheckpointPrepared prepared, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_checkpoint_publish")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CheckpointPublish(nint owner, ref MetricOwnerCheckpointPrepared prepared, long utcMs, out MetricOwnerFailure failure);
}

[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerHistoryRequirement
{
    public ulong MetricId;
    public uint Rounds, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerMixedHistoryRequirement
{
    public uint Kind, Rounds;
    public ulong Id;
    public MetricVectorRef Address;
    public ulong Identity;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerMixedHistoryBinding
{
    public MetricOwnerHistoryBinding Base;
    public uint KindPosition, AddressPosition, TextPosition, LivePosition;
    public uint Version, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerGpuHistoryLocation
{
    public ulong SourceId, Identity, ColumnIdentitySequence;
    public uint Row, PublicStatus;
    public long AttemptUnixMs;
    public ulong NumericBits;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerHistoryBinding
{
    public byte* Region;
    public ulong ByteLength;
    public uint RowsSource, DataSource;
    public fixed uint Positions[9];
    public uint Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerHistorySample
{
    public long TimeUnixMs;
    public ulong NumericBits;
    public uint Present, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerCheckpointPrepared
{
    public byte* Work;
    public ulong WorkLength, ImageLength, CatalogGeneration, RoundGeneration, Token, Checksum;
}
