using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

/// <summary>The caller's original topology rules and persistence, without a second collector or publication.</summary>
public sealed record DeviceTopologyCapturePolicy(
    IDeviceTopologyReader Reader,
    Func<DeviceTopologySnapshot, string> ComputeSemanticHash,
    Func<DeviceTopologySnapshot?, DateTimeOffset?, DeviceTopologyInformationProjectionResult> ProjectInformation,
    Func<DeviceTopologyChangedCapture, CancellationToken, Task> PersistChanged,
    Func<Exception, DeviceTopologyCaptureFailure> ClassifyFailure,
    Action<Exception, string, IReadOnlyList<DeviceTopologySourceDiagnostic>, bool> LogFailure,
    Action<Exception> LogPersistenceFailure,
    Action LogSmbiosUnavailable);

/// <summary>One projection owns both information objects and their topology port references.</summary>
public sealed record DeviceTopologyInformationProjectionResult(DeviceTopologySnapshot? Snapshot, DeviceInformationSnapshot Information);

public sealed record DeviceTopologyChangedCapture(long ContentGeneration, string SemanticHash, DeviceTopologySnapshot Snapshot);
public sealed record DeviceTopologyCaptureFailure(string Code, IReadOnlyList<DeviceTopologySourceDiagnostic> Diagnostics);

/// <summary>Adapts the caller's existing active/demand checks and atomic retirement commit gate.
/// TryPublish must validate retirement and invoke the action atomically.</summary>
public sealed record DeviceTopologyCapturePermission(
    Func<bool> IsActive,
    Func<Action, bool> TryPublish,
    Func<bool> HasCurrentDemand);
