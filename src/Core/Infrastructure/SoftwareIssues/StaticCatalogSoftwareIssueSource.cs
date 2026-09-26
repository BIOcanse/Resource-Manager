using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

public sealed class StaticCatalogSoftwareIssueSource(
    ISoftwareIssueCatalog catalog) : ISoftwareIssueSource
{
    public async Task<IReadOnlyList<SoftwareIssueAssignment>> CollectAsync(
        IReadOnlyList<SoftwareRecord> software,
        CancellationToken cancellationToken)
    {
        var issuesByIdentity = new Dictionary<
            string,
            IReadOnlyList<SoftwareIssueTag>>(StringComparer.Ordinal);
        var assignments = new List<SoftwareIssueAssignment>();
        foreach (var record in software)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(record.SoftwareIdentityId))
            {
                continue;
            }

            if (!issuesByIdentity.TryGetValue(
                record.SoftwareIdentityId,
                out var issues))
            {
                issues = await catalog.GetIssuesAsync(
                    record.SoftwareIdentityId,
                    cancellationToken);
                issuesByIdentity.Add(record.SoftwareIdentityId, issues);
            }

            assignments.AddRange(issues.Select(issue =>
                new SoftwareIssueAssignment(record.Id, issue)));
        }

        return assignments;
    }
}
