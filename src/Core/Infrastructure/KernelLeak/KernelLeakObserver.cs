using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// Own named demand lasts for the backend lifetime. Report subscriptions only replay/read publications.
public sealed class KernelLeakObserver(IKernelLeakSampleSource source, IKernelLeakAttributionReader attribution,
    RuntimePlanProvider plans, ILogger<KernelLeakObserver> logger) : IKernelLeakReportSource, IHostedService, IDisposable
{
    private const int MaximumSubscribers = 64;
    private readonly object gate = new();
    private readonly HashSet<Channel<KernelLeakReport>> readers = new();
    private KernelLeakReport? current;
    private KernelLeakDiagnostics diagnostics = new(0, 0, 0, 0, 0, 0);
    private CancellationTokenSource? stop;
    private Task? pump;
    private bool started;
    public KernelLeakReport Read() { lock (gate) return current ?? throw new InvalidOperationException("Kernel leak observation plan has not been published."); }
    public KernelLeakDiagnostics ReadDiagnostics() { lock (gate) return diagnostics; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started) return Task.CompletedTask;
            started = true;
            plans.Published += StartFromPlan;
            StartFromPlan(plans.Current);
        }
        return Task.CompletedTask;
    }
    private void StartFromPlan(CompiledRuntimePlan plan)
    {
        lock (gate)
        {
            // Options are rebuild-backend values; one immutable selection for this backend lifetime.
            if (!started || current is not null || !plan.HostManager.IsPublished) return;
            var options = (plan.HostManager.KernelLeak ?? throw new InvalidOperationException("Kernel leak observation plan is unavailable.")).Validate();
            current = new(options.Enabled ? KernelLeakReportStatus.Observing : KernelLeakReportStatus.Disabled,
                null, [], KernelLeakFailure.None, new(null, 0, 0, options.MinimumObservationSeconds, options.MinimumSamples, null));
            foreach (var reader in readers) reader.Writer.TryWrite(current);
            if (!options.Enabled) return;
            stop = new();
            var token = stop.Token;
            pump = Task.Run(() => RunAsync(options, token), CancellationToken.None);
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (gate)
        {
            started = false;
            plans.Published -= StartFromPlan;
            cancellation = stop; running = pump;
        }
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (running is not null) await running.ConfigureAwait(false); // Drain the owned demand, including on host shutdown.
            lock (gate)
            {
                if (ReferenceEquals(stop, cancellation)) { stop = null; pump = null; cancellation.Dispose(); }
                diagnostics = diagnostics with { HolderCount = 0, SeriesBytes = 0 };
            }
        }
    }
    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async IAsyncEnumerable<KernelLeakReport> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channel = Channel.CreateBounded<KernelLeakReport>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            if (readers.Count == MaximumSubscribers) throw new InvalidOperationException("Kernel leak subscriber capacity exceeded.");
            channel.Writer.TryWrite(Read());
            readers.Add(channel);
        }
        try { await foreach (var report in channel.Reader.ReadAllAsync(cancellationToken)) yield return report; }
        finally { lock (gate) readers.Remove(channel); }
    }
    private async Task RunAsync(KernelLeakOptions options, CancellationToken token)
    {
        var series = new KernelLeakSeries(options);
        DateTimeOffset? runStart = null;
        var samples = 0;
        var elapsed = 0d;
        KernelLeakRunFailure? previousFailure = null, failedRun = null;
        KernelLeakObservation Progress() => new(runStart, elapsed, samples,
            options.MinimumObservationSeconds, options.MinimumSamples, previousFailure);
        DateTimeOffset BeginRunIfNeeded(DateTimeOffset at)
        {
            if (runStart is not null && failedRun is null) return runStart.Value;
            runStart = at; elapsed = 0; samples = 0;
            if (failedRun is not null) { previousFailure = failedRun; failedRun = null; }
            return at;
        }
        void Fail(KernelLeakFailure failure, DateTimeOffset at, int unavailable = 0)
        {
            series.Clear();
            if (runStart is { } began) elapsed = Math.Max(0, (at - began).TotalSeconds);
            failedRun = new(runStart, at, failure);
            Publish(new(KernelLeakReportStatus.Unavailable, at, [], failure, Progress())
                { UnavailableHolderCount = unavailable }, series, 0, 0);
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var receivedFrame = false;
                try
                {
                    await foreach (var frame in source.SubscribeAsync("kernel-leak-observer", TimeSpan.FromMilliseconds(options.IntervalMilliseconds), token))
                    {
                        receivedFrame = true;
                        token.ThrowIfCancellationRequested();
                        // The next interval is a new run even when that attempt also fails.
                        var began = BeginRunIfNeeded(frame.CapturedAt);
                        if (!frame.Available) { Fail(KernelLeakFailure.SourceUnavailable, frame.CapturedAt, frame.UnavailableHolderCount); continue; }
                        try
                        {
                            var timer = Stopwatch.StartNew();
                            if (frame.CapturedAt < began) throw new InvalidDataException("Kernel leak sample precedes the observation run.");
                            var suspects = series.Observe(frame);
                            var judgment = timer.Elapsed.TotalMilliseconds;
                            elapsed = (frame.CapturedAt - began).TotalSeconds;
                            samples = checked(samples + 1);
                            timer.Restart();
                            var unknownHolders = suspects.Select(item => item.Holder).ToArray();
                            IReadOnlyList<KernelLeakHolder> identities;
                            try
                            {
                                identities = attribution.ReadBatch(unknownHolders, token);
                                if (identities.Count != suspects.Count) throw new InvalidDataException("Kernel leak attribution result count mismatch.");
                            }
                            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                            {
                                logger.LogWarning(error, "Kernel leak attribution is unavailable; retaining independently observed growth evidence.");
                                identities = unknownHolders;
                            }
                            var items = new KernelLeakSuspect[suspects.Count];
                            for (var index = 0; index < items.Length; index++) items[index] = suspects[index] with { Holder = identities[index] };
                            token.ThrowIfCancellationRequested();
                            var status = items.Length > 0 ? KernelLeakReportStatus.Suspected
                                : samples < options.MinimumSamples || elapsed < options.MinimumObservationSeconds
                                    ? KernelLeakReportStatus.Observing : KernelLeakReportStatus.Empty;
                            Publish(new(status, frame.CapturedAt, items, KernelLeakFailure.None, Progress())
                                { UnavailableHolderCount = frame.UnavailableHolderCount }, series, judgment, timer.Elapsed.TotalMilliseconds);
                        }
                        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                        {
                            logger.LogWarning(error, "Kernel leak observation run failed; next interval starts a visible new run.");
                            Fail(Classify(error), frame.CapturedAt, frame.UnavailableHolderCount);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!receivedFrame) BeginRunIfNeeded(DateTimeOffset.UtcNow);
                    if (failedRun is null) Fail(KernelLeakFailure.SourceUnavailable, DateTimeOffset.UtcNow);
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    logger.LogWarning(error, "Kernel leak sampling demand failed; retrying at the next configured interval.");
                    var at = DateTimeOffset.UtcNow;
                    BeginRunIfNeeded(at);
                    Fail(Classify(error), at);
                }
                // An ended/failed source is reacquired only at the configured interval, never in a tight retry loop.
                await Task.Delay(options.IntervalMilliseconds, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { series.Clear(); }
    }
    private static KernelLeakFailure Classify(Exception error) => error is OverflowException ? KernelLeakFailure.CapacityExceeded
        : error is InvalidDataException ? KernelLeakFailure.InvalidObservation : KernelLeakFailure.SourceUnavailable;
    private void Publish(KernelLeakReport report, KernelLeakSeries series, double judgement, double attributionCost)
    {
        lock (gate)
        {
            current = report;
            diagnostics = new(series.Count, series.Bytes, Math.Max(diagnostics.PeakSeriesBytes, series.PeakBytes),
                diagnostics.ObservedFrames + 1, judgement, attributionCost);
            foreach (var reader in readers) reader.Writer.TryWrite(report);
        }
    }
}
