using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// The persistent object owns publications and readers; its block entry owns observation subscription.
public sealed partial class KernelLeakObserver(IKernelLeakSampleSource source, IKernelLeakAttributionReader attribution,
    RuntimePlanProvider plans, ILogger<KernelLeakObserver> logger, IHostEnvironment environment,
    RuntimePersistenceCapabilityPolicy persistence)
    : IKernelLeakReportSource, IBusinessBlock, IHostedService, IDisposable
{
    private const int MaximumSubscribers = 64;
    private readonly object gate = new();
    private readonly HashSet<Channel<KernelLeakReport>> readers = new();
    private KernelLeakReport current = NotObserved(null);
    private KernelLeakDiagnostics diagnostics = new(0, 0, 0, 0, 0, 0);
    private bool started;
    public KernelLeakReport Read() { lock (gate) return current; }
    public KernelLeakDiagnostics ReadDiagnostics() { lock (gate) return diagnostics; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started) return Task.CompletedTask;
            RestoreItemsOnce();
            started = true;
            plans.Published += UpdateNotObserved;
            UpdateNotObserved(plans.Current);
        }
        return Task.CompletedTask;
    }
    private void UpdateNotObserved(CompiledRuntimePlan _)
    {
        lock (gate)
        {
            if (!started) return;
            // Only report progress belongs here. The existing plan-fact binding owns enablement.
            var plan = plans.Current;
            if (current.Status == KernelLeakReportStatus.NotObserved)
                SetReport(NotObserved(plan.HostManager.IsPublished ? plan.HostManager.KernelLeak : null));
        }
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (!started) return;
            started = false;
            plans.Published -= UpdateNotObserved;
        }
    }

    public async IAsyncEnumerable<KernelLeakReport> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channel = Channel.CreateBounded<KernelLeakReport>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            if (readers.Count == MaximumSubscribers) throw new InvalidOperationException("Kernel leak subscriber capacity exceeded.");
            channel.Writer.TryWrite(current);
            readers.Add(channel);
        }
        try { await foreach (var report in channel.Reader.ReadAllAsync(cancellationToken)) yield return report; }
        finally { lock (gate) readers.Remove(channel); }
    }
    public async Task RunAsync(BusinessBlockRunContext context)
    {
        var token = context.StopNotification;
        token.ThrowIfCancellationRequested();
        var options = (plans.Current.HostManager.KernelLeak
            ?? throw new InvalidOperationException("Kernel leak observation plan is unavailable.")).Validate();
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
            // A subscription can fail/end before a frame starts a run. Publish its typed report
            // failure without manufacturing a run start or replacing an already failed run.
            if (runStart is { } began && failedRun is null)
            {
                elapsed = Math.Max(0, (at - began).TotalSeconds);
                failedRun = new(began, at, failure);
            }
            Publish(new(KernelLeakReportStatus.Unavailable, at, [], failure, Progress())
                { UnavailableHolderCount = unavailable }, series, 0, 0);
        }
        try
        {
            lock (gate)
            {
                SetReport(new(KernelLeakReportStatus.Observing, null, [], KernelLeakFailure.None, Progress()));
            }
            context.ReportRunning();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await foreach (var frame in source.SubscribeAsync("kernel-leak-observer", TimeSpan.FromMilliseconds(options.IntervalMilliseconds), token))
                    {
                        token.ThrowIfCancellationRequested();
                        // The next interval is a new run even when that attempt also fails.
                        var began = BeginRunIfNeeded(frame.CapturedAt);
                        if (!frame.Available) { Fail(KernelLeakFailure.SourceUnavailable, frame.CapturedAt, frame.UnavailableHolderCount); continue; }
                        try
                        {
                            var timer = Stopwatch.StartNew();
                            if (frame.CapturedAt < began) throw new InvalidDataException("Kernel leak sample precedes the observation run.");
                            var result = series.Observe(frame);
                            var suspects = result.Suspects;
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
                            catch (Exception error) when (IsObservationFailure(error))
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
                                { UnavailableHolderCount = frame.UnavailableHolderCount }, series, judgment, timer.Elapsed.TotalMilliseconds,
                                result with { Suspects = items }, frame, options.ReportedItemCapacity);
                        }
                        catch (Exception error) when (IsObservationFailure(error))
                        {
                            logger.LogWarning(error, "Kernel leak observation run failed; next interval starts a visible new run.");
                            Fail(Classify(error), frame.CapturedAt, frame.UnavailableHolderCount);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (failedRun is null) Fail(KernelLeakFailure.SourceUnavailable, DateTimeOffset.UtcNow);
                }
                catch (Exception error) when (IsObservationFailure(error))
                {
                    logger.LogWarning(error, "Kernel leak sampling subscription failed; retrying at the next configured interval.");
                    var at = DateTimeOffset.UtcNow;
                    Fail(Classify(error), at);
                }
                // An ended/failed source is reacquired only at the configured interval, never in a tight retry loop.
                await Task.Delay(options.IntervalMilliseconds, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            series.Clear();
            lock (gate)
            {
                diagnostics = diagnostics with { HolderCount = 0, SeriesBytes = 0 };
                SetReport(NotObserved(options));
            }
        }
    }
    private static bool IsObservationFailure(Exception error)
        => error is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    private static KernelLeakReport NotObserved(KernelLeakOptions? options) => new(KernelLeakReportStatus.NotObserved,
        null, [], KernelLeakFailure.None, new(null, 0, 0, options?.MinimumObservationSeconds ?? 0, options?.MinimumSamples ?? 0, null));
    // Caller holds gate, so registering a reader and replaying the current report stay atomic.
    private void SetReport(KernelLeakReport report)
    {
        RestoreItemsOnce();
        current = report with { Items = reportedItems };
        foreach (var reader in readers) reader.Writer.TryWrite(current);
    }
    private static KernelLeakFailure Classify(Exception error) => error is OverflowException ? KernelLeakFailure.CapacityExceeded
        : error is InvalidDataException ? KernelLeakFailure.InvalidObservation : KernelLeakFailure.SourceUnavailable;
    private void Publish(KernelLeakReport report, KernelLeakSeries series, double judgement, double attributionCost,
        KernelLeakJudgment? result = null, KernelLeakSampleFrame? frame = null, int capacity = 0)
    {
        lock (gate)
        {
            if (result is not null)
            {
                report = report with { UnrecordedItemCount = ApplyJudgment(result, frame!, capacity) };
                if (report.Status == KernelLeakReportStatus.Suspected
                    && !result.Suspects.Any(suspect => reportedItems.Any(item => Key(item) == Key(suspect))))
                    report = report with { Status = KernelLeakReportStatus.Empty };
            }
            diagnostics = new(series.Count, series.Bytes, Math.Max(diagnostics.PeakSeriesBytes, series.PeakBytes),
                diagnostics.ObservedFrames + 1, judgement, attributionCost);
            SetReport(report);
        }
    }
}
