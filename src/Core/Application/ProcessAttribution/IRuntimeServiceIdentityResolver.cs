using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeServiceIdentityResolver : IRuntimeAttributionInputSource
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);

    /// <summary>Resolve against the immutable external input captured for this batch.</summary>
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process, object inputVersion) => Observe(process);
}
