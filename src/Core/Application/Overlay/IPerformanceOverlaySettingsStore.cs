using ResourceManager.App.Domain.Overlay;

namespace ResourceManager.App.Application.Overlay;

public interface IPerformanceOverlaySettingsStore
{
    Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken);
    Task<PerformanceOverlaySettings> GetSoftwareAsync(string softwareId, CancellationToken cancellationToken);
    Task<PerformanceOverlaySettings> SaveSoftwareAsync(PerformanceOverlaySettings settings, CancellationToken cancellationToken);
}
