using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum SmartRoundOutput { None, Residency, Process, Hardware }

/// <summary>Smart's three demands and its one existing subscription output. No value cache or signal.</summary>
internal sealed class HostManagerSchedulingSubscriptions(
    ICpuCoreResidencyReader residency, ISchedulingProcessFactObservationSource processes,
    IMetricSnapshotObservationSource hardware, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long cadenceStarted;
    private TimeSpan nextOutputAfter;
    private bool hasRun;
    private TimeSpan requestedInterval;
    private IDisposable? processLease, hardwareLease;
    private IOutput? output;
    private readonly object outputGate = new();
    private (bool Cpu, SchedulingProcessMetricMask Process, string Hardware, TimeSpan Interval)? applied;
    public SmartRoundOutput Driver { get; private set; }
    public bool HasOutput => output is not null;
    internal Task? Publication => output?.Publication;

    internal static SmartRoundOutput SelectDriver(bool cpu, bool process, bool hardware)
        => cpu ? SmartRoundOutput.Residency : process ? SmartRoundOutput.Process
            : hardware ? SmartRoundOutput.Hardware : SmartRoundOutput.None;

    public async Task UpdateAsync(bool cpu, SchedulingProcessMetricMask process,
        MetricSampleRequest hardwareRequest, TimeSpan interval, CancellationToken cancellationToken)
    {
        var identity = (cpu, process, hardwareRequest.CacheKey, interval);
        if (applied == identity) return;
        var driver = SelectDriver(cpu, process != SchedulingProcessMetricMask.None, !hardwareRequest.IsEmpty);
        IOutput? nextOutput = null;
        IDisposable? nextProcess = null, nextHardware = null;
        try
        {
            if (driver == SmartRoundOutput.Residency)
                nextOutput = new Output<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot?>(
                    token => residency.SubscribeAsync("host-manager-smart-coordinator:cpu-cores", interval, token), cancellationToken);
            if (process != SchedulingProcessMetricMask.None)
            {
                if (driver == SmartRoundOutput.Process)
                {
                    var push = processes as IResourceBreakdownPushSource
                        ?? throw new InvalidOperationException("Smart process observation must expose its existing subscription output.");
                    var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(),
                        ProcessSampleDetailLevel.SmartSchedulingLite, process);
                    nextOutput = new Output<ResourceBreakdownSnapshot>(
                        token => push.SubscribeAsync("host-manager-smart-coordinator:compute", request, interval, token), cancellationToken);
                }
                else nextProcess = processes.AcquireSubscription("host-manager-smart-coordinator:compute", process, interval);
            }
            if (!hardwareRequest.IsEmpty)
            {
                if (driver == SmartRoundOutput.Hardware)
                {
                    var push = hardware as IMetricSnapshotPushSource
                        ?? throw new InvalidOperationException("Smart hardware observation must expose its existing subscription output.");
                    nextOutput = new Output<HardwareMetricSnapshot>(
                        token => push.SubscribeAsync("host-manager-smart-coordinator", hardwareRequest, interval, token), cancellationToken);
                }
                else nextHardware = hardware.AcquireSubscription("host-manager-smart-coordinator", hardwareRequest, interval);
            }
        }
        catch
        {
            nextProcess?.Dispose(); nextHardware?.Dispose();
            if (nextOutput is not null) await nextOutput.DisposeAsync();
            throw;
        }
        // Admit the replacement before retiring the old frequency: demand never briefly falls to zero.
        var oldOutput = output; var oldProcess = processLease; var oldHardware = hardwareLease;
        lock (outputGate) output = nextOutput;
        processLease = nextProcess; hardwareLease = nextHardware;
        Driver = driver; applied = identity;
        cadenceStarted = clock.GetTimestamp();
        nextOutputAfter = hasRun ? interval : TimeSpan.Zero;
        requestedInterval = interval;
        try { if (oldOutput is not null) await oldOutput.DisposeAsync(); }
        finally { oldProcess?.Dispose(); oldHardware?.Dispose(); }
    }

    internal void RoundCompleted()
    {
        hasRun = true;
        if (nextOutputAfter == TimeSpan.Zero) nextOutputAfter = requestedInterval;
    }

    internal async Task<HostManagerWakeDeadlineSnapshot> WaitAsync(
        HostManagerVersionedWakeDeadline deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (output is not { } current) return await deadline.WaitAsync(cancellationToken);
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var early = deadline.WaitAsync(wake.Token);
            try
            {
                if (await Task.WhenAny(current.Publication, early) == early) return await early;
                await current.Publication;
                lock (outputGate)
                {
                    if (!ReferenceEquals(current, output)) continue;
                    current.Advance();
                }
                // Shared producers may publish for another, faster subscriber.
                // Eligibility is checked only on output, never by polling or a second timer.
                var elapsed = clock.GetElapsedTime(cadenceStarted);
                if (elapsed < nextOutputAfter) continue;
                // Keep the subscription's due slots. Round execution time must
                // not move the next due beyond the producer's next publication.
                nextOutputAfter = TimeSpan.FromTicks(checked(
                    (elapsed.Ticks / requestedInterval.Ticks + 1) * requestedInterval.Ticks));
                return deadline.Snapshot;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && !ReferenceEquals(current, output)) { }
            finally
            {
                wake.Cancel();
                try { await early; } catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var old = output; output = null;
        try { if (old is not null) await old.DisposeAsync(); }
        finally { processLease?.Dispose(); hardwareLease?.Dispose(); processLease = hardwareLease = null; applied = null; }
    }

    private interface IOutput : IAsyncDisposable { Task Publication { get; } void Advance(); }
    private sealed class Output<T> : IOutput
    {
        private readonly CancellationTokenSource lifetime;
        private readonly IAsyncEnumerator<T> enumerator;
        public Task Publication { get; private set; } = Task.CompletedTask;
        public Output(Func<CancellationToken, IAsyncEnumerable<T>> subscribe, CancellationToken cancellationToken)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            enumerator = subscribe(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
            Advance();
        }
        public void Advance() => Publication = WaitAsync();
        private async Task WaitAsync()
        {
            if (!await enumerator.MoveNextAsync())
                throw new InvalidOperationException("Smart's subscription output ended while its demand was active.");
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            try { await Publication; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { try { await enumerator.DisposeAsync(); } finally { lifetime.Dispose(); } }
        }
    }
}
