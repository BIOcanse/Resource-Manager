using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>Explicit managed-oracle fixture. It never borrows a native session or implements a production broker.</summary>
internal sealed class OracleKernelEtwSessionBroker(Func<DateTimeOffset>? utcNow = null)
    : IOracleKernelEtwSessionBroker, IDisposable
{
    private readonly Dictionary<long, OracleKernelEtwSubscriptionRegistration> holders = [];
    private OracleKernelEtwSubscriptionDispatcher? dispatcher;
    private KernelEtwSessionContext context = new(0, DateTimeOffset.MinValue);
    private long sequence, generation;
    private bool disposed;
    internal int EventsLost { get; set; }
    internal int Creations { get; private set; }
    internal int Stops { get; private set; }
    internal List<KernelTraceEventParser.Keywords> KeywordUpdates { get; } = [];
    internal OracleKernelEtwSubscriptionDispatcher Dispatcher => dispatcher ?? throw new InvalidOperationException("Acquire an oracle hold first.");

    public IOracleKernelEtwSubscription Acquire(OracleKernelEtwSubscriptionRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentNullException.ThrowIfNull(request.Bind);
        var id = checked(++sequence);
        holders.Add(id, new(id, request.Id, request.Keywords, request.Bind));
        try { Refresh(); }
        catch { holders.Remove(id); throw; }
        return new Lease(this, id);
    }

    public KernelEtwSessionSnapshot GetSnapshot() => new(disposed ? "Stopped" : holders.Count == 0 ? "Idle" : "Running",
        "managed oracle fixture", context.Generation, context.StartedAt,
        holders.Values.Aggregate(KernelTraceEventParser.Keywords.None, static (value, holder) => value | holder.Keywords),
        holders.Count, EventsLost);

    private void Refresh()
    {
        if (holders.Count == 0)
        {
            if (dispatcher is not null) { dispatcher.CancelAll(); dispatcher = null; Stops++; }
            return;
        }
        if (dispatcher is null)
        {
            context = new(checked(++generation), utcNow?.Invoke() ?? DateTimeOffset.UnixEpoch);
            dispatcher = new();
            dispatcher.InitializeSubscriptions(holders.Values.ToArray(), context);
            Creations++;
        }
        else dispatcher.UpdateSubscriptions(holders.Values.ToArray(), context, KeywordUpdates.Add);
    }
    internal void Rebind()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (dispatcher is not null) { dispatcher.CancelAll(); dispatcher = null; Stops++; }
        Refresh();
    }
    internal void Dispatch<T>(T data, Action<OracleKernelEtwEventSource, T> deliver) => Dispatcher.Dispatch(data, deliver);
    private void Release(long id) { if (holders.Remove(id) && !disposed) Refresh(); }
    public void Dispose()
    {
        if (disposed) return;
        holders.Clear(); Refresh(); disposed = true;
    }
    private sealed class Lease(OracleKernelEtwSessionBroker owner, long id) : IOracleKernelEtwSubscription
    {
        private OracleKernelEtwSessionBroker? owner = owner;
        public KernelEtwSessionSnapshot GetSnapshot() => owner?.GetSnapshot()
            ?? new("Stopped", "oracle hold returned", 0, DateTimeOffset.MinValue, KernelTraceEventParser.Keywords.None, 0, 0);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(id);
    }
}
