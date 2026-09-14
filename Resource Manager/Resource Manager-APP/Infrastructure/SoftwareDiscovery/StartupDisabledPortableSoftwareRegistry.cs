using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareDiscovery;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal sealed class StartupDisabledPortableSoftwareRegistry
    : IPortableSoftwareRegistry
{
    internal const string DisabledReason =
        "profile-disabled: portable software registry mutation is disabled by the startup profile.";

    public void Observe(PortableSoftwareObservation observation)
        => throw CreateDisabledException();

    public IReadOnlyList<PortableSoftwareRegistration> GetSnapshot() => [];

    public Task<IReadOnlyList<PortableSoftwareRegistration>> RefreshAsync(
        CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<PortableSoftwareRegistration>>(
            CreateDisabledException());

    public Task<PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
        PortableSoftwareRootConfirmationRequest request,
        CancellationToken cancellationToken)
        => Task.FromException<PortableSoftwareRootConfirmationResult>(
            CreateDisabledException());

    private static InvalidOperationException CreateDisabledException()
        => new(DisabledReason);
}
