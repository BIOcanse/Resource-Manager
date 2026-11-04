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

    // The native due time starts a sample; its finite observation window is
    // collected by that sample. Preparing before due shortened the first
    // cold-to-steady publication interval by one whole observation window.
    // Delivery-only wakes likewise must not start context-switch capture.
    private Task WaitForNextWindowPlanAsync(TimeSpan delay, CancellationToken cancellationToken)
        => demandSignal.WaitAsync(delay, cancellationToken);

    // A demand change interrupts the window, so removal/frequency changes do not wait for old coverage.
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
