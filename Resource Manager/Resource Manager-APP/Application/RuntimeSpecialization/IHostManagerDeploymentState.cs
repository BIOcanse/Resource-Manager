using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Application.RuntimeSpecialization;

public interface IHostManagerDeploymentState
{
    HostManagerDeploymentSnapshot Snapshot { get; }

    HostManagerDeploymentDiagnosticsSnapshot CaptureDiagnostics();
}
