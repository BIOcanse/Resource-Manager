using System.Text.Json;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

public sealed class BundledSoftwareIssueCatalog : ISoftwareIssueCatalog
{
    private const string RelativeCatalogPath =
        "Infrastructure/Resources/SoftwareIssues/software-issues.v1.json";
    private readonly SoftwareIssueCatalogDocument document;
    private readonly SoftwareIssueCatalogCompiler compiler;

    public BundledSoftwareIssueCatalog(
        IHostEnvironment environment,
        ISoftwareIdentityCatalog softwareIdentityCatalog)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(softwareIdentityCatalog);
        var path = ResolveCatalogPath(environment)
            ?? throw new FileNotFoundException(
                $"Required software issue catalog '{RelativeCatalogPath}' was not found.");
        using var stream = File.OpenRead(path);
        document = JsonSerializer.Deserialize<SoftwareIssueCatalogDocument>(
            stream,
            JsonSerializerOptions.Web)
            ?? throw new InvalidDataException(
                $"Software issue catalog '{path}' is empty.");
        compiler = new SoftwareIssueCatalogCompiler(
            document,
            new SoftwareArtifactIdentityProbe(),
            softwareIdentityCatalog.Entries
                .Select(static entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal));
    }

    public string Version => document.Version;

    public IReadOnlyList<SoftwareIssueCatalogEntry> Entries => document.Entries;

    public ValueTask<IReadOnlyList<SoftwareIssueTag>> GetIssuesAsync(
        string softwareIdentityId,
        CancellationToken cancellationToken)
        => compiler.GetIssuesAsync(softwareIdentityId, cancellationToken);

    public bool OwnsArtifactPath(
        string softwareIdentityId,
        string artifactPath)
        => compiler.OwnsArtifactPath(softwareIdentityId, artifactPath);

    private static string? ResolveCatalogPath(IHostEnvironment environment)
    {
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, RelativeCatalogPath),
            Path.Combine(AppContext.BaseDirectory, RelativeCatalogPath)
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
