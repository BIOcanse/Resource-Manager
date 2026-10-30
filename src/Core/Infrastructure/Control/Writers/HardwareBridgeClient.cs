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
    private static HardwareBridgeProcessOptions ProductOptions { get; } = CreateProductOptions(HardwareControlSettings.ProductDefaults);

    private static HardwareBridgeProcessOptions CreateProductOptions(CompiledHardwareControlSettings settings)
        => new(settings.HelperRequestTimeout,
            TimeSpan.FromMilliseconds(settings.HelperProcessTiming.DiagnosticDrainTimeoutMilliseconds),
            ControlHelperErrorOutput.ProductOptions)
        {
            StopTimeout = TimeSpan.FromMilliseconds(settings.HelperProcessTiming.StopTimeoutMilliseconds),
            ReadingInterval = TimeSpan.FromMilliseconds(settings.HelperProcessTiming.ReadingIntervalMilliseconds),
            AvailabilityCheckInterval = ReadAvailabilityCheckInterval(),
            IsRuntimeAvailable = IsRuntimeAvailable
        };

    // The client is constructed before the first runtime plan publication. Compile the same
    // embedded declaration as the typed plan, as the backend control owner does at startup.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static TimeSpan ReadAvailabilityCheckInterval()
        => HostManagerPlanCompiler.CompileHardwareBridgeAvailabilityCheckInterval(EmbeddedFreedomPointDefaults.Begin());

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
