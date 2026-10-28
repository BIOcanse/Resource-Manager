using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Startup applies saved intent once; subsequent ticks maintain successful items only.</summary>
public sealed class ControlDesiredStateReassertion(
    IControlPlane plane,
    IAppSettingsStore settings,
    RuntimePlanProvider plans,
    ILogger<ControlDesiredStateReassertion>? logger = null) : BackgroundService
{
    private readonly SemaphoreSlim settingsChanged = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Changed(CompiledRuntimePlan _) { try { settingsChanged.Release(); } catch (SemaphoreFullException) { } }
        plans.Published += Changed;
        try
        {
            var lastPass = Environment.TickCount64;
            while (!stoppingToken.IsCancellationRequested)
            {
                // A plan publication only re-reads the interval; the next pass stays due from the last one.
                var current = await settings.LoadReadOnlyAsync(stoppingToken).ConfigureAwait(false);
                var interval = TimeSpan.FromSeconds(current.Settings.Control?.ReassertionIntervalSeconds ?? 120);
                var remaining = interval - TimeSpan.FromMilliseconds(Environment.TickCount64 - lastPass);
                if (remaining > TimeSpan.Zero
                    && await settingsChanged.WaitAsync(remaining, stoppingToken).ConfigureAwait(false)) continue;
                try { await plane.ReassertAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger?.LogWarning(error, "Control reassertion failed.");
                }
                lastPass = Environment.TickCount64;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { plans.Published -= Changed; }
    }
}
