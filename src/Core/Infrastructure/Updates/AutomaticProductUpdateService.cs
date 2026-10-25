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
    private readonly TimeProvider clock;
    private readonly ILogger<AutomaticProductUpdateService> logger;
    private bool checkAtStart = true;
    private DateTimeOffset? nextPackageUseCheck;

    public AutomaticProductUpdateService(IAppSettingsStore settings, ProductUpdateCoordinator coordinator,
        IProductPackageUsage packageUsage, RuntimePlanProvider plans, TimeProvider clock, ILogger<AutomaticProductUpdateService> logger)
        : this(async token => (await settings.LoadReadOnlyAsync(token)).Settings.Update
            ?? throw new InvalidDataException("Normalized update settings are missing."), coordinator, packageUsage, plans, clock, logger) { }

    internal AutomaticProductUpdateService(Func<CancellationToken, Task<AppUpdateSettings>> readSettings,
        ProductUpdateCoordinator coordinator, IProductPackageUsage packageUsage, RuntimePlanProvider plans, TimeProvider clock,
        ILogger<AutomaticProductUpdateService> logger)
    {
        this.readSettings = readSettings;
        this.plans = plans;
        this.coordinator = coordinator;
        this.packageUsage = packageUsage;
        this.clock = clock;
        this.logger = logger;
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        var settings = await readSettings(token);
        await RunPassAsync(settings, checkPackageUse: true, token);
    }

    private async Task RunPassAsync(AppUpdateSettings settings, bool checkPackageUse, CancellationToken token)
    {
        if (!settings.AutoUpdateEnabled) { checkAtStart = true; return; }
        var status = coordinator.Status;
        if (checkAtStart || status.LastCheckedAt is null
            || clock.GetUtcNow() - status.LastCheckedAt >= TimeSpan.FromHours(settings.CheckIntervalHours))
        {
            checkAtStart = false;
            await coordinator.CheckAutomaticAsync(token);
        }
        // Publications refresh settings; only turning the switch off cancels
        // this preparation token and prevents the subsequent application.
        token.ThrowIfCancellationRequested();
        if (coordinator.Status.Stage == "pendingApply"
            && (checkPackageUse || status.Stage != "pendingApply"))
        {
            nextPackageUseCheck = clock.GetUtcNow() + PackageUseRecheckInterval;
            if (!packageUsage.AnyInUse()) await coordinator.ApplyAsync(token);
        }
        if (coordinator.Status.Stage != "pendingApply") nextPackageUseCheck = null;
    }

    private TimeSpan NextDelay(AppUpdateSettings settings)
    {
        if (!settings.AutoUpdateEnabled) return Timeout.InfiniteTimeSpan;
        if (checkAtStart) return TimeSpan.Zero;
        var status = coordinator.Status;
        var now = clock.GetUtcNow();
        if (status.Stage == "pendingApply")
            return Remaining((nextPackageUseCheck ?? now) - now);
        // An in-flight prepare is awaited below. After handoff, the registered
        // manager owns completion/recovery and normally restarts this service.
        if (status.Stage is "checking" or "downloading" or "verifying" or "waitingForExit")
            return Timeout.InfiniteTimeSpan;
        return Remaining((status.LastCheckedAt ?? now) + TimeSpan.FromHours(settings.CheckIntervalHours) - now);
    }

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
        CancellationTokenSource? passCancellation = null;
        void Wake() { try { settingsChanged.Release(); } catch (SemaphoreFullException) { } }
        void Changed(CompiledRuntimePlan _) => Wake();
        plans.Published += Changed;
        coordinator.StageChanged += Wake;
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
                    if (!settings.AutoUpdateEnabled)
                    {
                        checkAtStart = true;
                        nextPackageUseCheck = null;
                        passCancellation?.Cancel();
                    }
                    if (activePass?.IsCompleted == true)
                    {
                        try { await activePass; }
                        catch (OperationCanceledException) when (passCancellation!.IsCancellationRequested) { }
                        finally { activePass = null; passCancellation!.Dispose(); passCancellation = null; }
                    }
                    if (activePass is null && NextDelay(settings) == TimeSpan.Zero)
                    {
                        passCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        activePass = RunPassAsync(settings,
                            nextPackageUseCheck is null || clock.GetUtcNow() >= nextPackageUseCheck,
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
            plans.Published -= Changed;
            coordinator.StageChanged -= Wake;
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
