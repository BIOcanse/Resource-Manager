using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimePackageIdentityResolver
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
