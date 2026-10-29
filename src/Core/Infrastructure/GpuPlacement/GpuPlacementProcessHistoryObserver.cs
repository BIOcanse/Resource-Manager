using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

/// <summary>Owns short Present leases while registered software has an unidentified live executable.</summary>
public sealed class GpuPlacementProcessHistoryObserver(
    ISchedulingProcessFactObservationSource processes,
    ISoftwareRegistryView software,
    IRuntimePlanProvider plans,
    IGpuPlacementProcessHistoryStore history,
    IPresentGraphicsApiObservationSource presents,
    ILogger<GpuPlacementProcessHistoryObserver> logger) : BackgroundService
{
    internal static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly SchedulingProcessFactRequest Request = new(
        SchedulingProcessMetricMask.RuntimeState,
        new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Unavailable, 0, 0, 0, 0, 0, 0, []));
    private readonly Dictionary<(int Pid, ulong Birth), Attempt> attempts = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = processes.AcquireSubscription(
            "gpu-placement-process-history", SchedulingProcessMetricMask.RuntimeState, RefreshInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(processes.ReadLatest(Request),
                        (await software.GetSoftwareAsync(stoppingToken)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        TimeProvider.System.GetTimestamp(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    ReleaseSubscriptions();
                    logger.LogWarning(error, "Present graphics API history observation failed.");
                }
                await Task.Delay(RefreshInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { ReleaseSubscriptions(); attempts.Clear(); }
    }

    internal async Task ReconcileAsync(SchedulingProcessFactSnapshot? snapshot, IReadOnlySet<string> registered,
        long timestamp, CancellationToken cancellationToken)
    {
        var current = snapshot is { InventoryStatus: SamplingObservationStatus.Current }
            && snapshot.CurrentMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
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
            if (knownLiveInstances is not null && !knownLiveInstances.Contains(key))
            {
                attempt.Release();
                attempts.Remove(key);
                continue;
            }
            if (current is not null && (!current.TryGetValue(key, out var process)
                || !registered.Contains(process.SoftwareId) || Excluded(process)
                || !string.Equals(attempt.SoftwareId, process.SoftwareId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(attempt.Process.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                attempt.Release();
                // Keep a completed attempt until exit, including policy changes; no repeating
                // short sessions for the same unidentified process on every polling cycle.
                continue;
            }
            if (TimeProvider.System.GetElapsedTime(attempt.Started, timestamp) >= ObservationWindow)
            {
                attempt.Release();
                continue;
            }
        }
        if (current is null) { ReleaseSubscriptions(); return; }

        foreach (var group in current.Values.Where(process => registered.Contains(process.SoftwareId)
            && !Excluded(process))
            .GroupBy(process => process.SoftwareId, StringComparer.OrdinalIgnoreCase))
        {
            if (group.All(process => attempts.TryGetValue((process.ProcessId, process.ProcessStartKey), out var ended)
                && ended.Subscription is null)) continue;
            var saved = await history.GetSoftwareHistoryAsync(group.Key, group.First().SoftwareName, cancellationToken);
            foreach (var process in group)
            {
                // Policy may have changed while the history file was being read.
                if (Excluded(process)) continue;
                var instance = new GpuPlacementProcessInstance(process.ProcessId, process.ProcessStartKey,
                    process.ProcessName, process.ExecutablePath!);
                var known = saved.Processes.Any(item => item.GraphicsApi is not null && item.ProcessKey.Equals(
                    JsonGpuPlacementProcessHistoryStore.BuildProcessKey(instance.ProcessName, instance.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase));
                if (!attempts.TryGetValue((process.ProcessId, process.ProcessStartKey), out var attempt))
                {
                    attempt = new(process.SoftwareId, process.SoftwareName, instance, timestamp,
                        known ? null : presents.AcquireGraphicsApiSubscription(instance));
                    attempts.Add((process.ProcessId, process.ProcessStartKey), attempt);
                }
                if (known) attempt.Release();
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
