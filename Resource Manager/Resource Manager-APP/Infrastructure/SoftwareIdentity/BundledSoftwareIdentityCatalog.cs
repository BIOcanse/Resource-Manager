using System.Text.Json;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.SoftwareIdentity;

public sealed class BundledSoftwareIdentityCatalog :
    ISoftwareIdentityCatalog,
    INativeSoftwareIdentityProcessObserver,
    IDisposable
{
    private const string RelativeCatalogPath = "Infrastructure/Resources/SoftwareIdentity/software-identities.v1.json";
    private readonly object gate = new();
    private readonly SoftwareIdentityCatalogDocument document;
    private readonly HostManagerSoftwareIdentityOwner owner;
    private SoftwareIdentityCatalogCompiler? compiled;

    public BundledSoftwareIdentityCatalog(
        IHostEnvironment environment,
        HostManagerSoftwareIdentityOwner owner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = ResolveCatalogPath(environment)
            ?? throw new FileNotFoundException(
                $"Required software identity catalog '{RelativeCatalogPath}' was not found.");
        using var stream = File.OpenRead(path);
        document = JsonSerializer.Deserialize<SoftwareIdentityCatalogDocument>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"Software identity catalog '{path}' is empty.");
        this.owner = owner;
    }

    public string Version => document.Version;

    public IReadOnlyList<SoftwareIdentityCatalogEntry> Entries => document.Entries;

    public SoftwareIdentityCatalogEntry? MatchInstalledSoftware(InstalledSoftwareEntry software)
    {
        lock (gate)
        {
            return GetCompiled().MatchInstalledSoftware(software);
        }
    }

    public SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process)
    {
        lock (gate)
        {
            return GetCompiled().MatchProcess(process);
        }
    }

    public PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process)
    {
        lock (gate)
        {
            return GetCompiled().MatchPortableProcess(process);
        }
    }

    NativeCatalogProcessObservation INativeSoftwareIdentityProcessObserver.ObserveProcess(
        RuntimeProcessIdentity process)
    {
        lock (gate)
        {
            return GetCompiled().ObserveProcess(process);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            compiled?.Dispose();
            compiled = null;
        }
    }

    private SoftwareIdentityCatalogCompiler GetCompiled()
    {
        using var lease = owner.Acquire();
        lock (gate)
        {
            if (compiled is null
                || compiled.ConfigurationSha256 != lease.CatalogPlan.ConfigurationSha256)
            {
                var replacement = new SoftwareIdentityCatalogCompiler(
                    document,
                    lease.CatalogPlan)
                {
                    ConfigurationSha256 = lease.CatalogPlan.ConfigurationSha256
                };
                var previous = compiled;
                compiled = replacement;
                previous?.Dispose();
            }
            return compiled;
        }
    }

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
