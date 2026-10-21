using System.Diagnostics;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product dependency path and logging adapter. Control policy stays with the existing writers.</summary>
public sealed class HardwareBridgeClient : HardwareBridgeProcessClient
{
    public HardwareBridgeClient(IHostEnvironment environment, ILogger<HardwareBridgeClient>? logger = null)
        : base(() => ResolveExecutablePath(environment), ProductOptions,
            logger is null ? null : (error, detail) => logger.LogWarning(error, "Hardware helper: {Detail}", detail)) { }
    internal HardwareBridgeClient(IHostEnvironment environment, Func<ProcessStartInfo, Process?> startFactory)
        : base(() => ResolveExecutablePath(environment), ProductOptions, startFactory: startFactory) { }
    private static HardwareBridgeProcessOptions ProductOptions { get; } =
        new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1), ControlHelperErrorOutput.ProductOptions);
    private static string? ResolveExecutablePath(IHostEnvironment environment)
    {
        var path = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies", "hardware-bridge", "ResourceManager.HardwareBridge.exe");
        return File.Exists(path) ? path : null;
    }
}
