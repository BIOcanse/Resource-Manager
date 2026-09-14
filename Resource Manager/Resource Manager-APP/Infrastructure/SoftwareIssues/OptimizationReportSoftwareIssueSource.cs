using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

public sealed class OptimizationReportSoftwareIssueSource(
    IOptimizationSoftwareIssueSignalSource signalSource,
    ISoftwareIssueCatalog catalog) : ISoftwareIssueSource
{
    public Task<IReadOnlyList<SoftwareIssueAssignment>> CollectAsync(
        IReadOnlyList<SoftwareRecord> software,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = signalSource.ReadSoftwareIssueSnapshot();
        var pending = new List<PendingAssignment>();
        foreach (var signal in snapshot.Signals)
        {
            if (!SoftwareIssueKinds.IsDynamic(signal.Kind))
            {
                throw new InvalidDataException(
                    $"Optimization issue signal '{signal.ReportId}' has invalid kind '{signal.Kind}'.");
            }

            foreach (var record in ResolveTargets(signal, software))
            {
                if (signal.Kind == SoftwareIssueKinds.AbnormalMemoryUsage
                    && record.Kind == SoftwareKinds.Game)
                {
                    continue;
                }

                pending.Add(new PendingAssignment(record.Id, signal));
            }
        }

        var assignments = pending
            .GroupBy(
                static item => $"{item.SoftwareId}\0{item.Signal.Kind}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var softwareId = group.First().SoftwareId;
                var signals = group
                    .Select(static item => item.Signal)
                    .OrderBy(static signal => signal.ReportId, StringComparer.Ordinal)
                    .ToArray();
                var first = signals[0];
                return new SoftwareIssueAssignment(
                    softwareId,
                    new SoftwareIssueTag(
                        $"dynamic:{first.Kind}:{softwareId}",
                        first.Kind,
                        signals.OrderByDescending(static signal =>
                                SeverityRank(signal.Severity))
                            .First()
                            .Severity,
                        SoftwareIssueSources.DynamicReport,
                        first.Label,
                        string.Join(
                            " | ",
                            signals.Select(static signal => signal.Message)
                                .Where(static message => !string.IsNullOrWhiteSpace(message))
                                .Distinct(StringComparer.Ordinal)),
                        Dynamic: true,
                        [],
                        signals.Select(static signal => signal.ReportId)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()));
            })
            .OrderBy(static assignment => assignment.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static assignment => assignment.Issue.Kind, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SoftwareIssueAssignment>>(assignments);
    }

    private IReadOnlyList<SoftwareRecord> ResolveTargets(
        OptimizationSoftwareIssueSignal signal,
        IReadOnlyList<SoftwareRecord> software)
    {
        if (!string.IsNullOrWhiteSpace(signal.SoftwareId))
        {
            return ResolveSingleOwnerGroup(
                software.Where(record =>
                    record.Id.Equals(
                        signal.SoftwareId,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        record.SoftwareIdentityId,
                        signal.SoftwareId,
                        StringComparison.Ordinal)));
        }

        var artifactPath = SoftwareIssueArtifactPath.Normalize(signal.ArtifactPath);
        if (artifactPath is null)
        {
            return [];
        }

        return ResolveSingleOwnerGroup(software.Where(record =>
            OwnsPath(record, artifactPath)));
    }

    private bool OwnsPath(SoftwareRecord record, string artifactPath)
    {
        if ((record.RootPaths ?? [])
            .Select(SoftwareIssueArtifactPath.Normalize)
            .Any(root => root is not null && IsSameOrUnder(artifactPath, root)))
        {
            return true;
        }

        if ((record.ExecutablePaths ?? [])
            .Select(SoftwareIssueArtifactPath.Normalize)
            .Any(path => string.Equals(
                path,
                artifactPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(record.SoftwareIdentityId)
            && catalog.OwnsArtifactPath(
                record.SoftwareIdentityId,
                artifactPath);
    }

    private static IReadOnlyList<SoftwareRecord> ResolveSingleOwnerGroup(
        IEnumerable<SoftwareRecord> candidates)
    {
        var groups = candidates
            .GroupBy(
                static record => string.IsNullOrWhiteSpace(record.SoftwareIdentityId)
                    ? $"record:{record.Id}"
                    : $"identity:{record.SoftwareIdentityId}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return groups.Length == 1 ? groups[0].ToArray() : [];
    }

    private static bool IsSameOrUnder(string candidate, string root)
        => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

    private static int SeverityRank(string severity)
        => severity switch
        {
            SoftwareIssueSeverities.Critical => 3,
            SoftwareIssueSeverities.Warning => 2,
            _ => 1
        };

    private sealed record PendingAssignment(
        string SoftwareId,
        OptimizationSoftwareIssueSignal Signal);
}
