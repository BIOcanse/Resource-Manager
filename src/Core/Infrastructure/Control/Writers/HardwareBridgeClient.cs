using System.Diagnostics;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product dependency path and logging adapter. Control policy stays with the existing writers.</summary>
public sealed class HardwareBridgeClient : HardwareBridgeProcessClient
{
    public HardwareBridgeClient(IHostEnvironment environment, ILogger<HardwareBridgeClient>? logger = null,
        StateAuthority? authority = null)
        : this(() => ResolveExecutablePath(environment), ProductOptions, authority, logger) { }

    internal HardwareBridgeClient(Func<string?> resolveExecutablePath, HardwareBridgeProcessOptions options,
        StateAuthority? authority, ILogger<HardwareBridgeClient>? logger = null,
        Action<HardwareBridgeProcessOwner, bool>? beforeAvailabilityPublication = null)
        : base(resolveExecutablePath, options,
            logger is null ? null : (error, detail) => logger.LogWarning(error, "Hardware helper: {Detail}", detail))
    {
        _ = new HardwareBridgeReadingSubscriptions(Owner, authority);
        if (authority is not null)
        {
            var available = authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable);
            void Publish(bool value)
            {
                beforeAvailabilityPublication?.Invoke(Owner, value);
                _ = PublishAvailabilityAsync(available, value, logger);
            }
            Owner.AvailabilityChanged += Publish;
            Owner.PublishCurrentAvailability();
        }
    }
    internal HardwareBridgeClient(IHostEnvironment environment, Func<ProcessStartInfo, Process?> startFactory)
        : base(() => ResolveExecutablePath(environment), ProductOptions, startFactory: startFactory) { }
    private static HardwareBridgeProcessOptions ProductOptions { get; } =
        new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1), ControlHelperErrorOutput.ProductOptions)
        {
            AvailabilityCheckInterval = ReadAvailabilityCheckInterval(),
            IsRuntimeAvailable = IsRuntimeAvailable
        };

    // The client is constructed before the first runtime plan publication. Compile the same
    // embedded declaration as the typed plan, as the backend control owner does at startup.
    private static TimeSpan ReadAvailabilityCheckInterval()
    {
        var registry = FreedomPointRegistry.LoadEmbedded();
        var profile = StrictHostManagerProfileLoader.LoadBytes(
            StrictHostManagerProfileLoader.ReadExpandedDefaultBytes(), "embedded/HostManager.default.json").Profile;
        return HostManagerPlanCompiler.CompileHardwareBridgeAvailabilityCheckInterval(
            registry.BeginCompilation(profile, new CompiledCpuScoringPlan(1d, CompiledCpuCoreWeights.Empty)));
    }

    private static bool IsRuntimeAvailable()
    {
        var runtimes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "shared", "Microsoft.NETCore.App");
        return Directory.Exists(runtimes) && Directory.EnumerateDirectories(runtimes, "10.*")
            .Any(path => File.Exists(Path.Combine(path, "coreclr.dll")));
    }

    private static async Task PublishAvailabilityAsync(StateCell<bool> cell, bool value,
        ILogger<HardwareBridgeClient>? logger)
    {
        try
        {
            await cell.PublishAsync(value, new(BusinessBlockRequestKind.Configuration,
                nameof(HardwareBridgeClient), "bridge-availability", 0)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Hardware helper availability publication failed.");
        }
    }
    private static string? ResolveExecutablePath(IHostEnvironment environment)
    {
        var path = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies", "hardware-bridge", "ResourceManager.HardwareBridge.exe");
        return File.Exists(path) ? path : null;
    }
}
