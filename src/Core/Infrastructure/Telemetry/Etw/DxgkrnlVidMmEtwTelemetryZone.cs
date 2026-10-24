using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class DxgkrnlVidMmEtwTelemetryZone(
    ILogger<DxgkrnlVidMmEtwTelemetryZone> logger) : BackgroundService, IResourceManagerSelfComputeZone
{
    public const string ProviderStateId = "dxgkrnl-vidmm-etw";

    private const string ProviderName = "Microsoft-Windows-DxgKrnl";
    private const ulong DxgKrnlKeywords =
        0x0000000000000040UL
        | 0x0000000000000080UL
        | 0x0000000000010000UL
        | 0x0000000000100000UL
        | 0x0000000000200000UL
        | 0x0000000020000000UL;

    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwDxgkrnlVidMm}");
    private static readonly TimeSpan ProcessStopWait = TimeSpan.FromSeconds(2);
    private static readonly string SessionName = $"ResourceManagerDxgKrnlVidMm-{Environment.ProcessId}";
    private static readonly string[] ProcessIdPayloadNames =
    [
        "ProcessId",
        "ProcessID",
        "Pid",
        "PID",
        "OwnerProcessId",
        "OwnerProcessID"
    ];

    private ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence> evidenceByProcess = new();
    private readonly object sessionGate = new();
    private TraceEventSession? session;
    private Task? processingTask;
    private bool disposed;
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess = WindowsNetworkEndpointProcessLease.Open;
    private DateTimeOffset lastRequestAt = DateTimeOffset.MinValue;
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = "Idle";
    private volatile string message = "DXGKrnl/VidMm ETW 显存残差归因按需启动。";

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwDxgkrnlVidMm;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;
    public string State => state;
    public string Message => message;

    internal DxgkrnlVidMmEtwTelemetryZone(ILogger<DxgkrnlVidMmEtwTelemetryZone> logger,
        Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess) : this(logger)
        => this.openProcess = openProcess;

    public IReadOnlyList<DxgkrnlVidMmEtwMemoryEvidence> ReadEvidence(TimeSpan evidenceWindow)
    {
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
        {
            state = "Idle";
            message = "DXGKrnl/VidMm ETW 显存残差归因当前处于功能区冻结。";
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        lastRequestAt = now;
        EnsureStarted();
        if (state != "Running") return [];
        return ReadCurrentEvidence(now, evidenceWindow);
    }

    internal IReadOnlyList<DxgkrnlVidMmEtwMemoryEvidence> ReadCurrentEvidence(DateTimeOffset now, TimeSpan evidenceWindow)
    {
        PruneStaleEvidence(now, evidenceWindow + evidenceWindow);
        return evidenceByProcess.Values
            .Where(evidence => evidence.ValueBytes > 0 && now - evidence.ObservedAt <= evidenceWindow
                && ReadProcessBirth(evidence.ProcessId) == evidence.ProcessStartKey)
            .ToArray();
    }

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        currentComputeMode = mode;
        if (mode == ResourceManagerComputeZoneMode.Freeze)
        {
            StopSession("DXGKrnl/VidMm ETW 显存残差归因已按功能区冻结停止。");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken);
                if (IsRunning && DateTimeOffset.UtcNow - lastRequestAt > CurrentIdleStopAfter())
                {
                    StopSession("DXGKrnl/VidMm ETW 显存残差归因已因无请求而停止。");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopSession("DXGKrnl/VidMm ETW 显存残差归因正在随程序停止。");
    }

    private TimeSpan CurrentIdleStopAfter()
    {
        return currentComputeMode switch
        {
            ResourceManagerComputeZoneMode.LowPower => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(5)
        };
    }

    private bool IsRunning
    {
        get
        {
            lock (sessionGate)
            {
                return session is not null;
            }
        }
    }

    private void EnsureStarted()
    {
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
        {
            state = "Idle";
            message = "DXGKrnl/VidMm ETW 显存残差归因当前处于功能区冻结。";
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            state = "Unavailable";
            message = "当前系统不是 Windows，DXGKrnl/VidMm ETW 不可用。";
            return;
        }

        if (TraceEventSession.IsElevated() != true)
        {
            state = "Unavailable";
            message = "DXGKrnl/VidMm ETW 需要管理员权限或性能日志权限，当前进程未提权。";
            return;
        }

        lock (sessionGate)
        {
            if (disposed) return;
            if (session is not null && processingTask is { IsCompleted: true })
                StopSession("DXGKrnl/VidMm ETW 事件流已结束。");
            if (session is not null)
            {
                return;
            }

            try
            {
                session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create)
                {
                    StopOnDispose = true
                };
                session.EnableProvider(ProviderName, TraceEventLevel.Verbose, DxgKrnlKeywords);
                var currentEvidence = evidenceByProcess;
                session.Source.Dynamic.All += data => OnDynamicEvent(data, currentEvidence);
                state = "Running";
                message = "DXGKrnl/VidMm ETW 正在采集 GPU 内存归因事件。";
                var current = session;
                processingTask = Task.Run(() => ProcessTraceSource(current), CancellationToken.None);
            }
            catch (Exception ex)
            {
                StopSession("DXGKrnl/VidMm ETW 显存残差归因启动失败。");
                state = "Unavailable";
                message = $"DXGKrnl/VidMm ETW 启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start DXGKrnl/VidMm ETW telemetry zone.");
            }
        }
    }

    private void ProcessTraceSource(TraceEventSession current)
    {
        try
        {
            current.Source.Process();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "DXGKrnl/VidMm source processing stopped.");
        }
        finally
        {
            lock (sessionGate)
            {
                if (ReferenceEquals(session, current))
                {
                    evidenceByProcess.Clear();
                    state = "Unavailable";
                    message = "DXGKrnl/VidMm ETW 事件流已结束，等待下一次需求重新启动。";
                }
            }
        }
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
            logger.LogDebug(ex, "Ignored malformed DXGKrnl/VidMm event.");
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
        currentEvidence[processId] = new(processId, NormalizeProcessName(processName, processId),
            valueBytes, eventName, observedAt) { ProcessStartKey = birth.Value };
    }

    private void PruneStaleEvidence(DateTimeOffset now, TimeSpan retention)
    {
        foreach (var item in evidenceByProcess)
        {
            if (now - item.Value.ObservedAt > retention || item.Value.ProcessId <= 4)
            {
                evidenceByProcess.TryRemove(item.Key, out _);
            }
        }
    }

    private void StopSession(string idleMessage)
    {
        TraceEventSession? sessionToStop;
        Task? taskToWait;
        lock (sessionGate)
        {
            sessionToStop = session;
            taskToWait = processingTask;
            session = null;
            processingTask = null;
            Interlocked.Exchange(ref evidenceByProcess, new());
        }

        if (sessionToStop is null)
        {
            if (!state.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
            {
                state = "Idle";
                message = idleMessage;
            }

            return;
        }

        try
        {
            sessionToStop.Source.StopProcessing();
        }
        catch
        {
        }

        try
        {
            sessionToStop.Stop(noThrow: true);
        }
        catch
        {
        }

        try
        {
            sessionToStop.Dispose();
        }
        catch
        {
        }

        try
        {
            taskToWait?.Wait(ProcessStopWait);
        }
        catch
        {
        }

        state = "Idle";
        message = idleMessage;
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

    public override void Dispose()
    {
        lock (sessionGate)
        {
            disposed = true;
        }
        StopSession("DXGKrnl/VidMm ETW 显存残差归因正在随程序停止。");
        base.Dispose();
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
