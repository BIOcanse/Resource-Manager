using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class ServiceRecoverySettings(
    RuntimePlanProvider plans,
    BackendHostEnvironment environment,
    IServiceRecoveryControl control,
    ILogger<ServiceRecoverySettings> logger) : IHostedService
{
    private readonly object gate = new();
    private (string Revision, bool Enabled, int Delay, int Count, int Reset)? attempted;
    private bool skipLogged;
    private ServiceRecoveryDiagnostics diagnostics = new("notApplied", 0);

    public ServiceRecoveryDiagnostics Diagnostics => Volatile.Read(ref diagnostics);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        plans.Published += Apply;
        if (plans.Current.Version > 0) Apply(plans.Current);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        plans.Published -= Apply;
        return Task.CompletedTask;
    }

    private void Apply(CompiledRuntimePlan plan)
    {
        lock (gate)
        {
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
                skip = !environment.IsWindowsService
                    ? "not-running-as-installed-service" : control.GetSkipReason();
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
