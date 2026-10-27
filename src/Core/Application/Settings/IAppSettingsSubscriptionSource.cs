using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

/// <summary>A named holder receives the current backend settings, then committed content changes.</summary>
public interface IAppSettingsSubscriptionSource
{
    IAsyncEnumerable<AppSettings> SubscribeAsync(string holder, CancellationToken cancellationToken);
}
