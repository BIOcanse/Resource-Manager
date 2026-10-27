using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

public sealed class HostMemoryProtectionService(IHostMemoryProtectionSource source,
    IHostMemoryProtectionExecutor executor, BusinessBlockScheduler scheduler, IHostEnvironment environment,
    ILogger<HostMemoryProtectionService> logger) : IBusinessBlock
{
    public const string BlockId = "HostMemoryProtection";
    public static string EnabledFact => BusinessBlockScheduler.EnabledFact(BlockId);
    public const string PointFact = "memory-protection:normal-running-point-exists";
    public const int RecordLimit = 64;
    private readonly object gate = new();
    private readonly SemaphoreSlim pointCreation = new(1, 1);
    private readonly Queue<HostMemoryActionRecord> actions = new();
    private readonly Dictionary<(int, string), HostMemoryExclusion> cannotOpen = new();
    private IReadOnlyList<HostMemoryExclusion> exclusions = [];
    private NormalRunningPoint? point;
    private HostMemoryProtectionSettings settings = new();
    private CancellationTokenSource? subscriptionCancellation;
    private DateTimeOffset actionFinishedAt;

    public string? ValidateSettings(HostMemoryProtectionSettings value)
    {
        if (!double.IsFinite(value.ThresholdPercent) || value.ThresholdPercent is <= 0 or > 100) return "invalid-threshold-percent";
        if (!double.IsFinite(value.CheckIntervalSeconds) || value.CheckIntervalSeconds is < 1 or > 86400) return "invalid-check-interval";
        if (string.IsNullOrWhiteSpace(value.DumpDirectory)) return "invalid-dump-directory";
        try { _ = ResolveDirectory(value.DumpDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return "invalid-dump-directory"; }
        lock (gate) return value.Enabled && point is null ? "no-normal-running-point" : null;
    }

    // Called in the existing serialized runtime-settings publication path, never from a private settings store.
    public async Task ApplySettingsAsync(HostMemoryProtectionSettings value)
    {
        var error = ValidateSettings(value with { Enabled = false });
        if (error is not null) throw new HostMemoryProtectionSettingsException(error);
        lock (gate)
        {
            // Disablement is delivered by the parent. Only a frequency change renews this run's subscriptions.
            if (value.Enabled && settings.CheckIntervalSeconds != value.CheckIntervalSeconds) subscriptionCancellation?.Cancel();
            settings = value;
        }
        await scheduler.SetEnabledAsync(BlockId, value.Enabled, Reason("settings-published"));
    }

    public NormalRunningPoint? ReadPoint() { lock (gate) return point; }

    public async Task<NormalRunningPoint> CreatePointAsync(CancellationToken cancellationToken)
    {
        await pointCreation.WaitAsync(cancellationToken);
        try
        {
            HostMemoryProtectionSettings current;
            lock (gate) current = settings;
            await foreach (var reading in source.SubscribeAsync("host-memory-protection:normal-running-point",
                TimeSpan.FromSeconds(current.CheckIntervalSeconds), cancellationToken))
            {
                if (reading.Commit is null || reading.Processes is null) continue;
                var next = new NormalRunningPoint(reading.At, reading.Commit, reading.Processes.ToArray());
                lock (gate) { point = next; cannotOpen.Clear(); exclusions = []; }
                await scheduler.SetFactAsync(PointFact, true, Reason("normal-running-point-created"));
                return next;
            }
            throw new InvalidOperationException("normal-running-point-source-ended");
        }
        finally { pointCreation.Release(); }
    }

    public HostMemoryProtectionStatus ReadStatus()
    {
        var commit = source.ReadCurrentCommit();
        lock (gate)
        {
            decimal? trigger = commit is null ? null : (decimal)commit.LimitBytes * (decimal)settings.ThresholdPercent / 100m;
            return new(settings.Enabled, settings.ThresholdPercent, settings.CheckIntervalSeconds, ResolveDirectory(settings.DumpDirectory),
                trigger, trigger - commit?.CurrentBytes, commit,
                point is null ? null : new(point.At, point.ProcessCount),
                point is null ? "no-normal-running-point" : !settings.Enabled ? "disabled" : null,
                exclusions.ToArray(), RecordLimit, actions.Reverse().ToArray());
        }
    }

    public async Task RunAsync(BusinessBlockRunContext context)
    {
        context.ReportRunning();
        while (!context.StopNotification.IsCancellationRequested)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.StopNotification);
            HostMemoryProtectionSettings current;
            lock (gate) { current = settings; subscriptionCancellation = cancellation; }
            try
            {
                await foreach (var reading in source.SubscribeAsync("host-memory-protection",
                    TimeSpan.FromSeconds(current.CheckIntervalSeconds), cancellation.Token))
                {
                    // A buffered delivery from before the preceding action cannot trigger another action.
                    if (reading.At <= actionFinishedAt) continue;
                    await Task.Run(() => ProcessDelivery(reading, current, context.StopNotification), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError(error, "Host memory protection subscription failed.");
                throw;
            }
            finally
            {
                lock (gate) if (ReferenceEquals(subscriptionCancellation, cancellation)) subscriptionCancellation = null;
            }
        }
    }

    private void ProcessDelivery(HostMemoryReading reading, HostMemoryProtectionSettings current, CancellationToken stop)
    {
        NormalRunningPoint baseline;
        Dictionary<(int, string), HostMemoryProcess> baselineProcesses;
        Dictionary<(int, string), HostMemoryExclusion> blocked;
        lock (gate)
        {
            if (point is null) return;
            current = settings;
            baseline = point;
            baselineProcesses = baseline.Processes.ToDictionary(item => (item.ProcessId, item.ProcessStartKey));
            blocked = new(cannotOpen);
        }
        if (reading.Processes is null) return;
        var excluded = blocked.Values.ToList();
        var candidates = new List<(HostMemoryProcess Process, ulong Baseline, decimal Growth)>();
        foreach (var process in reading.Processes)
        {
            if (blocked.ContainsKey((process.ProcessId, process.ProcessStartKey))) continue;
            var before = baselineProcesses.GetValueOrDefault((process.ProcessId, process.ProcessStartKey));
            var reason = process.IsResourceManager || process.ProcessId == Environment.ProcessId ? "resource-manager-process"
                : process.ProcessId <= 4 ? "critical-process"
                : process.ProcessStartKey == "0" ? "process-identity-unavailable"
                : process.PrivateCommitBytes is null ? "private-commit-unavailable"
                : before is { PrivateCommitBytes: null } ? "baseline-private-commit-unavailable" : null;
            if (reason is not null) { excluded.Add(Exclude(process, reason)); continue; }
            var bytes = before?.PrivateCommitBytes ?? 0;
            candidates.Add((process, bytes, (decimal)process.PrivateCommitBytes!.Value - bytes));
        }
        // No sampling and no hold/debounce: commit percentOfTotal atOrAbove the configured threshold on each delivery.
        if (reading.Commit is null || reading.Commit.Percent < current.ThresholdPercent)
        {
            lock (gate) if (ReferenceEquals(point, baseline)) exclusions = excluded.ToArray();
            return;
        }
        foreach (var candidate in candidates.OrderByDescending(item => item.Growth).ThenBy(item => item.Process.ProcessId))
        {
            if (stop.IsCancellationRequested) break;
            using var target = executor.Open(candidate.Process);
            if (target.ExclusionReason is { } reason)
            {
                var exclusion = Exclude(candidate.Process, reason);
                excluded.Add(exclusion);
                // Failed effect opens (including unreadable criticality/identity) stay excluded for this point.
                lock (gate) if (ReferenceEquals(point, baseline)) cannotOpen[(candidate.Process.ProcessId, candidate.Process.ProcessStartKey)] = exclusion;
                continue;
            }
            var result = target.Execute(ResolveDirectory(current.DumpDirectory));
            if (result.Outcome == "excluded")
            {
                var exclusion = Exclude(candidate.Process, result.Reason ?? "cannot-open");
                excluded.Add(exclusion);
                lock (gate) if (ReferenceEquals(point, baseline)) cannotOpen[(candidate.Process.ProcessId, candidate.Process.ProcessStartKey)] = exclusion;
                continue;
            }
            if (!result.SuspendFailed) actionFinishedAt = DateTimeOffset.UtcNow;
            var record = new HostMemoryActionRecord(DateTimeOffset.UtcNow, candidate.Process.ProcessId, candidate.Process.ProcessStartKey,
                candidate.Process.Name, candidate.Baseline, candidate.Process.PrivateCommitBytes!.Value, candidate.Growth,
                result.DumpPath, result.BytesWritten, result.Outcome, result.Reason);
            lock (gate)
            {
                if (ReferenceEquals(point, baseline)) exclusions = excluded.ToArray();
                actions.Enqueue(record);
                while (actions.Count > RecordLimit) actions.Dequeue();
            }
            logger.LogInformation("Host memory protection action: {@Action}", record);
            if (!result.SuspendFailed) break;
        }
        lock (gate) if (ReferenceEquals(point, baseline)) exclusions = excluded.ToArray();
    }

    private string ResolveDirectory(string value)
        => Path.GetFullPath(value, PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath));
    private BusinessBlockReason Reason(string cause) => new(BusinessBlockRequestKind.Configuration, BlockId, cause, scheduler.CurrentPlanVersion);
    private static HostMemoryExclusion Exclude(HostMemoryProcess process, string reason)
        => new(process.ProcessId, process.ProcessStartKey, process.Name, reason);
}
