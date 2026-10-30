using System.Collections.Concurrent;
using ResourceManager.ReadWrite;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed record EtwEventKindDrops(string Kind, long Received, long Dropped);
public sealed record EtwEnabledProvider(string Provider, ulong Keywords);
public sealed record EtwSessionEventDrops(string Name, long Generation, DateTimeOffset StartedAt,
    IReadOnlyList<EtwEnabledProvider> EnabledProviders, long EventsLost, IReadOnlyList<EtwEventKindDrops> Kinds,
    string? EventsLostError);
public sealed record EtwEventDropsSnapshot(IReadOnlyList<EtwSessionEventDrops> Sessions)
{
    public ReadVolatility Volatility => ReadVolatility.Sampled;
}

// Exactly one processing thread writes a counter. Reading never resets it.
public sealed class EtwEventDropCounter(string kind, Func<EtwEventKindDrops?>? readNative = null,
    bool supplementReceived = false)
{
    private readonly long[] counts = new long[2];
    public void Receive() => counts[0]++;
    public void Drop() => counts[1]++;
    internal EtwEventKindDrops Read()
    {
        var dropped = Volatile.Read(ref counts[1]);
        if (readNative?.Invoke() is { } native)
            return new(kind, native.Received + (supplementReceived ? Volatile.Read(ref counts[0]) : 0),
                native.Dropped + dropped);
        return new(kind, Volatile.Read(ref counts[0]), dropped);
    }
}

public sealed class EtwEventDropSession
{
    private readonly object gate = new();
    private readonly Dictionary<string, ulong> providers = [];
    private readonly ConcurrentDictionary<(Guid Provider, int Id, int Opcode), EtwEventDropCounter> dynamicKinds = new();
    private readonly ConcurrentDictionary<string, EtwEventDropCounter> namedKinds = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<EtwEventDropCounter> fixedKinds;
    private Func<int>? readEventsLost;
    private long eventsLost;
    private string? eventsLostError;
    internal string Name { get; }
    internal long Generation { get; }
    private readonly DateTimeOffset startedAt;

    internal EtwEventDropSession(string name, long generation, DateTimeOffset startedAt,
        Func<int> readEventsLost, IReadOnlyList<EtwEventDropCounter> fixedKinds)
    {
        Name = name;
        Generation = generation;
        this.startedAt = startedAt;
        this.readEventsLost = readEventsLost;
        this.fixedKinds = fixedKinds;
    }

    // Metadata is created once per newly observed kind, never per delivery.
    public EtwEventDropCounter Kind(Guid provider, int id, int opcode)
    {
        var key = (provider, id, opcode);
        if (dynamicKinds.TryGetValue(key, out var counter)) return counter;
        return dynamicKinds.GetOrAdd(key, static value => new($"{value.Provider:D}/{value.Id}/{value.Opcode}"));
    }

    // Consumer counters are separate from physical delivery totals. Stable holder/event
    // names keep repeated acquisitions from growing diagnostic history without bound.
    public EtwEventDropCounter NamedKind(string kind)
        => namedKinds.GetOrAdd(kind, static name => new(name));

    internal void RegisterCounter(string kind, EtwEventDropCounter counter)
        => namedKinds.TryAdd(kind, counter);

    public void EnableProvider(string provider, ulong keywords)
    {
        lock (gate) providers[provider] = keywords;
    }
    public void DisableProvider(string provider)
    {
        lock (gate) providers.Remove(provider);
    }
    // Complete only detaches the loss reader. A failed native release still
    // owns its last enabled providers; clear them only after confirmed release.
    public void ConfirmNativeRelease()
    {
        lock (gate) providers.Clear();
    }
    // Detach before disposal so reads cannot touch a disposed controller.
    public void Complete(bool refreshLoss = true)
    {
        lock (gate)
        {
            if (refreshLoss) RefreshLoss();
            readEventsLost = null;
        }
    }
    public void SampleLoss()
    {
        lock (gate) RefreshLoss();
    }
    private void RefreshLoss()
    {
        if (readEventsLost is null) return;
        try
        {
            eventsLost = readEventsLost();
            eventsLostError = null;
        }
        catch (Exception error)
        {
            // Diagnostics must not make a failed query prevent native cleanup.
            eventsLostError = $"{error.GetType().Name}: {error.Message}";
        }
    }
    internal EtwSessionEventDrops Read()
    {
        lock (gate)
        {
            RefreshLoss();
            return new(Name, Generation, startedAt,
                providers.Select(pair => new EtwEnabledProvider(pair.Key, pair.Value)).ToArray(), eventsLost,
                fixedKinds.Concat(dynamicKinds.Values).Concat(namedKinds.Values).Select(counter => counter.Read()).ToArray(), eventsLostError);
        }
    }
}

public static class EtwEventDropDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, EtwEventDropSession> Sessions = [];

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public static EtwEventDropSession Start(string name, Func<int> eventsLost,
        IReadOnlyList<EtwEventDropCounter>? kinds = null, long? generation = null, DateTimeOffset? startedAt = null)
    {
        lock (Gate)
        {
            var next = generation ?? (Sessions.TryGetValue(name, out var previous) ? previous.Generation + 1 : 1);
            var run = new EtwEventDropSession(name, next, startedAt ?? DateTimeOffset.UtcNow, eventsLost, kinds ?? []);
            Sessions[name] = run;
            return run;
        }
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public static EtwEventDropsSnapshot Read()
    {
        EtwEventDropSession[] current;
        lock (Gate) current = Sessions.Values.ToArray();
        return new(current.Select(run => run.Read()).ToArray());
    }
}
