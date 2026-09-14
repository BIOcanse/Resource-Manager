using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Domain.PublicResources;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal sealed class StartupDisabledHostPublicResourceCapability
    : IHostPublicResourceCapability
{
    private static readonly HostPublicResourceCapabilitySnapshot Snapshot = new(
        HostPublicResourceCapabilityStates.Unavailable,
        false,
        "profile-disabled: shared-resource ownership is disabled by the startup profile.");

    public HostPublicResourceCapabilitySnapshot GetCapability() => Snapshot;
}
