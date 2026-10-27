using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

public sealed class HostMemoryProtectionService(IHostMemoryProtectionSource source,
    IHostMemoryProtectionExecutor executor, IHostMemoryProtectionReserve reserve, BusinessBlockScheduler scheduler, IHostEnvironment environment,
    ILogger<HostMemoryProtectionService> logger, IGpuPlacementPolicyStore softwarePolicies) : IBusinessBlock
{
    private readonly StateCell<bool> enabled = scheduler.Authority.ClaimCell<bool>(StateItemUids.HostMemoryProtectionEnabled);
    private readonly StateCell<bool> pointExists = scheduler.Authority.ClaimCell<bool>(StateItemUids.MemoryProtectionNormalRunningPointExists);
    private readonly StateCell<bool> reserveBuilt = scheduler.Authority.ClaimCell<bool>(StateItemUids.MemoryProtectionReserveBuilt);
    public const int RecordLimit = 64;
    private readonly object gate = new();
    private readonly SemaphoreSlim pointCreation = new(1, 1);
    private readonly SemaphoreSlim configuration = new(1, 1);
    private readonly Queue<HostMemoryActionRecord> actions = new();
    private readonly Dictionary<(int, string), HostMemoryExclusion> cannotOpen = new();
    private IReadOnlyList<HostMemoryExclusion> exclusions = [];
    private NormalRunningPoint? point;
    private HostMemoryProtectionSettings settings = new();
    private CancellationTokenSource? subscriptionCancellation;
    private DateTimeOffset actionFinishedAt;

    public string? ValidateSettings(HostMemoryProtectionSettings value)
    {
        if (!HostMemoryProtectionActionModes.IsSupported(value.ActionMode)) return "invalid-action-mode";
        if (!double.IsFinite(value.ThresholdPercent) || value.ThresholdPercent is <= 0 or > 100) return "invalid-threshold-percent";
        if (!double.IsFinite(value.CheckIntervalSeconds) || value.CheckIntervalSeconds is < 1 or > 86400) return "invalid-check-interval";
        try
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong total = 0;
            foreach (var entry in value.Reserves ?? [])
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Location)) return "invalid-reserve-location";
                if (entry.SizeBytes is 0 or > long.MaxValue) return "invalid-reserve-size";
                var location = ResolveDirectory(entry.Location);
                if (!locations.Add(location)) return "duplicate-reserve-location";
                total = checked(total + entry.SizeBytes);
                if (total > long.MaxValue) return "invalid-reserve-total-size";
                // A read-only volume query rejects partial clusters before settings commit.
                var cluster = HostMemoryDiskReserveNative.Volume(ExistingDirectory(location)).Cluster;
                if (entry.SizeBytes % cluster != 0) return $"reserve-size-must-be-whole-clusters:cluster-bytes={cluster}";
            }
        }
        catch (OverflowException) { return "invalid-reserve-total-size"; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return "invalid-reserve-location:" + error.Message; }
        lock (gate) if (value.Enabled && point is null) return "no-normal-running-point";
        return value.Enabled ? ReserveUnavailableReason(value, reserve.ReadStatus()) : null;
    }

    // Called in the existing serialized runtime-settings publication path, never from a private settings store.
    public async Task ApplySettingsAsync(HostMemoryProtectionSettings value)
    {
        var error = ValidateSettings(value with { Enabled = false });
        if (error is not null) throw new HostMemoryProtectionSettingsException(error);
        await configuration.WaitAsync();
        try
        {
            var entries = ResolveEntries(value);
            var currentReserve = reserve.ReadStatus();
            if (!Matches(entries, currentReserve))
                await reserveBuilt.PublishAsync(false, Reason("reserve-configuring"));
            await Task.Run(() => reserve.Prepare(entries));
            lock (gate)
            {
                // Disablement is delivered by the parent. Only a frequency change renews subscriptions.
                if (value.Enabled && settings.CheckIntervalSeconds != value.CheckIntervalSeconds) subscriptionCancellation?.Cancel();
                settings = value;
            }
            await PublishStateAsync("settings-published");
        }
        finally { configuration.Release(); }
    }

    public async Task<HostMemoryProtectionStatus> RebuildReserveAsync(CancellationToken cancellationToken)
    {
        await configuration.WaitAsync(cancellationToken);
        try
        {
            await reserveBuilt.PublishAsync(false, Reason("reserve-rebuilding"));
            // Once started, this finite allocation finishes and publishes its real result even if HTTP disconnects.
            HostMemoryProtectionSettings current;
            lock (gate) current = settings;
            var entries = ResolveEntries(current);
            await Task.Run(() =>
            {
                var prepared = reserve.Prepare(entries);
                return Matches(entries, prepared) ? reserve.Rebuild() : prepared;
            });
            await PublishStateAsync("reserve-rebuilt");
            return ReadStatus();
        }
        finally { configuration.Release(); }
    }

    private async Task PublishStateAsync(string cause)
    {
        HostMemoryProtectionSettings current;
        lock (gate) current = settings;
        await enabled.PublishAsync(current.Enabled, Reason(cause));
        await reserveBuilt.PublishAsync(ReserveUnavailableReason(current, reserve.ReadStatus()) is null, Reason(cause));
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
                await pointExists.PublishAsync(true, Reason("normal-running-point-created"));
                return next;
            }
            throw new InvalidOperationException("normal-running-point-source-ended");
        }
        finally { pointCreation.Release(); }
    }

    public HostMemoryProtectionStatus ReadStatus()
    {
        var commit = source.ReadCurrentCommit();
        var currentReserve = reserve.ReadStatus();
        lock (gate)
        {
            decimal? trigger = commit is null ? null : (decimal)commit.LimitBytes * (decimal)settings.ThresholdPercent / 100m;
            return new(settings.Enabled, settings.ThresholdPercent, settings.CheckIntervalSeconds, currentReserve,
                trigger, trigger - commit?.CurrentBytes, commit,
                point is null ? null : new(point.At, point.ProcessCount),
                point is null ? "no-normal-running-point" : !settings.Enabled ? "disabled" : ReserveUnavailableReason(settings, currentReserve),
                exclusions.ToArray(), RecordLimit, actions.Reverse().ToArray()) { ActionMode = settings.ActionMode };
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
                    await Task.Run(() => ProcessDeliveryAsync(reading, current, context.StopNotification), CancellationToken.None);
                    if (!reserve.ReadStatus().Built)
                    {
                        await configuration.WaitAsync();
                        try { await PublishStateAsync("reserve-unavailable"); }
                        finally { configuration.Release(); }
                    }
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

    private async Task ProcessDeliveryAsync(HostMemoryReading reading, HostMemoryProtectionSettings current, CancellationToken stop)
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
        if (ReserveUnavailableReason(current, reserve.ReadStatus()) is not null) return;
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
        // Read the existing per-software policies only when their no-dump flag can affect an action.
        var noDumpSoftware = current.ActionMode == HostMemoryProtectionActionModes.DumpThenTerminate && candidates.Count != 0
            ? (await softwarePolicies.GetAsync(stop)).SoftwarePolicies.Where(item => item.HostMemoryProtectionNoDump)
                .Select(item => item.SoftwareId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var result = target.Execute(reserve, new(current.ActionMode,
                candidate.Process.SoftwareId is { } softwareId && noDumpSoftware.Contains(softwareId)));
            if (result.Outcome == "reserve-unavailable") break;
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
                result.DumpPaths, result.BytesWritten, result.Outcome, result.Reason);
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
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath)));
    private string? ReserveUnavailableReason(HostMemoryProtectionSettings value, HostMemoryReserveStatus status)
        => !Matches(ResolveEntries(value), status) ? "disk-reserve-not-built"
            : !status.Built ? status.Reason ?? "disk-reserve-not-built" : null;
    private HostMemoryReserveSettings[] ResolveEntries(HostMemoryProtectionSettings value)
        => (value.Reserves ?? []).Select(entry => entry with { Location = ResolveDirectory(entry.Location) }).ToArray();
    private static bool Matches(IReadOnlyList<HostMemoryReserveSettings> entries, HostMemoryReserveStatus status)
        => entries.Count == status.Entries.Count && entries.Where((entry, index) =>
            !string.Equals(entry.Location, status.Entries[index].Location, StringComparison.OrdinalIgnoreCase)
            || entry.SizeBytes != status.Entries[index].ConfiguredSizeBytes).Any() == false;
    private static string ExistingDirectory(string location)
    {
        for (var count = 0; count < 256; count++)
        {
            if (Directory.Exists(location)) return location;
            location = Path.GetDirectoryName(location) ?? throw new IOException("reserve-volume-not-found");
        }
        throw new IOException("reserve-path-too-deep");
    }
    private BusinessBlockReason Reason(string cause) => new(BusinessBlockRequestKind.Configuration, BlockUids.HostMemoryProtection.ToString(System.Globalization.CultureInfo.InvariantCulture), cause, scheduler.CurrentPlanVersion);
    private static HostMemoryExclusion Exclude(HostMemoryProcess process, string reason)
        => new(process.ProcessId, process.ProcessStartKey, process.Name, reason);
}
