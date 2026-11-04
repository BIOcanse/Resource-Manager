using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.MetricOwner;

// Declarations only. The run scope owns active/work leases and publication
// admission. Native pointers in Round expire on commit, rejection or detach.
public static unsafe partial class MetricOwnerAbi
{
    public const uint Version = 0x0001_0001;
    public const uint None = uint.MaxValue;
    public const uint NoSubscriptionReason = 0xffff_ff01;
    private const string Library = "ResourceManager.NativeCore";

    [LibraryImport(Library, EntryPoint = "rm_metric_owner_required_sizes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RequiredSizes(in MetricOwnerConfig config, out MetricOwnerSizes sizes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_bind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Bind(in MetricOwnerConfig config, byte* active, ulong length, MetricOwnerTableBinding* sources, uint count, in MetricOwnerTableBinding result, out nint owner, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_bind_grouped")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint BindGrouped(in MetricOwnerConfig config, byte* active, ulong length, MetricOwnerSourceBinding* sources, uint count, MetricOwnerTableBinding* resultGroups, uint groupCount, out nint owner, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_replace_catalog_bound")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ReplaceCatalogBound(nint owner, in MetricOwnerCatalog catalog, MetricOwnerSourceBinding* sources, uint count, byte* work, ulong length, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_result_route")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ResultRoute(nint owner, uint metricIndex, out MetricOwnerResultRoute route, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_catalog_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CatalogInput(nint owner, byte* work, ulong length, out MetricOwnerCatalog catalog, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_replace_catalog")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ReplaceCatalog(nint owner, in MetricOwnerCatalog catalog, byte* work, ulong length, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_plan")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Plan(nint owner, uint* metricIndices, uint count, byte* work, ulong length, long utcMs, out MetricOwnerRound round, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_prepare")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Prepare(nint owner, ulong token, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_prepared_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint PreparedView(nint owner, ulong token, out MetricOwnerPrepared prepared, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_commit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Commit(nint owner, ulong token, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_commit_receipt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CommitReceipt(nint owner, ulong token, long utcMs, out MetricOwnerReceipt receipt, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_receipt_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ReceiptView(nint owner, ulong token, out MetricOwnerReceipt* receipt, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_abort_detach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint AbortDetach(nint owner, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_unbind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Unbind(nint owner, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_unbind_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint UnbindAt(nint owner, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_owner_diagnostics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Diagnostics(nint owner, out MetricOwnerDiagnostics diagnostics);
}
public enum MetricOwnerCode : uint { Ok, InvalidContract, StalePlan, StaleIdentity, CapacityExceeded, WorkspaceUnavailable, PayloadCapacity, PublicationRejected, Canceled, SharedSection, Closed }
public enum MetricOwnerFamily : uint { None, Sources, Rules, Metrics, Active, Work, Payload, Generation, Binding }
// Exact existing ValueKind semantics. Presence carries absent/present/null.
public enum MetricOwnerValueKind : uint { Float64 = 1, Signed64, Unsigned64, Unsigned128, Bytes, Boolean }
public enum MetricOwnerObservationStatus : uint { Current = 1, Unavailable, Unsupported, Skipped }
public enum MetricOwnerSourceAvailability : uint { Available = 1, Unavailable, Unsupported }
public enum MetricOwnerRulePosition : uint { Identity, LowBits, HighBits, Kind, Presence, Generation, ObservedMs, DurationMs, PayloadReference, PayloadBytes, Status, Reason, AttemptMs, Quality, CapabilityMask, RuleId }
public enum MetricOwnerResultPosition : uint { Identity, SelectedRuleId, RetainedRuleId, ValueGeneration, Status, Reason, AttemptMs, Generation, RuleIdentity, SourceBindingIndex, SourceLocalRow, PublicStatus }
public enum MetricOwnerSamplingObservationStatus : uint { Current = 1, RetainedLastGood, Unavailable, NotRequested, Unsupported, Partial, Warming }
[Flags]
public enum MetricOwnerObservationFlags : uint { None = 0, CounterPair = 1, TimedCounter = 2, FloatingPair = 4, InvalidateSource = 8, CpuCounters = 16, UnsupportedSource = 32 }
public enum MetricOwnerAccessRegion : uint { Normal = 0, Administrator = 1 }
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerFailure { public MetricOwnerCode Code; public MetricOwnerFamily Family; public ulong Required, Limit; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerConfig
{
    public uint AbiVersion, SourceCapacity, RuleCapacity, MetricCapacity, MaxValueBytes, MaximumFutureSkewMs;
    public ulong ActiveLimit, WorkLimit, PayloadWorkBytes;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerSizes { public ulong Active, Work; public uint Alignment, Reserved; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerTableBinding
{
    public byte* Region; public ulong ByteLength;
    public uint RowsSource, ValueSource, HealthSource, Reserved;
    public fixed uint Positions[16];
    public uint ProvenanceSource, ProvenancePosition;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerSourceBinding
{
    public ulong SourceId;
    public MetricOwnerAccessRegion AccessRegion;
    public uint Reserved;
    public MetricOwnerTableBinding Table;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerResultRoute
{
    public uint Group, Row;
    public ulong MetricId, Identity, CatalogGeneration;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerSourceColumns
{
    public uint Count, Reserved; public ulong* Ids; public ulong* Incarnations; public ulong* Capabilities; public uint* Availability;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerRuleColumns
{
    public uint Count, Reserved;
    public ulong* Ids; public ulong* SourceIds; public ulong* MetricIds; public ulong* Identities;
    public uint* Kinds; public uint* Flags; public uint* Priorities;
    public uint* ValueKinds; public ulong* MinimumBits; public ulong* MaximumBits; public ulong* CapabilityMasks;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerMetricColumns { public uint Count, Reserved; public ulong* Ids; public ulong* Identities; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerCatalog { public MetricOwnerSourceColumns Sources; public MetricOwnerRuleColumns Rules; public MetricOwnerMetricColumns Metrics; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerObservations
{
    public uint Capacity, Reserved;
    public uint* RuleIndices; public uint* Status; public uint* Kinds; public uint* Presence; public uint* Reasons; public uint* Flags;
    public ulong* Low; public ulong* High; public ulong* Reference; public ulong* MonotonicNs; public ulong* TickFrequency; public ulong* DurationMs;
    public long* ObservedMs; public uint* PayloadOffsets; public uint* PayloadLengths;
    public ulong* CapabilityMasks; public uint* Quality;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerQueries
{
    public uint Capacity, Reserved;
    public ulong* RuleIds; public ulong* SourceIds; public ulong* MetricIds; public ulong* Identities;
    public ulong* Incarnations; public ulong* Capabilities; public ulong* CapabilityMasks;
    public uint* ValueKinds; public uint* MetricKinds;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerRound
{
    public ulong Token, CatalogGeneration; public MetricOwnerObservations Observations;
    public uint Count, Reserved; public byte* Payload; public ulong PayloadCapacity;
    public MetricOwnerQueries Queries;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricOwnerDiagnostics
{
    public ulong CatalogGeneration, RoundGeneration, ActiveBytes, WorkBytes, BytesWritten, BytesTouched, BytesRead, PayloadWritten;
    public uint Attached, Phase;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerPrepared
{
    public ulong Token, CatalogGeneration; public uint* MetricIndices; public uint Count, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricOwnerReceipt
{
    public ulong Token, CatalogGeneration, RoundGeneration;
    public long UtcMs;
    public uint* MetricIndices; public ulong* MetricIds;
    public uint AffectedCount, QueryCount;
    public MetricOwnerQueries Queries;
    public uint* InvalidatedAliases; public uint InvalidatedAliasCount, Committed;
}
