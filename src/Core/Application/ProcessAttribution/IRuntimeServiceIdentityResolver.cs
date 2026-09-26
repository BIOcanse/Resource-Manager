using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeServiceIdentityResolver
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
