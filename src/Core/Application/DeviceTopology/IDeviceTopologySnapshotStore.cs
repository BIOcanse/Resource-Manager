using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.DeviceTopology;

public interface IDeviceTopologySnapshotStore
{
    Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed record DeviceTopologyPersistedSnapshot(
    string SchemaVersion,
    string MachineIdentity,
    long ContentGeneration,
    string SemanticHash,
    DeviceTopologySnapshot Snapshot);
