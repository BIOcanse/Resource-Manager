using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Metrics;

/// <summary>A structured locator for one metric device. Device kind lives on the enclosing descriptor.</summary>
public sealed record MetricDeviceAddress
{
    public static MetricDeviceAddress Empty { get; } = new() { Kind = "unknown" };

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "unknown";

    [JsonPropertyName("processorGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ushort? ProcessorGroup { get; init; }

    [JsonPropertyName("groupRelativeIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte? GroupRelativeIndex { get; init; }

    [JsonPropertyName("processorAddresses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MetricProcessorAddress>? ProcessorAddresses { get; init; }

    [JsonPropertyName("coreId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoreId { get; init; }

    [JsonPropertyName("packageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; init; }

    [JsonPropertyName("dieId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DieId { get; init; }

    [JsonPropertyName("nodeNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ushort? NodeNumber { get; init; }

    [JsonPropertyName("pnpInstanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PnpInstanceId { get; init; }

    [JsonPropertyName("pnpInstanceIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? PnpInstanceIds { get; init; }

    [JsonPropertyName("physicalAdapterIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PhysicalAdapterIndex { get; init; }

    [JsonPropertyName("physicalIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PhysicalIndex { get; init; }

    [JsonPropertyName("engineIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EngineIndex { get; init; }

    [JsonPropertyName("engineClass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EngineClass { get; init; }

    [JsonPropertyName("adapterLuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdapterLuid { get; init; }

    [JsonPropertyName("physicalDiskDeviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PhysicalDiskDeviceId { get; init; }

    [JsonPropertyName("uniqueId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueId { get; init; }

    [JsonPropertyName("serialNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SerialNumber { get; init; }

    [JsonPropertyName("fanProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FanProvider { get; init; }

    [JsonPropertyName("sensorIdentifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SensorIdentifier { get; init; }

    [JsonPropertyName("fanRole")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FanRole { get; init; }

    [JsonPropertyName("fanIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? FanIndex { get; init; }

    [JsonPropertyName("devicePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DevicePath { get; init; }

    [JsonPropertyName("interfaceGuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InterfaceGuid { get; init; }

    [JsonPropertyName("interfaceLuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InterfaceLuid { get; init; }

    [JsonPropertyName("interfaceIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? InterfaceIndex { get; init; }

    public static MetricDeviceAddress LogicalProcessor(ushort group, byte index)
        => new() { Kind = "cpu_logical_processor", ProcessorGroup = group, GroupRelativeIndex = index };

    public static MetricDeviceAddress ProcessorMembers(IEnumerable<MetricProcessorAddress> members)
        => new() { ProcessorAddresses = members.OrderBy(static member => member.ProcessorGroup)
            .ThenBy(static member => member.GroupRelativeIndex).ToArray() };

    public static MetricDeviceAddress CpuCore(string coreId, IEnumerable<MetricProcessorAddress> members)
        => ProcessorMembers(members) with { Kind = "cpu_core", CoreId = RequireText(coreId, nameof(coreId)) };

    public static MetricDeviceAddress CpuPackage(string packageId, IEnumerable<MetricProcessorAddress> members)
        => ProcessorMembers(members) with { Kind = "cpu_package", PackageId = RequireText(packageId, nameof(packageId)) };

    public static MetricDeviceAddress CpuDie(string dieId, IEnumerable<MetricProcessorAddress> members)
        => ProcessorMembers(members) with { Kind = "cpu_die", DieId = RequireText(dieId, nameof(dieId)) };

    public static MetricDeviceAddress CpuPlatform(IEnumerable<MetricProcessorAddress> members)
        => ProcessorMembers(members) with { Kind = "cpu_platform" };

    public static MetricDeviceAddress NumaNode(ushort nodeNumber)
        => new() { Kind = "numa_node", NodeNumber = nodeNumber };

    public static MetricDeviceAddress GpuAdapter(ulong adapterLuid, IEnumerable<string> pnpInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(pnpInstanceIds);
        var members = pnpInstanceIds.ToArray();
        if (adapterLuid == 0)
            throw new ArgumentOutOfRangeException(nameof(adapterLuid));
        if (members.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Physical PnP identities cannot be empty.", nameof(pnpInstanceIds));
        members = members.Select(static member => Pnp(member, nameof(pnpInstanceIds))).ToArray();
        return new()
        {
            Kind = members.Length == 1 ? "gpu_adapter" : "gpu_adapter_group",
            AdapterLuid = adapterLuid.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            PnpInstanceId = members.Length == 1 ? members[0] : null,
            PnpInstanceIds = members.Length > 1 ? members : null
        };
    }

    public static MetricDeviceAddress Npu(string pnpInstanceId)
        => new() { Kind = "npu", PnpInstanceId = Pnp(pnpInstanceId, nameof(pnpInstanceId)) };

    public static MetricDeviceAddress NpuEngine(string pnpInstanceId, uint physicalAdapterIndex, uint engineIndex)
        => new() { Kind = "npu_engine", PnpInstanceId = Pnp(pnpInstanceId, nameof(pnpInstanceId)),
            PhysicalAdapterIndex = physicalAdapterIndex, EngineIndex = engineIndex };

    public static MetricDeviceAddress GpuFan(string pnpInstanceId, uint fanIndex)
        => new() { Kind = "fan", PnpInstanceId = Pnp(pnpInstanceId, nameof(pnpInstanceId)), FanIndex = fanIndex };

    public static MetricDeviceAddress GpuFanTuningGroup(ulong adapterLuid, IEnumerable<string> pnpInstanceIds)
        => GpuAdapter(adapterLuid, pnpInstanceIds);

    public static MetricDeviceAddress SystemFan(string provider, string sensorIdentifier)
        => new() { Kind = "fan", FanProvider = RequireText(provider, nameof(provider)),
            SensorIdentifier = RequireText(sensorIdentifier, nameof(sensorIdentifier)) };

    public static MetricDeviceAddress OemFan(string role)
        => new() { Kind = "fan", FanRole = RequireText(role, nameof(role)) };

    public static MetricDeviceAddress StorageDevice(int physicalDiskDeviceId, string? uniqueId, string? serialNumber)
        => new() { Kind = "storage", PhysicalDiskDeviceId = physicalDiskDeviceId, UniqueId = Clean(uniqueId), SerialNumber = Clean(serialNumber) };

    public static MetricDeviceAddress Battery(string devicePath, string uniqueId)
        => new() { Kind = "battery", DevicePath = RequireText(devicePath, nameof(devicePath)),
            UniqueId = RequireText(uniqueId, nameof(uniqueId)) };

    public static MetricDeviceAddress NetworkInterface(Guid guid, ulong luid, uint index)
        => new() { Kind = "network_interface", InterfaceGuid = guid.ToString("D").ToUpperInvariant(), InterfaceLuid = luid.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            InterfaceIndex = index };

    public static MetricDeviceAddress GpuEngine(MetricDeviceAddress adapter, ulong adapterLuid,
        uint physicalIndex, uint engineIndex, uint engineClass)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new()
        {
            Kind = "gpu_engine",
            PnpInstanceId = adapter.PnpInstanceId,
            PnpInstanceIds = adapter.PnpInstanceIds,
            AdapterLuid = adapterLuid.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            PhysicalIndex = physicalIndex,
            EngineIndex = engineIndex,
            EngineClass = engineClass
        };
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    // PnP instance ids are case-insensitive. Addresses use the device-topology spelling
    // (trimmed, upper invariant) so consumers can match port DeviceId by exact equality.
    private static string Pnp(string value, string parameterName)
        => RequireText(value, parameterName).Trim().ToUpperInvariant();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record MetricProcessorAddress(ushort ProcessorGroup, byte GroupRelativeIndex);

/// <summary>Display and locator metadata for one device represented in the metric catalog.</summary>
public sealed record MetricDeviceDescriptor(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("address"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] MetricDeviceAddress? Address,
    [property: JsonPropertyName("displayName"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DisplayName,
    [property: JsonPropertyName("ordinal")] int Ordinal)
{
    // Only synthetic in-memory fixtures use this value. Production catalog projection sets a source-backed descriptor.
    public static MetricDeviceDescriptor Unspecified { get; } = new("unknown", MetricDeviceAddress.Empty, null, 0);
}
