namespace ResourceManager.App.Domain.DeviceTopology;

/// <summary>Authoritative physical form and the raw facts used to decide it. No display text.</summary>
public sealed record DeviceTopologyMachineIdentity(string FormFactor, DeviceTopologyMachineEvidence Evidence)
{
    public static DeviceTopologyMachineIdentity Unknown { get; } = new("unknown", new(
        new(null, "not-queried"), new(null, "not-queried"),
        new(null, "not-queried"), new(null, "not-queried"), []));
}

public sealed record DeviceTopologyIdentityFact<T>(T Value, string? Reason);

public sealed record DeviceTopologyMachineEvidence(
    DeviceTopologyIdentityFact<IReadOnlyList<byte>?> ChassisTypes,
    DeviceTopologyIdentityFact<bool?> SystemBatteryPresent,
    DeviceTopologyIdentityFact<IReadOnlyList<string>?> CpuModels,
    DeviceTopologyIdentityFact<IReadOnlyList<string>?> GpuModels,
    IReadOnlyList<DeviceTopologyWorkstationComponent> WorkstationComponents)
{
    public DeviceTopologyIdentityFact<IReadOnlyList<DeviceTopologyMachineGpuAttachment>?> GpuAttachments { get; init; }
        = new(null, "not-queried");
}

public sealed record DeviceTopologyMachineGpuAttachment(string Model, bool? IsDetachable, uint? AdapterTypeFlags)
{
    public string? AttachmentKind => IsDetachable == true ? "graphics_dock" : null;
    public string? EvidenceSource => AdapterTypeFlags.HasValue ? "d3dkmt-adapter-type" : null;
}

public sealed record DeviceTopologyWorkstationComponent(string Component, string Model, string RuleId);
