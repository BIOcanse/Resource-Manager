using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

/// <summary>Owns short Present leases while registered software has an unidentified live executable.</summary>
public sealed class GpuPlacementProcessHistoryObserver(
    ISchedulingProcessFactObservationSource processes,
    ISoftwareRegistryView software,
    IRuntimePlanProvider plans,
    IGpuPlacementProcessHistoryStore history,
    IPresentGraphicsApiObservationSource presents,
    ILogger<GpuPlacementProcessHistoryObserver> logger,
    TimeProvider? clock = null) : BackgroundService
{
    internal static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly SchedulingProcessFactRequest Request = new(
        SchedulingProcessMetricMask.GpuUsage,
        new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Unavailable, 0, 0, 0, 0, 0, 0, []));
    private static readonly SchedulingProcessFactRequest InventoryRequest = Request with { RequestedMetricMask = SchedulingProcessMetricMask.None };
    private readonly Dictionary<(int Pid, ulong Birth), Attempt> attempts = [];
    internal int Rounds { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Passive publication registration: no periodic metric sampling demand.
        using var publications = (processes as ISchedulingProcessFactPublicationSource)?.Publications.Subscribe(
            [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution, SamplingDatasetIds.ProcessGpuUsage]);
        var results = new CurrentValuePublicationSignal();
        using var changed = results.Subscribe();
        presents.ObservationChanged += results.Publish;
        string? previousFacts = null;
        IReadOnlySet<string> registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Task? publicationWait = null;
        Task? resultWait = null;
        var completionWake = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // live: publication values, never a fresh hardware read.
                    var snapshot = processes.ReadLatest(Request) ?? processes.ReadLatest(InventoryRequest);
                    var facts = FactKey(snapshot);
                    var factChanged = facts != previousFacts;
                    if (factChanged)
                    {
                        previousFacts = facts;
                        registered = snapshot?.Processes.Any(process => UsesGpu(process)
                            && !attempts.ContainsKey((process.ProcessId, process.ProcessStartKey))) == true
                            ? (await software.GetSoftwareAsync(stoppingToken)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                            : registered;
                    }
                    if (factChanged && (snapshot?.Processes.Any(UsesGpu) == true || attempts.Count != 0) || completionWake)
                        await ReconcileAsync(snapshot, registered, time.GetTimestamp(), stoppingToken);
                    completionWake = false;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    ReleaseSubscriptions();
                    logger.LogWarning(error, "Present graphics API history observation failed.");
                }
                publicationWait ??= publications is null ? Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                    : publications.WaitAsync(stoppingToken).AsTask();
                resultWait ??= changed.WaitAsync(stoppingToken).AsTask();
                using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var remaining = attempts.Values.Where(item => item.Subscription is not null)
                    .Select(item => ObservationWindow - time.GetElapsedTime(item.Started, time.GetTimestamp()))
                    .DefaultIfEmpty(Timeout.InfiniteTimeSpan).Min();
                var deadline = Task.Delay(remaining < TimeSpan.Zero && remaining != Timeout.InfiniteTimeSpan
                    ? TimeSpan.Zero : remaining, time, deadlineCancellation.Token);
                var wake = await Task.WhenAny(publicationWait, resultWait, deadline);
                await deadlineCancellation.CancelAsync();
                await wake;
                completionWake = wake == resultWait || wake == deadline;
                if (wake == publicationWait) publicationWait = null;
                if (wake == resultWait) resultWait = null;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            presents.ObservationChanged -= results.Publish;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                foreach (var attempt in attempts.Values) await EndEmptyAttemptAsync(attempt, cleanup.Token);
            }
            catch (Exception error) { logger.LogWarning(error, "Present history observation could not persist its final result."); }
            finally { ReleaseSubscriptions(); attempts.Clear(); }
        }
    }

    private static bool UsesGpu(SchedulingProcessFact process) => process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
        && process.Gpus.Any(gpu => gpu.HasMetric(SchedulingProcessMetricMask.GpuUsage) && double.IsFinite(gpu.UsagePercent) && gpu.UsagePercent > 0);

    private static string FactKey(SchedulingProcessFactSnapshot? snapshot) => snapshot is null ? "unavailable"
        : $"{snapshot.InventoryStatus}:{snapshot.CurrentMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)};"
            + string.Join('|', snapshot.CurrentInventoryProcesses.OrderBy(item => item.ProcessId)
            .Select(item => $"{item.ProcessId}:{item.ProcessStartKey}:{item.ExecutablePath}")) + ";"
            + string.Join('|', snapshot.Processes.Where(UsesGpu).OrderBy(item => item.ProcessId)
                .Select(item => $"{item.ProcessId}:{item.ProcessStartKey}:{item.SoftwareId}:{item.ExecutablePath}"));

    internal async Task ReconcileAsync(SchedulingProcessFactSnapshot? snapshot, IReadOnlySet<string> registered,
        long timestamp, CancellationToken cancellationToken)
    {
        Rounds++;
        var current = snapshot is { InventoryStatus: SamplingObservationStatus.Current }
            && snapshot.CurrentMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
            ? snapshot.Processes.Where(process => process.ProcessId > 4 && process.ProcessStartKey is > 0 and <= long.MaxValue
                && !string.IsNullOrWhiteSpace(process.ExecutablePath) && Path.IsPathFullyQualified(process.ExecutablePath))
                .ToDictionary(process => (process.ProcessId, process.ProcessStartKey)) : null;
        // Processes is a metric projection, not the inventory. A missing runtime
        // row must not erase a completed attempt and reopen Present on its return.
        var knownLiveInstances = snapshot?.IsInventoryCurrentComplete() == true
            ? snapshot.CurrentInventoryProcesses.Select(process => (process.ProcessId, process.ProcessStartKey)).ToHashSet()
            : null;

        foreach (var (key, attempt) in attempts.ToArray())
        {
            if (attempt.Subscription?.Read() is { } api)
            {
                await history.SaveFirstGraphicsApiAsync(attempt.SoftwareId, attempt.SoftwareName,
                    attempt.Process, api, "etw-present", cancellationToken);
                foreach (var sibling in attempts.Values.Where(item => item.SoftwareId.Equals(attempt.SoftwareId, StringComparison.OrdinalIgnoreCase)
                    && ProcessKey(item.Process).Equals(ProcessKey(attempt.Process), StringComparison.OrdinalIgnoreCase))) sibling.Release();
            }
            else if (attempt.Subscription?.IsComplete == true)
                await EndEmptyAttemptAsync(attempt, cancellationToken);
            if (knownLiveInstances is not null && !knownLiveInstances.Contains(key))
            {
                await EndEmptyAttemptAsync(attempt, cancellationToken);
                attempts.Remove(key);
                continue;
            }
            if (current is not null && (!current.TryGetValue(key, out var process)
                || !registered.Contains(process.SoftwareId) || Excluded(process)
                || !string.Equals(attempt.SoftwareId, process.SoftwareId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(attempt.Process.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                await EndEmptyAttemptAsync(attempt, cancellationToken);
                // Keep a completed attempt until exit, including policy changes; no repeating
                // short sessions for the same unidentified process on later publications.
                continue;
            }
            if (attempt.Subscription is not null && time.GetElapsedTime(attempt.Started, timestamp) >= ObservationWindow)
            {
                await EndEmptyAttemptAsync(attempt, cancellationToken);
                continue;
            }
        }
        if (current is null)
        {
            foreach (var attempt in attempts.Values) await EndEmptyAttemptAsync(attempt, cancellationToken);
            return;
        }

        foreach (var group in current.Values.Where(process => registered.Contains(process.SoftwareId)
            && UsesGpu(process) && !Excluded(process))
            .GroupBy(process => process.SoftwareId, StringComparer.OrdinalIgnoreCase))
        {
            var saved = await history.GetSoftwareHistoryAsync(group.Key, group.First().SoftwareName, cancellationToken);
            foreach (var process in group)
            {
                // Policy may have changed while the history file was being read.
                if (Excluded(process)) continue;
                var instance = new GpuPlacementProcessInstance(process.ProcessId, process.ProcessStartKey,
                    process.ProcessName, process.ExecutablePath!);
                var archived = saved.Processes.FirstOrDefault(item => item.ProcessKey.Equals(ProcessKey(instance), StringComparison.OrdinalIgnoreCase));
                var known = archived?.GraphicsApi is not null || archived?.NoPresentUntil > time.GetUtcNow();
                if (attempts.TryGetValue((process.ProcessId, process.ProcessStartKey), out var ended)
                    && ended.Subscription is null && archived?.NoPresentUntil <= time.GetUtcNow())
                    attempts.Remove((process.ProcessId, process.ProcessStartKey));
                if (!attempts.TryGetValue((process.ProcessId, process.ProcessStartKey), out var attempt))
                {
                    attempt = new(process.SoftwareId, process.SoftwareName, instance, timestamp,
                        known ? null : presents.AcquireGraphicsApiSubscription(instance));
                    attempts.Add((process.ProcessId, process.ProcessStartKey), attempt);
                }
                if (known) await EndEmptyAttemptAsync(attempt, cancellationToken);
                else if (attempt.Subscription?.Read() is { } api)
                {
                    attempt.Release();
                    saved = await history.SaveFirstGraphicsApiAsync(attempt.SoftwareId, attempt.SoftwareName,
                        attempt.Process, api, "etw-present", cancellationToken);
                    foreach (var sibling in attempts.Values.Where(item => string.Equals(item.SoftwareId, attempt.SoftwareId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.Process.ExecutablePath, instance.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                        sibling.Release();
                }
            }
        }
    }

    private static string ProcessKey(GpuPlacementProcessInstance process)
        => JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath);

    private async Task EndEmptyAttemptAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Subscription is null) return;
        var api = attempt.Subscription.Complete();
        attempt.Release();
        if (api is { } identified)
            await history.SaveFirstGraphicsApiAsync(attempt.SoftwareId, attempt.SoftwareName, attempt.Process,
                identified, "etw-present", cancellationToken);
        else
            await history.SaveNoPresentAsync(attempt.SoftwareId, attempt.SoftwareName, attempt.Process,
                time.GetUtcNow().AddSeconds(plans.Current.HostManager.HotPublish.PlacementCoordinator.NoPresentExpirySeconds), cancellationToken);
    }

    private bool Excluded(SchedulingProcessFact process) => plans.Current.GpuPlacement.Resolve(
        process.SoftwareId, process.SoftwareName, process.SoftwareKind,
        JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath)).GpuSchedulingExcluded;

    private void ReleaseSubscriptions()
    {
        foreach (var attempt in attempts.Values) attempt.Release();
    }

    private sealed class Attempt(string softwareId, string softwareName, GpuPlacementProcessInstance process,
        long started, IPresentGraphicsApiSubscription? subscription)
    {
        internal string SoftwareId { get; } = softwareId;
        internal string SoftwareName { get; } = softwareName;
        internal GpuPlacementProcessInstance Process { get; } = process;
        internal long Started { get; } = started;
        internal IPresentGraphicsApiSubscription? Subscription { get; private set; } = subscription;
        internal void Release() { Subscription?.Dispose(); Subscription = null; }
    }
}
