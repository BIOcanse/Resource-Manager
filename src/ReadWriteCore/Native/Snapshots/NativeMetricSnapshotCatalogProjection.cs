using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using System.Collections.Immutable;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.App.Infrastructure.Monitoring;

public readonly record struct NativeMetricSnapshotGpuCatalogIdentity(
    int DisplayIndex,
    string DisplayName,
    string ExactIdentityKey,
    ulong AdapterLuid,
    ulong InventoryGeneration,
    uint VendorMask,
    bool SupportsDedicatedMetrics)
{
    public IReadOnlyList<string> PhysicalPnpInstanceIds { get; init; } = [];
}

public readonly record struct NativeMetricSnapshotOrdinalCatalogIdentity(
    int DisplayIndex,
    string ExactIdentityKey,
    ulong TopologyGeneration)
{
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
    public bool SupportsNvmeHealth { get; init; }
    public bool SupportsAtaSmart { get; init; }
    public NotebookOemPhysicalFanRole? NotebookOemRole { get; init; }
    public NvidiaNvmlFanIdentity? NvmlFan { get; init; }
    public IntelGclFanIdentity? IgclFan { get; init; }
}

public readonly record struct NativeMetricSnapshotGpuScopeBinding(
    int DisplayIndex,
    string DisplayName,
    ulong ScopeHandle,
    ulong AdapterLuid,
    ulong InventoryGeneration,
    string ExactIdentityKey,
    bool SupportsDedicatedMetrics)
{
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
}

public readonly record struct NativeMetricSnapshotRuleCatalogBinding(
    ulong RuleHandle,
    ulong MetricHandle,
    ulong SourceHandle,
    ulong ScopeHandle,
    string MetricId,
    string SourceId,
    uint MetricKind,
    uint ValueKind,
    ulong CapabilityMask,
    uint MetricFlags,
    ulong MinimumValueBits,
    ulong MaximumValueBits)
{
    public MetricFieldDefinition? Field { get; init; }
    public MetricFieldCode FieldCode => Field?.Code ?? MetricFieldCode.Unknown;
    public string DeviceIdentity { get; init; } = string.Empty;
    public int DisplayIndex { get; init; } = -1;
    public string LegacyMetricId { get; init; } = string.Empty;
    public NvidiaNvmlFanIdentity? NvmlFan { get; init; }
    public IntelGclFanIdentity? IgclFan { get; init; }
    public NpuEngineCatalogIdentity? NpuEngine { get; init; }
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
}

public sealed record NativeMetricSnapshotCatalogProjection(
    ImmutableArray<NativeMetricSnapshotSourcePolicyInput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricDefinitionInput> Rules,
    ImmutableDictionary<ulong, string> MetricIds,
    ImmutableDictionary<string, ulong> MetricHandles,
    ImmutableDictionary<ulong, string> SourceIds,
    ImmutableDictionary<string, ulong> SourceHandles,
    ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding>
        RuleBindings,
    ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>
        GpuScopeBindings,
    NativeMetricSnapshotCatalogHandleMap HandleMap,
    ulong NativeRowFingerprint,
    string CatalogIdentitySha256)
{
    public ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding> MetricBindings { get; init; } =
        ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding>.Empty;
    public ImmutableDictionary<string, ulong> LegacyMetricHandles { get; init; } =
        ImmutableDictionary<string, ulong>.Empty;
    public ImmutableArray<NativeMetricSnapshotOrdinalCatalogIdentity> StorageIdentities { get; init; } = [];
    public uint IgclFanEffectiveMaximumInstanceCount { get; init; }
    public ImmutableArray<NativeMetricSnapshotOrdinalCatalogIdentity> SystemFanIdentities { get; init; } = [];
    public ImmutableArray<BatteryCatalogIdentity> BatteryIdentities { get; init; } = [];
    public ImmutableArray<NetworkInterfaceCatalogIdentity> NetworkInterfaceIdentities { get; init; } = [];
    public ImmutableArray<NumaMemoryCatalogIdentity> NumaIdentities { get; init; } = [];
    public ImmutableArray<NpuEngineCatalogIdentity> NpuEngineIdentities { get; init; } = [];
    public IWindowsNpuEngineDirectory? NpuEngineDirectory { get; init; }
    public uint NpuEngineEffectiveMaximumInstanceCount { get; init; }
    public ImmutableArray<NpuMemoryCatalogIdentity> NpuMemoryIdentities { get; init; } = [];
    public uint NpuMemoryEffectiveMaximumInstanceCount { get; init; }
    public ImmutableArray<IntelMsrCatalogIdentity> IntelCpuIdentities { get; init; } = [];
    public static NativeMetricSnapshotCatalogProjection Create(
        NativeMetricSnapshotCatalogConfiguration plan,
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> gpuAdapters,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> storageSensors,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> systemFans,
        NativeMetricSnapshotCatalogHandleMap handleMap,
        MetricVendor cpuVendor = MetricVendor.All,
        IReadOnlyList<BatteryCatalogIdentity>? batteries = null,
        IReadOnlyList<NumaMemoryCatalogIdentity>? numaNodes = null,
        IReadOnlyList<IntelMsrCatalogIdentity>? intelCpus = null,
        IReadOnlyList<NetworkInterfaceCatalogIdentity>? networkInterfaces = null,
        IReadOnlyList<NpuMemoryCatalogIdentity>? npuMemoryIdentities = null,
        IReadOnlyList<NpuEngineCatalogIdentity>? npuEngineIdentities = null, IWindowsNpuEngineDirectory? npuEngineDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gpuAdapters);
        ArgumentNullException.ThrowIfNull(storageSensors);
        ArgumentNullException.ThrowIfNull(systemFans);
        ArgumentNullException.ThrowIfNull(handleMap);
        if (!plan.IsPublished)
        {
            throw new ArgumentException(
                "The metric-snapshot plan must be published.",
                nameof(plan));
        }

        ValidateGpuIdentities(
            gpuAdapters,
            plan.MaximumGpuAdapterCount);
        ValidateOrdinalIdentities(storageSensors, "storage sensor");
        ValidateOrdinalIdentities(systemFans, "system fan");
        if (systemFans.Count(static identity => identity.NvmlFan is not null) > NvidiaNvmlReader.MaxFanCount
            || systemFans.Any(static identity => identity.NvmlFan is { FanIndex: >= NvidiaNvmlReader.MaxFansPerDevice }))
            throw new InvalidOperationException("The NVML fan catalog exceeds its source capacity.");
        if (systemFans.Where(static identity => identity.NotebookOemRole is not null)
                .GroupBy(static identity => identity.NotebookOemRole)
                .Any(static group => group.Count() != 1)
            || systemFans.Any(static identity => identity.NotebookOemRole is not null && identity.NvmlFan is not null)
            || systemFans.Any(static identity => identity.NvmlFan is { } fan && identity.ExactIdentityKey != fan.ExactIdentityKey)
            || storageSensors.Any(static identity => identity.NotebookOemRole is not null || identity.NvmlFan is not null))
        {
            throw new InvalidOperationException("The OEM fan role identities are invalid.");
        }
        var intelFanMaximum = WindowsIntelFanTopology.EffectiveMaximumInstanceCount(plan.RuleTemplates);
        if (systemFans.Count(static fan => fan.IgclFan is not null) > intelFanMaximum
            || systemFans.Any(static fan => fan.IgclFan is { } target &&
                (target.DirectoryId == Guid.Empty || fan.ExactIdentityKey != target.ExactIdentityKey
                    || fan.NvmlFan is not null || fan.NotebookOemRole is not null))
            || storageSensors.Any(static fan => fan.IgclFan is not null))
            throw new InvalidOperationException("The Intel fan identities exceed or do not match the compiled directory.");
        batteries ??= [];
        numaNodes ??= [];
        intelCpus ??= [];
        networkInterfaces ??= [];
        npuMemoryIdentities ??= [];
        npuEngineIdentities ??= [];
        npuEngineDirectory ??= npuEngineIdentities.FirstOrDefault()?.Directory;
        var npuEngineMaximumInstanceCount = WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(plan.RuleTemplates);
        if (npuEngineIdentities.Count > npuEngineMaximumInstanceCount
            || npuEngineIdentities.Any(identity => identity.DirectoryId == Guid.Empty || !IsExactIdentityKey(identity.ExactIdentityKey)
                || !ReferenceEquals(identity.Directory, npuEngineDirectory))
            || npuEngineIdentities.Select(static identity => identity.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != npuEngineIdentities.Count
            || (npuEngineDirectory is not null && (npuEngineDirectory.Identities.Count != npuEngineIdentities.Count
                || !npuEngineDirectory.Identities.ToHashSet().SetEquals(npuEngineIdentities))))
            throw new InvalidOperationException("The NPU engine identities must describe the entire retained bounded directory.");
        var npuMemoryMaximumInstanceCount = WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(plan.RuleTemplates);
        if (npuMemoryIdentities.Count > npuMemoryMaximumInstanceCount
            || npuMemoryIdentities.Any(static identity => string.IsNullOrWhiteSpace(identity.PnpInstanceId)
                || !IsExactIdentityKey(identity.ExactIdentityKey))
            || npuMemoryIdentities.Select(static identity => identity.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != npuMemoryIdentities.Count)
            throw new InvalidOperationException("The NPU memory identities are invalid or exceed the bounded catalog.");
        if (networkInterfaces.Any(static row => row.DisplayIndex < 0 || row.Luid == 0 || row.Guid == Guid.Empty)
            || networkInterfaces.Select(static row => row.DisplayIndex).Distinct().Count() != networkInterfaces.Count
            || networkInterfaces.Select(static row => row.Luid).Distinct().Count() != networkInterfaces.Count
            || networkInterfaces.Select(static row => row.Guid).Distinct().Count() != networkInterfaces.Count)
            throw new InvalidOperationException("The network interface identities are invalid.");
        if (intelCpus.Any(static cpu => cpu.DisplayIndex < 0 || cpu.Processor.GroupRelativeIndex >= 64
                || !Enum.IsDefined(cpu.Kind) || !IsExactIdentityKey(cpu.ExactIdentityKey))
            || intelCpus.Select(static cpu => cpu.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != intelCpus.Count
            || intelCpus.Select(static cpu => (cpu.Kind, cpu.DisplayIndex)).Distinct().Count() != intelCpus.Count)
            throw new InvalidOperationException("The Intel MSR CPU identities are invalid.");
        if (intelCpus.Count(static cpu => cpu.IsPlatform) > 1
            || intelCpus.Any(static cpu => cpu.IsPlatform && cpu.DisplayIndex != 0))
            throw new InvalidOperationException("The Intel platform identity must be unique at index zero.");
        if (numaNodes.Select(static node => node.NodeNumber).Distinct().Count() != numaNodes.Count)
            throw new InvalidOperationException("The metric-snapshot NUMA identities are invalid.");
        if (batteries.Count > 32 || batteries.Any(battery => battery.DisplayIndex < 0
                || string.IsNullOrWhiteSpace(battery.DevicePath) || string.IsNullOrWhiteSpace(battery.UniqueId)
                || !IsExactIdentityKey(battery.ExactIdentityKey))
            || batteries.Select(static battery => battery.DisplayIndex).Distinct().Count() != batteries.Count
            || batteries.Select(static battery => battery.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != batteries.Count)
            throw new InvalidOperationException("The metric-snapshot battery identities are invalid.");
        var allocator = handleMap.CreateAllocator();
        foreach (var adapter in gpuAdapters.OrderBy(
                     static adapter => adapter.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"gpu|{adapter.ExactIdentityKey}");
        }
        foreach (var identity in storageSensors.OrderBy(
                     static identity => identity.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"storage|{identity.ExactIdentityKey}");
        }
        foreach (var identity in systemFans.OrderBy(
                     static identity => identity.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"fan|{identity.ExactIdentityKey}");
        }
        var resolvedGpuAdapters = gpuAdapters.OrderBy(static adapter => adapter.DisplayIndex)
            .Select((adapter, ordinal) => new ResolvedGpuIdentity(
                adapter.DisplayIndex,
                adapter.DisplayName,
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Scope,
                    $"gpu|{adapter.ExactIdentityKey}"),
                adapter.ExactIdentityKey,
                adapter.AdapterLuid,
                adapter.InventoryGeneration,
                adapter.VendorMask,
                adapter.SupportsDedicatedMetrics,
                new MetricDeviceDescriptor("gpu_adapter",
                    MetricDeviceAddress.GpuAdapter(adapter.AdapterLuid, adapter.PhysicalPnpInstanceIds),
                    CleanDisplayName(adapter.DisplayName), ordinal)))
            .ToArray();
        var resolvedStorageSensors = storageSensors.OrderBy(static identity => identity.DisplayIndex)
            .Select((identity, ordinal) => ResolveOrdinalIdentity(
                allocator,
                identity,
                "storage",
                ordinal))
            .ToArray();
        var resolvedSystemFans = systemFans.OrderBy(static identity => identity.DisplayIndex)
            .Select((identity, ordinal) => ResolveOrdinalIdentity(
                allocator,
                identity,
                "fan",
                ordinal))
            .ToArray();

        var resolvedBatteries = batteries.OrderBy(static battery => battery.DisplayIndex)
            .Select((battery, ordinal) => new ResolvedBatteryIdentity(battery.DisplayIndex,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"battery|{battery.ExactIdentityKey}"),
                battery.ExactIdentityKey, battery.Information.IsRelative,
                new MetricDeviceDescriptor("battery", MetricDeviceAddress.Battery(battery.DevicePath, battery.UniqueId), null,
                    ordinal))).ToArray();
        var resolvedNumaNodes = numaNodes.OrderBy(static node => node.NodeNumber)
            .Select((node, ordinal) => new ResolvedNumaIdentity(node.NodeNumber,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"numa|{node.ExactIdentityKey}"),
                node.ExactIdentityKey, node.NodeNumber,
                new MetricDeviceDescriptor("numa_node", MetricDeviceAddress.NumaNode(node.NodeNumber), null,
                    ordinal))).ToArray();
        var intelOrdinals = intelCpus.GroupBy(static cpu => cpu.Kind)
            .SelectMany(group => group.OrderBy(static cpu => cpu.DisplayIndex)
                .ThenBy(static cpu => cpu.ExactIdentityKey, StringComparer.Ordinal)
                .Select((cpu, ordinal) => (cpu.ExactIdentityKey, Ordinal: ordinal)))
            .ToDictionary(static row => row.ExactIdentityKey, static row => row.Ordinal, StringComparer.Ordinal);
        var resolvedIntelCpus = intelCpus.OrderBy(static cpu => cpu.Kind)
            .ThenBy(static cpu => cpu.DisplayIndex)
            .ThenBy(static cpu => cpu.ExactIdentityKey, StringComparer.Ordinal)
            .Select(cpu => new ResolvedIntelCpuIdentity(cpu.DisplayIndex,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"intel-cpu|{cpu.ExactIdentityKey}"),
                cpu.ExactIdentityKey, cpu, CreateIntelDevice(cpu, intelOrdinals[cpu.ExactIdentityKey]))).ToArray();
        var resolvedNetworkInterfaces = networkInterfaces.OrderBy(static row => row.DisplayIndex)
            .Select((row, ordinal) => new ResolvedNetworkInterfaceIdentity(row.DisplayIndex,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"network-interface|{row.ExactIdentityKey}"),
                row.ExactIdentityKey, row.BindingKey,
                new MetricDeviceDescriptor("network_interface",
                    MetricDeviceAddress.NetworkInterface(row.Guid, row.Luid, row.Index), CleanDisplayName(row.NameDeclared),
                    ordinal))).ToArray();
        var resolvedNpus = npuMemoryIdentities.OrderBy(static row => row.ExactIdentityKey, StringComparer.Ordinal)
            .Select((row, index) => new ResolvedNpuIdentity(index,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"npu|{row.ExactIdentityKey}"),
                row.ExactIdentityKey, row.PnpInstanceId,
                new MetricDeviceDescriptor("npu", MetricDeviceAddress.Npu(row.PnpInstanceId),
                    CleanDisplayName(row.NameDeclared), index))).ToArray();
        var resolvedNpuEngines = npuEngineIdentities.OrderBy(static row => row.ExactIdentityKey, StringComparer.Ordinal)
            .Select((row, index) => new ResolvedNpuEngineIdentity(index,
                allocator.GetOrAdd(NativeMetricSnapshotCatalogHandleKind.Scope, $"npu-engine|{row.ExactIdentityKey}"),
                row.ExactIdentityKey, row, row.PnpInstanceId,
                new MetricDeviceDescriptor("npu_engine",
                    MetricDeviceAddress.NpuEngine(row.PnpInstanceId, row.PhysicalAdapterIndex, row.EngineIndex),
                    CleanDisplayName(row.NameDeclared), index))).ToArray();
        var sources = plan.SourcePolicies
            .OrderBy(static policy => policy.SourceHandle)
            .Select(static policy => new NativeMetricSnapshotSourcePolicyInput
            {
                StructSize = SizeOf<NativeMetricSnapshotSourcePolicyInput>(),
                Flags = policy.Required
                    ? (uint)NativeMetricSnapshotSourceFlags.Required
                    : 0U,
                SourceHandle = policy.SourceHandle,
                SourceRole = policy.SourceRole,
                Priority = policy.Priority,
                RetentionPolicy = policy.RetentionPolicy,
                ReservedU32 = 0,
                CapabilityMask = policy.CapabilityMask,
                SemanticFingerprint = policy.SemanticFingerprint
            })
            .ToImmutableArray();
        var expanded = plan.RuleTemplates
            .SelectMany(template => ExpandTemplate(
                template,
                resolvedGpuAdapters,
                resolvedStorageSensors,
                resolvedSystemFans,
                resolvedBatteries,
                resolvedNumaNodes, resolvedIntelCpus, resolvedNetworkInterfaces, resolvedNpus, resolvedNpuEngines))
            .Where(rule => rule.Template.ScopeExpansion is 10 or 11 or 12 or 13 || rule.Field?.Resource != MetricResource.Cpu || cpuVendor == MetricVendor.All
                || (rule.Field.Vendor is MetricVendor.All || rule.Field.Vendor == cpuVendor)
                    && (rule.Template.SourceId != "amd.smu" || cpuVendor == MetricVendor.Amd))
            .ToArray();
        if (expanded.Length == 0
            || expanded.Length > plan.MaximumRuleCount)
        {
            throw new InvalidOperationException(
                "The expanded metric-snapshot catalog has an invalid rule count.");
        }
        ValidateMetricIdentities(expanded);
        var allocated = expanded.Select(rule => new AllocatedRule(
                rule,
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Metric,
                rule.StableMetricKey),
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Rule,
                    rule.StableRuleKey)))
            .OrderBy(static rule => rule.MetricHandle)
            .ThenBy(static rule => rule.Rule.Template.SourcePriority)
            .ThenBy(static rule => rule.RuleHandle)
            .ToArray();
        var finalHandleMap = allocator.Freeze();

        var metricIds = ImmutableDictionary.CreateBuilder<ulong, string>();
        var metricHandles =
            ImmutableDictionary.CreateBuilder<string, ulong>(
                StringComparer.OrdinalIgnoreCase);
        var sourceIds = plan.SourcePolicies
            .ToImmutableDictionary(
                static policy => policy.SourceHandle,
                static policy => policy.SourceId);
        var sourceHandles = plan.SourcePolicies
            .ToImmutableDictionary(
                static policy => policy.SourceId,
                static policy => policy.SourceHandle,
                StringComparer.Ordinal);
        var ruleBindings = ImmutableDictionary.CreateBuilder<
            ulong,
            NativeMetricSnapshotRuleCatalogBinding>();
        var rules = ImmutableArray.CreateBuilder<
            NativeMetricSnapshotMetricDefinitionInput>(expanded.Length);
        foreach (var allocatedRule in allocated)
        {
            var rule = allocatedRule.Rule;
            if (metricIds.TryGetValue(
                    allocatedRule.MetricHandle,
                    out var existingMetricId))
            {
                if (!string.Equals(
                    existingMetricId,
                    rule.MetricId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A stable metric handle resolved to two display metric identifiers.");
                }
            }
            else
            {
                metricIds.Add(
                    allocatedRule.MetricHandle,
                    rule.MetricId);
            }
            if (metricHandles.TryGetValue(
                    rule.MetricId,
                    out var existingMetricHandle))
            {
                if (existingMetricHandle != allocatedRule.MetricHandle)
                {
                    throw new InvalidOperationException(
                        "A display metric identifier resolved to multiple stable metric handles.");
                }
            }
            else
            {
                metricHandles.Add(
                    rule.MetricId,
                    allocatedRule.MetricHandle);
            }
            ruleBindings.Add(
                allocatedRule.RuleHandle,
                new NativeMetricSnapshotRuleCatalogBinding(
                    allocatedRule.RuleHandle,
                    allocatedRule.MetricHandle,
                    rule.Template.SourceHandle,
                    rule.ScopeHandle,
                    rule.MetricId,
                     rule.Template.SourceId,
                     rule.Template.MetricKind,
                     rule.Template.ValueKind,
                     rule.Template.CapabilityMask,
                     rule.Template.MetricFlags,
                     rule.Template.MinimumValueBits,
                     rule.Template.MaximumValueBits)
                {
                    Field = rule.Field,
                    DeviceIdentity = rule.DeviceIdentity,
                    DisplayIndex = rule.DisplayIndex,
                    LegacyMetricId = rule.LegacyMetricId,
                    NvmlFan = rule.NvmlFan, IgclFan = rule.IgclFan, NpuEngine = rule.NpuEngine,
                    Device = rule.Device
                });
            rules.Add(new NativeMetricSnapshotMetricDefinitionInput
            {
                StructSize =
                    SizeOf<NativeMetricSnapshotMetricDefinitionInput>(),
                Flags = rule.Template.MetricFlags,
                RuleHandle = allocatedRule.RuleHandle,
                MetricHandle = allocatedRule.MetricHandle,
                SourceHandle = rule.Template.SourceHandle,
                ScopeHandle = rule.ScopeHandle,
                CapabilityMask = rule.Template.CapabilityMask,
                MetricKind = rule.Template.MetricKind,
                ScopeKind = rule.Template.ScopeKind,
                ValueKind = rule.Template.ValueKind,
                RetentionPolicy = rule.Template.RetentionPolicy,
                SourcePriority = rule.Template.SourcePriority,
                ReservedU32 = 0,
                MinimumValueBits = rule.Template.MinimumValueBits,
                MaximumValueBits = rule.Template.MaximumValueBits,
                SemanticFingerprint = ExpandedFingerprint(
                    rule.Template.SemanticFingerprint,
                    rule.ScopeHandle, rule.NpuEngine?.BindingKey)
            });
        }

        if (metricIds.Count > plan.MaximumMetricCount)
        {
            throw new InvalidOperationException(
                "The expanded metric-snapshot catalog has too many metrics.");
        }
        var sourceRows = sources.ToArray();
        var ruleRows = rules.ToArray();
        var nativeFingerprint =
            NativeMetricSnapshotSession.CalculateCatalogFingerprint(
                sourceRows,
                ruleRows);
        var activeTopologyKeys = resolvedGpuAdapters
            .Select(static identity => $"gpu|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}")
            .Concat(resolvedStorageSensors.Select(
                static identity => $"storage|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedSystemFans.Select(
                static identity => $"fan|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedBatteries.Select(static identity => $"battery|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedNumaNodes.Select(static identity => $"numa|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedNpuEngines.Select(static identity => $"npu-engine|{identity.Engine.BindingKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedNpus.Select(static identity => $"npu|{identity.ExactIdentityKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedNetworkInterfaces.Select(static identity => $"network-interface|{identity.BindingKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(resolvedIntelCpus.Select(static identity => $"intel-cpu|{identity.Cpu.BindingKey}|{DeviceFingerprint(identity.Device)}"))
            .Concat(allocated.Where(static row => row.Rule.LegacyMetricId.Length != 0)
                .GroupBy(static row => row.Rule.LegacyMetricId, StringComparer.Ordinal)
                .Select(static group => group.OrderBy(static row => row.Rule.Template.SourcePriority)
                    .ThenBy(static row => row.RuleHandle).First())
                .Select(static row => $"request|{row.Rule.LegacyMetricId}|{row.Rule.StableMetricKey}"))
            .ToArray();
        var metricIdMap = metricIds.ToImmutable();
        var catalogIdentitySha256 =
            NativeMetricSnapshotCatalogIdentity.ComputeSha256(
                plan.CatalogManifestSha256,
                nativeFingerprint,
                finalHandleMap,
                metricIdMap,
                activeTopologyKeys);
        var gpuScopeBindings = resolvedGpuAdapters.ToImmutableDictionary(
            static identity => identity.ScopeHandle,
            static identity => new NativeMetricSnapshotGpuScopeBinding(
                identity.DisplayIndex,
                identity.DisplayName,
                identity.ScopeHandle,
                identity.AdapterLuid,
                identity.InventoryGeneration,
                identity.ExactIdentityKey,
                identity.SupportsDedicatedMetrics)
            { Device = identity.Device });
        return new NativeMetricSnapshotCatalogProjection(
            sources,
            rules.MoveToImmutable(),
            metricIdMap,
            metricHandles.ToImmutable(),
            sourceIds,
            sourceHandles,
            ruleBindings.ToImmutable(),
            gpuScopeBindings,
            finalHandleMap,
            nativeFingerprint,
            catalogIdentitySha256)
        {
            StorageIdentities = storageSensors.ToImmutableArray(),
            SystemFanIdentities = systemFans.ToImmutableArray(),
            IgclFanEffectiveMaximumInstanceCount = intelFanMaximum,
            BatteryIdentities = batteries.ToImmutableArray(),
            NumaIdentities = numaNodes.ToImmutableArray(),
            IntelCpuIdentities = intelCpus.ToImmutableArray(),
            NpuEngineIdentities = npuEngineIdentities.ToImmutableArray(),
            NpuEngineDirectory = npuEngineDirectory,
            NpuEngineEffectiveMaximumInstanceCount = npuEngineMaximumInstanceCount,
            NpuMemoryIdentities = npuMemoryIdentities.ToImmutableArray(),
            NpuMemoryEffectiveMaximumInstanceCount = npuMemoryMaximumInstanceCount,
            NetworkInterfaceIdentities = networkInterfaces.ToImmutableArray(),
            MetricBindings = ruleBindings.Values.GroupBy(static row => row.MetricHandle)
                .ToImmutableDictionary(static group => group.Key,
                    group => group.OrderBy(row => plan.SourcePolicies
                        .First(source => source.SourceHandle == row.SourceHandle).Priority).First()),
            LegacyMetricHandles = allocated.Where(static row => row.Rule.LegacyMetricId.Length != 0)
                .GroupBy(static row => row.Rule.LegacyMetricId, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(static group => group.Key,
                    static group => group.OrderBy(static row => row.Rule.Template.SourcePriority)
                        .ThenBy(static row => row.RuleHandle).First().MetricHandle,
                    StringComparer.OrdinalIgnoreCase)
        }.WithLegacyAddresses();
    }

    private NativeMetricSnapshotCatalogProjection WithLegacyAddresses()
        => this with { MetricHandles = MetricHandles.SetItems(LegacyMetricHandles) };

    private static IEnumerable<ExpandedRule> ExpandTemplate(
        NativeMetricSnapshotRuleTemplate template,
        IReadOnlyList<ResolvedGpuIdentity> gpuAdapters,
        IReadOnlyList<ResolvedOrdinalIdentity> storageSensors,
        IReadOnlyList<ResolvedOrdinalIdentity> systemFans,
        IReadOnlyList<ResolvedBatteryIdentity> batteries,
        IReadOnlyList<ResolvedNumaIdentity> numaNodes, IReadOnlyList<ResolvedIntelCpuIdentity> intelCpus,
        IReadOnlyList<ResolvedNetworkInterfaceIdentity> networkInterfaces, IReadOnlyList<ResolvedNpuIdentity> npus,
        IReadOnlyList<ResolvedNpuEngineIdentity> npuEngines)
    {
        return template.ScopeExpansion switch
        {
            1 => [CreateStatic(
                template,
                HostScopeHandle,
                "static|host")],
            2 => [CreateStatic(
                template,
                CpuScopeHandle,
                "static|cpu")],
            3 => [CreateStatic(
                template,
                MemoryScopeHandle,
                "static|memory")],
            7 => [CreateStatic(
                template,
                VirtualMemoryScopeHandle,
                "static|virtual-memory")],
            4 => ExpandBounded(
                template,
                gpuAdapters.Where(adapter =>
                    (template.GpuVendorMask & adapter.VendorMask) != 0
                    && ((template.ApplicabilityMask & DedicatedOnly) == 0
                        || adapter.SupportsDedicatedMetrics)),
                "{gpu}"),
            5 => ExpandBounded(template, template.SourceId switch
            {
                NativeMetricSnapshotSources.WindowsNvmeHealth => storageSensors.Where(static disk => disk.SupportsNvmeHealth),
                NativeMetricSnapshotSources.WindowsAtaSmart => storageSensors.Where(static disk => disk.SupportsAtaSmart),
                _ => storageSensors
            }, "{disk}"),
            6 => ExpandBounded(template, template.SourceId switch
                {
                    NativeMetricSnapshotSources.IntelGcl => systemFans.Where(static fan => fan.IgclFan is not null),
                    NativeMetricSnapshotSources.NvidiaNvml => systemFans.Where(static fan => fan.NvmlFan is not null),
                    NativeMetricSnapshotSources.NotebookOemFan => systemFans.Where(static fan => fan.NotebookOemRole is not null && fan.NvmlFan is null && fan.IgclFan is null),
                    NativeMetricSnapshotSources.HardwareMonitorWmi => systemFans.Where(static fan => fan.NotebookOemRole is null && fan.NvmlFan is null && fan.IgclFan is null),
                    _ => []
                },
                "{fan}"),
            8 => ExpandBounded(template, batteries.Where(battery => WindowsBatteryReader.AppliesTo(
                MetricFieldRegistry.FindByTemplate(template.MetricIdTemplate, template.SourceId)?.Code
                    ?? MetricFieldCode.Unknown, battery.IsRelative)), "{battery}"),
            16 => ExpandBounded(template, npus, "{npu}"),
            17 => ExpandBounded(template, npuEngines, "{engine}"),
            14 => ExpandBounded(template, networkInterfaces, "{interface}"),
            9 => ExpandBounded(template, numaNodes, "{numa}"),
            10 or 11 or 12 or 13 or 15 => ExpandBounded(template, intelCpus.Where(cpu =>
                cpu.Cpu.Kind == (template.ScopeExpansion switch { 10 => IntelMsrTargetKind.PhysicalCore,
                    11 => IntelMsrTargetKind.Package, 13 => IntelMsrTargetKind.PhysicalDie,
                    15 => IntelMsrTargetKind.Platform, _ => IntelMsrTargetKind.LogicalProcessor })
                && cpu.Cpu.AppliesTo(MetricFieldRegistry.FindByTemplate(template.MetricIdTemplate, template.SourceId)?.Code
                    ?? MetricFieldCode.Unknown)), template.ScopeExpansion switch { 10 => "{core}", 11 => "{package}", 13 => "{die}", 15 => "{platform}", _ => "{thread}" }),
            _ => throw new InvalidOperationException(
                "The compiled metric-snapshot scope expansion is invalid.")
        };
    }

    private static ExpandedRule CreateStatic(
        NativeMetricSnapshotRuleTemplate template,
        ulong scopeHandle,
        string exactScopeKey)
        => CreateExpanded(template, scopeHandle, exactScopeKey, -1, template.MetricIdTemplate) with
        { Device = CreateStaticDevice(template.ScopeExpansion) };

    private static ExpandedRule CreateIndexed(
        NativeMetricSnapshotRuleTemplate template,
        string placeholder,
        int index,
        ulong scopeHandle,
        string exactScopeKey)
        => CreateExpanded(template, scopeHandle, exactScopeKey, index,
            template.MetricIdTemplate.Replace(
                placeholder,
                index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal));

    private static ExpandedRule CreateExpanded(
        NativeMetricSnapshotRuleTemplate template, ulong scopeHandle,
        string exactScopeKey, int index, string legacyId)
    {
        var field = MetricFieldRegistry.FindByTemplate(template.MetricIdTemplate, template.SourceId);
        if (field is not null) field = field with { ValueKind = (NativeMetricSnapshotValueKind)template.ValueKind switch
        {
            NativeMetricSnapshotValueKind.Float64 => "float64",
            NativeMetricSnapshotValueKind.Signed64 => "signed64",
            NativeMetricSnapshotValueKind.Unsigned64 => "unsigned64",
            NativeMetricSnapshotValueKind.Unsigned128 => "unsigned128",
            NativeMetricSnapshotValueKind.Boolean => "boolean",
            NativeMetricSnapshotValueKind.Bytes => "bytes",
            _ => throw new InvalidOperationException("Unknown metric value kind.")
        } };
        var definitionKey = field?.Name ?? template.MetricIdTemplate;
        var address = field is null ? legacyId : $"{scopeHandle:X16}/{field.Name}";
        return new ExpandedRule(
            address,
            scopeHandle,
            CreateMetricIdentity(template, scopeHandle),
            $"metric|{definitionKey}|{exactScopeKey}",
            $"rule|{template.TemplateId}|{exactScopeKey}",
            template)
        { Field = field, DeviceIdentity = exactScopeKey, DisplayIndex = index,
            LegacyMetricId = field?.HasLegacyAddress == false ? string.Empty : legacyId };
    }

    private static IEnumerable<ExpandedRule> ExpandBounded<T>(
        NativeMetricSnapshotRuleTemplate template,
        IEnumerable<T> source,
        string placeholder)
        where T : IResolvedOrdinalIdentity
    {
        var values = source
            .OrderBy(static value => value.DisplayIndex)
            .ToArray();
        if (values.Length > template.MaximumInstanceCount)
        {
            throw new InvalidOperationException(
                $"Metric-snapshot template '{template.TemplateId}' exceeds its maximum instance count.");
        }
        return values.Select(identity =>
        {
            var expanded = CreateIndexed(
            template,
            placeholder,
            identity.DisplayIndex,
            identity.ScopeHandle,
            identity.ExactIdentityKey) with
        {
            Device = identity.Device,
            NvmlFan = identity is ResolvedOrdinalIdentity ordinal ? ordinal.NvmlFan : null,
            IgclFan = identity is ResolvedOrdinalIdentity intelOrdinal ? intelOrdinal.IgclFan : null,
            NpuEngine = identity is ResolvedNpuEngineIdentity engine ? engine.Engine : null
        };
            // ADLX's chip report on APUs is not a confirmed graphics-only
            // power domain. Keep the raw field, but do not label it GPU power.
            if (identity is ResolvedGpuIdentity gpu && !gpu.SupportsDedicatedMetrics
                && template.SourceId == NativeMetricSnapshotSources.AmdAdlx
                && expanded.Field?.Code == MetricFieldCode.GpuPower)
                expanded = expanded with { LegacyMetricId = string.Empty };
            return expanded;
        });
    }

    private static ResolvedOrdinalIdentity ResolveOrdinalIdentity(
        NativeMetricSnapshotCatalogHandleMap.Allocator allocator,
        NativeMetricSnapshotOrdinalCatalogIdentity identity,
        string kind,
        int ordinal)
        => new(
            identity.DisplayIndex,
            allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"{kind}|{identity.ExactIdentityKey}"),
            identity.ExactIdentityKey,
            identity.TopologyGeneration,
            identity.SupportsNvmeHealth,
            identity.SupportsAtaSmart,
            identity.NotebookOemRole,
            identity.NvmlFan, identity.IgclFan, identity.Device with { Ordinal = ordinal });

    private static NativeMetricIdentityKey CreateMetricIdentity(
        NativeMetricSnapshotRuleTemplate template,
        ulong scopeHandle)
        => new(
            scopeHandle,
            template.MetricKind,
            template.ScopeKind,
            template.ValueKind,
            template.MetricFlags,
            template.MinimumValueBits,
            template.MaximumValueBits);

    private static void ValidateMetricIdentities(
        IReadOnlyList<ExpandedRule> expanded)
    {
        foreach (var group in expanded.GroupBy(
            static rule => rule.MetricId,
            StringComparer.Ordinal))
        {
            var expected = group.First().MetricIdentity;
            var conflict = group.FirstOrDefault(
                rule => rule.MetricIdentity != expected);
            if (conflict is not null)
            {
                var templates = string.Join(
                    ", ",
                    group.Select(static rule => rule.Template.TemplateId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal));
                throw new InvalidOperationException(
                    $"Metric-snapshot metric '{group.Key}' has conflicting native identity across templates: {templates}.");
            }
        }
        foreach (var group in expanded.GroupBy(
            static rule => rule.StableMetricKey,
            StringComparer.Ordinal))
        {
            if (group.Select(static rule => rule.MetricId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                != 1)
            {
                throw new InvalidOperationException(
                    "A stable metric identity resolved to multiple display identifiers.");
            }
        }
    }

    private static void ValidateGpuIdentities(
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> values,
        uint maximumCount)
    {
        if (values.Count > maximumCount
            || values.Any(static value =>
                value.DisplayIndex < 0
                || string.IsNullOrWhiteSpace(value.DisplayName)
                || !IsExactIdentityKey(value.ExactIdentityKey)
                || value.AdapterLuid == 0
                || value.InventoryGeneration == 0
                || value.VendorMask is 0 or > 0x08U
                || (value.VendorMask & (value.VendorMask - 1)) != 0)
            || values.Select(static value => value.DisplayIndex)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.ExactIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != values.Count
            || values.Select(static value => value.AdapterLuid)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.InventoryGeneration)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                "The metric-snapshot GPU catalog identities are invalid.");
        }
    }

    private static void ValidateOrdinalIdentities(
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> values,
        string name)
    {
        if (values.Any(static value =>
                value.DisplayIndex < 0
                || !IsExactIdentityKey(value.ExactIdentityKey)
                || value.TopologyGeneration == 0)
            || values.Select(static value => value.DisplayIndex)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.ExactIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != values.Count
            || values.Select(static value => value.TopologyGeneration)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                $"The metric-snapshot {name} catalog identities are invalid.");
        }
    }

    private static ulong ExpandedFingerprint(
        ulong templateFingerprint,
        ulong scopeHandle, string? bindingKey = null)
    {
        var result = templateFingerprint
            ^ (scopeHandle + 0x9E37_79B9_7F4A_7C15UL
                + (templateFingerprint << 6)
                + (templateFingerprint >> 2));
        // Per-rule binding evidence is part of Native sameRuleDefinition. A global
        // catalog fingerprint alone cannot invalidate an existing RuleRecord baseline.
        if (bindingKey is not null)
            foreach (var octet in System.Text.Encoding.UTF8.GetBytes(bindingKey))
                result = unchecked((result ^ octet) * 1_099_511_628_211UL);
        return result == 0 ? 1UL : result;
    }

    private static MetricDeviceDescriptor CreateStaticDevice(uint scopeExpansion)
        => scopeExpansion switch
        {
            1 => new("host", null, null, 0),
            2 => new("cpu", null, null, 0),
            3 => new("memory", null, null, 0),
            7 => new("virtual_memory", null, null, 0),
            _ => throw new InvalidOperationException("A bounded metric scope cannot use a static device descriptor.")
        };

    private static MetricDeviceDescriptor CreateIntelDevice(IntelMsrCatalogIdentity cpu, int ordinal)
    {
        var members = cpu.ProcessorAddresses.Select(static address =>
            new MetricProcessorAddress(address.ProcessorGroup, address.GroupRelativeIndex)).ToArray();
        if (members.Length == 0)
            members = [new(cpu.Processor.ProcessorGroup, cpu.Processor.GroupRelativeIndex)];
        var address = cpu.Kind switch
        {
            IntelMsrTargetKind.PhysicalCore when !string.IsNullOrWhiteSpace(cpu.CoreId)
                => MetricDeviceAddress.CpuCore(cpu.CoreId, members),
            IntelMsrTargetKind.Package when !string.IsNullOrWhiteSpace(cpu.PackageId)
                => MetricDeviceAddress.CpuPackage(cpu.PackageId, members),
            IntelMsrTargetKind.PhysicalDie when !string.IsNullOrWhiteSpace(cpu.DieId)
                => MetricDeviceAddress.CpuDie(cpu.DieId, members),
            IntelMsrTargetKind.Platform => MetricDeviceAddress.CpuPlatform(members),
            IntelMsrTargetKind.LogicalProcessor => MetricDeviceAddress.LogicalProcessor(
                cpu.Processor.ProcessorGroup, cpu.Processor.GroupRelativeIndex),
            IntelMsrTargetKind.PhysicalCore or IntelMsrTargetKind.Package or IntelMsrTargetKind.PhysicalDie
                => MetricDeviceAddress.ProcessorMembers(members) with
                {
                    Kind = cpu.Kind switch
                    {
                        IntelMsrTargetKind.PhysicalCore => "cpu_core",
                        IntelMsrTargetKind.Package => "cpu_package",
                        _ => "cpu_die"
                    }
                },
            _ => null
        };
        var kind = cpu.Kind switch
        {
            IntelMsrTargetKind.PhysicalCore => "cpu_core",
            IntelMsrTargetKind.Package => "cpu_package",
            IntelMsrTargetKind.LogicalProcessor => "cpu_logical_processor",
            IntelMsrTargetKind.PhysicalDie => "cpu_die",
            IntelMsrTargetKind.Platform => "cpu_platform",
            _ => throw new InvalidOperationException("Unknown Intel CPU metric scope.")
        };
        return new(kind, address, CleanDisplayName(cpu.NameDeclared), ordinal);
    }

    private static string? CleanDisplayName(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DeviceFingerprint(MetricDeviceDescriptor device)
        => System.Text.Json.JsonSerializer.Serialize(device);

    private static uint SizeOf<T>()
        where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static bool IsExactIdentityKey(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value == value.Trim()
            && !value.Contains('\0')
            && System.Text.Encoding.UTF8.GetByteCount(value)
                <= NativeMetricSnapshotCatalogHandleMap
                    .MaximumExactKeyByteCount;

    private const uint DedicatedOnly = 1U;
    private const ulong HostScopeHandle = 0x1000_0000_0000_0001UL;
    private const ulong CpuScopeHandle = 0x1000_0000_0000_0002UL;
    private const ulong MemoryScopeHandle = 0x1000_0000_0000_0003UL;
    private const ulong VirtualMemoryScopeHandle = 0x1000_0000_0000_0004UL;

    private sealed record ExpandedRule(
        string MetricId,
        ulong ScopeHandle,
        NativeMetricIdentityKey MetricIdentity,
        string StableMetricKey,
        string StableRuleKey,
        NativeMetricSnapshotRuleTemplate Template)
    {
        public MetricFieldDefinition? Field { get; init; }
        public string DeviceIdentity { get; init; } = string.Empty;
        public int DisplayIndex { get; init; } = -1;
        public string LegacyMetricId { get; init; } = string.Empty;
        public NvidiaNvmlFanIdentity? NvmlFan { get; init; }
        public IntelGclFanIdentity? IgclFan { get; init; }
        public NpuEngineCatalogIdentity? NpuEngine { get; init; }
        public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
    }

    private sealed record AllocatedRule(
        ExpandedRule Rule,
        ulong MetricHandle,
        ulong RuleHandle);

    private interface IResolvedOrdinalIdentity
    {
        int DisplayIndex { get; }

        ulong ScopeHandle { get; }

        string ExactIdentityKey { get; }

        MetricDeviceDescriptor Device { get; }
    }

    private readonly record struct ResolvedGpuIdentity(
        int DisplayIndex,
        string DisplayName,
        ulong ScopeHandle,
        string ExactIdentityKey,
        ulong AdapterLuid,
        ulong InventoryGeneration,
        uint VendorMask,
        bool SupportsDedicatedMetrics,
        MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedOrdinalIdentity(
        int DisplayIndex,
        ulong ScopeHandle,
        string ExactIdentityKey,
        ulong TopologyGeneration,
        bool SupportsNvmeHealth,
        bool SupportsAtaSmart,
        NotebookOemPhysicalFanRole? NotebookOemRole,
        NvidiaNvmlFanIdentity? NvmlFan, IntelGclFanIdentity? IgclFan,
        MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedBatteryIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, bool IsRelative, MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedIntelCpuIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, IntelMsrCatalogIdentity Cpu,
        MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedNetworkInterfaceIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, string BindingKey, MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedNpuEngineIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, NpuEngineCatalogIdentity Engine,
        string PnpInstanceId, MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedNpuIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, string PnpInstanceId, MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedNumaIdentity(int DisplayIndex, ulong ScopeHandle,
        string ExactIdentityKey, ushort NodeNumber, MetricDeviceDescriptor Device) : IResolvedOrdinalIdentity;

    private readonly record struct NativeMetricIdentityKey(
        ulong ScopeHandle,
        uint MetricKind,
        uint ScopeKind,
        uint ValueKind,
        uint Flags,
        ulong MinimumValueBits,
        ulong MaximumValueBits);
}
