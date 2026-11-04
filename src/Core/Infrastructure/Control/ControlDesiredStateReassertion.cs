using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>While the tree runs this block, maintain successful items at the configured interval.</summary>
public sealed class ControlDesiredStateReassertion(
    IControlPlane plane,
    IAppSettingsStore settings,
    RuntimePlanProvider plans,
    ILogger<ControlDesiredStateReassertion>? logger = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var settingsChanged = new SemaphoreSlim(0, 1);
        var clock = timeProvider ?? TimeProvider.System;
        void Changed(CompiledRuntimePlan _)
        {
            try { settingsChanged.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { } // A publication may already have captured the detached callback.
        }
        plans.Published += Changed;
        try
        {
            var lastPass = clock.GetTimestamp();
            while (!stoppingToken.IsCancellationRequested)
            {
                // A plan publication only re-reads the interval; the next pass stays due from the last one.
                var current = await settings.LoadReadOnlyAsync(stoppingToken).ConfigureAwait(false);
                var interval = TimeSpan.FromSeconds(current.Settings.Control?.ReassertionIntervalSeconds ?? 120);
                var remaining = interval - clock.GetElapsedTime(lastPass);
                if (remaining > TimeSpan.Zero)
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    try
                    {
                        await settingsChanged.WaitAsync(wake.Token).WaitAsync(remaining, clock, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (TimeoutException) { }
                    finally { await wake.CancelAsync().ConfigureAwait(false); }
                }
                stoppingToken.ThrowIfCancellationRequested();
                try { await plane.ReassertAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger?.LogWarning(error, "Control reassertion failed.");
                }
                lastPass = clock.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { plans.Published -= Changed; }
    }
}
