using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimePackageIdentityResolver : IRuntimeAttributionInputSource
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
