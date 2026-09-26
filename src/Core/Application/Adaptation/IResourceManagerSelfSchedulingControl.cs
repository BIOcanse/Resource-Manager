using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Application.Adaptation;

public interface IResourceManagerSelfSchedulingControl
{
    ResourceManagerSelfSchedulingSnapshot GetSchedulingSnapshot();

    AdapterSoftwareSchedulingResult ApplyScheduling(AdapterSoftwareSchedulingEnvelope envelope);

    AdapterSchedulingStateExportResult ExportCoordinatorSchedulingState(
        int maximumPayloadBytes,
        DateTimeOffset deadline);

    AdapterSchedulingStateRestoreResult RestoreCoordinatorSchedulingState(
        AdapterSchedulingStateRestoreCommand command);
}
