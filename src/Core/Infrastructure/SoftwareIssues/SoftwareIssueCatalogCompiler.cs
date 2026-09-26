using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

internal sealed class SoftwareIssueCatalogCompiler
{
    private readonly ISoftwareArtifactIdentityProbe artifactProbe;
    private readonly IReadOnlyDictionary<string, SoftwareIssueCatalogEntry[]> entriesByIdentity;

    internal SoftwareIssueCatalogCompiler(
        SoftwareIssueCatalogDocument document,
        ISoftwareArtifactIdentityProbe artifactProbe,
        IReadOnlySet<string> knownSoftwareIdentityIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(artifactProbe);
        ArgumentNullException.ThrowIfNull(knownSoftwareIdentityIds);
        ValidateDocument(document, knownSoftwareIdentityIds);
        this.artifactProbe = artifactProbe;
        entriesByIdentity = document.Entries
            .GroupBy(static entry => entry.SoftwareIdentityId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static entry => entry.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    internal async ValueTask<IReadOnlyList<SoftwareIssueTag>> GetIssuesAsync(
        string softwareIdentityId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareIdentityId);
        if (!entriesByIdentity.TryGetValue(softwareIdentityId, out var entries))
        {
            return [];
        }

        var identityByPath = new Dictionary<string, SoftwareArtifactIdentity?>(
            StringComparer.OrdinalIgnoreCase);
        var issues = new List<SoftwareIssueTag>(entries.Length);
        foreach (var entry in entries)
        {
            var matches = true;
            foreach (var requirement in entry.Artifacts)
            {
                var normalizedPath = SoftwareIssueArtifactPath.Normalize(requirement.Path)!;
                if (!identityByPath.TryGetValue(normalizedPath, out var identity))
                {
                    identity = await artifactProbe.ReadAsync(
                        normalizedPath,
                        cancellationToken);
                    identityByPath.Add(normalizedPath, identity);
                }

                if (!Matches(requirement, identity))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            issues.Add(new SoftwareIssueTag(
                entry.Id,
                entry.Kind,
                entry.Severity,
                SoftwareIssueSources.StaticCatalog,
                entry.Label,
                entry.Message,
                Dynamic: false,
                entry.References,
                []));
        }

        return issues;
    }

    internal bool OwnsArtifactPath(
        string softwareIdentityId,
        string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareIdentityId);
        var normalizedCandidate = SoftwareIssueArtifactPath.Normalize(artifactPath);
        return normalizedCandidate is not null
            && entriesByIdentity.TryGetValue(softwareIdentityId, out var entries)
            && entries.SelectMany(static entry => entry.Artifacts)
                .Select(static artifact => SoftwareIssueArtifactPath.Normalize(artifact.Path))
                .Any(path => path is not null
                    && path.Equals(
                        normalizedCandidate,
                        StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(
        SoftwareIssueArtifactRequirement requirement,
        SoftwareArtifactIdentity? identity)
        => identity is not null
            && identity.Length == requirement.Length
            && identity.Sha256.Equals(
                requirement.Sha256,
                StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(requirement.FileVersion)
                || string.Equals(
                    identity.FileVersion,
                    requirement.FileVersion,
                    StringComparison.Ordinal));

    private static void ValidateDocument(
        SoftwareIssueCatalogDocument document,
        IReadOnlySet<string> knownSoftwareIdentityIds)
    {
        if (string.IsNullOrWhiteSpace(document.Version))
        {
            throw new InvalidDataException("Software issue catalog version is required.");
        }

        if (document.Entries is null)
        {
            throw new InvalidDataException("Software issue catalog entries are required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
            {
                throw new InvalidDataException(
                    $"Software issue catalog entry ID '{entry.Id}' is empty or duplicated.");
            }
            if (string.IsNullOrWhiteSpace(entry.SoftwareIdentityId))
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' requires a software identity ID.");
            }
            if (!knownSoftwareIdentityIds.Contains(entry.SoftwareIdentityId))
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' references unknown software identity '{entry.SoftwareIdentityId}'.");
            }
            if (!SoftwareIssueKinds.IsKnown(entry.Kind)
                || SoftwareIssueKinds.IsDynamic(entry.Kind))
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' has invalid static kind '{entry.Kind}'.");
            }
            if (!SoftwareIssueSeverities.IsKnown(entry.Severity))
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' has invalid severity '{entry.Severity}'.");
            }
            if (string.IsNullOrWhiteSpace(entry.Label)
                || string.IsNullOrWhiteSpace(entry.Message))
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' requires a label and message.");
            }
            if (entry.Artifacts is null || entry.Artifacts.Count == 0)
            {
                throw new InvalidDataException(
                    $"Software issue '{entry.Id}' requires an exact artifact.");
            }

            foreach (var artifact in entry.Artifacts)
            {
                if (SoftwareIssueArtifactPath.Normalize(artifact.Path) is null
                    || artifact.Length <= 0
                    || artifact.Sha256.Length != 64
                    || !artifact.Sha256.All(static value => char.IsAsciiHexDigit(value)))
                {
                    throw new InvalidDataException(
                        $"Software issue '{entry.Id}' has an invalid exact artifact requirement.");
                }
            }

            foreach (var reference in entry.References ?? [])
            {
                if (string.IsNullOrWhiteSpace(reference.Label)
                    || !Uri.TryCreate(reference.Url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException(
                        $"Software issue '{entry.Id}' has an invalid HTTPS reference.");
                }
            }
        }
    }
}
