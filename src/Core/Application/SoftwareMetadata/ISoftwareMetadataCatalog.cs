using ResourceManager.App.Domain.SoftwareMetadata;

namespace ResourceManager.App.Application.SoftwareMetadata;

public interface ISoftwareMetadataCatalog
{
    string Version { get; }

    bool Contains(string softwareIdentityId);

    ResolvedSoftwareMetadata? Resolve(string softwareIdentityId, string language);
}
