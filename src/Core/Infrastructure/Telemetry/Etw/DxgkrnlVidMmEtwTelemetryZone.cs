using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class DxgkrnlVidMmEtwTelemetryZone(
    ILogger<DxgkrnlVidMmEtwTelemetryZone> logger,
    BusinessBlockScheduler controller,
    BusinessBlockDiagnosticsRegistry diagnostics,
    IEtwConsumerThreadObserver? consumerThreadObserver = null) : IBusinessBlock, IResourceManagerSelfComputeZone
{
    public const string ProviderStateId = "dxgkrnl-vidmm-etw";
    internal const string BusinessBlockId = "DxgkrnlVidMmEtwTelemetryZone";
    private const string ProviderName = "Microsoft-Windows-DxgKrnl";
    private const ulong DxgKrnlKeywords = 0x40UL | 0x80UL | 0x10000UL | 0x100000UL | 0x200000UL | 0x20000000UL;
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwDxgkrnlVidMm}");
    private static readonly string SessionName = $"ResourceManagerDxgKrnlVidMm-{Environment.ProcessId}";
    private static readonly string[] ProcessIdPayloadNames = ["ProcessId", "ProcessID", "Pid", "PID", "OwnerProcessId", "OwnerProcessID"];
    private static readonly TimeSpan EvidenceRetention = TimeSpan.FromSeconds(90);
    private ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> evidenceByProcess = new();
    private DxgkrnlVidMmEtwMemoryEvidence[]? retainedEvidence;
    private long stoppedAtUtcTicks;
    private long nextPruneUtcTicks;
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess = WindowsNetworkEndpointProcessLease.Open;
    private readonly Func<string, IVidMmEtwSession> createSession = name => new TraceEventVidMmSession(name, ProviderName, DxgKrnlKeywords);
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwDxgkrnlVidMm;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;
    public string State => controller.ReadState(BusinessBlockId).ToString();
    public string Message => diagnostics.Read(BusinessBlockId).Result?.Outcome == BusinessBlockOutcome.Failed ? "etw-session-failed" : string.Empty;
    internal DxgkrnlVidMmEtwTelemetryZone(ILogger<DxgkrnlVidMmEtwTelemetryZone> logger,
        BusinessBlockScheduler controller, BusinessBlockDiagnosticsRegistry diagnostics, Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess,
        Func<string, IVidMmEtwSession>? createSession = null, IEtwConsumerThreadObserver? consumerThreadObserver = null)
        : this(logger, controller, diagnostics, consumerThreadObserver)
    {
        this.openProcess = openProcess;
        if (createSession is not null) this.createSession = createSession;
    }

    public IBusinessBlockDependency AcquireSubscription(string owner, TimeSpan interval)
        => new Subscription(this, controller.AcquireDependency(BusinessBlockId,
            new(BusinessBlockRequestKind.Dependency, owner, "gpu-vram-residual-subscription", controller.CurrentPlanVersion)));

    public IReadOnlyList<DxgkrnlVidMmEtwMemoryEvidence> ReadEvidence(TimeSpan evidenceWindow)
    {
        var stoppedAt = Volatile.Read(ref stoppedAtUtcTicks);
        return ReadCurrentEvidence(stoppedAt == 0 ? DateTimeOffset.UtcNow : new(stoppedAt, TimeSpan.Zero), evidenceWindow);
    }

    internal IReadOnlyList<DxgkrnlVidMmEtwMemoryEvidence> ReadCurrentEvidence(DateTimeOffset now, TimeSpan evidenceWindow)
    {
        IEnumerable<DxgkrnlVidMmEtwMemoryEvidence> current = Volatile.Read(ref retainedEvidence)
            ?? (IEnumerable<DxgkrnlVidMmEtwMemoryEvidence>)Volatile.Read(ref evidenceByProcess).Values;
        return current.Where(evidence => evidence.ProcessId > 4 && evidence.ValueBytes > 0
            && now - evidence.ObservedAt <= evidenceWindow).ToArray();
    }

    // Residual production validates this captured identity at its due time. A raw
    // current-value read does not query process metadata or activate any producer.
    internal bool MatchesCurrentProcess(DxgkrnlVidMmEtwMemoryEvidence evidence)
        => evidence.ProcessStartKey > 0 && ReadProcessBirth(evidence.ProcessId) == evidence.ProcessStartKey;

    public void ApplyMode(ResourceManagerComputeZoneMode mode) => _ = ApplyModeAsync(mode);
    internal Task ApplyModeAsync(ResourceManagerComputeZoneMode mode)
    {
        currentComputeMode = mode;
        var dispatch = controller.SetEnabledAsync(BusinessBlockId, mode != ResourceManagerComputeZoneMode.Freeze,
            new(BusinessBlockRequestKind.Configuration, "compute-zone", MonitoringSourceZoneIds.EtwDxgkrnlVidMm, controller.CurrentPlanVersion));
        return mode == ResourceManagerComputeZoneMode.Freeze ? AwaitReleasedAsync(dispatch) : dispatch;
    }
    private void OnDynamicEvent(TraceEvent data, ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> currentEvidence)
    {
        try
        {
            if (!string.Equals(data.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase)
                || !IsProcessMemoryEvent(data.EventName)
                || !TryReadProcessId(data, out var processId)
                || processId <= 0
                || !TryReadByteValue(data, out var valueBytes))
            {
                return;
            }

            ObserveProcessMemory(currentEvidence, processId, data.ProcessName, valueBytes,
                data.EventName, EtwNetworkTcpIpCaptureOwner.ToEventFileTime(data.TimeStamp), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            SignalFailure(ex);
        }
    }

    internal void ObserveProcessMemory(int processId, string? processName, double valueBytes,
        string eventName, ulong? eventTime, DateTimeOffset observedAt)
        => ObserveProcessMemory(Volatile.Read(ref evidenceByProcess), processId, processName,
            valueBytes, eventName, eventTime, observedAt);

    private void ObserveProcessMemory(ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> currentEvidence,
        int processId, string? processName, double valueBytes, string eventName, ulong? eventTime, DateTimeOffset observedAt)
    {
        var birth = ReadProcessBirth(processId);
        if (birth is null || eventTime is null || eventTime.Value < birth.Value) return;
        // Retention is maintained only when producing evidence, never by an idle
        // checker or a current-value read. No events means no new retained entries.
        if (observedAt.UtcTicks >= Volatile.Read(ref nextPruneUtcTicks))
        {
            foreach (var item in currentEvidence)
                if (observedAt - item.Value.ObservedAt > EvidenceRetention)
                    currentEvidence.TryRemove(item.Key, out _);
            Volatile.Write(ref nextPruneUtcTicks, (observedAt + EvidenceRetention / 2).UtcTicks);
        }
        currentEvidence[processId] = new(processId, NormalizeProcessName(processName, processId),
            valueBytes, eventName, observedAt) { ProcessStartKey = birth.Value };
    }

    private static string NormalizeProcessName(string? processName, int processId)
    {
        return !string.IsNullOrWhiteSpace(processName)
            ? Path.GetFileNameWithoutExtension(processName.Trim())
            : $"PID {processId}";
    }

    private ulong? ReadProcessBirth(int processId)
    {
        try
        {
            using var process = openProcess(checked((uint)processId));
            return process is not null && process.ProcessId == (uint)processId
                && process.TryReadCreationFileTime(out var birth) && birth > 0 ? birth : null;
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Could not verify DXGKrnl process identity for {ProcessId}.", processId);
            return null;
        }
    }

    private static bool IsProcessMemoryEvent(string eventName)
    {
        return eventName.Equals("VidMmProcessUsageChange", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("VidMmProcessCommitmentChange", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("VidMmProcessDemotedCommitmentChange", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("ReportCommittedAllocation", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("ProcessAllocation", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("ProcessAllocationDetails", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadProcessId(TraceEvent data, out int processId)
    {
        foreach (var name in ProcessIdPayloadNames)
        {
            if (TryReadIntPayload(data, name, out processId) && processId > 0)
            {
                return true;
            }
        }

        processId = data.ProcessID;
        return processId > 0;
    }

    private static bool TryReadByteValue(TraceEvent data, out double value)
    {
        var candidates = data.PayloadNames
            .Where(IsLikelyBytePayloadName)
            .OrderByDescending(BytePayloadNameScore)
            .ToArray();
        foreach (var name in candidates)
        {
            if (TryReadDoublePayload(data, name, out value) && IsPlausibleByteValue(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool IsLikelyBytePayloadName(string name)
    {
        return name.Contains("Usage", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Commit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Resident", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bytes", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Size", StringComparison.OrdinalIgnoreCase);
    }

    private static int BytePayloadNameScore(string name)
    {
        var score = 0;
        if (name.Contains("Current", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (name.Contains("Usage", StringComparison.OrdinalIgnoreCase)) score += 18;
        if (name.Contains("Commitment", StringComparison.OrdinalIgnoreCase)) score += 16;
        if (name.Contains("Committed", StringComparison.OrdinalIgnoreCase)) score += 14;
        if (name.Contains("Resident", StringComparison.OrdinalIgnoreCase)) score += 12;
        if (name.Contains("Bytes", StringComparison.OrdinalIgnoreCase)) score += 8;
        if (name.Contains("Size", StringComparison.OrdinalIgnoreCase)) score += 4;
        return score;
    }

    private static bool IsPlausibleByteValue(double value)
    {
        return !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value >= 4096
            && value <= 512d * 1024 * 1024 * 1024;
    }

    private static bool TryReadIntPayload(TraceEvent data, string name, out int value)
    {
        value = 0;
        try
        {
            var payload = data.PayloadByName(name);
            if (payload is null)
            {
                return false;
            }

            value = Convert.ToInt32(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadDoublePayload(TraceEvent data, string name, out double value)
    {
        value = 0;
        try
        {
            var payload = data.PayloadByName(name);
            if (payload is null)
            {
                return false;
            }

            value = Convert.ToDouble(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record DxgkrnlVidMmEtwMemoryEvidence(
    int ProcessId,
    string ProcessName,
    double ValueBytes,
    string EventName,
    DateTimeOffset ObservedAt)
{
    public ulong ProcessStartKey { get; init; }
}
