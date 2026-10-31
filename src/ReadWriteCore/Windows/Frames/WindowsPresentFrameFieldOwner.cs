using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>
/// One fixed static-field query over the original Present source. Construction
/// and cached reads are passive; direct capture and subscription borrow share
/// this gate, current publication and the same source instance.
/// </summary>
public sealed partial class WindowsPresentFrameFieldOwner : IDisposable
{
    public const string CollectionItemId = "information.present-frames";

    internal SemaphoreSlim CaptureGate { get; } = new(1, 1);
    private readonly object currentGate = new();
    private readonly IFrameTimingObservationSource source;
    private readonly TimeProvider clock;
    private readonly InformationReadWriteBackend backend;
    private DeviceInformationSnapshot current;
    private IDisposable? directSourceLease;
    private bool resourcesReleased;
    private int disposed;

    public PresentFrameFieldQuery Query { get; }
    public SamplingPublicationSignal Publications { get; } = new();

    public WindowsPresentFrameFieldOwner(IFrameTimingObservationSource source,
        PresentFrameFieldQuery query, TimeProvider? clock = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        Query = query ?? throw new ArgumentNullException(nameof(query));
        // The window budget is the source's declared ledger retention (frame_pipeline retention_ms).
        if (query.Window > source.Retention)
            throw new ArgumentOutOfRangeException(nameof(query), $"Window must be within (0, {source.Retention}].");
        this.clock = clock ?? TimeProvider.System;
        current = CreateInitialCurrent(Query, this.clock.GetUtcNow());
        backend = new(ReadInformation, CaptureAllAsync,
            static definition => FrameFieldRegistry.IsSupported(definition)
                ? new(true, null, definition.Name == FrameFieldRegistry.WindowDurationConfigured
                    ? [] : ["windows.present-etw"])
                : OperationSupport.Unsupported("frame-display-completion-source-not-bound"),
            CaptureSelectedAsync);
    }

    public InformationReadWriteBackend CreateBackend() => backend;

    /// <summary>A passive immutable view; it never reads or starts the ETW source.</summary>
    public DeviceInformationSnapshot ReadInformation() => Volatile.Read(ref current);

    public ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(
        CancellationToken cancellationToken = default)
        => CaptureSelectedAsync(CreateSelections(), cancellationToken);

    private async ValueTask<DeviceInformationSnapshot> CaptureAllAsync(CancellationToken cancellationToken)
        => await CaptureInformationAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<DeviceInformationSnapshot> CaptureSelectedAsync(
        ImmutableArray<InformationFieldSelection> selection, CancellationToken cancellationToken)
        => (await CaptureSelectedCoreAsync(selection, cancellationToken, null, null).ConfigureAwait(false)).Snapshot;

    private async ValueTask<FrameFieldCaptureResult> CaptureSelectedCoreAsync(
        ImmutableArray<InformationFieldSelection> selection,
        CancellationToken cancellationToken,
        IMetricPublicationPermit? permit,
        FrameFieldCollectionBorrow? borrow)
    {
        ValidateSelection(selection);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await CaptureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            borrow?.RequireActive();
            if (selection.IsEmpty || !selection.Any(static item =>
                FrameFieldRegistry.IsSupported(item.Definition)
                && item.Definition.Name != FrameFieldRegistry.WindowDurationConfigured))
                return new(ReadInformation(), false);

            FrameTimingSnapshot? sample;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (borrow is null)
                    directSourceLease ??= source.AcquireSubscription(new([Query.ProcessId], FrameDetail.None));
                else
                    EnsureBorrowLease(borrow);

                cancellationToken.ThrowIfCancellationRequested();
                sample = source.Read(Query.Window);
                cancellationToken.ThrowIfCancellationRequested();
                if (sample is not null && sample.Window != Query.Window)
                    throw new InvalidDataException("The Present source returned a different window than the fixed query.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var failed = Project(Query, null, clock.GetUtcNow());
                _ = Commit(failed, selection, permit, borrow, cancellationToken);
                throw;
            }

            var observedAt = sample?.ObservedAt ?? clock.GetUtcNow();
            var projection = Project(Query, sample, observedAt);
            var committed = Commit(projection, selection, permit, borrow, cancellationToken);
            return new(ReadInformation(), committed);
        }
        finally
        {
            CaptureGate.Release();
            TryDisposeRetiredGate();
        }
    }

    private bool Commit(DeviceInformationSnapshot sample,
        ImmutableArray<InformationFieldSelection> selection,
        IMetricPublicationPermit? permit,
        FrameFieldCollectionBorrow? borrow,
        CancellationToken cancellationToken)
    {
        var committed = false;
        void Publish()
        {
            lock (currentGate)
            {
                if (Volatile.Read(ref disposed) != 0 || borrow?.IsDisposed == true) return;
                var merged = Merge(ReadInformation(), sample, selection);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref current, merged);
                committed = true;
            }
        }

        if (permit is null) Publish();
        else if (permit.IsActive) _ = permit.TryPublish(Publish);
        if (committed) Publications.Publish([CollectionItemId]);
        return committed;
    }

    private void ValidateSelection(ImmutableArray<InformationFieldSelection> selection)
    {
        if (selection.IsDefault) throw new ArgumentException("Supply an initialized field selection.", nameof(selection));
        foreach (var item in selection)
            if (!Query.Matches(item.Field, item.Definition))
                throw new ArgumentException("Field selection does not match this fixed Present query.", nameof(selection));
    }

    private ImmutableArray<InformationFieldSelection> CreateSelections()
        => [.. FrameFieldRegistry.Definitions.Select(definition =>
            new InformationFieldSelection(Query.Field(definition.Name), definition))];

    private static DeviceInformationSnapshot CreateInitialCurrent(
        PresentFrameFieldQuery query, DateTimeOffset configuredAt)
    {
        var objects = FrameFieldRegistry.Definitions.GroupBy(FrameFieldRegistry.ObjectType)
            .Select(group => new DeviceInformationObject(
                query.DeviceIdentityFor(group.First()), FrameFieldRegistry.ObjectType(group.First()),
                group.Select(definition => definition.Name == FrameFieldRegistry.WindowDurationConfigured
                    ? new DeviceInformationField(definition,
                        JsonSerializer.SerializeToElement(query.Window.TotalSeconds)) { ObservedAt = configuredAt }
                    : new DeviceInformationField(definition, null)).ToArray()))
            .ToArray();
        return new(configuredAt, objects);
    }

    private static DeviceInformationSnapshot Project(
        PresentFrameFieldQuery query, FrameTimingSnapshot? snapshot, DateTimeOffset observedAt)
    {
        FrameTimingStream? stream = null;
        FrameTimingPresent? lastPresent = null;
        FrameTimingDisplayStream? displayStream = null;
        if (snapshot is { Complete: true })
        {
            var process = snapshot.Processes.SingleOrDefault(item =>
                item.ProcessId == query.ProcessId && item.ProcessStartKey == query.ProcessStartFileTimeUtc);
            stream = process?.Streams.SingleOrDefault(item =>
                item.Source == query.Source && item.SwapChain == query.SwapChain);
            lastPresent = snapshot.LastPresents.SingleOrDefault(item =>
                item.ProcessId == query.ProcessId
                && item.ProcessStartKey == query.ProcessStartFileTimeUtc
                && item.Source == query.Source
                && item.SwapChain == query.SwapChain);
            var displayCandidates = snapshot.DisplayStreams.Where(item =>
                    item.ProcessId == query.ProcessId
                    && item.ProcessStartKey == query.ProcessStartFileTimeUtc
                    && item.Source == query.Source
                    && item.SwapChain == query.SwapChain)
                .Take(2)
                .ToArray();
            if (displayCandidates.Length == 1)
            {
                displayStream = displayCandidates[0];
            }
        }

        var statistics = stream?.Statistics;
        var hasIntervals = statistics is { IntervalCount: > 0 }
            && double.IsFinite(statistics.DurationMs) && statistics.DurationMs > 0;
        var displayStatistics = displayStream is { Complete: true } ? displayStream.Statistics : null;
        var hasDisplayIntervals = displayStatistics is { IntervalCount: > 0 }
            && double.IsFinite(displayStatistics.DurationMs) && displayStatistics.DurationMs > 0;
        var fields = FrameFieldRegistry.Definitions.Select(definition => new DeviceInformationField(
            definition,
            definition.Name switch
            {
                FrameFieldRegistry.DisplayIntervalMs when hasDisplayIntervals
                    => JsonSerializer.SerializeToElement(displayStatistics!.DurationMs / displayStatistics.IntervalCount),
                FrameFieldRegistry.DisplayedFps when hasDisplayIntervals
                    => JsonSerializer.SerializeToElement(displayStatistics!.AverageFps),
                FrameFieldRegistry.DroppedCount when displayStream is { Complete: true, DroppedCount: { } droppedCount }
                    => JsonSerializer.SerializeToElement(droppedCount.ToString(CultureInfo.InvariantCulture)),
                FrameFieldRegistry.DisplayIntervalP95Ms when hasDisplayIntervals
                    => JsonSerializer.SerializeToElement(displayStatistics!.P95FrameTimeMs),
                FrameFieldRegistry.DisplayIntervalP99Ms when hasDisplayIntervals
                    => JsonSerializer.SerializeToElement(displayStatistics!.P99FrameTimeMs),
                FrameFieldRegistry.FpsDerived or FrameFieldRegistry.PresentedFps when hasIntervals
                    => JsonSerializer.SerializeToElement(statistics!.AverageFps),
                FrameFieldRegistry.PresentIntervalMs when hasIntervals
                    => JsonSerializer.SerializeToElement(statistics!.DurationMs / statistics.IntervalCount),
                FrameFieldRegistry.PresentTimeActual when lastPresent is not null || stream is not null
                    => JsonSerializer.SerializeToElement((double)(lastPresent?.LastPresentAt
                        ?? stream!.LastPresentAt).ToUnixTimeMilliseconds()),
                FrameFieldRegistry.PresentIntervalP95Ms when hasIntervals
                    => JsonSerializer.SerializeToElement(statistics!.P95FrameTimeMs),
                FrameFieldRegistry.PresentIntervalP99Ms when hasIntervals
                    => JsonSerializer.SerializeToElement(statistics!.P99FrameTimeMs),
                FrameFieldRegistry.TimePercentileDerived when hasIntervals
                    => JsonSerializer.SerializeToElement(query.Percentile switch
                    {
                        FrameTimePercentile.P50 => statistics!.P50FrameTimeMs,
                        FrameTimePercentile.P95 => statistics!.P95FrameTimeMs,
                        FrameTimePercentile.P99 => statistics!.P99FrameTimeMs,
                        _ => throw new ArgumentOutOfRangeException(nameof(query))
                    }),
                FrameFieldRegistry.WindowDurationConfigured
                    => JsonSerializer.SerializeToElement(query.Window.TotalSeconds),
                _ => (System.Text.Json.JsonElement?)null
            }) { ObservedAt = observedAt }).ToArray();

        var objects = fields.GroupBy(field => FrameFieldRegistry.ObjectType(field.Definition))
            .Select(group => new DeviceInformationObject(
                query.DeviceIdentityFor(group.First().Definition), group.Key, group.ToArray()))
            .ToArray();
        return new(observedAt, objects);
    }

    private static DeviceInformationSnapshot Merge(
        DeviceInformationSnapshot previous,
        DeviceInformationSnapshot sample,
        ImmutableArray<InformationFieldSelection> selection)
    {
        var selected = selection.Select(static item => item.Field).ToHashSet();
        var objects = previous.Devices.Select(device => device with
        {
            Fields = device.Fields.Select(field =>
            {
                var reference = new FieldReference(device.DeviceIdentity, field.Definition.Name);
                if (!selected.Contains(reference)) return field;
                return sample.FindField(reference) ?? new DeviceInformationField(field.Definition, null)
                    { ObservedAt = sample.ObservedAt };
            }).ToArray()
        }).ToArray();
        return new(sample.ObservedAt ?? previous.ObservedAt, objects);
    }

    public void Dispose()
    {
        lock (currentGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        }
        TryDisposeRetiredGate();
    }

    private void TryDisposeRetiredGate()
    {
        if (Volatile.Read(ref disposed) == 0 || !CaptureGate.Wait(0)) return;
        try
        {
            if (resourcesReleased) return;
            resourcesReleased = true;
            directSourceLease?.Dispose();
            directSourceLease = null;
            ReleaseBorrowResources();
        }
        finally { CaptureGate.Release(); }
    }

    private readonly record struct FrameFieldCaptureResult(DeviceInformationSnapshot Snapshot, bool Committed);
}
