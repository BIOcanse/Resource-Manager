namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Development acceptance can observe one exact owned session. No subscriber,
// timestamps, event objects or retained delivery history exist during normal use.
internal static class GpuAllocationObservation
{
    internal readonly record struct Command(string Operation, Guid Provider, DateTimeOffset At,
        bool Completed, bool? Succeeded);
    internal readonly record struct Delivery(Guid Provider, int Id, DateTime EventTimestampUtc,
        DateTimeOffset ReceivedAt, bool Accepted);
    internal interface ISink
    {
        void OnCommand(Command value);
        void OnDelivery(Delivery value);
    }
    private sealed class Registration(string session, ISink sink) : IDisposable
    {
        internal string Session { get; } = session;
        internal ISink Sink { get; } = sink;
        public void Dispose() => Interlocked.CompareExchange(ref active, null, this);
    }
    private static Registration? active;
    internal static IDisposable Subscribe(string session, ISink sink)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        ArgumentNullException.ThrowIfNull(sink);
        var registration = new Registration(session, sink);
        if (Interlocked.CompareExchange(ref active, registration, null) is not null)
            throw new InvalidOperationException("A GPU allocation acceptance observer already exists.");
        return registration;
    }
    internal static void RecordCommand(string? session, string operation, Guid provider, bool completed, bool? succeeded)
    {
        var current = Volatile.Read(ref active);
        if (current is null || !string.Equals(current.Session, session, StringComparison.Ordinal)) return;
        try { current.Sink.OnCommand(new(operation, provider, DateTimeOffset.UtcNow, completed, succeeded)); }
        catch { /* Acceptance diagnostics cannot change native provider ownership. */ }
    }
    internal static void RecordDelivery(string session, Guid provider, int id, DateTime eventTimestampUtc, bool accepted)
    {
        var current = Volatile.Read(ref active);
        if (current is null || !string.Equals(current.Session, session, StringComparison.Ordinal)) return;
        try { current.Sink.OnDelivery(new(provider, id, eventTimestampUtc, DateTimeOffset.UtcNow, accepted)); }
        catch { /* Acceptance diagnostics cannot change collector publication. */ }
    }
}
