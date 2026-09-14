using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public interface IDashboardSettingsStore
{
    Task<DashboardSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken);

    Task<DashboardSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken cancellationToken);

    Task<DashboardSettingsUpdateResult> SaveAsync(DashboardSettings settings, CancellationToken cancellationToken);
}
