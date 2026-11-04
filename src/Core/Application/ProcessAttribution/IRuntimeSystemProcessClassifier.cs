using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeSystemProcessClassifier : IRuntimeAttributionInputSource
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
