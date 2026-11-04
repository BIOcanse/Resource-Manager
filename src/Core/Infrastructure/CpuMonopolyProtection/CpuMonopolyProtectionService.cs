using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.CpuMonopolyProtection;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.CpuMonopolyProtection;

public sealed partial class CpuMonopolyProtectionService(ICpuMonopolyProtectionSource source,
    ICpuMonopolyProtectionExecutor executor, BusinessBlockScheduler scheduler, ResourceManagerLedgerOwner ledger,
    IRuntimePlanProvider plans, IHostEnvironment environment, RuntimePersistenceCapabilityPolicy persistence,
    INotificationOwner notifications, ILogger<CpuMonopolyProtectionService> logger) : IBusinessBlock
{
    private readonly StateCell<bool> enabled = scheduler.Authority.ClaimCell<bool>(StateItemUids.CpuMonopolyProtectionEnabled);
    private readonly object gate = new();
    private readonly HashSet<Channel<CpuMonopolyPublication>> subscribers = [];
    private CpuMonopolyProtectionSettings settings = new();
    private CpuMonopolyNative? native;
    private CancellationTokenSource? refresh;
    private bool running;
    private string? currentActionId;
    private string? unavailableReason;
    private IReadOnlyList<CpuMonopolyExclusion> exclusions = [];
    private DateTimeOffset lastAt;

    public static string? ValidateSettings(CpuMonopolyProtectionSettings value)
    {
        if (!double.IsFinite(value.ThresholdPercent) || value.ThresholdPercent is <= 0 or > CpuMonopolyProtectionSettings.MaximumThresholdPercent) return "invalid-cpu-monopoly-threshold";
        if (!double.IsFinite(value.SustainedSeconds) || value.SustainedSeconds is < CpuMonopolyProtectionSettings.MinimumSeconds or > CpuMonopolyProtectionSettings.MaximumSeconds) return "invalid-cpu-monopoly-duration";
        if (!double.IsFinite(value.CheckIntervalSeconds) || value.CheckIntervalSeconds is < CpuMonopolyProtectionSettings.MinimumSeconds or > CpuMonopolyProtectionSettings.MaximumSeconds) return "invalid-cpu-monopoly-interval";
        if (!double.IsFinite(value.MaximumSampleGapSeconds) || value.MaximumSampleGapSeconds < value.CheckIntervalSeconds || value.MaximumSampleGapSeconds > CpuMonopolyProtectionSettings.MaximumSeconds) return "invalid-cpu-monopoly-sample-gap";
        if (value.RecordCapacity is < CpuMonopolyProtectionSettings.MinimumCapacity or > CpuMonopolyProtectionSettings.MaximumRecordCapacity) return "invalid-cpu-monopoly-record-capacity";
        if (value.ProcessCapacity is < CpuMonopolyProtectionSettings.MinimumCapacity or > CpuMonopolyProtectionSettings.MaximumProcessCapacity) return "invalid-cpu-monopoly-process-capacity";
        if (value.SubscriberCapacity is < CpuMonopolyProtectionSettings.MinimumCapacity or > CpuMonopolyProtectionSettings.MaximumSubscriberCapacity) return "invalid-cpu-monopoly-subscriber-capacity";
        return null;
    }

    public async Task ApplySettingsAsync(CpuMonopolyProtectionSettings value)
    {
        if (ValidateSettings(value) is { } error) throw new ArgumentException(error, nameof(value));
        lock (gate)
        {
            RestoreRecordsOnce();
            if (settings != value) refresh?.Cancel();
            settings = value;
            Publish([], false);
        }
        await enabled.PublishAsync(value.Enabled, new BusinessBlockReason(BusinessBlockRequestKind.Configuration,
            BlockUids.CpuMonopolyProtection.ToString(CultureInfo.InvariantCulture), "settings-published", scheduler.CurrentPlanVersion));
    }

    public CpuMonopolyProtectionStatus Read()
    {
        lock (gate) { RestoreRecordsOnce(); return Status(); }
    }
    private CpuMonopolyProtectionStatus Status() => new(settings.Enabled, running && settings.Enabled,
        !settings.Enabled ? "disabled" : !running ? "not-running" : unavailableReason,
        settings.ThresholdPercent, settings.SustainedSeconds, settings.CheckIntervalSeconds, currentActionId,
        records, settings.RecordCapacity, unrecordedRecordCount, exclusions);

    public async IAsyncEnumerable<CpuMonopolyPublication> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<CpuMonopolyPublication> channel;
        lock (gate)
        {
            RestoreRecordsOnce();
            channel = Channel.CreateBounded<CpuMonopolyPublication>(new BoundedChannelOptions(settings.SubscriberCapacity)
                { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            channel.Writer.TryWrite(new(Status(), [], IsReplay: true));
            subscribers.Add(channel);
        }
        try { await foreach (var publication in channel.Reader.ReadAllAsync(cancellationToken)) yield return publication; }
        finally { lock (gate) subscribers.Remove(channel); }
    }
    // Caller holds gate; a slow subscriber disconnects explicitly instead of silently losing an action event.
    private void Publish(IReadOnlyList<CpuMonopolyProtectionEvent> events, bool recordsChanged)
    {
        var publication = new CpuMonopolyPublication(Status(), events, RecordsChanged: recordsChanged);
        foreach (var subscriber in subscribers)
            if (!subscriber.Writer.TryWrite(publication)) subscriber.Writer.TryComplete(new IOException("cpu-monopoly-protection-subscriber-too-slow"));
    }

    public async Task RunAsync(BusinessBlockRunContext context)
    {
        context.ReportRunning();
        executor.UserPrioritySet += UserPrioritySet;
        IComputeScratch? scratch = null;
        ComputeScratchLease? lease = null;
        System.Buffers.MemoryHandle pin = default;
        try
        {
            using var priorityGuard = executor.GuardAutomaticPriorityWrites((pid, key) =>
            {
                lock (gate) return !ulong.TryParse(key, out var number) || native?.CanAutomaticWrite(pid, number) != false;
            });
            while (!context.StopNotification.IsCancellationRequested)
            {
                CpuMonopolyProtectionSettings current;
                lock (gate) { current = settings; RestoreRecordsOnce(); }
                if (!current.Enabled) break;
                if (native is null || current.ProcessCapacity > native.Capacity)
                {
                    var nextScratch = ledger.CreateScratch(CpuMonopolyNative.Bytes(current.ProcessCapacity));
                    ComputeScratchLease? nextLease = null;
                    System.Buffers.MemoryHandle nextPin = default;
                    var adopted = false;
                    try
                    {
                        ComputeScratchNames.Name(nextScratch, "cpu-monopoly-protection", $"processes={current.ProcessCapacity}");
                        nextLease = nextScratch.Acquire();
                        nextLease.GetMemoryChunk(0, checked((int)nextLease.Length)).Span.Clear();
                        nextLease.PublishAndRetain();
                        nextPin = nextLease.GetMemoryChunk(0, checked((int)nextLease.Length)).Pin();
                        lock (gate)
                        {
                            CpuMonopolyNative replacement;
                            unsafe { replacement = new((nint)nextPin.Pointer, current.ProcessCapacity); }
                            if (native is not null) native.CopyTo(replacement);
                            else
                                foreach (var record in records)
                                {
                                    if (!ulong.TryParse(record.ProcessStartKey, out var key)) continue;
                                    if (record.State == "active" && !replacement.Restore(record.ProcessId, key))
                                        throw new InvalidOperationException("cpu-monopoly-active-process-capacity");
                                    if (record.EndReason == "user-priority-set") replacement.MarkExplicit(record.ProcessId, key);
                                }
                            native = replacement;
                        }
                        pin.Dispose(); lease?.Dispose(); scratch?.Dispose();
                        pin = nextPin; lease = nextLease; scratch = nextScratch;
                        adopted = true;
                    }
                    finally
                    {
                        if (!adopted) { nextPin.Dispose(); nextLease?.Dispose(); nextScratch.Dispose(); }
                    }
                }
                // Run-local leases cover live native timing/ownership state, including
                // callback references. Refreshes keep that state; growth rehashes it.
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.StopNotification);
                lock (gate)
                {
                    native.Unavailable();
                    refresh = cancellation;
                    if (!running) { running = true; lastAt = default; Publish([new("active")], false); }
                }
                try
                {
                    await foreach (var reading in source.SubscribeAsync("cpu-monopoly-protection",
                        TimeSpan.FromSeconds(current.CheckIntervalSeconds), cancellation.Token))
                        ProcessReading(reading, current);
                    if (!cancellation.IsCancellationRequested) throw new IOException("cpu-monopoly-process-subscription-ended");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally
                {
                    lock (gate) if (ReferenceEquals(refresh, cancellation)) refresh = null;
                }
            }
        }
        finally
        {
            executor.UserPrioritySet -= UserPrioritySet;
            lock (gate)
            {
                native = null; refresh = null; running = false;
                Publish([new("inactive", settings.Enabled ? "stopped" : "disabled")], false);
            }
            pin.Dispose(); lease?.Dispose(); scratch?.Dispose();
        }
    }

    private void ProcessReading(CpuMonopolyReading reading, CpuMonopolyProtectionSettings current)
    {
        var excluded = new List<CpuMonopolyExclusion>();
        var prepared = new List<(CpuMonopolyProcess Process, string? Reason)>();
        // Live plan projection, same authoritative list and matching code as memory protection.
        var whitelist = plans.Current.MemoryProtection.ProtectedProcesses;
        foreach (var process in reading.Processes ?? [])
        {
            var reason = process.IsResourceManager ? "resource-manager" : HostMemoryProtectionWhitelist.Matches(
                new HostMemoryProcess(process.ProcessId, process.ProcessStartKey, process.Name, null) { ExecutablePath = process.ExecutablePath }, whitelist)
                ? "protected-process" : null;
            if (reason is null && process.CpuPercent >= current.ThresholdPercent && process.PriorityClass is "High" or "RealTime")
                reason = executor.ExclusionReason(process);
            if (reason is not null) excluded.Add(new(process.ProcessId, process.ProcessStartKey, process.Name, reason));
            prepared.Add((process, reason));
        }
        CpuMonopolyNative.Result selected;
        lock (gate)
        {
            if (native is null || !settings.Enabled || settings != current || reading.At <= lastAt) return;
            lastAt = reading.At;
            exclusions = excluded;
            if (reading.Processes is null || reading.Processes.Count > current.ProcessCapacity)
            {
                native.Unavailable();
                unavailableReason = "process-data-unavailable";
                Publish([], false);
                return;
            }
            unavailableReason = reading.Complete ? null : "process-exit-proof-unavailable";
            native.Begin();
            var config = new CpuMonopolyNative.Configuration { Threshold = current.ThresholdPercent,
                DurationMs = checked((long)(current.SustainedSeconds * 1000)), MaximumGapMs = checked((long)(current.MaximumSampleGapSeconds * 1000)) };
            var events = new List<CpuMonopolyProtectionEvent>();
            foreach (var (process, reason) in prepared)
            {
                if (!ulong.TryParse(process.ProcessStartKey, out var key) || process.ProcessId <= 0) continue;
                var result = native.Observe(config, new() { Pid = (uint)process.ProcessId, Key = key,
                    At = reading.At.ToUnixTimeMilliseconds(), Cpu = process.CpuPercent ?? double.NaN,
                    Eligible = reason is null && process.PriorityClass is "High" or "RealTime" ? 1u : 0u });
                if ((result.Flags & 1) != 0) events.Add(new("threshold-reached"));
                if ((result.Flags & 2) != 0) events.Add(new("below-threshold"));
                if ((result.Flags & 16) != 0) unavailableReason = "process-capacity";
            }
            var sweepCount = 0;
            while (reading.Complete && sweepCount++ < native.Capacity)
            {
                var ended = native.Sweep();
                if (ended.Flags == 0) break;
                EndRecord((int)ended.Pid, ended.Key.ToString(CultureInfo.InvariantCulture), reading.At, "process-exited", events);
            }
            // A failed disk commit leaves the published record unchanged. Retry
            // its native-determined transition on a later complete delivery.
            foreach (var record in records.Where(record => record.State == "active").ToArray())
            {
                var reason = native.EndReason(record.ProcessId, ulong.Parse(record.ProcessStartKey, CultureInfo.InvariantCulture));
                if (reason != 0) EndRecord(record.ProcessId, record.ProcessStartKey, reading.At,
                    reason == 1 ? "user-priority-set" : "process-exited", events);
            }
            selected = native.Select();
            Publish(events, events.Any(item => item.Kind == "action-ended"));
        }
        if (selected.Pid == 0) return;
        var target = prepared.First(item => item.Process.ProcessId == selected.Pid
            && item.Process.ProcessStartKey == selected.Key.ToString(CultureInfo.InvariantCulture)).Process;
        string actionId;
        lock (gate)
        {
            actionId = Guid.NewGuid().ToString("N"); currentActionId = actionId;
            Publish([new("action-started")], false);
        }
        // Explicit priority writes and this final eligibility check share the writer's
        // gate; a user's choice between selection and effect cancels the native due row.
        var resultWrite = executor.SetNormal(target, () =>
        {
            lock (gate) return native?.Eligible(target.ProcessId, selected.Key) == true && settings == current && settings.Enabled
                && !HostMemoryProtectionWhitelist.Matches(new HostMemoryProcess(target.ProcessId, target.ProcessStartKey, target.Name, null)
                    { ExecutablePath = target.ExecutablePath }, plans.Current.MemoryProtection.ProtectedProcesses);
        },
            success => { lock (gate) native?.Finish(target.ProcessId, selected.Key, success); });
        lock (gate)
        {
            var endedReason = resultWrite.Reason == "process-exited" ? "process-exited"
                : native?.EndReason(target.ProcessId, selected.Key) == 1 ? "user-priority-set" : null;
            var record = new CpuMonopolyActionRecord(actionId, target.ProcessId, target.ProcessStartKey, target.Name,
                reading.At, selected.Cpu, target.PriorityClass!, current.ThresholdPercent, selected.ElapsedMs / 1000d,
                resultWrite.Succeeded && endedReason is null ? "active" : "history", resultWrite.Succeeded ? "normalized" : "failed",
                resultWrite.Reason, resultWrite.Detail, resultWrite.Succeeded && endedReason is null ? null : reading.At,
                resultWrite.Succeeded ? endedReason : "write-failed");
            AppendRecord(record);
            currentActionId = null;
            Publish([new("action", record.Reason, record)], true);
            notifications.Post(NotificationKinds.CpuMonopolyProtectionAction.Name, NotificationFacts.Of(
                new CpuMonopolyProtectionActionNotificationFacts(actionId, target.Name, target.ProcessId, record.Outcome, record.Reason)));
        }
    }

    private void UserPrioritySet(int pid, string key)
    {
        lock (gate)
        {
            if (native is null || !ulong.TryParse(key, out var number)) return;
            native.MarkExplicit(pid, number);
            var events = new List<CpuMonopolyProtectionEvent>();
            EndRecord(pid, key, DateTimeOffset.UtcNow, "user-priority-set", events);
            Publish(events, events.Count != 0);
        }
    }
}
