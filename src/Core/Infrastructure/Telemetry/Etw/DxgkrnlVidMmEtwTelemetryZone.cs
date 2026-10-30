using BlockUids = ResourceManager.App.StateItems.BlockUids;
using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class DxgkrnlVidMmEtwTelemetryZone(
    ILogger<DxgkrnlVidMmEtwTelemetryZone> logger,
    BusinessBlockScheduler controller,
    BusinessBlockDiagnosticsRegistry diagnostics,
    NativeEtwSessionAvailability availability,
    IEtwConsumerThreadObserver? consumerThreadObserver = null) : IBusinessBlock
{
    public const string ProviderStateId = "dxgkrnl-vidmm-etw";
    private const ulong DxgKrnlKeywords = 0x40UL | 0x80UL | 0x10000UL | 0x100000UL | 0x200000UL | 0x20000000UL;
    private static readonly string SessionName = ResourceManagerEtwSessionNames.Create(
        ResourceManagerEtwSessionNames.DxgkrnlVidMmPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
    private static readonly TimeSpan EvidenceRetention = TimeSpan.FromSeconds(90);
    private ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> evidenceByProcess = new();
    private DxgkrnlVidMmEtwMemoryEvidence[]? retainedEvidence;
    private long stoppedAtUtcTicks;
    private long nextPruneUtcTicks;
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess = WindowsNetworkEndpointProcessLease.Open;
    private readonly Func<string, IVidMmEtwSession> createSession = name => new TraceEventVidMmSession(name, DxgkrnlVidMmEtwLayouts.Provider, DxgKrnlKeywords, logger);

    public string State => controller.ReadState(BlockUids.DxgkrnlVidMmEtwTelemetryZone).ToString();
    public string Message => diagnostics.Read(BlockUids.DxgkrnlVidMmEtwTelemetryZone).Result?.Outcome == BusinessBlockOutcome.Failed ? "etw-session-failed" : string.Empty;
    internal DxgkrnlVidMmEtwTelemetryZone(ILogger<DxgkrnlVidMmEtwTelemetryZone> logger,
        BusinessBlockScheduler controller, BusinessBlockDiagnosticsRegistry diagnostics, NativeEtwSessionAvailability availability, Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess,
        Func<string, IVidMmEtwSession>? createSession = null, IEtwConsumerThreadObserver? consumerThreadObserver = null)
        : this(logger, controller, diagnostics, availability, consumerThreadObserver)
    {
        this.openProcess = openProcess;
        if (createSession is not null) this.createSession = createSession;
    }

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

    private bool OnFixedLayoutEvent(TraceEvent data, ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> currentEvidence)
    {
        try
        {
            return TryReadProcessMemory(data, out var processId, out var valueBytes, out var eventName)
                && ObserveProcessMemory(currentEvidence, processId, data.ProcessName, valueBytes,
                    eventName, EtwNetworkTcpIpCaptureOwner.ToEventFileTime(data.TimeStamp), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            SignalFailure(ex);
            return false;
        }
    }

    // Reads a process-memory event at its fixed layout (DxgkrnlVidMmEtwLayouts): the payload
    // ProcessId when positive, else the header process; then the first byte-value field in a
    // plausible range. Other events, and events that fail their layout, read nothing.
    internal static bool TryReadProcessMemory(TraceEvent data, out int processId, out double valueBytes, out string eventName)
    {
        processId = 0;
        valueBytes = 0;
        eventName = string.Empty;
        if (data.ProviderGuid != DxgkrnlVidMmEtwLayouts.Provider
            || !DxgkrnlVidMmEtwLayouts.Events.TryGetValue((int)data.ID, out var memoryEvent)
            || DxgkrnlVidMmEtwLayouts.Catalog.Find(data, out var payload) != EtwFixedLayoutStatus.Decoded)
        {
            return false;
        }

        processId = payload.Has((int)VidMmField.ProcessId) && payload.Int32((int)VidMmField.ProcessId) is > 0 and var payloadProcessId
            ? payloadProcessId
            : data.ProcessID;
        eventName = memoryEvent.EventName;
        return processId > 0 && TryReadByteValue(payload, memoryEvent.ByteValues, out valueBytes);
    }

    internal void ObserveProcessMemory(int processId, string? processName, double valueBytes,
        string eventName, ulong? eventTime, DateTimeOffset observedAt)
        => ObserveProcessMemory(Volatile.Read(ref evidenceByProcess), processId, processName,
            valueBytes, eventName, eventTime, observedAt);

    private bool ObserveProcessMemory(ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> currentEvidence,
        int processId, string? processName, double valueBytes, string eventName, ulong? eventTime, DateTimeOffset observedAt)
    {
        var birth = ReadProcessBirth(processId);
        if (birth is null || eventTime is null || eventTime.Value < birth.Value) return false;
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
        return true;
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

    private static bool TryReadByteValue(EtwFixedPayload payload, VidMmField[] candidates, out double value)
    {
        foreach (var candidate in candidates)
        {
            value = payload.Int64((int)candidate);
            if (IsPlausibleByteValue(value)) return true;
        }

        value = 0;
        return false;
    }

    private static bool IsPlausibleByteValue(double value)
    {
        return !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value >= 4096
            && value <= 512d * 1024 * 1024 * 1024;
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
