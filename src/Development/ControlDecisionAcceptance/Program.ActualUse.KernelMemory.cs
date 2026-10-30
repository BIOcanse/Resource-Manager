using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private sealed record ActualUseInterruptModuleIdentity(long Generation, ulong OwnerSerial, ulong ActiveModules);
    private sealed record ActualUseKernelMemorySample(string Phase, DateTimeOffset At, bool WindowBefore, bool WindowAfter,
        KernelEtwAllocatorUsage? Allocation, CpuResidencyAggregationDiagnostics? Aggregation, long ManagedAllocatedBytes);
    private sealed record ActualUseManagedAllocationRow(string Phase, double WindowSeconds, long WindowBytes,
        double BetweenSeconds, long BetweenBytes, double WindowBytesPerSecond, double BetweenBytesPerSecond);
    private sealed record ActualUseKernelMemoryRow(string Scenario, ulong CommittedBytes, ulong PeakCommittedBytes,
        ulong RequestedBytes, string Measurement, int Samples, int? ActivePairs, int? Threads,
        IReadOnlyList<KernelEtwAllocationOwnerUsage> Owners);

    // This finite validation owns and consumes an IRQ reference. It starts no block.
    // The real broker owns the session for exactly this hold's try/finally lifetime.
    private static async Task ObserveIrqOnlyMemory(IKernelEtwSessionBroker broker,
        List<ActualUseKernelMemorySample> samples, CancellationToken token)
    {
        KernelEtwEventSource? source = null;
        using var hold = broker.Acquire(new KernelEtwSubscriptionRequest("actual-use:irq-memory",
            KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls,
            (value, _) => source = value) { NativeOutputs = KernelEtwNativeOutputs.Interrupts });
        await WaitAsync(() => source?.IsActive == true && hold.GetSnapshot().IsRunning, token);
        var session = source!.NativeSession;
        ulong sequence = 0;
        await WaitAsync(() => KernelEtwNativeReaders.ReadInterrupts(session, ref sequence,
            static view => view.EventCount, out _, out _), token);
        var usage = hold.GetSnapshot().AllocationUsage
            ?? throw new InvalidDataException("IRQ-only hold has no native allocation evidence.");
        if (usage.CommittedBytes == 0 || usage.CommitMeasurement != "VirtualQuery"
            || usage.Owners.Any(value => value.Owner is not (0 or 2) && value.CommittedBytes != 0))
            throw new InvalidDataException("IRQ-only hold allocated an unrelated child or lacks committed-page evidence.");
        samples.Add(new("irq-only", DateTimeOffset.UtcNow, false, false, usage, null, GC.GetTotalAllocatedBytes()));
    }

    // Candidate .4 (managed residency) allocated 140-340 MB in each 5 s residency window against 15-25 MB per 5 s
    // otherwise, and those objects died in gen2. With residency native, time inside a window allocates at about the
    // between-window rate. Only intervals whose two samples agree on the window state are counted; the probe's own
    // sampling allocates on both sides.
    private static ActualUseManagedAllocationRow[] SummarizeManagedWindowAllocation(List<ActualUseKernelMemorySample> samples)
    {
        var rows = new List<ActualUseManagedAllocationRow>();
        foreach (var phase in new[] { "b-", "c-" })
        {
            double windowSeconds = 0, betweenSeconds = 0;
            long windowBytes = 0, betweenBytes = 0;
            ActualUseKernelMemorySample? previous = null;
            foreach (var sample in samples.Where(value => value.Phase.StartsWith(phase, StringComparison.Ordinal)))
            {
                if (previous is not null && previous.Phase == sample.Phase)
                {
                    var seconds = (sample.At - previous.At).TotalSeconds;
                    var bytes = sample.ManagedAllocatedBytes - previous.ManagedAllocatedBytes;
                    if (previous.WindowBefore && previous.WindowAfter && sample.WindowBefore && sample.WindowAfter)
                    { windowSeconds += seconds; windowBytes += bytes; }
                    else if (!previous.WindowBefore && !previous.WindowAfter && !sample.WindowBefore && !sample.WindowAfter)
                    { betweenSeconds += seconds; betweenBytes += bytes; }
                }
                previous = sample;
            }
            rows.Add(new(phase.TrimEnd('-'), windowSeconds, windowBytes, betweenSeconds, betweenBytes,
                windowSeconds > 0 ? windowBytes / windowSeconds : 0, betweenSeconds > 0 ? betweenBytes / betweenSeconds : 0));
        }
        return rows.ToArray();
    }

    private static void ValidateManagedWindowAllocation(ActualUseManagedAllocationRow[] rows)
    {
        foreach (var row in rows)
        {
            if (row.WindowSeconds < 5 || row.BetweenSeconds < 5)
                throw new InvalidDataException($"Phase {row.Phase} lacks managed allocation coverage: window {row.WindowSeconds:F1} s, between {row.BetweenSeconds:F1} s.");
            if (row.WindowBytesPerSecond > 2 * row.BetweenBytesPerSecond + 1024 * 1024)
                throw new InvalidDataException($"Phase {row.Phase} residency windows allocate {row.WindowBytesPerSecond / 1048576:F1} MiB/s of managed memory against {row.BetweenBytesPerSecond / 1048576:F1} MiB/s between windows.");
        }
    }

    // IsWindowActive is the managed request. The native flow frees the closed window's pairs and buckets at its next
    // buffer boundary and advances the continuity epoch then, so a between-window sample still at the closed window's
    // epoch is that release in flight. It is retention only if the next window opens before the epoch advances.
    private static ActualUseKernelMemorySample[] SettledBetweenWindowSamples(List<ActualUseKernelMemorySample> samples)
    {
        var settled = new List<ActualUseKernelMemorySample>();
        ActualUseKernelMemorySample? inFlight = null;
        long? closedEpoch = null;
        string? phase = null;
        foreach (var sample in samples)
        {
            if (sample.Phase != phase) { phase = sample.Phase; closedEpoch = null; inFlight = null; }
            if (sample.Aggregation is not { } aggregation) continue;
            if (sample.WindowBefore || sample.WindowAfter)
            {
                if (inFlight?.Aggregation is { } held)
                    throw new InvalidDataException($"Residency window memory survived between windows at {inFlight.At:O}: pairs={held.ActivePairCount}, retainedBucketBytes={held.RetainedBucketBytes}; the next window opened before its release.");
                if (sample.WindowBefore && sample.WindowAfter) closedEpoch = aggregation.ContinuityEpoch;
                continue;
            }
            if (aggregation.ContinuityEpoch == closedEpoch) { inFlight ??= sample; continue; }
            inFlight = null;
            settled.Add(sample);
        }
        return settled.ToArray();
    }

    private static ActualUseKernelMemoryRow[] ValidateNativeMemoryEvidence(List<ActualUseKernelMemorySample> samples)
    {
        var idle = SettledBetweenWindowSamples(samples);
        if (idle.Length == 0) throw new InvalidDataException("No between-window residency memory sample was observed.");
        foreach (var sample in idle)
        {
            var aggregation = sample.Aggregation!.Value;
            if (aggregation.ActivePairCount != 0 || aggregation.RetainedBucketBytes != 0
                || sample.Allocation?.Tags.Any(tag => tag.Owner == 1 && tag.Scope == 1 && tag.CommittedBytes != 0) == true)
                throw new InvalidDataException($"Residency window memory survived between windows at {sample.At:O}: pairs={aggregation.ActivePairCount}, retainedBucketBytes={aggregation.RetainedBucketBytes}.");
        }
        ActualUseKernelMemoryRow Row(string label, ActualUseKernelMemorySample[] values, bool absent = false)
        {
            if (values.Length == 0) throw new InvalidDataException($"Native committed-memory scenario '{label}' has no sample.");
            if (absent)
            {
                if (values.Any(value => value.Allocation is { CommittedBytes: > 0 }))
                    throw new InvalidDataException("Native memory remains without a holder.");
                return new(label, 0, 0, 0, "no native owner", values.Length, 0, 0, []);
            }
            if (values.Any(value => value.Allocation is null || value.Allocation.CommitMeasurement != "VirtualQuery"))
                throw new InvalidDataException($"Scenario '{label}' lacks actual Windows committed-page measurements.");
            var peak = values.MaxBy(value => value.Allocation!.CommittedBytes)!;
            return new(label, peak.Allocation!.CommittedBytes, peak.Allocation.PeakCommittedBytes,
                peak.Allocation.RequestedBytes, peak.Allocation.CommitMeasurement, values.Length,
                peak.Aggregation?.ActivePairCount, peak.Aggregation?.CurrentThreadCount, peak.Allocation.Owners);
        }
        return
        [
            Row("no holder", samples.Where(value => value.Phase is "a-no-subscriptions" or "e-subscriptions-removed").ToArray(), absent: true),
            Row("IRQ only", samples.Where(value => value.Phase == "irq-only").ToArray()),
            // Case c records its settled tray state as "c-tray-smart". Memory follows use, so network (owner 4) appears in
            // the row's owners only when the tray actually holds network attribution; it is reported, not required.
            Row("tray: CPU between windows + IRQ", idle.Where(value => value.Phase == "c-tray-smart"
                && value.Allocation?.Owners.Any(owner => owner.Owner == 2 && owner.CommittedBytes > 0) == true).ToArray()),
            Row("CPU inside window", samples.Where(value => value.WindowBefore && value.WindowAfter && value.Aggregation?.ActivePairCount > 0).ToArray())
        ];
    }
}
