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
/// the default profile. While recording, the target process set is checked every sample_interval_ms and
/// processes that appear are merged into the known set. End reasons: user-stop; max-duration;
/// all-processes-exited (the recording saw a target process and every known process has been gone for
/// exit_confirmation_ticks checks); no-process (the recording's own checks never saw a target process in
/// no_process_ticks ready checks; its samples are discarded and only the status is kept).</remarks>
public sealed class TargetedRecordingService(
    TargetedRecordingStore store,
    ISoftwareRegistryView softwareRegistry,
    IFrameTimingObservationSource frames,
    IResourceBreakdownObservationSource breakdown,
    IMetricSnapshotObservationSource metrics,
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

    /// <summary>The frame ledger keeps about 30 seconds; reads stay inside it.</summary>
    private static readonly TimeSpan FrameWindow = TimeSpan.FromSeconds(29);
    private static readonly TimeSpan FrameReadLag = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FrameTailDelay = TimeSpan.FromMilliseconds(300);

    private readonly TargetedRecordingOptions settings = options ?? MonitoringSettings.ProductDefaults.TargetedRecording;
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
            var current = new ActiveRecording(header, equivalentIds, plans);
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
        // Saved profile items that this machine does not offer right now are recorded as unavailable gaps.
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
                ? TargetedRecordingItemReader.SoftwareRequest(item, liveCatalog) : null
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
        var leases = new List<IDisposable>();
        var framesPlan = current.Plans.FirstOrDefault(static plan => plan.Item.Kind == TargetedRecordingItemKinds.Frames);
        var sampled = current.Plans.Where(static plan => plan.Item.Kind != TargetedRecordingItemKinds.Frames
            && plan.Catalog is not null).ToArray();
        var controlInterval = TimeSpan.FromMilliseconds(settings.SampleIntervalMilliseconds);
        try
        {
            // Every item is its own named holder; the process check is one more holder.
            leases.Add(breakdown.AcquireSubscription($"targeted-recording:{id}:processes",
                TargetedRecordingTargets.ProcessRequest, controlInterval));
            if (framesPlan?.Catalog is not null) leases.Add(frames.AcquireSubscription());
            foreach (var plan in sampled)
            {
                var interval = TimeSpan.FromMilliseconds(plan.IntervalMs!.Value);
                if (plan.Item.Kind == TargetedRecordingItemKinds.Metric)
                {
                    leases.Add(metrics.AcquireSubscription($"targeted-recording:{id}:metric:{plan.Item.Id}",
                        TargetedRecordingItemReader.MetricRequest(plan.Item), interval));
                }
                else
                {
                    leases.Add(breakdown.AcquireSubscription($"targeted-recording:{id}:software:{plan.Item.Id}",
                        plan.BreakdownRequest, interval));
                }
                plan.NextDue = startedAt + interval;
            }

            var tick = sampled.Select(static plan => TimeSpan.FromMilliseconds(plan.IntervalMs!.Value))
                .Append(controlInterval).Min();
            var nextControl = startedAt + controlInterval;
            using var timer = new PeriodicTimer(tick);
            while (await timer.WaitForNextTickAsync(current.Cancellation.Token))
            {
                var now = DateTimeOffset.UtcNow;
                var nowMs = (long)(now - startedAt).TotalMilliseconds;
                if (now >= nextControl)
                {
                    nextControl = now + controlInterval;
                    var snapshot = breakdown.ReadLatest(TargetedRecordingTargets.ProcessRequest);
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
                }

                var software = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                var system = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                var due = new List<ItemPlan>();
                foreach (var plan in sampled)
                {
                    if (now < plan.NextDue) continue;
                    var interval = TimeSpan.FromMilliseconds(plan.IntervalMs!.Value);
                    plan.NextDue = plan.NextDue + interval > now ? plan.NextDue + interval : now + interval;
                    var reading = plan.Item.Kind == TargetedRecordingItemKinds.Metric
                        ? TargetedRecordingItemReader.ReadMetric(metrics, plan.Item, plan.LastObservedAt)
                        : TargetedRecordingItemReader.ReadSoftware(breakdown.ReadLatest(plan.BreakdownRequest!),
                            plan.Item, current.EquivalentIds, current.IsKnown);
                    if (reading.Value is not { } number)
                    {
                        if (!reading.Unchanged)
                            plan.AddGap(nowMs, nowMs + plan.IntervalMs.Value, TargetedRecordingGapReasons.ValueUnavailable);
                        continue;
                    }
                    if (plan.Item.Kind == TargetedRecordingItemKinds.Metric) plan.LastObservedAt = reading.ObservedAt;
                    if (reading.ActualSource is { } actualSource) plan.ActualSources.Add(actualSource);
                    if (plan.Unit.Length == 0 && reading.Unit is { Length: > 0 } readUnit) plan.Unit = readUnit;
                    (plan.Item.Kind == TargetedRecordingItemKinds.Metric ? system : software)[plan.Item.Id] = number;
                    due.Add(plan);
                }
                if (due.Count > 0)
                {
                    var sample = new TargetedResourceSample(now, software, system, []);
                    if (storedBytes + TargetedRecordingSize.EstimateRowBytes(sample) > maximumBytes)
                    {
                        foreach (var plan in due)
                            plan.AddGap(nowMs, nowMs + plan.IntervalMs!.Value, TargetedRecordingGapReasons.SizeLimit);
                        sizeLimited = true;
                    }
                    else
                    {
                        storedBytes += await store.AppendAsync(id, [], sample, CancellationToken.None);
                        foreach (var plan in due) plan.RecordPoint(nowMs);
                    }
                }

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
                if (now - startedAt >= TimeSpan.FromSeconds(current.Header.MaximumDurationSeconds))
                {
                    reason = TargetedRecordingStopReasons.MaximumDuration;
                    break;
                }
            }

            if (current.Cancellation.IsCancellationRequested)
            {
                reason = current.StopReason;
            }
        }
        catch (OperationCanceledException) when (current.Cancellation.IsCancellationRequested)
        {
            reason = current.StopReason;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Targeted recording {RecordingId} failed.", id);
            reason = TargetedRecordingStopReasons.CaptureError;
            incomplete = true;
        }
        finally
        {
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
            }
            catch (Exception error)
            {
                logger.LogError(error, "Targeted recording {RecordingId} could not read its final frames.", id);
                incomplete = true;
            }
            foreach (var lease in leases) lease.Dispose();

            try
            {
                var endMs = MillisecondsSince(startedAt, DateTimeOffset.UtcNow);
                if (reason == TargetedRecordingStopReasons.NoProcess)
                {
                    // No target process was ever observed while recording: keep the status, not a report.
                    await store.DiscardSamplesAsync(id, CancellationToken.None);
                    storedBytes = 0;
                    incomplete = true;
                }
                if (sizeLimited || reason is TargetedRecordingStopReasons.Interrupted) incomplete = true;
                var items = current.Plans.Select(plan => plan.FinalReport(endMs,
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

    private static long MillisecondsSince(DateTimeOffset startedAt, DateTimeOffset at)
        => Math.Max(0, (long)(at - startedAt).TotalMilliseconds);

    private sealed record ItemSelection(string? ProfileId, string? ProfileName, int DefaultIntervalMs,
        IReadOnlyList<TargetedRecordingItem> Items);

    private sealed class ActiveRecording(TargetedRecordingHeader header, IReadOnlySet<string> equivalentIds,
        IReadOnlyList<ItemPlan> plans)
    {
        private readonly Dictionary<(int, long), TargetedRecordingProcess> known = header.Processes
            .ToDictionary(static process => (process.ProcessId, process.ProcessStartKey));

        public TargetedRecordingHeader Header { get; } = header;
        public IReadOnlySet<string> EquivalentIds { get; } = equivalentIds;
        public IReadOnlyList<ItemPlan> Plans { get; } = plans;
        public CancellationTokenSource Cancellation { get; } = new();
        public string StopReason { get; set; } = TargetedRecordingStopReasons.UserStop;
        public Task? Worker { get; set; }

        public void Merge(IReadOnlyList<ResourceProcessSegment> running, DateTimeOffset now)
        {
            foreach (var process in running)
            {
                var key = (process.ProcessId, process.ProcessStartKey ?? 0);
                if (!known.ContainsKey(key))
                    known[key] = new TargetedRecordingProcess(key.Item1, key.Item2, process.Name, now, null);
            }
        }

        /// <summary>Stamps known processes that no longer exist; true when every known process is gone.</summary>
        public bool MarkExited(ITargetedProcessLiveness liveness, DateTimeOffset now)
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

        public bool IsKnown(int processId, long startKey)
            => known.ContainsKey((processId, startKey)) || known.ContainsKey((processId, 0));

        public IReadOnlyList<FrameIntervalSample> MatchingFrames(FrameIntervalBatch? batch)
            => batch?.Intervals.Where(item => known.ContainsKey((item.ProcessId, item.ProcessStartKey))
                || known.ContainsKey((item.ProcessId, 0))).ToArray() ?? [];

        public IReadOnlyList<TargetedRecordingProcess> ProcessList()
            => known.Values.OrderBy(static process => process.FirstSeenAt).ThenBy(static process => process.ProcessId).ToArray();
    }

    internal sealed class ItemPlan(TargetedRecordingItem item, TargetedRecordingCatalogItem? catalog, string unit,
        int? intervalMs, string intervalOrigin)
    {
        private const int MaximumSpacings = 8192;
        private readonly List<long> spacings = [];
        private readonly List<TargetedRecordingGap> gaps = [];
        private long? lastPointMs;

        public TargetedRecordingItem Item { get; } = item;
        public TargetedRecordingCatalogItem? Catalog { get; } = catalog;
        public string Unit { get; set; } = unit;
        public int? IntervalMs { get; } = intervalMs;
        public string IntervalOrigin { get; } = intervalOrigin;
        public HashSet<string> ActualSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long PointCount { get; private set; }
        public DateTimeOffset NextDue { get; set; }
        public DateTimeOffset? LastObservedAt { get; set; }
        public ResourceBreakdownSampleRequest? BreakdownRequest { get; set; }

        public void RecordPoint(long atMs)
        {
            PointCount++;
            if (lastPointMs is { } last && spacings.Count < MaximumSpacings) spacings.Add(atMs - last);
            lastPointMs = atMs;
        }

        public void RecordFrames(IReadOnlyList<FrameIntervalSample> intervals)
        {
            PointCount += intervals.Count;
            foreach (var source in intervals.Select(static interval => interval.Source).Distinct())
                ActualSources.Add(source.ToString());
        }

        /// <summary>Adds a gap, extending the previous one when it has the same reason and touches it.</summary>
        public void AddGap(long fromMs, long toMs, string reason)
        {
            if (toMs < fromMs) toMs = fromMs;
            if (gaps.Count > 0 && gaps[^1] is var last && last.Reason == reason && fromMs <= last.ToMs + (IntervalMs ?? 0))
            {
                gaps[^1] = last with { ToMs = Math.Max(last.ToMs, toMs) };
                return;
            }
            gaps.Add(new TargetedRecordingGap(fromMs, toMs, reason));
        }

        public TargetedRecordingItemReport InitialReport() => new(Item.Kind, Item.Id, Unit, Item.Source, [],
            IntervalMs, IntervalOrigin, null, Item.SyncPrecision, 0, []);

        public TargetedRecordingItemReport FinalReport(long endMs, bool discarded)
        {
            if (Catalog is null) AddGap(0, endMs, TargetedRecordingGapReasons.ItemUnavailable);
            int? actual = null;
            if (spacings.Count > 0)
            {
                var ordered = spacings.Order().ToArray();
                actual = (int)Math.Min(int.MaxValue, ordered[ordered.Length / 2]);
            }
            return new TargetedRecordingItemReport(Item.Kind, Item.Id, Unit, Item.Source,
                ActualSources.Order(StringComparer.Ordinal).ToArray(), IntervalMs, IntervalOrigin,
                discarded ? null : actual, Item.SyncPrecision, discarded ? 0 : PointCount, gaps.ToArray());
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
            if (plan.IntervalMs is not { } interval || plan.Catalog is null) continue;
            points += (long)durationSeconds * 1000 / interval;
            minimumInterval = Math.Min(minimumInterval, interval);
        }
        var rows = minimumInterval == int.MaxValue ? 0 : (long)durationSeconds * 1000 / minimumInterval;
        return points * PointBytes + rows * RowBytes + HeaderBytes;
    }

    public static long EstimateRowBytes(TargetedResourceSample sample)
        => RowBytes + (long)(sample.Software.Count + sample.System.Count) * PointBytes;
}
