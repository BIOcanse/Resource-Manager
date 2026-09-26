using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeRootIdentityResolver
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
