using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeSystemProcessClassifier
{
    RuntimeAttributionObservation Observe(RuntimeProcessIdentity process);
}
