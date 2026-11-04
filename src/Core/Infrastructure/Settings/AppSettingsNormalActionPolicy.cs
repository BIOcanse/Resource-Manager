using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Settings;

// This adapter owns no current value. Both HTTP transports consume the same settings owner.
public sealed class AppSettingsNormalActionPolicy(StateAuthority authority, IAppSettingsStore settings)
    : INativeHttpNormalActionPolicy
{
    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Live: read the settings owner's latest committed publication, without sampling or polling.
        var current = authority.Read<AppSettings>(StateItemUids.AppSettingsCurrent);
        return current.HasValue
            ? ValueTask.FromResult(current.Value.NormalActionsAllowed)
            : ReadInitialAsync(cancellationToken);
    }

    private async ValueTask<bool> ReadInitialAsync(CancellationToken cancellationToken)
    {
        // Before initial publication a request may read the existing source, but never migrate or repair it.
        var initial = await settings.LoadReadOnlyAsync(cancellationToken);
        var current = authority.Read<AppSettings>(StateItemUids.AppSettingsCurrent);
        return current.HasValue ? current.Value.NormalActionsAllowed : initial.Settings.NormalActionsAllowed;
    }
}
