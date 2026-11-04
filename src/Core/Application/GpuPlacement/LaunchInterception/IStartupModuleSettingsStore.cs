using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IStartupModuleSettingsStore
{
    Task<StartupModuleSettingsDocument> GetAsync(CancellationToken cancellationToken);
    Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken cancellationToken);
}
