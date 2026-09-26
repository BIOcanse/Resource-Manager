using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.SoftwareIssues;

public interface ISoftwareIssueCatalog
{
    string Version { get; }

    IReadOnlyList<SoftwareIssueCatalogEntry> Entries { get; }

    ValueTask<IReadOnlyList<SoftwareIssueTag>> GetIssuesAsync(
        string softwareIdentityId,
        CancellationToken cancellationToken);

    bool OwnsArtifactPath(
        string softwareIdentityId,
        string artifactPath);
}
