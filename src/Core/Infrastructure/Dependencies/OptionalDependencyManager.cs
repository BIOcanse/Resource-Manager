using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager : IOptionalDependencyManager
{
    private readonly HttpClient httpClient;
    private readonly IFileChangeTracker fileChangeTracker;
    private readonly IInstalledSoftwareInventory installedSoftwareInventory;
    private readonly IDependencyInstallerSourceResolver installerSourceResolver;
    private readonly IInteractiveUserSessionBroker sessionBroker;
    private readonly string packageRoot;
    private readonly HardwareBridgeClient hardwareBridge;
    private readonly ILogger<OptionalDependencyManager>? logger;

    public OptionalDependencyManager(
        HttpClient httpClient,
        IHostEnvironment environment,
        IFileChangeTracker fileChangeTracker,
        IInstalledSoftwareInventory installedSoftwareInventory,
        IDependencyInstallerSourceResolver installerSourceResolver,
        HardwareBridgeClient hardwareBridge,
        IInteractiveUserSessionBroker? sessionBroker = null,
        ILogger<OptionalDependencyManager>? logger = null)
    {
        this.httpClient = httpClient;
        this.fileChangeTracker = fileChangeTracker;
        this.installedSoftwareInventory = installedSoftwareInventory;
        this.installerSourceResolver = installerSourceResolver;
        this.hardwareBridge = hardwareBridge;
        this.logger = logger;
        this.sessionBroker = sessionBroker ?? new WindowsInteractiveUserSessionBroker();
        packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
    }

    private string DependenciesRoot => Path.Combine(packageRoot, "Dependencies");

    private string InstallerCacheRoot => Path.Combine(packageRoot, "Misc", "DependencyInstallers");

    public async Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        => await GetStatusesAsync(windowsSid: null, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
    {
        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(windowsSid, cancellationToken);
        IReadOnlyList<OptionalDependencyStatus> statuses = OptionalDependencyCatalog.Definitions
            .Select(definition => BuildStatus(definition, installedSoftware))
            .ToArray();

        return statuses;
    }

    public async Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        => await GetStatusAsync(id, windowsSid: null, cancellationToken: cancellationToken);

    public async Task<OptionalDependencyStatus?> GetStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken)
    {
        var definition = OptionalDependencyCatalog.Find(id);
        if (definition is null)
        {
            return null;
        }

        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(windowsSid, cancellationToken);
        return BuildStatus(definition, installedSoftware);
    }

    public Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken)
    {
        var definition = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Unknown dependency: {id}");
        return installerSourceResolver.GetVersionOptionsAsync(definition, cancellationToken);
    }
}
