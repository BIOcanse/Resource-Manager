using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

public sealed class SoftwareIssueProjection(
    IEnumerable<ISoftwareIssueSource> sources) : ISoftwareIssueProjection
{
    private readonly ISoftwareIssueSource[] sources = sources.ToArray();

    public async Task<IReadOnlyList<SoftwareRecord>> ProjectAsync(
        IReadOnlyList<SoftwareRecord> software,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(software);
        cancellationToken.ThrowIfCancellationRequested();
        var batches = await Task.WhenAll(sources.Select(source =>
            source.CollectAsync(software, cancellationToken)));
        var bySoftware = batches
            .SelectMany(static batch => batch)
            .GroupBy(static assignment => assignment.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<SoftwareIssueTag>)group
                    .Select(static assignment => assignment.Issue)
                    .GroupBy(static issue => issue.Id, StringComparer.Ordinal)
                    .Select(static issueGroup => issueGroup.First())
                    .OrderByDescending(static issue => SeverityRank(issue.Severity))
                    .ThenBy(static issue => issue.Dynamic)
                    .ThenBy(static issue => issue.Label, StringComparer.Ordinal)
                    .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return software
            .Select(record => record with
            {
                Issues = bySoftware.TryGetValue(record.Id, out var issues)
                    ? issues
                    : []
            })
            .ToArray();
    }

    private static int SeverityRank(string severity)
        => severity switch
        {
            SoftwareIssueSeverities.Critical => 3,
            SoftwareIssueSeverities.Warning => 2,
            _ => 1
        };
}
