using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.MetricOwner;

// Declarations only. Every returned pointer borrows the SAME local operation
// work lease through accepted synchronous dispatch. No callbacks or ownership.
public static unsafe partial class MetricOwnerVectorAbi
{
    public const uint Version = MetricOwnerAbi.Version;
    private const string Library = "ResourceManager.NativeCore";
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_field_code")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint FieldCode(uint slot);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_npu_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint NpuSize(nint owner, in MetricVectorBindings bindings, in MetricVectorScopeTopology topology, uint* requested, uint count, ulong sourceId, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_npu_expand")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint NpuExpand(nint owner, in MetricVectorBindings bindings, in MetricVectorScopeTopology topology, uint* requested, uint count, ulong sourceId, byte* work, ulong length, out MetricVectorNpuPlan plan, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_npu_attach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint NpuAttach(nint owner, ulong token, ulong expectedSourceObservation, ref MetricVectorNpuPlan plan, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_gpu_expand_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GpuExpandSize(nint owner, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_gpu_expand")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GpuExpand(nint owner, in MetricVectorBindings bindings, in MetricVectorScopeTopology topology, in MetricVectorRequest request, uint* initial, uint count, byte* work, ulong length, out MetricVectorSelection selection, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_gpu_bind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GpuBind(nint owner, in MetricVectorBinding binding, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_gpu_work_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GpuWorkSize(nint owner, uint rawRows, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_gpu_prepare")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GpuPrepare(nint owner, ulong token, in MetricVectorBindings bindings, in MetricVectorScopeTopology topology, in MetricVectorRequest request, in MetricVectorGpuInput input, byte* work, ulong length, out MetricVectorGpuPublication* publication, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_vector_notebook_apply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint NotebookApply(nint owner, ulong token, in MetricVectorBindings bindings, in MetricVectorScopeTopology topology, in MetricVectorSensorInput input, out MetricOwnerFailure failure);
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricVectorRef { public uint Offset, Length; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorText { public byte* Bytes; public uint Length, Reserved; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorBindings
{
    public uint Count, Reserved; public ulong CatalogGeneration;
    public uint* FieldCodes; public uint* ScopeIndices; public uint* MetricIndices;
    public ulong* ScopeIds; public MetricVectorRef* Addresses; public MetricVectorRef* DeviceKeys;
    public MetricVectorText Text;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorScopeTopology
{
    public uint Count, Reserved; public MetricVectorText Text;
    public MetricVectorRef* Keys; public MetricVectorRef* Parents; public MetricVectorRef* Pnp; public MetricVectorRef* PnpDrivers;
    public ulong* DirectoryLow; public ulong* DirectoryHigh; public ulong* Luids; public ulong* Drivers; public ulong* Generations;
    public uint* Vendors; public uint* Devices; public uint* EngineIndices; public uint* EngineCounts; public uint* DriverPresent; public uint* Flags; public uint* Expansions;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorRequest { public uint Flags, Count; public MetricVectorText Text; public MetricVectorRef* Addresses; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorNpuPlan
{
    public byte* Base; public ulong Length, CatalogGeneration, Token, SourceId;
    public uint TargetCount, EngineCount, MetricCount, OriginalCount, ScopeCount, Reserved;
    public uint* Targets; public uint* Offsets; public uint* Engines; public uint* Metrics; public uint* Originals; public uint* Complete;
    public ulong* SampleGenerations; public uint* TargetObservations;
    public ulong* TargetIds; public ulong* TargetIdentities; public ulong* EngineIds; public ulong* EngineIdentities;
    public uint* EngineScopes; public ulong ExpectedGeneration;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorBinding
{
    public byte* Region; public ulong ByteLength, SourceId;
    public uint RowsSource, ValueSource, HealthSource, Reserved;
    public fixed uint Positions[21];
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorGpuInput
{
    public uint Count, Reserved; public ulong SourceId, Incarnation, Capability, Observation;
    public long ObservedMs; public uint Status, Reason; public ulong ValidMask;
    public ulong* Luids; public uint* Physical; public uint* Engines; public uint* Classes; public double* Usage;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorSensorInput
{
    public ulong SourceId; public long ObservedMs; public uint Status, Reason;
    public double CpuRpm, CpuPercent; public uint CpuValid, Count;
    public uint* Indices; public double* Rpm; public double* Percent; public uint* Valid;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorSelection { public uint Count, Reserved; public uint* MetricIndices; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricVectorGpuPublication
{
    public nint Handle; public ulong Token; public uint Rows, SelectedCount;
    public uint* SelectedRows; public MetricVectorRef* Addresses; public MetricVectorText Text;
    public uint Published, Reserved;
    public uint* SuppressedMetrics; public uint SuppressedCount, TailReserved;
}
public enum MetricVectorPosition : uint
{
    Identity, AdapterLuid, PhysicalIndex, EngineIndex, EngineClass, UsageBits, Kind, Presence,
    ValueGeneration, ObservedMs, RawStatus, Reason, AttemptMs, AddressReference, TextBytes,
    DeviceKeyReference, TriggerMetricId, ScopeIndex, ScopeIdentity, SourceObservation, PublicStatus
}
