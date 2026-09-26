using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class EmptyTrustedAdapterRegistrationCatalog : ITrustedAdapterRegistrationCatalog
{
    public ValueTask<TrustedAdapterRegistration?> FindAsync(
        string adapterId,
        string appId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<TrustedAdapterRegistration?>(null);
    }
}
