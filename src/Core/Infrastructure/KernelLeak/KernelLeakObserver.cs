using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// One named sampling demand shared by all report readers. Last reader drains the demand.
public sealed class KernelLeakObserver(IKernelLeakSampleSource source, IKernelLeakAttributionReader attribution,
    Func<KernelLeakOptions> readOptions, ILogger<KernelLeakObserver> logger) : IKernelLeakReportSource
{
    private const int MaximumSubscribers = 64; // Subscription channel's structural maximum.
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<Channel<KernelLeakReport>> readers = new();
    private KernelLeakReport current = new(KernelLeakReportStatus.Unavailable, null, [], KernelLeakFailure.NotObserved);
    private KernelLeakDiagnostics diagnostics = new(0, 0, 0, 0, 0, 0);
    private CancellationTokenSource? stop;
    private Task? pump;
    public KernelLeakReport Read() { lock (gate) return current; }
    public KernelLeakDiagnostics ReadDiagnostics() { lock (gate) return diagnostics; }

    public async IAsyncEnumerable<KernelLeakReport> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channel = Channel.CreateBounded<KernelLeakReport>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            KernelLeakOptions? options = null;
            lock (gate)
            {
                if (readers.Count == MaximumSubscribers) throw new InvalidOperationException("Kernel leak subscriber capacity exceeded.");
                if (readers.Count == 0) options = readOptions().Validate();
                readers.Add(channel);
                channel.Writer.TryWrite(current);
            }
            if (options is not null)
            {
                stop = new();
                pump = Task.Run(() => RunAsync(options, stop.Token), CancellationToken.None);
            }
        }
        finally { lifecycle.Release(); }
        try
        {
            await foreach (var report in channel.Reader.ReadAllAsync(cancellationToken)) yield return report;
        }
        finally { await ReleaseAsync(channel); }
    }

    private async Task ReleaseAsync(Channel<KernelLeakReport> channel)
    {
        await lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            lock (gate) readers.Remove(channel);
            if (readers.Count == 0 && stop is not null)
            {
                await stop.CancelAsync();
                if (pump is not null) await pump;
                stop.Dispose(); stop = null; pump = null;
                lock (gate) diagnostics = diagnostics with { HolderCount = 0, SeriesBytes = 0 };
            }
        }
        finally { lifecycle.Release(); }
    }
    private async Task RunAsync(KernelLeakOptions options, CancellationToken token)
    {
        var series = new KernelLeakSeries(options);
        try
        {
            await foreach (var frame in source.SubscribeAsync("kernel-leak-observer", TimeSpan.FromMilliseconds(options.IntervalMilliseconds), token))
            {
                token.ThrowIfCancellationRequested();
                if (!frame.Available)
                {
                    series.Clear(); Publish(new(KernelLeakReportStatus.Unavailable, frame.CapturedAt, [], KernelLeakFailure.SourceUnavailable) { UnavailableHolderCount = frame.UnavailableHolderCount }, series, 0, 0);
                    continue;
                }
                var timer = Stopwatch.StartNew();
                var suspects = series.Observe(frame);
                var judgment = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                var unknownHolders = suspects.Select(item => item.Holder).ToArray();
                IReadOnlyList<KernelLeakHolder> identities;
                try { identities = attribution.ReadBatch(unknownHolders, token); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "Kernel leak attribution is unavailable; retaining independently observed growth evidence.");
                    identities = unknownHolders;
                }
                var items = new KernelLeakSuspect[suspects.Count];
                for (var index = 0; index < items.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = suspects[index];
                    items[index] = item with { Holder = identities[index] };
                }
                token.ThrowIfCancellationRequested();
                Publish(new(items.Length == 0 ? KernelLeakReportStatus.Empty : KernelLeakReportStatus.Suspected,
                    frame.CapturedAt, items, KernelLeakFailure.None) { UnavailableHolderCount = frame.UnavailableHolderCount }, series, judgment, timer.Elapsed.TotalMilliseconds);
            }
            if (!token.IsCancellationRequested)
            {
                series.Clear(); Publish(new(KernelLeakReportStatus.Unavailable, null, [], KernelLeakFailure.SourceUnavailable), series, 0, 0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning(error, "Kernel leak observation failed.");
            series.Clear();
            Publish(new(KernelLeakReportStatus.Unavailable, null, [], error is OverflowException ? KernelLeakFailure.CapacityExceeded
                : error is InvalidDataException ? KernelLeakFailure.InvalidObservation : KernelLeakFailure.SourceUnavailable), series, 0, 0);
        }
        finally { series.Clear(); }
    }
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