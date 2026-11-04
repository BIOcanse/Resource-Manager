using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

/// <summary>A named holder receives the current module selections, then committed content changes.</summary>
public interface IStartupModuleSettingsSubscriptionSource
{
    IAsyncEnumerable<StartupModuleSettingsDocument> SubscribeAsync(string holder, CancellationToken cancellationToken);
}
