using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Updates;

public interface IProductPackageUsage
{
    bool AnyInUse();
}

// Scheduling advice only. The updater owns the exclusive lease during mutation.
public sealed class ProductPackageUsage(IHostEnvironment environment) : IProductPackageUsage
{
    public bool AnyInUse()
    {
        using var lease = PackageUseGuard.TryAcquireMaintenance(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath));
        return lease is null;
    }
}

public sealed class AutomaticProductUpdateService : BackgroundService
{
    // File sharing locks have no release notification. This only schedules handoff;
    // the manager re-acquires and holds the actual maintenance lease.
    internal static readonly TimeSpan PackageUseRecheckInterval = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim settingsChanged = new(0, 1);
    private readonly Func<CancellationToken, Task<AppUpdateSettings>> readSettings;
    private readonly RuntimePlanProvider plans;
    private readonly ProductUpdateCoordinator coordinator;
    private readonly IProductPackageUsage packageUsage;
    private readonly IInternetConnectivity connectivity;
    private readonly TimeProvider clock;
    private readonly ILogger<AutomaticProductUpdateService> logger;
    private volatile bool checkAtStart = true;
    private DateTimeOffset? nextPackageUseCheck;

    public AutomaticProductUpdateService(IAppSettingsStore settings, ProductUpdateCoordinator coordinator,
        IProductPackageUsage packageUsage, RuntimePlanProvider plans, TimeProvider clock, ILogger<AutomaticProductUpdateService> logger,
        IInternetConnectivity connectivity)
        : this(async token => (await settings.LoadReadOnlyAsync(token)).Settings.Update
            ?? throw new InvalidDataException("Normalized update settings are missing."), coordinator, packageUsage, plans, clock, logger, connectivity) { }

    internal AutomaticProductUpdateService(Func<CancellationToken, Task<AppUpdateSettings>> readSettings,
        ProductUpdateCoordinator coordinator, IProductPackageUsage packageUsage, RuntimePlanProvider plans, TimeProvider clock,
        ILogger<AutomaticProductUpdateService> logger, IInternetConnectivity connectivity)
    {
        this.readSettings = readSettings;
        this.plans = plans;
        this.coordinator = coordinator;
        this.packageUsage = packageUsage;
        this.connectivity = connectivity;
        this.clock = clock;
        this.logger = logger;
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        var settings = await readSettings(token);
        ConfigureSchedule(settings);
        await RunPassAsync(settings, checkPackageUse: true, token);
    }

    private async Task RunPassAsync(AppUpdateSettings settings, bool checkPackageUse, CancellationToken token)
    {
        var status = coordinator.Status;
        if (settings.Mode == AppUpdateModes.Off) { checkAtStart = true; return; }
        if (connectivity.IsOnline && (checkAtStart || settings.PeriodicCheckEnabled
            && status.NextCheckAt is { } next && clock.GetUtcNow().ToUniversalTime() >= next))
        {
            if (await coordinator.CheckAutomaticAsync(token, prepareUpdate: false)) checkAtStart = false;
            ConfigureSchedule(settings);
        }
        if (settings.Mode != AppUpdateModes.Automatic) return;
        await coordinator.PrepareAutomaticAsync(token);
        // Leaving automatic mode cancels preparation and prevents application.
        token.ThrowIfCancellationRequested();
        if (coordinator.Status.Stage == "pendingApply"
            && (checkPackageUse || status.Stage != "pendingApply"))
        {
            nextPackageUseCheck = clock.GetUtcNow().ToUniversalTime() + PackageUseRecheckInterval;
            if (!packageUsage.AnyInUse()) await coordinator.ApplyAsync(token);
        }
        if (coordinator.Status.Stage != "pendingApply") nextPackageUseCheck = null;
    }

    private TimeSpan NextDelay(AppUpdateSettings settings)
    {
        if (settings.Mode == AppUpdateModes.Off) return Timeout.InfiniteTimeSpan;
        var status = coordinator.Status;
        var now = clock.GetUtcNow().ToUniversalTime();
        // An in-flight prepare is awaited below. After handoff, the registered
        // manager owns completion/recovery and normally restarts this service.
        if (status.Stage is "checking" or "downloading" or "verifying" or "waitingForExit")
            return Timeout.InfiniteTimeSpan;
        if (checkAtStart && connectivity.IsOnline) return TimeSpan.Zero;
        var delay = status.NextCheckAt is { } next ? Remaining(next - now) : Timeout.InfiniteTimeSpan;
        if (settings.Mode == AppUpdateModes.Automatic)
        {
            if (coordinator.HasPendingAutomaticPreparation) return TimeSpan.Zero;
            if (status.Stage == "pendingApply")
            {
                var packageUseDelay = Remaining((nextPackageUseCheck ?? now) - now);
                if (delay == Timeout.InfiniteTimeSpan || packageUseDelay < delay) delay = packageUseDelay;
            }
        }
        return delay;
    }

    private void ConfigureSchedule(AppUpdateSettings settings)
        => coordinator.ConfigureSchedule(settings.Mode, settings.PeriodicCheckEnabled,
            settings.CheckIntervalHours, connectivity.IsOnline, checkAtStart);

    private static TimeSpan Remaining(TimeSpan delay) => delay > TimeSpan.Zero ? delay : TimeSpan.Zero;

    private async Task<bool> WaitForWakeAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay == Timeout.InfiniteTimeSpan)
        {
            await settingsChanged.WaitAsync(token);
            return true;
        }
        // A cancellable semaphore waiter avoids leaving an abandoned waiter that
        // could consume a later publication after the injected-clock timeout.
        using var deadline = new CancellationTokenSource(delay, clock);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { await settingsChanged.WaitAsync(wait.Token); return true; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task? activePass = null;
        string? activePassMode = null;
        CancellationTokenSource? passCancellation = null;
        void Wake() { try { settingsChanged.Release(); } catch (SemaphoreFullException) { } }
        void Changed(CompiledRuntimePlan _) => Wake();
        AppUpdateSettings? currentSettings = null;
        var connectivityCallbackGate = new object();
        var acceptingConnectivityCallbacks = true;
        void ConnectivityChanged()
        {
            lock (connectivityCallbackGate)
            {
                if (!acceptingConnectivityCallbacks) return;
                if (Volatile.Read(ref currentSettings) is { } current) ConfigureSchedule(current);
                Wake();
            }
        }
        plans.Published += Changed;
        coordinator.Changed += Wake;
        connectivity.Changed += ConnectivityChanged;
        try
        {
            var settings = await readSettings(stoppingToken);
            var refreshSettings = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (refreshSettings)
                    {
                        settings = await readSettings(stoppingToken);
                        refreshSettings = false;
                    }
                    if (settings.Mode != AppUpdateModes.Automatic)
                    {
                        nextPackageUseCheck = null;
                        // A notify check is allowed to finish; only preparation
                        // started in automatic mode must be cancelled.
                        if (settings.Mode == AppUpdateModes.Off || activePassMode == AppUpdateModes.Automatic) passCancellation?.Cancel();
                    }
                    if (settings.Mode == AppUpdateModes.Off) checkAtStart = true;
                    Volatile.Write(ref currentSettings, settings);
                    ConfigureSchedule(settings);
                    if (activePass?.IsCompleted == true)
                    {
                        try { await activePass; }
                        catch (OperationCanceledException) when (passCancellation!.IsCancellationRequested) { }
                        finally { activePass = null; passCancellation!.Dispose(); passCancellation = null; }
                    }
                    if (activePass is null && NextDelay(settings) == TimeSpan.Zero)
                    {
                        passCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        activePassMode = settings.Mode;
                        activePass = RunPassAsync(settings,
                            nextPackageUseCheck is null || clock.GetUtcNow().ToUniversalTime() >= nextPackageUseCheck,
                            passCancellation.Token);
                    }

                    using var wakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var wake = WaitForWakeAsync(activePass is null ? NextDelay(settings) : Timeout.InfiniteTimeSpan,
                        wakeCancellation.Token);
                    if (activePass is not null && await Task.WhenAny(activePass, wake) == activePass)
                    {
                        wakeCancellation.Cancel();
                        try { refreshSettings = await wake; }
                        catch (OperationCanceledException) when (wakeCancellation.IsCancellationRequested) { }
                    }
                    else refreshSettings = await wake;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Automatic update scheduling failed.");
                    refreshSettings = await WaitForWakeAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (connectivityCallbackGate) acceptingConnectivityCallbacks = false;
            plans.Published -= Changed;
            coordinator.Changed -= Wake;
            connectivity.Changed -= ConnectivityChanged;
            passCancellation?.Cancel();
            if (activePass is not null)
            {
                try { await activePass; }
                catch (OperationCanceledException) when (passCancellation!.IsCancellationRequested) { }
            }
            passCancellation?.Dispose();
        }
    }
}
