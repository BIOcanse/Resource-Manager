using System.Diagnostics;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Updates;

public interface IProductUiSessionPresence
{
    bool AnyOpen();
}

// GetProcessesByName includes processes in every Windows user session. An
// inaccessible process is treated as present, so inspection cannot force an exit.
public sealed class ProductUiSessionPresence(IHostEnvironment environment) : IProductUiSessionPresence
{
    public bool AnyOpen()
    {
        var root = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var processes = Process.GetProcessesByName("ResourceManager.NativeUi");
        var open = false;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } path
                        || Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) open = true;
                }
                catch (System.ComponentModel.Win32Exception) { open = true; }
                catch (InvalidOperationException) { /* Process exited while inspected. */ }
            }
        }
        return open;
    }
}

public sealed class AutomaticProductUpdateService : BackgroundService
{
    // Windows does not notify us when a tray/UI process exits. Application may
    // therefore wait up to one interval after the last UI exits.
    internal static readonly TimeSpan UiPresenceRecheckInterval = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim settingsChanged = new(0, 1);
    private readonly Func<CancellationToken, Task<AppUpdateSettings>> readSettings;
    private readonly RuntimePlanProvider plans;
    private readonly ProductUpdateCoordinator coordinator;
    private readonly IProductUiSessionPresence sessions;
    private readonly TimeProvider clock;
    private readonly ILogger<AutomaticProductUpdateService> logger;
    private bool checkAtStart = true;
    private DateTimeOffset? nextUiPresenceCheck;

    public AutomaticProductUpdateService(IAppSettingsStore settings, ProductUpdateCoordinator coordinator,
        IProductUiSessionPresence sessions, RuntimePlanProvider plans, TimeProvider clock, ILogger<AutomaticProductUpdateService> logger)
        : this(async token => (await settings.LoadReadOnlyAsync(token)).Settings.Update
            ?? throw new InvalidDataException("Normalized update settings are missing."), coordinator, sessions, plans, clock, logger) { }

    internal AutomaticProductUpdateService(Func<CancellationToken, Task<AppUpdateSettings>> readSettings,
        ProductUpdateCoordinator coordinator, IProductUiSessionPresence sessions, RuntimePlanProvider plans, TimeProvider clock,
        ILogger<AutomaticProductUpdateService> logger)
    {
        this.readSettings = readSettings;
        this.plans = plans;
        this.coordinator = coordinator;
        this.sessions = sessions;
        this.clock = clock;
        this.logger = logger;
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        var settings = await readSettings(token);
        await RunPassAsync(settings, checkPresence: true, token);
    }

    private async Task RunPassAsync(AppUpdateSettings settings, bool checkPresence, CancellationToken token)
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
            && (checkPresence || status.Stage != "pendingApply"))
        {
            nextUiPresenceCheck = clock.GetUtcNow() + UiPresenceRecheckInterval;
            if (!sessions.AnyOpen()) await coordinator.ApplyAsync(token);
        }
        if (coordinator.Status.Stage != "pendingApply") nextUiPresenceCheck = null;
    }

    private TimeSpan NextDelay(AppUpdateSettings settings)
    {
        if (!settings.AutoUpdateEnabled) return Timeout.InfiniteTimeSpan;
        if (checkAtStart) return TimeSpan.Zero;
        var status = coordinator.Status;
        var now = clock.GetUtcNow();
        if (status.Stage == "pendingApply")
            return Remaining((nextUiPresenceCheck ?? now) - now);
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
                        nextUiPresenceCheck = null;
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
                            nextUiPresenceCheck is null || clock.GetUtcNow() >= nextUiPresenceCheck,
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
