using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>Whether a recorded target process still exists (same pid and creation time).</summary>
public interface ITargetedProcessLiveness
{
    bool IsRunning(TargetedProcessIdentity identity);
}

internal sealed class OperatingSystemTargetedProcessLiveness : ITargetedProcessLiveness
{
    public static readonly OperatingSystemTargetedProcessLiveness Instance = new();

    public bool IsRunning(TargetedProcessIdentity identity)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            if (process.HasExited) return false;
            if (identity.ProcessStartKey == 0) return true;
            try { return process.StartTime.ToFileTimeUtc() == identity.ProcessStartKey; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}

/// <summary>Owns one explicit recording: the start check, one named subscription per recorded item, the
/// target process set and the end reason.</summary>
/// <remarks>Start: the software must have a running attributed process (same matcher as the target list),
/// else 409 target-not-running and no report. Items come from the named profile, the request's own list, or
/// the default profile. An item the live catalog lacks, or marks not applicable or without a source, gets one
/// whole-span gap with that reason and no subscription. Every other item is its own named subscription on the
/// subscription chain at its own interval, and each publication delivered to it is one point at the source's
/// observation time; a source silent for more than not_refreshed_interval_percent of the interval leaves a gap
/// with the reason it gave, else source-not-refreshed. The recording's process-check subscription
/// (sample_interval_ms) drives the rest: each of its publications merges new target processes, checks exits,
/// reads frames and stores the points delivered since. End reasons: user-stop; max-duration;
/// all-processes-exited (the recording saw a target process and every known process has been gone for
/// exit_confirmation_ticks checks); no-process (the recording's own checks never saw a target process in
/// no_process_ticks ready checks; its samples are discarded and only the status is kept).</remarks>
public sealed class TargetedRecordingService(
    TargetedRecordingStore store,
    ISoftwareRegistryView softwareRegistry,
    IFrameTimingObservationSource frames,
    IResourceBreakdownPushSource breakdown,
    IMetricSnapshotPushSource metrics,
    ITargetedRecordingProcessReader targets,
    ITargetedRecordingItemCatalog catalog,
    ILogger<TargetedRecordingService> logger,
    ITargetedRecordingProfileReader? profiles = null,
    TargetedRecordingOptions? options = null,
    ITargetedProcessLiveness? liveness = null) : IHostedService
{
    public static readonly int DefaultMaximumDurationSeconds =
        MonitoringSettings.ProductDefaults.TargetedRecording.DefaultMaximumDurationSeconds;
    public static readonly int MaximumDurationLimitSeconds =
        MonitoringSettings.ProductDefaults.TargetedRecording.MaximumDurationLimitSeconds;

    private readonly TargetedRecordingOptions settings = options ?? MonitoringSettings.ProductDefaults.TargetedRecording;
    private TimeSpan FrameWindow => MonitoringSettings.ProductDefaults.FramePipeline.RecordingFrameWindow;
    private TimeSpan FrameReadLag => MonitoringSettings.ProductDefaults.FramePipeline.RecordingFrameReadLag;
    private TimeSpan FrameTailDelay => TimeSpan.FromMilliseconds(settings.FrameTailDelayMilliseconds);
    private readonly ITargetedProcessLiveness processLiveness = liveness ?? OperatingSystemTargetedProcessLiveness.Instance;
    private readonly SemaphoreSlim control = new(1, 1);
    private ActiveRecording? active;

    public Task StartAsync(CancellationToken cancellationToken)
        => store.RecoverInterruptedAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveRecording? current;
        await control.WaitAsync(cancellationToken);
        try
        {
            current = active;
            if (current is not null)
            {
                current.StopReason = TargetedRecordingStopReasons.Interrupted;
                current.Cancellation.Cancel();
            }
        }
        finally
        {
            control.Release();
        }

        if (current?.Worker is { } worker)
        {
            await worker.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Starts with the default profile.</summary>
    public Task<TargetedRecordingHeader> BeginAsync(string softwareId, int maximumDurationSeconds,
        CancellationToken cancellationToken)
        => BeginAsync(softwareId, null, null, maximumDurationSeconds, cancellationToken);

    /// <summary>Starts a recording of the named profile, or of the request's own items, or of the default
    /// profile when neither is given.</summary>
    /// <exception cref="ArgumentException">Missing software id or out-of-range duration (400).</exception>
    /// <exception cref="InvalidOperationException">A recording is already running (409).</exception>
    /// <exception cref="KeyNotFoundException">The software is neither registered nor running (404).</exception>
    /// <exception cref="TargetedRecordingStartException">Profile, item, size or running-target failures.</exception>
    public async Task<TargetedRecordingHeader> BeginAsync(string softwareId, string? profileId,
        IReadOnlyList<TargetedRecordingItem>? items, int maximumDurationSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            throw new ArgumentException("Software ID is required.", nameof(softwareId));
        }
        if (maximumDurationSeconds < 1 || maximumDurationSeconds > MaximumDurationLimitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDurationSeconds));
        }

        await control.WaitAsync(cancellationToken);
        try
        {
            if (active is not null)
            {
                throw new InvalidOperationException("A targeted recording is already running.");
            }

            var records = await softwareRegistry.GetSoftwareAsync(cancellationToken);
            var record = records.FirstOrDefault(item => string.Equals(item.Id, softwareId, StringComparison.OrdinalIgnoreCase));
            var snapshot = await targets.ReadOnceAsync(cancellationToken);
            var name = record?.Name ?? TargetedRecordingTargets.RuntimeName(snapshot, softwareId)
                ?? throw new KeyNotFoundException("Software was not found.");
            var equivalentIds = TargetedRecordingTargets.EquivalentIds(record?.Id ?? softwareId, records);

            var liveCatalog = await catalog.ReadAsync(cancellationToken);
            var selection = await SelectItemsAsync(profileId, items, liveCatalog, cancellationToken);

            var running = TargetedRecordingTargets.Processes(snapshot, equivalentIds);
            if (running.Count == 0)
            {
                throw new TargetedRecordingStartException(StatusCodes.Status409Conflict, "target-not-running");
            }

            var plans = selection.Items.Select(item => Plan(item, selection.DefaultIntervalMs, liveCatalog)).ToArray();
            if (TargetedRecordingSize.EstimateSampledBytes(plans, maximumDurationSeconds)
                > (long)settings.MaximumReportKib * 1024)
            {
                throw new TargetedRecordingStartException(StatusCodes.Status400BadRequest, "report-size-limit-exceeded");
            }

            var startedAt = DateTimeOffset.UtcNow;
            var header = new TargetedRecordingHeader(Guid.NewGuid().ToString("N"), record?.Id ?? softwareId,
                name, startedAt, null, maximumDurationSeconds, "recording", null, false)
            {
                ProfileId = selection.ProfileId,
                ProfileName = selection.ProfileName,
                Items = plans.Select(static plan => plan.InitialReport()).ToArray(),
                Processes = running.Select(process => new TargetedRecordingProcess(process.ProcessId,
                    process.ProcessStartKey ?? 0, process.Name, startedAt, null)).ToArray(),
                MaximumReportKib = settings.MaximumReportKib
            };
            await store.CreateAsync(header, cancellationToken);
            var current = new ActiveRecording(header, equivalentIds, plans, liveCatalog);
            active = current;
            current.Worker = RunAsync(current);
            return header;
        }
        finally
        {
            control.Release();
        }
    }

    public async Task<TargetedRecordingHeader?> FinishAsync(string id, CancellationToken cancellationToken)
    {
        ActiveRecording? current;
        await control.WaitAsync(cancellationToken);
        try
        {
            current = active;
            if (current is null || current.Header.Id != id)
            {
                return null;
            }
            current.StopReason = TargetedRecordingStopReasons.UserStop;
            current.Cancellation.Cancel();
        }
        finally
        {
            control.Release();
        }

        await current.Worker!.WaitAsync(cancellationToken);
        return (await store.ListAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
    }

    /// <summary>Software with at least one running attributed process, by the start check's matcher.</summary>
    public async Task<IReadOnlyList<TargetedRecordingTarget>> ListTargetsAsync(CancellationToken cancellationToken)
    {
        var records = await softwareRegistry.GetSoftwareAsync(cancellationToken);
        return TargetedRecordingTargets.List(await targets.ReadOnceAsync(cancellationToken), records);
    }

    public Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadItemCatalogAsync(CancellationToken cancellationToken)
        => catalog.ReadAsync(cancellationToken);

    public Task<IReadOnlyList<TargetedRecordingHeader>> ListAsync(CancellationToken cancellationToken)
        => store.ListAsync(cancellationToken);

    public Task<TargetedRecordingReport?> GetAsync(string id, CancellationToken cancellationToken)
        => store.GetAsync(id, cancellationToken);

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
        => store.DeleteAsync(id, cancellationToken);

    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
        => store.ClearHistoryAsync(cancellationToken);

    private async Task<ItemSelection> SelectItemsAsync(string? profileId, IReadOnlyList<TargetedRecordingItem>? items,
        IReadOnlyList<TargetedRecordingCatalogItem> liveCatalog, CancellationToken cancellationToken)
    {
        if (items is not null)
        {
            // A request's own list is validated like a profile and against the live catalog.
            if (TargetedRecordingItemReader.ValidateList(items, liveCatalog) is { } reason)
                throw new TargetedRecordingStartException(StatusCodes.Status400BadRequest, reason);
            return new ItemSelection(null, null, TargetedRecordingProfileDefaults.DefaultIntervalMs, items.ToArray());
        }

        var stored = profiles is null
            ? TargetedRecordingProfileDefaults.Create().Profiles
            : await profiles.ReadProfilesAsync(cancellationToken);
        TargetedRecordingProfile? profile;
        if (profileId is not null)
        {
            profile = stored.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal))
                ?? throw new TargetedRecordingStartException(StatusCodes.Status404NotFound,
                    TargetedRecordingProfileErrors.ProfileNotFound);
        }
        else
        {
            profile = stored.FirstOrDefault(static item => item.Id == TargetedRecordingProfileDefaults.DefaultProfileId)
                ?? stored.FirstOrDefault() ?? TargetedRecordingProfileDefaults.CreateDefaultProfile();
        }
        // Saved profile items this machine does not offer, or offers without a source, are recorded as whole-span gaps.
        return new ItemSelection(profile.Id, profile.Name, profile.DefaultIntervalMs, profile.Items.ToArray());
    }

    private static ItemPlan Plan(TargetedRecordingItem item, int profileDefaultIntervalMs,
        IReadOnlyList<TargetedRecordingCatalogItem> liveCatalog)
    {
        var entry = TargetedRecordingItemCatalogRules.Find(liveCatalog, item.Kind, item.Id);
        if (item.Kind == TargetedRecordingItemKinds.Frames)
            return new ItemPlan(item, entry, entry?.Unit ?? TargetedRecordingItemCatalogRules.FramesUnit, null,
                TargetedRecordingIntervalOrigins.PerPresent);
        var (interval, origin) = TargetedRecordingItemReader.Interval(item, entry, profileDefaultIntervalMs);
        return new ItemPlan(item, entry, entry?.Unit ?? string.Empty, interval, origin)
        {
            BreakdownRequest = item.Kind == TargetedRecordingItemKinds.Software
                ? TargetedRecordingItemReader.SoftwareRequest(item, liveCatalog)
                : TargetedRecordingItemCatalogRules.IsTargetGpu(item.Id)
                    ? TargetedRecordingItemReader.SoftwareRequest(
                        new(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuUsageTotalId), liveCatalog)
                    : null
        };
    }

    private async Task RunAsync(ActiveRecording current)
    {
        var id = current.Header.Id;
        var startedAt = current.Header.StartedAt;
        var cursor = startedAt;
        var incomplete = false;
        var seenProcess = false;
        var readyWithoutProcess = 0;
        var goneTicks = 0;
        long storedBytes = 0;
        var sizeLimited = false;
        var maximumBytes = (long)settings.MaximumReportKib * 1024;
        var reason = TargetedRecordingStopReasons.Interrupted;
        IDisposable? framesLease = null;
        var consumers = new List<Task>();
        var framesPlan = current.Plans.FirstOrDefault(static plan => plan.Item.Kind == TargetedRecordingItemKinds.Frames);
        var sampled = current.Plans.Where(static plan => plan.IsSampled).ToArray();
        var controlInterval = TimeSpan.FromMilliseconds(settings.SampleIntervalMilliseconds);
        var maximumDuration = TimeSpan.FromSeconds(current.Header.MaximumDurationSeconds);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(current.Cancellation.Token);
        try
        {
            stop.CancelAfter(maximumDuration);
            if (framesPlan?.Catalog is not null) framesLease = frames.AcquireSubscription();
            // Every item is its own named subscription; its consumer turns the publications delivered to it into points.
            foreach (var plan in sampled)
            {
                plan.Begin(startedAt, settings.NotRefreshedIntervalPercent, settings.ActualIntervalSamples);
                consumers.Add(ConsumeAsync(current, plan, stop.Token));
            }

            // The process check is one more named subscription; its publications drive the recording.
            await foreach (var snapshot in breakdown.SubscribeAsync($"targeted-recording:{id}:processes",
                TargetedRecordingTargets.ProcessRequest, controlInterval, stop.Token))
            {
                var now = DateTimeOffset.UtcNow;
                var running = TargetedRecordingTargets.Processes(snapshot, current.EquivalentIds);
                current.Merge(running, now);
                if (running.Count > 0)
                {
                    seenProcess = true;
                    readyWithoutProcess = 0;
                }
                else if (snapshot.Sampling.Status == ResourceBreakdownSamplingStatuses.Ready)
                {
                    readyWithoutProcess++;
                }
                goneTicks = current.MarkExited(processLiveness, now) ? goneTicks + 1 : 0;

                if (framesPlan?.Catalog is not null)
                {
                    var through = now - FrameReadLag;
                    if (through - cursor > FrameWindow)
                    {
                        framesPlan.AddGap(MillisecondsSince(startedAt, cursor),
                            MillisecondsSince(startedAt, through - FrameWindow), TargetedRecordingGapReasons.FramesWindowSkipped);
                        cursor = through - FrameWindow;
                        incomplete = true;
                    }
                    var batch = frames.ReadIntervals(cursor, through);
                    if (batch is null || !batch.Complete)
                    {
                        framesPlan.AddGap(MillisecondsSince(startedAt, cursor), MillisecondsSince(startedAt, through),
                            TargetedRecordingGapReasons.FramesIncomplete);
                        incomplete = true;
                    }
                    var matching = current.MatchingFrames(batch);
                    if (matching.Count > 0 && storedBytes >= maximumBytes)
                    {
                        framesPlan.AddGap(MillisecondsSince(startedAt, cursor), MillisecondsSince(startedAt, through),
                            TargetedRecordingGapReasons.SizeLimit);
                        sizeLimited = true;
                    }
                    else if (matching.Count > 0)
                    {
                        storedBytes += await store.AppendAsync(id, matching, null, CancellationToken.None);
                        framesPlan.RecordFrames(matching);
                    }
                    cursor = through;
                }

                (storedBytes, var limited) = await StorePointsAsync(id, startedAt, sampled, storedBytes, maximumBytes);
                sizeLimited |= limited;
                if (consumers.FirstOrDefault(static consumer => consumer.IsFaulted) is { } faulted) await faulted;

                if (seenProcess && goneTicks >= settings.ExitConfirmationTicks)
                {
                    reason = TargetedRecordingStopReasons.AllProcessesExited;
                    break;
                }
                if (!seenProcess && readyWithoutProcess >= settings.NoProcessTicks)
                {
                    reason = TargetedRecordingStopReasons.NoProcess;
                    break;
                }
                if (now - startedAt >= maximumDuration)
                {
                    reason = TargetedRecordingStopReasons.MaximumDuration;
                    break;
                }
            }
            if (reason == TargetedRecordingStopReasons.Interrupted)
            {
                // The process-check stream ended without a stop condition: the end comes from the user or the duration.
                await Task.Delay(Timeout.Infinite, stop.Token);
            }

            if (current.Cancellation.IsCancellationRequested)
            {
                reason = current.StopReason;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            reason = current.Cancellation.IsCancellationRequested ? current.StopReason : TargetedRecordingStopReasons.MaximumDuration;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Targeted recording {RecordingId} failed.", id);
            reason = TargetedRecordingStopReasons.CaptureError;
            incomplete = true;
        }
        finally
        {
            // Release every item subscription before the frame tail and the final store.
            await stop.CancelAsync();
            try { await Task.WhenAll(consumers); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                logger.LogError(error, "Targeted recording {RecordingId} item subscription failed.", id);
                incomplete = true;
            }
            try
            {
                if (framesPlan?.Catalog is not null && reason != TargetedRecordingStopReasons.NoProcess)
                {
                    // Allow the ETW flush timer to deliver the final Present events before closing the lease.
                    var stoppedAt = DateTimeOffset.UtcNow;
                    await Task.Delay(FrameTailDelay);
                    if (stoppedAt > cursor && stoppedAt - cursor <= FrameWindow && storedBytes < maximumBytes)
                    {
                        var finalBatch = frames.ReadIntervals(cursor, stoppedAt);
                        if (finalBatch is null || !finalBatch.Complete)
                        {
                            framesPlan.AddGap(MillisecondsSince(startedAt, cursor), MillisecondsSince(startedAt, stoppedAt),
                                TargetedRecordingGapReasons.FramesIncomplete);
                            incomplete = true;
                        }
                        var tail = current.MatchingFrames(finalBatch);
                        if (tail.Count > 0)
                        {
                            storedBytes += await store.AppendAsync(id, tail, null, CancellationToken.None);
                            framesPlan.RecordFrames(tail);
                        }
                    }
                }
                if (reason != TargetedRecordingStopReasons.NoProcess)
                {
                    (storedBytes, var limited) = await StorePointsAsync(id, startedAt, sampled, storedBytes, maximumBytes);
                    sizeLimited |= limited;
                }
            }
            catch (Exception error)
            {
                logger.LogError(error, "Targeted recording {RecordingId} could not store its final frames and points.", id);
                incomplete = true;
            }
            framesLease?.Dispose();

            try
            {
                var endedAt = DateTimeOffset.UtcNow;
                var endMs = MillisecondsSince(startedAt, endedAt);
                if (reason == TargetedRecordingStopReasons.NoProcess)
                {
                    // No target process was ever observed while recording: keep the status, not a report.
                    await store.DiscardSamplesAsync(id, CancellationToken.None);
                    storedBytes = 0;
                    incomplete = true;
                }
                if (sizeLimited || reason is TargetedRecordingStopReasons.Interrupted) incomplete = true;
                var items = current.Plans.Select(plan => plan.FinalReport(endedAt, endMs,
                    reason == TargetedRecordingStopReasons.NoProcess)).ToArray();
                await store.FinishAsync(id, reason, incomplete, items, current.ProcessList(), storedBytes,
                    CancellationToken.None);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Targeted recording {RecordingId} could not be finalized.", id);
            }
            await control.WaitAsync();
            try
            {
                if (ReferenceEquals(active, current))
                {
                    active = null;
                }
                current.Cancellation.Dispose();
            }
            finally
            {
                control.Release();
            }
        }
    }

    /// <summary>Stores the points delivered since the last call, one row per observation time (the software and
    /// system values observed at that instant), in time order. Rows over the size cap become size-limit gaps.</summary>
    private async Task<(long StoredBytes, bool SizeLimited)> StorePointsAsync(string id, DateTimeOffset startedAt,
        IReadOnlyList<ItemPlan> sampled, long storedBytes, long maximumBytes)
    {
        var rows = new SortedDictionary<DateTimeOffset, PointRow>();
        foreach (var plan in sampled)
        foreach (var (at, value) in plan.Drain())
        {
            if (!rows.TryGetValue(at, out var row))
            {
                row = new PointRow();
                rows.Add(at, row);
            }
            (plan.Item.Kind == TargetedRecordingItemKinds.Metric ? row.System : row.Software)[plan.Item.Id] = value;
            row.Plans.Add(plan);
        }
        var sizeLimited = false;
        foreach (var (at, row) in rows)
        {
            var atMs = MillisecondsSince(startedAt, at);
            var sample = new TargetedResourceSample(at, row.Software, row.System, []);
            if (storedBytes + TargetedRecordingSize.EstimateRowBytes(sample) > maximumBytes)
            {
                foreach (var plan in row.Plans)
                    plan.AddGap(atMs, atMs + plan.IntervalMs!.Value, TargetedRecordingGapReasons.SizeLimit);
                sizeLimited = true;
                continue;
            }
            storedBytes += await store.AppendAsync(id, [], sample, CancellationToken.None);
            foreach (var plan in row.Plans) plan.RecordPoint(atMs);
        }
        return (storedBytes, sizeLimited);
    }

    private sealed class PointRow
    {
        public Dictionary<string, double?> Software { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double?> System { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ItemPlan> Plans { get; } = [];
    }

    /// <summary>One item's subscription consumer until the recording stops.</summary>
    private async Task ConsumeAsync(ActiveRecording current, ItemPlan plan, CancellationToken cancellationToken)
    {
        var holder = $"targeted-recording:{current.Header.Id}:{plan.Item.Kind}:{plan.Item.Id}";
        var interval = TimeSpan.FromMilliseconds(plan.IntervalMs!.Value);
        try
        {
            if (plan.Item.Kind == TargetedRecordingItemKinds.Software)
            {
                await foreach (var snapshot in breakdown.SubscribeAsync(holder, plan.BreakdownRequest!, interval, cancellationToken))
                    plan.Accept(TargetedRecordingItemReader.ReadSoftware(snapshot, plan.Item, current.EquivalentIds,
                        current.IsKnown), DateTimeOffset.UtcNow);
                cancellationToken.ThrowIfCancellationRequested();
                plan.Report(TargetedRecordingGapReasons.NoSource, DateTimeOffset.UtcNow);
            }
            else if (TargetedRecordingItemCatalogRules.IsTargetGpu(plan.Item.Id))
            {
                await ConsumeTargetGpuAsync(current, plan, holder, interval, cancellationToken);
            }
            else
            {
                await ConsumeMetricAsync(plan, TargetedRecordingItemReader.MetricAddress(plan.Item), holder, interval,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>Points from one metric address's publications. A stream the source ends by itself means nothing
    /// on this machine can be subscribed at that address: no-source from then on.</summary>
    private async Task ConsumeMetricAsync(ItemPlan plan, string address, string holder, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        await foreach (var publication in metrics.SubscribeAsync(holder, MetricSampleRequest.ForIds([address]), interval,
            cancellationToken))
            plan.Accept(TargetedRecordingItemReader.ReadMetric(publication, address), DateTimeOffset.UtcNow);
        cancellationToken.ThrowIfCancellationRequested();
        plan.Report(TargetedRecordingGapReasons.NoSource, DateTimeOffset.UtcNow);
    }

    /// <summary>gpu.target.*: at each publication of the per-GPU usage bars the item resolves the GPU the target
    /// uses most (TargetedRecordingTargets.TargetGpu) and holds one metric subscription to that GPU's quantity,
    /// recording its publications. A changed GPU releases the old subscription and holds the new one. No GPU is
    /// target-gpu-unresolved; a GPU whose quantity the catalog marks not applicable or without a source gives
    /// that reason.</summary>
    private async Task ConsumeTargetGpuAsync(ActiveRecording current, ItemPlan plan, string holder, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var quantity = TargetedRecordingItemCatalogRules.TargetGpuQuantity(plan.Item.Id);
        int? selected = null;
        var resolved = false;
        CancellationTokenSource? gpuStop = null;
        Task? gpuConsumer = null;
        try
        {
            await foreach (var snapshot in breakdown.SubscribeAsync($"{holder}:gpus", plan.BreakdownRequest!, interval,
                cancellationToken))
            {
                var gpu = TargetedRecordingTargets.TargetGpu(snapshot, current.EquivalentIds);
                if (resolved && gpu == selected) continue;
                resolved = true;
                selected = gpu;
                await StopGpuConsumerAsync(gpuStop, gpuConsumer);
                gpuStop = null;
                gpuConsumer = null;
                var now = DateTimeOffset.UtcNow;
                if (gpu is not { } index)
                {
                    plan.Report(TargetedRecordingTargets.NotReadyReason(snapshot) ?? TargetedRecordingGapReasons.TargetGpuUnresolved, now);
                    continue;
                }
                var address = TargetedRecordingItemCatalogRules.GpuMetricId(index, quantity);
                var entry = TargetedRecordingItemCatalogRules.Find(current.Catalog, TargetedRecordingItemKinds.Metric, address);
                if (entry is null || entry.UnavailableReason is not null)
                {
                    plan.Report(entry?.UnavailableReason ?? TargetedRecordingGapReasons.NoSource, now);
                    continue;
                }
                gpuStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gpuConsumer = ConsumeMetricAsync(plan, address, $"{holder}:{address}", interval, gpuStop.Token);
            }
        }
        finally
        {
            await StopGpuConsumerAsync(gpuStop, gpuConsumer);
        }
    }

    private static async Task StopGpuConsumerAsync(CancellationTokenSource? stop, Task? consumer)
    {
        if (stop is null) return;
        await stop.CancelAsync();
        try { await consumer!; }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }

    private static long MillisecondsSince(DateTimeOffset startedAt, DateTimeOffset at)
        => Math.Max(0, (long)(at - startedAt).TotalMilliseconds);

    private sealed record ItemSelection(string? ProfileId, string? ProfileName, int DefaultIntervalMs,
        IReadOnlyList<TargetedRecordingItem> Items);

    private sealed class ActiveRecording(TargetedRecordingHeader header, IReadOnlySet<string> equivalentIds,
        IReadOnlyList<ItemPlan> plans, IReadOnlyList<TargetedRecordingCatalogItem> catalog)
    {
        // Item consumers match process fields against the known set while the process check extends it.
        private readonly Lock sync = new();
        private readonly Dictionary<(int, long), TargetedRecordingProcess> known = header.Processes
            .ToDictionary(static process => (process.ProcessId, process.ProcessStartKey));

        public TargetedRecordingHeader Header { get; } = header;
        public IReadOnlySet<string> EquivalentIds { get; } = equivalentIds;
        public IReadOnlyList<ItemPlan> Plans { get; } = plans;
        /// <summary>The live catalog at start; target GPU items look up each resolved GPU's quantity in it.</summary>
        public IReadOnlyList<TargetedRecordingCatalogItem> Catalog { get; } = catalog;
        public CancellationTokenSource Cancellation { get; } = new();
        public string StopReason { get; set; } = TargetedRecordingStopReasons.UserStop;
        public Task? Worker { get; set; }

        public void Merge(IReadOnlyList<ResourceProcessSegment> running, DateTimeOffset now)
        {
            lock (sync)
            {
                foreach (var process in running)
                {
                    var key = (process.ProcessId, process.ProcessStartKey ?? 0);
                    if (!known.ContainsKey(key))
                        known[key] = new TargetedRecordingProcess(key.Item1, key.Item2, process.Name, now, null);
                }
            }
        }

        /// <summary>Stamps known processes that no longer exist; true when every known process is gone.</summary>
        public bool MarkExited(ITargetedProcessLiveness liveness, DateTimeOffset now)
        {
            lock (sync)
            {
                var allGone = known.Count > 0;
                foreach (var (key, process) in known.ToArray())
                {
                    if (liveness.IsRunning(new TargetedProcessIdentity(key.Item1, key.Item2)))
                    {
                        allGone = false;
                        continue;
                    }
                    if (process.ExitedAt is null) known[key] = process with { ExitedAt = now };
                }
                return allGone;
            }
        }

        public bool IsKnown(int processId, long startKey)
        {
            lock (sync) return known.ContainsKey((processId, startKey)) || known.ContainsKey((processId, 0));
        }

        public IReadOnlyList<FrameIntervalSample> MatchingFrames(FrameIntervalBatch? batch)
        {
            lock (sync)
                return batch?.Intervals.Where(item => known.ContainsKey((item.ProcessId, item.ProcessStartKey))
                    || known.ContainsKey((item.ProcessId, 0))).ToArray() ?? [];
        }

        public IReadOnlyList<TargetedRecordingProcess> ProcessList()
        {
            lock (sync)
                return known.Values.OrderBy(static process => process.FirstSeenAt).ThenBy(static process => process.ProcessId).ToArray();
        }
    }

    /// <summary>One recorded item. Consumers hand it publications (Accept) and reasons (Report) on the
    /// subscription threads; the recording drains its points into rows and reads its report at the end.</summary>
    /// <remarks>A point is kept once per observation time: a publication whose observation is not newer than the
    /// last point is the same value again. Between two points, or between the start or the end and a point, a
    /// silence longer than not_refreshed_interval_percent of the interval is a gap from one interval after the
    /// earlier point (or from the start) to the later point (or the end). Its reason is the one the source gave
    /// during the silence (the first one from the silence's start, split where it changed), else
    /// source-not-refreshed.</remarks>
    internal sealed class ItemPlan(TargetedRecordingItem item, TargetedRecordingCatalogItem? catalog, string unit,
        int? intervalMs, string intervalOrigin)
    {
        private readonly Lock sync = new();
        private readonly List<long> spacings = [];
        private readonly List<TargetedRecordingGap> gaps = [];
        private readonly List<(DateTimeOffset At, double Value)> pending = [];
        private readonly List<(DateTimeOffset At, string Reason)> silence = [];
        private readonly HashSet<string> actualSources = new(StringComparer.OrdinalIgnoreCase);
        private long? lastPointMs;
        private DateTimeOffset startedAt;
        private DateTimeOffset? lastObservedAt;
        private long? notRefreshedMs;
        private int maximumSpacings;

        public TargetedRecordingItem Item { get; } = item;
        public TargetedRecordingCatalogItem? Catalog { get; } = catalog;
        public string Unit { get; private set; } = unit;
        public int? IntervalMs { get; } = intervalMs;
        public string IntervalOrigin { get; } = intervalOrigin;
        public long PointCount { get; private set; }
        public ResourceBreakdownSampleRequest? BreakdownRequest { get; init; }

        /// <summary>item-unavailable when the live catalog lacks the item, else the catalog's not-applicable or
        /// no-source; such an item is one whole-span gap and is not subscribed.</summary>
        public string? WholeSpanReason => Catalog is null ? TargetedRecordingGapReasons.ItemUnavailable : Catalog.UnavailableReason;

        public bool IsSampled => Item.Kind != TargetedRecordingItemKinds.Frames && WholeSpanReason is null;

        /// <summary>Starts sampling: the recording start, the not-refreshed share of the interval (percent) and how
        /// many point spacings the actual interval (their median) is taken from.</summary>
        public void Begin(DateTimeOffset start, int notRefreshedIntervalPercent, int actualIntervalSamples)
        {
            lock (sync)
            {
                startedAt = start;
                notRefreshedMs = (long)IntervalMs!.Value * notRefreshedIntervalPercent / 100;
                maximumSpacings = actualIntervalSamples;
            }
        }

        /// <summary>One delivered publication: a point at its observation time, or the reason it has none.</summary>
        public void Accept(TargetedRecordingItemReading reading, DateTimeOffset deliveredAt)
        {
            if (reading.Value is not { } value)
            {
                Report(reading.Reason ?? TargetedRecordingGapReasons.ValueUnavailable, deliveredAt);
                return;
            }
            lock (sync)
            {
                var at = reading.ObservedAt ?? deliveredAt;
                if (lastObservedAt is { } last && at <= last) return;
                CloseSilence(at);
                lastObservedAt = at;
                pending.Add((at, value));
                if (reading.ContributingSources.Count > 0) actualSources.UnionWith(reading.ContributingSources);
                else if (reading.ActualSource is { } source) actualSources.Add(source);
                if (Unit.Length == 0 && reading.Unit is { Length: > 0 } readUnit) Unit = readUnit;
            }
        }

        /// <summary>The source said why it has no value now.</summary>
        public void Report(string reason, DateTimeOffset at)
        {
            lock (sync)
                if (silence.Count == 0 || silence[^1].Reason != reason) silence.Add((at, reason));
        }

        public IReadOnlyList<(DateTimeOffset At, double Value)> Drain()
        {
            lock (sync)
            {
                if (pending.Count == 0) return [];
                var points = pending.ToArray();
                pending.Clear();
                return points;
            }
        }

        public void RecordPoint(long atMs)
        {
            lock (sync)
            {
                PointCount++;
                if (lastPointMs is { } last && spacings.Count < maximumSpacings) spacings.Add(atMs - last);
                lastPointMs = atMs;
            }
        }

        public void RecordFrames(IReadOnlyList<FrameIntervalSample> intervals)
        {
            lock (sync)
            {
                PointCount += intervals.Count;
                foreach (var source in intervals.Select(static interval => interval.Source).Distinct())
                    actualSources.Add(source.ToString());
            }
        }

        /// <summary>Adds a gap, extending the previous one when it has the same reason and touches it.</summary>
        public void AddGap(long fromMs, long toMs, string reason)
        {
            lock (sync) AddGapLocked(fromMs, toMs, reason);
        }

        private void AddGapLocked(long fromMs, long toMs, string reason)
        {
            if (toMs < fromMs) toMs = fromMs;
            // Gaps of one reason merge where they touch. Size-limit gaps are one interval per dropped row, so rows a
            // little more than an interval apart still form one gap; silence gaps never reach across a stored point.
            var reach = reason == TargetedRecordingGapReasons.SizeLimit ? IntervalMs ?? 0 : 0;
            if (gaps.Count > 0 && gaps[^1] is var last && last.Reason == reason && fromMs <= last.ToMs + reach)
            {
                gaps[^1] = last with { ToMs = Math.Max(last.ToMs, toMs) };
                return;
            }
            gaps.Add(new TargetedRecordingGap(fromMs, toMs, reason));
        }

        /// <summary>Records the silence before an observation at until (or before the end) as gaps when it is
        /// longer than the not-refreshed limit.</summary>
        private void CloseSilence(DateTimeOffset until)
        {
            var untilMs = Since(until);
            var lastMs = lastObservedAt is { } last ? Since(last) : 0;
            if (notRefreshedMs is { } limit && untilMs - lastMs > limit)
            {
                // The first reason the source gave explains the silence from its start; later ones split it.
                var segmentStart = lastObservedAt is null ? 0 : lastMs + IntervalMs!.Value;
                var current = silence.Count > 0 ? silence[0].Reason : TargetedRecordingGapReasons.SourceNotRefreshed;
                foreach (var (at, reason) in silence)
                {
                    var atMs = Since(at);
                    if (atMs <= segmentStart) { current = reason; continue; }
                    if (atMs >= untilMs) break;
                    if (reason == current) continue;
                    AddGapLocked(segmentStart, atMs, current);
                    current = reason;
                    segmentStart = atMs;
                }
                AddGapLocked(segmentStart, untilMs, current);
            }
            silence.Clear();
        }

        private long Since(DateTimeOffset at) => Math.Max(0, (long)(at - startedAt).TotalMilliseconds);

        public TargetedRecordingItemReport InitialReport() => new(Item.Kind, Item.Id, Unit, Item.Source, [],
            IntervalMs, IntervalOrigin, null, Item.SyncPrecision, 0, []);

        public TargetedRecordingItemReport FinalReport(DateTimeOffset endedAt, long endMs, bool discarded)
        {
            lock (sync)
            {
                if (WholeSpanReason is { } wholeSpan && (Catalog is null || Item.Kind != TargetedRecordingItemKinds.Frames))
                    AddGapLocked(0, endMs, wholeSpan);
                else if (IsSampled && !discarded)
                    CloseSilence(endedAt);
                int? actual = null;
                if (spacings.Count > 0)
                {
                    var ordered = spacings.Order().ToArray();
                    actual = (int)Math.Min(int.MaxValue, ordered[ordered.Length / 2]);
                }
                return new TargetedRecordingItemReport(Item.Kind, Item.Id, Unit, Item.Source,
                    actualSources.Order(StringComparer.Ordinal).ToArray(), IntervalMs, IntervalOrigin,
                    discarded ? null : actual, Item.SyncPrecision, discarded ? 0 : PointCount, gaps.ToArray());
            }
        }
    }
}

/// <summary>Report size estimate: bytes ≈ Σ items (duration × 1000 / interval × PointBytes) + rows × RowBytes
/// + frames × FrameBytes + HeaderBytes + processes × ProcessBytes. Frames depend on the application's frame
/// rate, so the start check estimates the sampled items only; frames count against the cap while recording.</summary>
public static class TargetedRecordingSize
{
    public const int PointBytes = 24;
    public const int RowBytes = 40;
    public const int FrameBytes = 12;
    public const int HeaderBytes = 2048;
    public const int ProcessBytes = 48;

    internal static long EstimateSampledBytes(IEnumerable<TargetedRecordingService.ItemPlan> plans, int durationSeconds)
    {
        long points = 0;
        var minimumInterval = int.MaxValue;
        foreach (var plan in plans)
        {
            if (plan.IntervalMs is not { } interval || !plan.IsSampled) continue;
            points += (long)durationSeconds * 1000 / interval;
            minimumInterval = Math.Min(minimumInterval, interval);
        }
        var rows = minimumInterval == int.MaxValue ? 0 : (long)durationSeconds * 1000 / minimumInterval;
        return points * PointBytes + rows * RowBytes + HeaderBytes;
    }

    public static long EstimateRowBytes(TargetedResourceSample sample)
        => RowBytes + (long)(sample.Software.Count + sample.System.Count) * PointBytes;
}
