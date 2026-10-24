using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeSoftwareObservationSource
{
    // Reads published attribution only; does not acquire subscriptions or scan.
    IReadOnlyList<RuntimeSoftwareAttribution> ReadLatestSoftware();
}
