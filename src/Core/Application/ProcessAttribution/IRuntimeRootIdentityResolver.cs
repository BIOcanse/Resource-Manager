using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeRootIdentityResolver : IRuntimeAttributionInputSource
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
