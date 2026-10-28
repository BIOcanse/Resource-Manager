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

public sealed partial class HostMemoryProtectionService(IHostMemoryProtectionSource source,
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
    private bool reserveAccepted;
    private string? currentActionId;
    private string? decisionReason;

    public string? ValidateSettings(HostMemoryProtectionSettings value)
    {
        if (!HostMemoryProtectionActionModes.IsSupported(value.ActionMode)) return "invalid-action-mode";
        if (value.MinimumProcessGrowthBytes <= 0) return "invalid-minimum-process-growth-bytes";
        if (value.ProtectedProcesses is null) return "invalid-protected-processes";
        var protectedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.ProtectedProcesses)
        {
            if (string.IsNullOrWhiteSpace(entry) || entry != entry.Trim() || !protectedEntries.Add(entry)) return "invalid-protected-processes";
            if (Path.IsPathFullyQualified(entry))
            {
                try { if (string.IsNullOrEmpty(Path.GetFileName(entry)) || entry.Contains('*') || entry.Contains('?')) return "invalid-protected-processes"; _ = Path.GetFullPath(entry); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return "invalid-protected-processes"; }
            }
            else if (Path.GetFileName(entry) != entry || entry.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "invalid-protected-processes";
        }
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
        lock (gate)
            return value.Enabled && !(settings.Enabled && reserveAccepted && Matches(ResolveEntries(value), reserve.ReadStatus()))
                ? ReserveUnavailableReason(value, reserve.ReadStatus()) : null;
    }

    // Called in the existing serialized runtime-settings publication path, never from a private settings store.
    public async Task ApplySettingsAsync(HostMemoryProtectionSettings value)
    {
        var error = ValidateSettings(value with { Enabled = false });
        if (error is not null) throw new HostMemoryProtectionSettingsException(error);
        await RestorePointsOnceAsync();
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
                reserveAccepted = value.Enabled && (Matches(entries, currentReserve) && reserveAccepted || reserve.ReadStatus().Built);
            }
            await PublishStateAsync("settings-published");
            PublishStatus();
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
            PublishStatus();
            return await ReadStatusAsync(cancellationToken);
        }
        finally { configuration.Release(); }
    }

    private async Task PublishStateAsync(string cause)
    {
        HostMemoryProtectionSettings current;
        lock (gate) current = settings;
        await enabled.PublishAsync(current.Enabled, Reason(cause));
        bool accepted;
        lock (gate) accepted = reserveAccepted;
        await reserveBuilt.PublishAsync(accepted || ReserveUnavailableReason(current, reserve.ReadStatus()) is null, Reason(cause));
    }

    public async Task<HostMemoryProtectionStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var commit = await source.ReadCurrentCommitAsync(cancellationToken);
        lock (gate) return BuildStatus(commit);
    }

    private HostMemoryProtectionStatus BuildStatus(HostMemoryCommit? commit)
    {
        var currentReserve = reserve.ReadStatus();
        lock (gate)
        {
            decimal? trigger = commit is null ? null : (decimal)commit.LimitBytes * (decimal)settings.ThresholdPercent / 100m;
            return new(settings.Enabled, settings.ThresholdPercent, settings.CheckIntervalSeconds, currentReserve,
                trigger, trigger - commit?.CurrentBytes, commit,
                point is null ? null : new(point.Id, point.At, point.ProcessCount, point.Commit),
                point is null ? "no-normal-running-point" : !settings.Enabled ? "disabled" : reserveAccepted ? null : ReserveUnavailableReason(settings, currentReserve),
                exclusions.ToArray(), RecordLimit, actions.Reverse().ToArray())
            {
                ActionMode = settings.ActionMode,
                AtOrAboveThreshold = commit is null ? null : commit.Percent >= settings.ThresholdPercent,
                CurrentActionId = currentActionId,
                MinimumProcessGrowthBytes = settings.MinimumProcessGrowthBytes,
                ProtectedProcesses = settings.ProtectedProcesses.ToArray(), DecisionReason = decisionReason
            };
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
        Dictionary<(int, string), HostMemoryProcess> before;
        Dictionary<(int, string), HostMemoryExclusion> blocked;
        lock (gate)
        {
            if (point is null) return;
            current = settings;
            baseline = point;
            before = baseline.Processes.ToDictionary(item => (item.ProcessId, item.ProcessStartKey));
            blocked = new(cannotOpen);
        }
        PublishStatus(reading.Commit, useReading: true, reading.At);
        if (reading.Processes is null) return;
        lock (gate) if (!reserveAccepted) return;
        var excluded = blocked.Values.ToList();
        if (reading.Commit is null || reading.Commit.Percent < current.ThresholdPercent)
        {
            Finish(reading.Commit is null ? "commit-unavailable" : "below-threshold");
            return;
        }
        var policies = (await softwarePolicies.GetAsync(stop)).SoftwarePolicies;
        var neverTerminate = policies.Where(item => item.HostMemoryProtectionNeverTerminate).Select(item => item.SoftwareId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noDump = policies.Where(item => item.HostMemoryProtectionNoDump).Select(item => item.SoftwareId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(HostMemoryProcess Process, decimal Growth)>();
        foreach (var process in reading.Processes)
        {
            var reason = CommonExclusion(process);
            var atPoint = before.GetValueOrDefault((process.ProcessId, process.ProcessStartKey));
            reason ??= process.PrivateCommitBytes is null ? "private-commit-unavailable"
                : atPoint is { PrivateCommitBytes: null } ? "baseline-private-commit-unavailable" : null;
            if (reason is not null) { AddExclusion(process, reason); continue; }
            candidates.Add((process, (decimal)process.PrivateCommitBytes!.Value - (atPoint?.PrivateCommitBytes ?? 0)));
        }
        foreach (var candidate in candidates.OrderByDescending(item => item.Growth).ThenBy(item => item.Process.ProcessId))
        {
            if (stop.IsCancellationRequested) return;
            if (candidate.Growth < current.MinimumProcessGrowthBytes) break;
            using var target = executor.Open(candidate.Process);
            if (target.ExclusionReason is { } reason) { AddExclusion(candidate.Process, reason, persist: true); continue; }
            var actionId = Begin();
            var result = target.Execute(reserve, new(current.ActionMode,
                current.ActionMode == HostMemoryProtectionActionModes.DumpThenTerminate && candidate.Process.SoftwareId is { } id && noDump.Contains(id))
                { ProtectedProcesses = current.ProtectedProcesses });
            actionFinishedAt = DateTimeOffset.UtcNow;
            if (result.Outcome == "excluded") AddExclusion(candidate.Process, result.Reason ?? "cannot-open", persist: true);
            else Record(candidate.Process, result, actionId, null,
                new("process-growth-at-least-minimum", candidate.Growth, null, current.MinimumProcessGrowthBytes));
            lock (gate) currentActionId = null;
            if (result.Outcome != "excluded" && !result.SuspendFailed) { Finish("process-handled"); return; }
            PublishStatus();
        }
        var anyPositiveTree = false;
        foreach (var tree in new HostMemoryProtectionTrees(reading.Processes, baseline.Processes).Ranked())
        {
            anyPositiveTree = true;
            if (stop.IsCancellationRequested) return;
            if (!tree.LeavesFirst.Any(process => CommonExclusion(process) is null)) continue;
            var actionId = Begin();
            var acted = false;
            foreach (var process in tree.LeavesFirst)
            {
                HostMemoryActionResult result;
                if (CommonExclusion(process) is { } reason) result = new("excluded", reason, [], 0);
                else
                {
                    using var target = executor.Open(process);
                    result = target.ExclusionReason is { } openReason ? new("excluded", openReason, [], 0)
                        : target.Execute(reserve, new(HostMemoryProtectionActionModes.TerminateOnly) { ProtectedProcesses = current.ProtectedProcesses });
                    if (result.Outcome == "terminated-without-dump") result = result with { Reason = "process-tree" };
                    if (result.Outcome == "excluded") AddExclusion(process, result.Reason ?? "cannot-open", persist: true);
                }
                if (result.Outcome == "excluded") AddExclusion(process, result.Reason ?? "cannot-open");
                else acted = true;
                Record(process, result, actionId, tree.Root,
                    new("largest-direct-children-memory-growth", null, tree.GrowthBytes, current.MinimumProcessGrowthBytes));
            }
            actionFinishedAt = DateTimeOffset.UtcNow;
            lock (gate) currentActionId = null;
            if (acted) { Finish("process-tree-handled"); return; }
            PublishStatus();
        }
        Finish(anyPositiveTree ? "no-eligible-tree" : "no-positive-child-memory-growth");

        string? CommonExclusion(HostMemoryProcess process)
            => blocked.TryGetValue((process.ProcessId, process.ProcessStartKey), out var previous) ? previous.Reason
                : process.IsResourceManager || process.ProcessId == Environment.ProcessId ? "resource-manager-process"
                : process.ProcessId <= 4 ? "critical-process"
                : process.ProcessStartKey == "0" ? "process-identity-unavailable"
                : process.SoftwareId is { } id && neverTerminate.Contains(id) ? "software-never-terminate"
                : HostMemoryProtectionWhitelist.Matches(process, current.ProtectedProcesses) ? "protected-process" : null;
        void AddExclusion(HostMemoryProcess process, string reason, bool persist = false)
        {
            var exclusion = Exclude(process, reason);
            if (!excluded.Contains(exclusion)) excluded.Add(exclusion);
            if (!persist || reason is "protected-process" or "software-never-terminate") return;
            blocked[(process.ProcessId, process.ProcessStartKey)] = exclusion;
            lock (gate) if (ReferenceEquals(point, baseline)) cannotOpen[(process.ProcessId, process.ProcessStartKey)] = exclusion;
        }
        string Begin()
        {
            var id = Guid.NewGuid().ToString("N");
            lock (gate) { currentActionId = id; decisionReason = null; }
            PublishStatus();
            return id;
        }
        void Finish(string reason)
        {
            lock (gate)
            {
                if (ReferenceEquals(point, baseline)) exclusions = excluded.ToArray();
                decisionReason = reason;
            }
            PublishStatus();
        }
        void Record(HostMemoryProcess process, HostMemoryActionResult result, string id, HostMemoryTreeRoot? root, HostMemorySelectionBasis basis)
        {
            var bytes = before.TryGetValue((process.ProcessId, process.ProcessStartKey), out var old) ? old.PrivateCommitBytes : 0;
            var record = new HostMemoryActionRecord(DateTimeOffset.UtcNow, process.ProcessId, process.ProcessStartKey, process.Name,
                bytes, process.PrivateCommitBytes, bytes is { } b && process.PrivateCommitBytes is { } now ? (decimal)now - b : null,
                result.DumpPaths, result.BytesWritten, result.Outcome, result.Reason, basis)
                { ActionId = id, TargetKind = root is null ? "process" : "processTree", TreeRoot = root,
                    Detail = result.Detail };
            lock (gate)
            {
                if (ReferenceEquals(point, baseline)) exclusions = excluded.ToArray();
                actions.Enqueue(record);
                while (actions.Count > RecordLimit) actions.Dequeue();
            }
            logger.LogInformation("Host memory protection action: {@Action}", record);
            PublishStatus();
        }
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
