using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.MetricOwner;

// Raw declarations only. Discovery policy lives in NativeCore discovery modules.
public static unsafe partial class MetricOwnerDiscoveryAbi
{
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_measure_full")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeMeasureFull(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricDiscoveryRoles roles,
        in MetricVectorScopeTopology topology, MetricDiscoveryVectorAdmission* admission, byte* work, ulong length, ulong inputPrefix,
        ulong learnedText, ulong canonicalReserve, ulong inputBytes, out MetricDiscoveryFullMeasurement result, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_attach_full")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeAttachFull(nint owner, in MetricDiscoveryFullSizes sizes, in MetricDiscoveryRoles roles,
        MetricDiscoveryInventoryBinding* inventory, MetricDiscoveryVectorAdmission* admission, ulong cooldown, out nint context, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_replace_full")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeReplaceFull(nint context, in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricVectorScopeTopology topology,
        MetricDiscoveryProviderInputs* initial, MetricDiscoveryCatalogBinding* catalog, byte* work, ulong length, ulong inputPrefix, long utc, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_topology_input_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeTopologyInputSize(uint count, uint textCapacity, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_topology_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeTopologyInput(uint count, uint textCapacity, byte* work, ulong length, out MetricVectorScopeTopology topology,
        out ulong inputPrefix, out MetricOwnerFailure failure);
    private const string Library = "ResourceManager.NativeCore";
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_catalog_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint CatalogSize(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_compile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Compile(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, byte* work, ulong length, out MetricDiscoveryCompiled compiled, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_selection_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint SelectionSize(in MetricDiscoveryCompiled compiled, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_select")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Select(in MetricDiscoveryCompiled compiled, in MetricDiscoveryReadingFacts facts, in MetricDiscoveryRequest request, byte* work, ulong length, out MetricDiscoverySelection selection, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_inventory_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint InventorySize(in MetricDiscoveryInventoryFacts facts, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_inventory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Inventory(in MetricDiscoveryInventoryFacts facts, in MetricDiscoveryInventoryBinding binding, byte* work, ulong length, out MetricDiscoveryInventory inventory, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_provider_begin")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ProviderBegin(ref MetricDiscoveryProviderState state, long nowMs, out ulong observation, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_provider_complete")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ProviderComplete(ref MetricDiscoveryProviderState state, in MetricDiscoveryProviderEvent completion, out uint availability, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_provider_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ProviderCapture(in MetricDiscoveryProviderState state, long nowMs, out uint availability, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_observe_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint ObserveSize(in MetricDiscoveryCompiled compiled, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_observe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Observe(in MetricDiscoveryCompiled compiled, in MetricDiscoveryReadingFacts prior, in MetricDiscoveryReadingEvents events, byte* work, ulong length, out MetricDiscoveryReadingFacts learned, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_inventory_catalog_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint InventoryCatalogSize(in MetricDiscoveryInventoryCatalogFacts facts, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_inventory_catalog")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint InventoryCatalog(in MetricDiscoveryCompiled compiled, in MetricDiscoveryInventoryCatalogFacts facts, in MetricDiscoveryInventoryBinding binding, byte* work, ulong length, out MetricDiscoveryInventory inventory, out MetricOwnerFailure failure);
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryRef { public uint Offset, Length; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryText { public byte* Bytes; public uint Length, Reserved; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoverySources
{
    public uint Count, Reserved;
    public ulong* Ids; public MetricDiscoveryRef* Names;
    public ulong* Incarnations; public ulong* Capabilities;
    public uint* Availability; public uint* Priorities;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryTemplates
{
    public uint Count, Reserved;
    public MetricDiscoveryRef* Names; public MetricDiscoveryRef* Addresses;
    public ulong* SourceIds; public ulong* Fingerprints;
    public uint* Expansions; public uint* Kinds; public uint* ScopeKinds; public uint* ValueKinds;
    public uint* Flags; public uint* Priorities; public uint* VendorMasks; public uint* Applicability;
    public uint* MaximumInstances; public ulong* Minimum; public ulong* Maximum; public ulong* Masks;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryFields
{
    public uint Count, Reserved;
    public MetricDiscoveryRef* Templates; public MetricDiscoveryRef* Names;
    public uint* Codes; public uint* Resources; public uint* Vendors; public uint* Legacy;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryScopes
{
    public uint Count, Reserved;
    public MetricDiscoveryRef* Keys; public MetricDiscoveryRef* Names;
    public uint* Expansions; public uint* DisplayIndices;
    public ulong* Generations; public ulong* Luids;
    public uint* VendorMasks; public uint* Flags;
    public MetricDiscoveryRef* BindingKeys;
    public uint* ThermalCaps; public uint* RaplDomains; public uint* PowerDomains; public uint* BaseMhz;
    public uint SupportCount, SupportReserved;
    public uint* SupportScopes; public uint* SupportFields;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryHandles
{
    public uint Count, Reserved; public uint* Kinds; public ulong* Ids; public MetricDiscoveryRef* Keys;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryFacts
{
    public MetricDiscoveryText Text; public MetricDiscoverySources Sources; public MetricDiscoveryTemplates Templates;
    public MetricDiscoveryFields Fields; public MetricDiscoveryScopes Scopes; public MetricDiscoveryHandles Handles;
    public uint CpuVendor, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryLimits { public uint Sources, Rules, Metrics, Scopes, Handles, TextBytes; public ulong WorkBytes; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryBindings
{
    public uint Count, Reserved;
    public MetricDiscoveryRef* Addresses; public MetricDiscoveryRef* Legacy; public MetricDiscoveryRef* FieldNames; public MetricDiscoveryRef* DeviceKeys;
    public ulong* ScopeIds; public uint* FieldCodes; public uint* TemplateIndices; public uint* ScopeIndices;
    public uint* SourceIndices; public uint* MetricIndices; public uint* DisplayIndices;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryCompiled
{
    public MetricOwnerCatalog Catalog; public MetricDiscoveryText Text; public MetricDiscoveryBindings Bindings;
    public MetricDiscoveryHandles Handles; public MetricDiscoverySources Sources; public MetricDiscoveryScopes Scopes; public MetricDiscoveryText FactText;
    public MetricDiscoveryLegacy Legacy;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryLegacy
{
    public uint Count, Reserved; public MetricDiscoveryRef* Keys; public uint* MetricIndices; public uint* RuleIndices;
}
[Flags]
public enum MetricDiscoveryRequestFlags : uint { None = 0, All = 1, AllGpuCore = 2, Catalog = 4 }
[Flags]
public enum MetricDiscoveryDatasetMask : uint
{
    None = 0, Catalog = 1, GpuInventory = 2, StorageInventory = 4, FanInventory = 8, BatteryInventory = 16,
    NumaInventory = 32, CpuUsage = 64, MemoryUsage = 128, VirtualMemoryUsage = 256
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryRequest { public MetricDiscoveryText Text; public MetricDiscoveryRef* Keys; public uint Count; public MetricDiscoveryRequestFlags Flags; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryReadingFacts
{
    public uint Count, Reserved; public ulong* Epochs; public uint* Availability; public MetricDiscoveryRef* Quantities;
    public MetricDiscoveryText Text; public MetricDiscoveryRef IntegratedGpu;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryReadingEvents
{
    public uint Count, Reserved;
    public uint* RuleIndices; public ulong* Incarnations; public ulong* Capabilities; public ulong* Observations;
    public uint* Statuses; public uint* Supported; public uint* SourceUnsupported; public MetricDiscoveryRef* Quantities;
    public ulong* CurrentObservations; public MetricDiscoveryText Text; public MetricDiscoveryRef IntegratedGpu;
    public uint IntegratedRule, TailReserved;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoverySelection
{
    public uint MetricCount, RuleCount; public uint* MetricIndices; public ulong* MetricIds;
    public uint* RuleIndices; public uint* SourceOffsets; public uint* PreferredRules; public uint* Availability;
    public uint* SourceRanks; public uint* AliasRanks;
    public MetricDiscoveryRef* Quantities; public MetricDiscoveryRef* ReadingIds;
    public MetricDiscoveryRef* LegacyKeys; public uint* LegacyRules;
    public uint LegacyCount; public MetricDiscoveryDatasetMask DatasetMask; public MetricDiscoveryText Text;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryProviderState
{
    public ulong Incarnation, Capability, Observation; public long UnavailableUntilMs; public uint Unsupported, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryProviderEvent
{
    public ulong Incarnation, Capability, Observation; public long CompletedMs; public ulong CooldownMs; public uint Status, ApplyCooldown;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryInventoryColumns
{
    public uint Count, Reserved;
    public ulong* AdapterIds; public ulong* LuidLow; public ulong* LuidHigh; public ulong* StableKeyIds;
    public ulong* CapabilityMasks; public ulong* Topology; public MetricDiscoveryRef* Keys;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryInventoryFacts
{
    public MetricDiscoveryInventoryColumns Columns; public MetricDiscoveryText Text;
    public ulong SourceId, Generation, Observation; public long ObservedMs; public ulong SourceCapabilityMask;
    public uint Requested, Status, Reason, MaximumCount;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryInventoryCatalogFacts
{
    public MetricDiscoveryInventoryFacts Raw; public ulong InventoryGeneration, TopologyFingerprint;
    public uint ObservedCount, SkippedCount, OverflowCount, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryInventoryBinding
{
    public byte* Region; public ulong Length; public uint Source, Reserved; public fixed uint Positions[14];
}
public enum MetricDiscoveryInventoryPosition : uint
{
    AdapterId, SourceId, Generation, ObservedMs, LuidLow, LuidHigh, StableKeyId,
    CapabilityMask, Topology, ValidMask, Status, Fingerprint, ExactKey, StringTable
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryInventory
{
    public MetricDiscoveryInventoryColumns Columns; public MetricDiscoveryText Text; public ulong* Fingerprints;
    public ulong SourceId, Generation; public long ObservedMs; public uint Status, Published;
}

[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryRuntimeSizes
{
    public ulong ExtraActiveBytes, ExtraOperationWorkBytes, CanonicalBytes, LearnedTextBytes;
    public uint SourceCapacity, RuleCapacity;
    public ulong CompilerWorkBytes, InputBytes;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryProviderInputs
{
    public uint Count, Reserved; public ulong* SourceIds; public MetricDiscoveryProviderState* States;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryMeasurement
{
    public MetricDiscoveryRuntimeSizes Sizes; public MetricDiscoveryCatalogDeclaration Catalog;
    public uint MetricCount, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryCatalogBinding
{
    public byte* Region; public ulong Length; public uint Source, Reserved; public fixed uint Positions[7];
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryCatalogDeclaration
{
    public uint Rows, StringBytes; public fixed uint Widths[7]; public fixed uint Kinds[7];
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoverySourceEvents
{
    public uint Count, Reserved; public uint* SourceIndices; public uint* Statuses;
    public uint* ApplyCooldown; public long* CompletedMs; public ulong* SourceIds; public ulong* Observations;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryDispatch
{
    public uint Count, Reserved; public uint* FieldCodes; public uint* ScopeIndices; public uint* DisplayIndices;
    public ulong* ScopeIds; public MetricDiscoveryRef* FieldNames; public MetricDiscoveryRef* DeviceKeys; public MetricDiscoveryText Text;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryRuntimePlan
{
    public nint Operation; public MetricOwnerRound Raw; public MetricDiscoverySelection Selection;
    public MetricDiscoveryDispatch Dispatch; public MetricDiscoverySourceEvents Sources; public MetricDiscoveryReadingEvents Readings;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoverySourceRole
{
    public ulong SourceId; public uint Flags, RowCapacity, TextCapacity, Reserved;
    public ulong SourceCapabilityMask, InventoryGeneration, TopologyFingerprint;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryRoles { public uint Count, Reserved; public MetricDiscoverySourceRole* Rows; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoverySourceRoleView { public uint Count, Reserved; public uint* Flags; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryInventoryHeader
{
    public ulong Generation, TopologyFingerprint; public long ObservedMs;
    public uint Count, ObservedCount, SkippedCount, OverflowCount, Status, Reason, TextLength, Reserved;
    public ulong QueryToken;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryInventoryInput
{
    public ulong SourceId, Incarnation, Capability, Observation, CatalogGeneration, QueryToken;
    public uint RowCapacity, TextCapacity; public MetricDiscoveryInventoryHeader* Header;
    public ulong* Luids; public MetricDiscoveryRef* Keys; public MetricDiscoveryText Text;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryFullExtra
{
    public ulong TopologyBytes, VectorWorkBytes;
    public uint ScopeCount, TopologyTextBytes, GpuBoundRows, Reserved;
    public ulong GroupingWorkBytes;
}
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryFullSizes { public MetricDiscoveryRuntimeSizes Base; public MetricDiscoveryFullExtra Extra; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryFullMeasurement { public MetricDiscoveryFullSizes Sizes; public MetricDiscoveryCatalogDeclaration Catalog; public uint MetricCount, Reserved; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryVectorAdmission { public MetricVectorBinding Binding; public uint BoundRowCapacity, Reserved; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoveryGpuHeader { public MetricVectorGpuInput Input; public ulong QueryToken; }
[StructLayout(LayoutKind.Sequential)]
public struct MetricDiscoverySensorHeader { public MetricVectorSensorInput Input; public ulong Incarnation, Capability, Observation, QueryToken; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryVectorInputs
{
    public MetricDiscoveryGpuHeader* Gpu; public MetricDiscoverySensorHeader* Sensor;
    public uint GpuCapacity, SensorCapacity, ScopeCount, Reserved;
    public ulong* NpuGenerations; public uint* NpuRowScopes;
    public uint ExcludedCount, RawCount; public uint* ExcludedRows;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricDiscoveryRequestProjection
{
    public MetricVectorBinding Binding;
    public ulong SourceId, CatalogGeneration, MembershipRevision, SelectionRevision;
    public uint SelectedCount, BaseCount; public uint* SelectedRows; public ulong* ExpectedIdentities;
    public uint* BaseMetricIndices; public ulong* BaseMetricIds;
    public uint SuppressedCount, Reserved; public uint* SuppressedMetricIndices; public ulong* SuppressedMetricIds;
}
public static unsafe partial class MetricOwnerDiscoveryAbi
{
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_project_request_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeProjectRequestSize(nint context, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_project_request")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeProjectRequest(nint context, in MetricDiscoveryRequest request, byte* work, ulong length,
        ulong inputPrefix, out MetricDiscoveryRequestProjection projection, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_sdk_dispatch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeSdkDispatch(nint context, nint operation, out MetricSdkDispatch dispatch, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_vector_inputs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeVectorInputs(nint context, nint operation, out MetricDiscoveryVectorInputs inputs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_vector_publication")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeVectorPublication(nint context, nint operation, out nint publication, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_measure_roles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeMeasureRoles(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricDiscoveryRoles roles,
        byte* work, ulong length, ulong inputPrefixBytes, ulong learnedTextBytes, ulong canonicalReserve, ulong inputBytes,
        out MetricDiscoveryMeasurement measurement, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_attach_roles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeAttachRoles(nint owner, in MetricDiscoveryRuntimeSizes sizes, in MetricDiscoveryRoles roles,
        MetricDiscoveryInventoryBinding* inventory, ulong cooldownMs, out nint context, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_inventory_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeInventoryView(nint context, nint operation, out MetricDiscoveryInventoryInput input, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_source_roles_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeSourceRolesView(nint context, nint operation, out MetricDiscoverySourceRoleView roles, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_measure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeMeasure(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, byte* work,
        ulong length, ulong inputPrefixBytes, ulong learnedTextBytes, ulong canonicalReserve, ulong inputBytes,
        out MetricDiscoveryMeasurement measurement, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_sizes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeSizes(in MetricDiscoveryCompiled candidate, ulong compilerWorkBytes, ulong learnedTextBytes, ulong canonicalReserve, ulong inputBytes, out MetricDiscoveryRuntimeSizes sizes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_catalog_declaration")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeCatalogDeclaration(in MetricDiscoveryCompiled candidate, out MetricDiscoveryCatalogDeclaration declaration, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_bind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeBind(nint owner, byte* tail, ulong length, in MetricDiscoveryRuntimeSizes sizes, ulong cooldownMs, out nint context, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_attach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeAttach(nint owner, in MetricDiscoveryRuntimeSizes sizes, ulong cooldownMs, out nint context, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_replace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeReplace(nint context, in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, MetricDiscoveryProviderInputs* initialProviders, MetricDiscoveryCatalogBinding* catalogBinding, byte* work, ulong length, ulong inputPrefixBytes, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_plan")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimePlan(nint context, in MetricDiscoveryRequest request, byte* work, ulong length, ulong inputPrefixBytes, long utcMs, out MetricDiscoveryRuntimePlan plan, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_prepare")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimePrepare(nint context, nint operation, in MetricDiscoveryReadingEvents readings, in MetricDiscoverySourceEvents sources, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_commit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeCommit(nint context, nint operation, MetricOwnerHistoryBinding* history, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_commit_grouped")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeCommitGrouped(nint context, nint operation, MetricOwnerHistoryBinding* history, uint count, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_commit_mixed")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RuntimeCommitMixed(nint context, nint operation, MetricOwnerHistoryBinding* scalar, uint scalarCount,
        MetricOwnerMixedHistoryBinding* mixed, uint mixedCount, long utcMs, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_detach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void RuntimeDetach(nint context);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_detach_operation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void RuntimeDetachOperation(nint context, nint operation);
    [LibraryImport(Library, EntryPoint = "rm_metric_discovery_runtime_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void RuntimeClose(nint context);
}
