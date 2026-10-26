namespace ResourceManager.App.Domain.DeviceTopology;

/// <summary>A remote host reached over a verified host-to-host link. Never part of the local machine inventory.</summary>
public sealed record DeviceTopologyExternalMachine(
    string LocalLinkDeviceId,
    string? PeerIdentity,
    string FormFactor,
    string? Model,
    IReadOnlyList<DeviceTopologyExternalMachineComponent> Components,
    DeviceTopologyExternalMachineEvidence Evidence);

public sealed record DeviceTopologyExternalMachineComponent(string Component, string? Model);

/// <summary>Local Windows facts establishing the link; they do not identify the remote machine or its silicon.</summary>
public sealed record DeviceTopologyExternalMachineEvidence(
    string Source,
    string CompatibleId,
    uint MediaConnectStateRaw,
    string PeerPropertiesReason);
