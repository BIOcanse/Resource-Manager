using System.Text.Json;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Application.SoftwareMetadata;
using ResourceManager.App.Domain.SoftwareMetadata;

namespace ResourceManager.App.Infrastructure.SoftwareMetadata;

public sealed class BundledSoftwareMetadataCatalog : ISoftwareMetadataCatalog
{
    private const string RelativeCatalogPath =
        "Infrastructure/Resources/SoftwareMetadata/software-metadata.v1.json";
    private readonly SoftwareMetadataCatalogCompiler compiled;

    public BundledSoftwareMetadataCatalog(
        IHostEnvironment environment,
        ISoftwareIdentityCatalog identityCatalog)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(identityCatalog);
        var path = ResolveCatalogPath(environment)
            ?? throw new FileNotFoundException(
                $"Required software metadata catalog '{RelativeCatalogPath}' was not found.");
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<SoftwareMetadataCatalogDocument>(
            stream,
            JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"Software metadata catalog '{path}' is empty.");
        compiled = new SoftwareMetadataCatalogCompiler(document, identityCatalog.Entries);
    }

    public string Version => compiled.Version;

    public bool Contains(string softwareIdentityId)
        => compiled.Contains(softwareIdentityId);

    public ResolvedSoftwareMetadata? Resolve(string softwareIdentityId, string language)
        => compiled.Resolve(softwareIdentityId, language);

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
