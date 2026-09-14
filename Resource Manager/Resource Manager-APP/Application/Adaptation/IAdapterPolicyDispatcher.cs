using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Application.Adaptation;

public interface IAdapterPolicyDispatcher
{
    Task<AdapterSoftwareSchedulingResult> ApplySoftwareSchedulingAsync(
        string softwareId,
        AdapterSoftwareSchedulingEnvelope envelope,
        CancellationToken cancellationToken);

    Task<AdapterSchedulingStateExportResult> ExportSoftwareSchedulingStateAsync(
        string softwareId,
        int maximumPayloadBytes,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);

    Task<AdapterSchedulingStateRestoreResult> RestoreSoftwareSchedulingStateAsync(
        string softwareId,
        AdapterSchedulingStateRestoreCommand command,
        CancellationToken cancellationToken);
}
