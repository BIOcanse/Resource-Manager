namespace ResourceManager.App.Application.Settings;

public interface INativeHttpNormalActionPolicy
{
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
}
