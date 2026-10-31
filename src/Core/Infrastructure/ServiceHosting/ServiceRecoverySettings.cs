using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.CrashReports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

// The service's crash handling: SCM recovery (restart after a crash), WER LocalDumps through CrashDumpSettings
// (a dump of the crash), and the crash-report discovery that runs once when the service starts (the record of it).
public sealed class ServiceRecoverySettings(
    RuntimePlanProvider plans,
    BackendHostEnvironment environment,
    IServiceRecoveryControl control,
    CrashDumpSettings crashDumps,
    ICrashReportOwner crashReports,
    ILogger<ServiceRecoverySettings> logger) : IHostedService
{
    private readonly object gate = new();
    private Action<CompiledRuntimePlan>? onPublished;
    private (string Revision, bool Enabled, int Delay, int Count, int Reset)? attempted;
    private bool skipLogged;
    private ServiceRecoveryDiagnostics diagnostics = new("notApplied", 0);
    private Task? discovery;

    public ServiceRecoveryDiagnostics Diagnostics => Volatile.Read(ref diagnostics);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (onPublished is not null) return Task.CompletedTask;
            Action<CompiledRuntimePlan> callback = null!;
            callback = plan => Apply(plan, callback);
            onPublished = callback;
            plans.Published += callback;
            if (plans.Current.Version > 0) callback(plans.Current);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task running;
        lock (gate)
        {
            if (onPublished is { } callback) plans.Published -= callback;
            onPublished = null;
            running = discovery ?? Task.CompletedTask;
        }
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DiscoverCrashes()
    {
        try { crashReports.DiscoverAtStartup(); }
        catch (Exception error)
        {
            logger.LogError(error, "Crash report discovery failed.");
        }
    }

    // Null when this process is the installed service and may change the machine's crash handling.
    private string? SkipReason()
        => !environment.IsWindowsService ? "not-running-as-installed-service" : control.GetSkipReason();

    private void Apply(CompiledRuntimePlan plan, Action<CompiledRuntimePlan> callback)
    {
        lock (gate)
        {
            if (!ReferenceEquals(onPublished, callback)) return;
            crashDumps.Apply(plan, SkipReason);
            // Once per start, with the first plan (it carries the crash_reports point): a crash before this start is
            // registered now, so a restart after a crash marks its dump. Stop waits for it.
            discovery ??= Task.Run(DiscoverCrashes, CancellationToken.None);
            var settings = plan.ServiceRecovery;
            var reset = plan.HostManager.ServiceRecoveryResetPeriodSeconds;
            var key = (plan.HostManager.BindingProvenance.SettingsEffectiveSha256,
                settings.RestartAfterCrashEnabled, settings.CrashRestartDelaySeconds,
                settings.CrashRestartCount, reset);
            if (attempted == key) return;
            attempted = key; // A failed attempt is not retried by unrelated plan rebuilds.

            string? skip;
            try
            {
                skip = SkipReason();
            }
            catch (Exception error)
            {
                logger.LogError(error, "Could not verify the background service identity for crash recovery.");
                Volatile.Write(ref diagnostics, new("failed", reset, WriteError: error.Message));
                return;
            }
            if (skip is not null)
            {
                if (!skipLogged)
                {
                    logger.LogInformation("Skipped background service crash recovery configuration: {Reason}", skip);
                    skipLogged = true;
                }
                Volatile.Write(ref diagnostics, new("skipped", reset, SkipReason: skip));
                return;
            }

            var desired = CreateConfiguration(settings, reset);
            string? writeError = null;
            string? readError = null;
            ServiceRecoveryConfiguration? actual = null;
            try { control.Write(desired); }
            catch (Exception error)
            {
                writeError = error.Message;
                logger.LogError(error, "Could not write background service crash recovery configuration.");
            }
            try { actual = control.Read(); }
            catch (Exception error)
            {
                readError = error.Message;
                logger.LogError(error, "Could not read background service crash recovery configuration.");
            }
            var matches = actual is not null
                && (!settings.RestartAfterCrashEnabled || actual.ResetPeriodSeconds == desired.ResetPeriodSeconds)
                && !actual.FailureActionsOnNonCrashFailures
                && actual.Actions.SequenceEqual(desired.Actions);
            var state = writeError is not null || readError is not null ? "failed" : matches ? "applied" : "mismatch";
            if (state == "mismatch")
                logger.LogWarning("The observed background service crash recovery configuration differs from the saved preference.");
            Volatile.Write(ref diagnostics, new(state, reset, actual, WriteError: writeError, ReadError: readError));
        }
    }

    internal static ServiceRecoveryConfiguration CreateConfiguration(AppSystemIntegrationSettings settings, int resetSeconds)
    {
        if (resetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(resetSeconds));
        if (!settings.RestartAfterCrashEnabled) return new((uint)resetSeconds, [], false);
        var actions = Enumerable.Repeat(new ServiceRecoveryAction(1, checked((uint)settings.CrashRestartDelaySeconds * 1000)),
            settings.CrashRestartCount).Append(new ServiceRecoveryAction(0, 0)).ToArray();
        // Windows repeats the last action. NONE terminates the crash loop after the allowed restarts.
        return new((uint)resetSeconds, actions, false);
    }
}
