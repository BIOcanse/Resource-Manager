using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.SoftwareIssues;

public interface ISoftwareIssueProjection
{
    Task<IReadOnlyList<SoftwareRecord>> ProjectAsync(
        IReadOnlyList<SoftwareRecord> software,
        CancellationToken cancellationToken);
}

public interface ISoftwareIssueSource
{
    Task<IReadOnlyList<SoftwareIssueAssignment>> CollectAsync(
        IReadOnlyList<SoftwareRecord> software,
        CancellationToken cancellationToken);
}

public sealed record SoftwareIssueAssignment(
    string SoftwareId,
    SoftwareIssueTag Issue);
