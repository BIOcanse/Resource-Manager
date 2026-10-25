using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

internal sealed class GpuAllocationTestSessionFactory : IGpuAllocationSessionFactory
{
    internal List<GpuAllocationTestSession> Sessions { get; } = [];
    internal int Attempts;
    internal int Failures;
    internal bool PartialCleanupFailure;
    internal Action<GpuAllocationTestSession>? Configure;
    internal GpuAllocationTestSession Current => Sessions[^1];
    public IGpuAllocationSession Create(string name)
    {
        Attempts++;
        if (PartialCleanupFailure) throw new KernelEtwSessionCreationCleanupException(new IOException("create"), new IOException("cleanup"));
        if (Failures-- > 0) throw new IOException("fake startup failure");
        var result = new GpuAllocationTestSession(name);
        Configure?.Invoke(result);
        Sessions.Add(result);
        return result;
    }
    internal GpuAllocationCollector Collector(TimeSpan? budget = null, ILogger<GpuAllocationCollector>? logger = null,
        IConfiguration? configuration = null, TimeProvider? timeProvider = null)
        => new(logger ?? NullLogger<GpuAllocationCollector>.Instance, configuration ?? new ConfigurationBuilder().Build(),
            this, timeProvider ?? TimeProvider.System, budget);
}

internal sealed class GpuAllocationTestSession(string name) : IGpuAllocationSession
{
    internal static readonly IReadOnlyDictionary<int, long> Processes = new Dictionary<int, long> { [41] = 100 };
    private Action<Guid, int, IGpuAllocationPayload, DateTime>? handler;
    private Action<Exception?>? completed;
    private (string Name, long Scan)? end;
    internal readonly HashSet<Guid> Enabled = [];
    internal int Captures;
    internal int Stops;
    internal int Pid = 41;
    internal ulong Bytes = 1024;
    internal bool HoldEnd;
    internal bool FailCapture;
    internal bool LoseEvents;
    internal bool Malformed;
    internal bool Incomplete;
    internal bool FailDisable;
    internal Action? DuringCapture;
    internal KernelEtwSessionStopResult StopResult = KernelEtwSessionStopResult.Released;
    public bool IsProcessing { get; private set; }
    public int EventsLost { get; private set; }
    public void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> callback) => handler = callback;
    public void StartProcessing(Action<Exception?> callback) { completed = callback; IsProcessing = true; }
    public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null) => Enabled.Add(provider);
    public void DisableProvider(Guid provider)
    {
        if (FailDisable) throw new IOException("fake disable failure");
        Enabled.Remove(provider);
    }
    public void BeginScan(string sessionName, long scan, long eventsLost)
        => Boundary(2, sessionName, scan, eventsLost);
    public void EndScan(string sessionName, long scan) => end = (sessionName, scan);
    public void Flush() { if (!HoldEnd) DeliverEnd(); }
    internal void DeliverEnd()
    {
        if (end is not { } boundary) return;
        end = null;
        Boundary(3, boundary.Name, boundary.Scan, 0);
    }
    private void Boundary(int id, string sessionName, long scan, long loss)
    {
        var payload = new Dictionary<string, object> { ["session"] = sessionName, ["scan"] = scan, ["eventsLost"] = loss };
        handler!(EventSource.GetGuid(typeof(GpuAllocationBoundary)), id, new GpuAllocationNamedPayload(key => payload[key]), DateTime.UtcNow);
    }
    public void CaptureState(Guid provider, ulong keywords)
    {
        Assert.Equal(GpuAllocationCollector.Provider, provider);
        Assert.Equal(0x41UL, keywords);
        Assert.Contains(provider, Enabled);
        Captures++;
        if (FailCapture) throw new IOException("fake rundown failure");
        Emit(110, ("pDxgAdapter", 1UL), ("AdapterLuid", 23UL));
        Emit(78, ("pDxgAdapter", 1UL), ("ulSegmentId", 2U), ("Flags", 0U));
        Emit(35, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL), ("allocSize", Bytes), ("PageTableOrDirectory", false));
        if (!Incomplete) Emit(36, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL), ("hVidMmAlloc", 11UL), ("hProcessId", (uint)Pid));
        Emit(80, ("hAllocationHandle", 11UL), ("hProcessId", (uint)Pid), ("ulSegmentId", 2U), ("Size", Bytes), ("SegmentOffset", 0UL));
        if (Malformed) handler!(provider, 35, new GpuAllocationNamedPayload(_ => throw new InvalidDataException("fake malformed event")), DateTime.UtcNow);
        DuringCapture?.Invoke();
        if (LoseEvents) EventsLost++;
    }
    internal void Emit(int id, params (string Key, object Value)[] fields)
    {
        var payload = fields.ToDictionary(item => item.Key, item => item.Value);
        handler!(GpuAllocationCollector.Provider, id, new GpuAllocationNamedPayload(key => payload[key]), DateTime.UtcNow);
    }
    internal void LoseEvent() => EventsLost++;
    internal void End(Exception? error = null) { IsProcessing = false; completed?.Invoke(error); }
    public KernelEtwSessionStopResult Stop(TimeSpan timeout)
    {
        Stops++;
        IsProcessing = false;
        if (StopResult.IsReleased) Enabled.Clear();
        return StopResult;
    }
}
