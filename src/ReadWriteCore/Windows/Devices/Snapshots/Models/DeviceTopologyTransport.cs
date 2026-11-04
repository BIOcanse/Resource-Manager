namespace ResourceManager.App.Domain.DeviceTopology;

/// <summary>Backend-owned transport identity and original source facts, without presentation text.</summary>
public sealed record DeviceTopologyTransport(string? Code, string? Reason, string? RuleId,
    IReadOnlyList<DeviceTopologyTransportEvidence> Evidence)
{
    public static DeviceTopologyTransport Unqueried { get; } = new(null, "not-queried", null, []);
}

public sealed record DeviceTopologyTransportEvidence(string Source, string DeviceId, int Depth,
    string? EnumeratorName, Guid? BusTypeGuid, string? Service, string? ParentDeviceId);
