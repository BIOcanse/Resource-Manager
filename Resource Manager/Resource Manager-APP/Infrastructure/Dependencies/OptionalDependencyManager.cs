using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager : IOptionalDependencyManager
{
    private readonly HttpClient httpClient;
    private readonly IFileChangeTracker fileChangeTracker;
    private readonly IInstalledSoftwareInventory installedSoftwareInventory;
    private readonly string packageRoot;

    public OptionalDependencyManager(
        HttpClient httpClient,
        IHostEnvironment environment,
        IFileChangeTracker fileChangeTracker,
        IInstalledSoftwareInventory installedSoftwareInventory)
    {
        this.httpClient = httpClient;
        this.fileChangeTracker = fileChangeTracker;
        this.installedSoftwareInventory = installedSoftwareInventory;
        packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
    }

    private string DependenciesRoot => Path.Combine(packageRoot, "Dependencies");

    private string InstallerCacheRoot => Path.Combine(packageRoot, "Misc", "DependencyInstallers");

    public async Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(cancellationToken);
        IReadOnlyList<OptionalDependencyStatus> statuses = OptionalDependencyCatalog.Definitions
            .Select(definition => BuildStatus(definition, installedSoftware))
            .ToArray();

        return statuses;
    }

    public async Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
    {
        var definition = OptionalDependencyCatalog.Find(id);
        if (definition is null)
        {
            return null;
        }

        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(cancellationToken);
        return BuildStatus(definition, installedSoftware);
    }
}
