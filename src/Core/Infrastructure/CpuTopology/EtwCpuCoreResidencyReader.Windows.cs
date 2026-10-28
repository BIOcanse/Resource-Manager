using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class EtwCpuCoreResidencyReader
{
    private TimeSpan captureInterval;
    private TimeSpan deliveryGrace;
    private TimeSpan ObservationWindow => runtimePlanProvider.Current.HostManager.RequirePublished().CpuCoreResidency.ObservationWindow;
    private bool WindowedCapture => captureInterval > ObservationWindow;

    private void UpdateCaptureFrequency(NativeItemSamplingSubscriptionPlan<CpuResidencySampleRequest, SamplingOwnerToken> plan)
    {
        var wasWindowed = WindowedCapture;
        var previousInterval = captureInterval;
        captureInterval = plan.SourceViews.Count == 0 ? TimeSpan.Zero : plan.SourceViews.Min(static source => source.Interval);
        deliveryGrace = plan.Schedule.FreshnessGrace;
        if (previousInterval > TimeSpan.Zero && !wasWindowed && WindowedCapture && Capture.IsWindowActive)
        {
            Capture.PauseWindow("Aggregation is paused after the source cadence changes from continuous to finite windows.");
        }
    }

    private async Task WaitForNextWindowPlanAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!WindowedCapture)
        {
            await demandSignal.WaitAsync(delay, cancellationToken);
            return;
        }
        // The native tracker still owns the due time. Only its observation window is prepared ahead of it.
        // A timer can return just before the native due time. Retain the same
        // unpublished window across that final wait rather than opening it twice.
        if (!Capture.IsWindowActive)
        {
            var idle = delay - ObservationWindow;
            if (idle > TimeSpan.Zero && await demandSignal.WaitAsync(RoundUpTimerDelay(idle), cancellationToken)) return;
            Capture.BeginWindow();
        }
        if (await WaitForWindowOrDemandAsync(cancellationToken)) return;
        // A failed or already-ready preparation must not spin/retry before the native due time.
        var remaining = delay - System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero) await demandSignal.WaitAsync(RoundUpTimerDelay(remaining), cancellationToken);
    }

    private static TimeSpan RoundUpTimerDelay(TimeSpan delay)
        => TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));

    // A demand change interrupts preparation, so removal/frequency changes do not wait for an old window.
    private async Task<bool> WaitForWindowOrDemandAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var window = Capture.WaitForCompleteWindowAsync(ObservationWindow, deliveryGrace, wait.Token);
        var demand = demandSignal.WaitAsync(wait.Token);
        try
        {
            if (await Task.WhenAny(window, demand) == demand) { await demand; return true; }
            await window;
            return false;
        }
        finally
        {
            wait.Cancel();
            try { await demand; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            try { await window; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        }
    }
}
