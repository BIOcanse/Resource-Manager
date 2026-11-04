using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

public sealed partial class EtwSystemInterruptSnapshotSource
{
    private readonly object metadataResourceGate = new();
    private IKernelEtwSubscription? durationSubscription;
    private long metadataEpoch, durationEpoch;
    private bool acquiringDuration;

    private void SynchronizeDurationHold()
    {
        bool required;
        long epoch = 0;
        IKernelEtwSubscription? release = null;
        lock (subscriptionGate)
        {
            required = !disposed && kernelSubscription is not null;
            if (!required)
            {
                ++durationEpoch;
                acquiringDuration = false;
                release = durationSubscription;
                durationSubscription = null;
            }
            else if (durationSubscription is not null || acquiringDuration) return;
            else { acquiringDuration = true; epoch = ++durationEpoch; }
        }
        release?.Dispose();
        if (!required) return;
        IKernelEtwSubscription? created = null;
        var accepted = false;
        try
        {
            // A broker may bind on another thread or finish after Stop.
            // Do not hold the control gate while waiting for that acquisition.
            created = kernelEtwBroker.Acquire(new KernelEtwSubscriptionRequest("system-interrupts-duration", DurationKeywords,
                (source, context) =>
                {
                    lock (subscriptionGate)
                        if (!disposed && epoch == durationEpoch)
                            BindDurationEvents(source, context);
                }) { NativeOutputs = KernelEtwNativeOutputs.Interrupts });
            _ = created.GetSnapshot();
            lock (subscriptionGate)
                if (!disposed && epoch == durationEpoch && kernelSubscription is not null)
                {
                    durationSubscription = created;
                    accepted = true;
                }
        }
        finally
        {
            lock (subscriptionGate)
            {
                if (epoch == durationEpoch) acquiringDuration = false;
                if (!accepted && ReferenceEquals(durationSubscription, created)) durationSubscription = null;
            }
            if (!accepted) created?.Dispose();
        }
    }

    private KernelEtwSessionSnapshot ReadBrokerSnapshot()
    {
        var duration = Volatile.Read(ref durationSubscription)?.GetSnapshot();
        var metadata = Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
        if (!metadata.IsRunning) return metadata;
        return duration ?? metadata with { State = "Unavailable", Message = "ETW interrupt keywords are unavailable." };
    }
}
