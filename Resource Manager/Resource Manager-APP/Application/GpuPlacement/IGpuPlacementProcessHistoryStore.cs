using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuPlacementProcessHistoryStore
{
    Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(
        string softwareId,
        string softwareName,
        CancellationToken cancellationToken);

    Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(
        GpuPlacementProcessObservationRequest request,
        CancellationToken cancellationToken);

    Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(
        string softwareId,
        string softwareName,
        GpuPlacementProcessInstance process,
        GpuGraphicsApi graphicsApi,
        CancellationToken cancellationToken);
}
