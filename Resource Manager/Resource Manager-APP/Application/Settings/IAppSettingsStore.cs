using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public interface IAppSettingsStore
{
    Task<AppSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken);

    Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken cancellationToken);

    Task<AppSettingsUpdateResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<AppSettingsUpdateResult> SaveValidatedAsync(
        AppSettings settings,
        Func<AppSettingsUpdateResult, CancellationToken, Task> validateBeforeCommit,
        CancellationToken cancellationToken);
}
