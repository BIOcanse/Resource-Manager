using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// H3: DI/lifecycle binding. H2: subscription delivery. H1: immutable HTTP projection.
// Native observation owns run progress, retry/judgment loops and the reported-item table.
public sealed partial class KernelLeakObserver(IKernelLeakSampleSource source, IKernelLeakAttributionReader attribution,
    RuntimePlanProvider plans, ILogger<KernelLeakObserver> logger, IHostEnvironment environment,
    RuntimePersistenceCapabilityPolicy persistence, ResourceManagerLedgerOwner? ledger = null)
    : IKernelLeakReportSource, IBusinessBlock, IHostedService, IDisposable
{
    private const int MaximumSubscribers = 64;
    private readonly object gate = new();
    private readonly HashSet<Channel<KernelLeakReport>> readers = [];
    private KernelLeakReport current = NotObserved(null);
    private KernelLeakDiagnostics diagnostics = new(0, 0, 0, 0, 0, 0);
    private NativeKernelLeakItemStore? items;
    private bool started, disposed;
    private NativeKernelLeakItemStore Items => items ??= new(ledger);
    public KernelLeakReport Read() { lock (gate) return current; }
    public KernelLeakDiagnostics ReadDiagnostics() { lock (gate) return diagnostics; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started) return Task.CompletedTask;
            RestoreItemsOnce(); started = true;
            plans.Published += UpdateNotObserved;
            UpdateNotObserved(plans.Current);
        }
        return Task.CompletedTask;
    }
    private void UpdateNotObserved(CompiledRuntimePlan _)
    {
        lock (gate)
            if (started && current.Status == KernelLeakReportStatus.NotObserved)
                SetReport(NotObserved(plans.Current.HostManager.IsPublished ? plans.Current.HostManager.KernelLeak : null));
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) { if (started) { started = false; plans.Published -= UpdateNotObserved; } }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StopAsync(default);
            items?.Dispose(); items = null;
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
            channel.Writer.TryWrite(current); readers.Add(channel);
        }
        try { await foreach (var report in channel.Reader.ReadAllAsync(cancellationToken)) yield return report; }
        finally { lock (gate) readers.Remove(channel); }
    }
    public Task RunAsync(BusinessBlockRunContext context)
    {
        context.StopNotification.ThrowIfCancellationRequested();
        var options = (plans.Current.HostManager.KernelLeak
            ?? throw new InvalidOperationException("Kernel leak observation plan is unavailable.")).Validate();
        lock (gate) RestoreItemsOnce();
        // The CLR task only hosts the native blocking subscription loop until H2/H3.
        return Task.Run(() => new NativeKernelLeakRun(this, source, attribution, options, context, ledger).Run());
    }
    internal void PublishNative(NativeKernelLeakRun.Snapshot snapshot, KernelLeakOptions options)
    {
        lock (gate)
        {
            var previous = snapshot.HasPrevious == 0 ? null : new KernelLeakRunFailure(
                new(snapshot.PreviousStart, TimeSpan.Zero), new(snapshot.PreviousAt, TimeSpan.Zero), (KernelLeakFailure)snapshot.PreviousFailure);
            var report = new KernelLeakReport((KernelLeakReportStatus)snapshot.Status,
                snapshot.Status == 0 || snapshot.Frames == 0 ? null : new DateTimeOffset(snapshot.Captured, TimeSpan.Zero), [],
                (KernelLeakFailure)snapshot.Failure, new(snapshot.HasStart == 0 ? null : new DateTimeOffset(snapshot.Started, TimeSpan.Zero),
                    snapshot.Elapsed, checked((int)snapshot.Samples), options.MinimumObservationSeconds, options.MinimumSamples, previous))
                { UnavailableHolderCount = (int)snapshot.Unavailable, UnrecordedItemCount = (int)snapshot.Unrecorded };
            diagnostics = new((int)snapshot.Holders, (long)snapshot.SeriesBytes,
                Math.Max(diagnostics.PeakSeriesBytes, (long)snapshot.PeakBytes), diagnostics.ObservedFrames + (snapshot.Status == 0 || snapshot.Frames == 0 ? 0 : 1),
                snapshot.JudgmentMilliseconds, snapshot.AttributionMilliseconds);
            SetReport(report);
        }
    }
    internal unsafe void PrepareNative(ref NativeKernelLeakRun.Snapshot snapshot,
        ReadOnlySpan<NativeKernelLeakSeriesAbi.Suspect> suspects, ReadOnlySpan<NativeKernelLeakItems.Key> judged,
        KernelLeakSampleFrame frame, KernelLeakOptions options, CancellationToken token)
    {
        var attributionStart = Stopwatch.GetTimestamp();
        var unknown = new KernelLeakHolder[suspects.Length];
        for (var i = 0; i < unknown.Length; i++) unknown[i] = NativeKernelLeakSeriesAbi.FromSuspect(in suspects[i]).Holder;
        IReadOnlyList<KernelLeakHolder> identities;
        try
        {
            identities = attribution.ReadBatch(unknown, token);
            if (identities.Count != suspects.Length) throw new InvalidDataException("Kernel leak attribution result count mismatch.");
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            logger.LogWarning(error, "Kernel leak attribution is unavailable; retaining independently observed growth evidence.");
            identities = unknown;
        }
        var values = new KernelLeakSuspect[suspects.Length];
        for (var i = 0; i < values.Length; i++) values[i] = NativeKernelLeakSeriesAbi.FromSuspect(in suspects[i]) with
            { Holder = identities[i], FirstObservedAt = frame.CapturedAt, LastObservedAt = frame.CapturedAt };
        snapshot.AttributionMilliseconds = Stopwatch.GetElapsedTime(attributionStart).TotalMilliseconds;
        var live = frame.Samples.Where(value => value.ResourceKind == KernelLeakSampleKind.ProcessHandles)
            .Select(value => NativeKernelLeakSeriesAbi.ToKey(value.ProcessId, value.StartKey)).ToArray();
        var unavailable = frame.UnavailableHolders.Select(value => NativeKernelLeakSeriesAbi.ToKey(value.ProcessId, value.StartKey)).ToArray();
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var result = Items.Plan(frame.CapturedAt.UtcTicks, live, unavailable, judged, values, options.ReportedItemCapacity, SaveItems);
            snapshot.Unrecorded = result.Unrecorded; snapshot.RecordedSuspects = result.RecordedSuspects;
        }
    }
    internal static bool IsObservationFailure(Exception error) => error is IOException or InvalidDataException or OverflowException
        or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    internal static uint FailureCode(Exception error) => error is OverflowException ? 4u : error is InvalidDataException ? 5u : 3u;
    private static KernelLeakReport NotObserved(KernelLeakOptions? options) => new(KernelLeakReportStatus.NotObserved, null, [],
        KernelLeakFailure.None, new(null, 0, 0, options?.MinimumObservationSeconds ?? 0, options?.MinimumSamples ?? 0, null));
    private void SetReport(KernelLeakReport report)
    {
        RestoreItemsOnce(); current = report with { Items = Items.Current };
        foreach (var reader in readers) reader.Writer.TryWrite(current);
    }
    public int ClearHistory()
    {
        lock (gate)
            return NativeKernelHistoryCommand.Invoke(RestoreItemsOnce,
                () => Items.Clear(SaveItems), () => SetReport(current));
    }
}
