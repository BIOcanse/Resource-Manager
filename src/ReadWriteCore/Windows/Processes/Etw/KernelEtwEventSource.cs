using System.Collections.Concurrent;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>A holder's native output reference. Binding is cold; event payloads never enter this type.</summary>
public sealed class KernelEtwEventSource
{
    private readonly string holder;
    private readonly Func<string, Func<EtwEventKindDrops?>?, bool, EtwEventDropCounter> createCounter;
    private readonly long generation;
    private ulong counterToken;
    private bool windowActive = true;
    private long windowStart, incompleteThrough;
    private readonly ConcurrentDictionary<(string Kind, string? Reason), EtwEventDropCounter> consumerCounters = new();
    private readonly object observationGate = new();
    private PublicationObservation[] observations = [];
    private int state;
    public NativeKernelEtwSession NativeSession { get; }
    public bool IsActive => Volatile.Read(ref state) == 1;
    public bool IsCancelled => Volatile.Read(ref state) < 0;
    internal ulong CounterToken => counterToken;

    public KernelEtwEventSource(NativeKernelEtwSession nativeSession)
        : this(nativeSession, "unbound", KernelEtwNativeOutputs.None, 0, static (name, read, supplement) => new(name, read, supplement)) { }

    internal KernelEtwEventSource(NativeKernelEtwSession nativeSession, string holder, KernelEtwNativeOutputs outputs,
        long generation, Func<string, Func<EtwEventKindDrops?>?, bool, EtwEventDropCounter> createCounter)
    {
        NativeSession = nativeSession ?? throw new ArgumentNullException(nameof(nativeSession));
        this.holder = holder;
        this.generation = generation;
        NativeOutputs = outputs;
        this.createCounter = createCounter;
    }

    internal KernelEtwNativeOutputs NativeOutputs { get; }

    /// <summary>Consumer use is separate from the native physical delivery totals.</summary>
    public EtwEventDropCounter ConsumerCounter(string kind) => Counter(kind, null);
    public EtwEventDropCounter ConsumerRejectionCounter(string kind, string reason) => Counter(kind, reason);

    public void RecordConsumerRejection(string kind, string reason)
    {
        var counter = Counter(kind, reason);
        counter.Receive();
        counter.Drop();
    }

    private EtwEventDropCounter Counter(string kind, string? reason)
        => consumerCounters.GetOrAdd((kind, reason), static (key, source) =>
        {
            lock (source.observationGate)
            {
                if (source.IsCancelled && source.counterToken == 0)
                    return source.createCounter($"holder/{source.holder}/{key.Kind}" + (key.Reason is null ? "" : $"/rejected:{key.Reason}"), null, key.Reason is not null);
                source.counterToken = source.counterToken == 0 ? source.NativeSession.GetConsumerCounterToken(source.holder) : source.counterToken;
                var reader = source.NativeSession.CreateConsumerCounterReader(source.counterToken, source.holder, source.NativeOutputs,
                    source.generation, key.Kind, key.Reason);
                source.UpdateAdmission();
                return source.createCounter($"holder/{source.holder}/{key.Kind}" + (key.Reason is null ? "" : $"/rejected:{key.Reason}"),
                    reader, key.Reason is not null);
            }
        }, this);

    /// <summary>Cold admission facts follow this consumer's capture window; no hold is acquired.</summary>
    public void ConfigureAdmission(bool windowActive, long windowStart = 0, long incompleteThrough = 0)
    {
        lock (observationGate)
        {
            if (IsCancelled) return;
            this.windowActive = windowActive;
            this.windowStart = windowStart; this.incompleteThrough = incompleteThrough;
            UpdateAdmission();
        }
    }
    private void UpdateAdmission()
    {
        if (counterToken != 0) NativeSession.ConfigureConsumerAdmission(counterToken, generation, NativeOutputs,
            IsActive, windowActive, windowStart, incompleteThrough);
    }

    /// <summary>Signals this binding's existing worker; reads neither payloads nor starts work.</summary>
    public IDisposable ObservePublications(Action signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (observationGate)
        {
            if (Volatile.Read(ref state) < 0) throw new ObjectDisposedException(nameof(KernelEtwEventSource));
            var observation = new PublicationObservation(this, signal);
            Volatile.Write(ref observations, [.. observations, observation]);
            return observation;
        }
    }

    internal void Activate()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("The kernel output binding cannot be activated twice.");
        lock (observationGate) UpdateAdmission();
    }

    internal void Cancel()
    {
        lock (observationGate)
        {
            if (Interlocked.Exchange(ref state, -1) < 0) return;
            UpdateAdmission();
            if (counterToken != 0) NativeSession.RetireConsumerCounter(counterToken);
            Volatile.Write(ref observations, []);
        }
    }

    internal bool DispatchPublication()
    {
        // Admit once per binding. A callback already admitted can finish; a
        // later binding in a captured dispatcher set must recheck its own state.
        var current = Volatile.Read(ref observations);
        if (!IsActive) return false;
        var used = false;
        foreach (var observation in current)
            used |= observation.Deliver();
        return used;
    }

    private void Remove(PublicationObservation observation)
    {
        lock (observationGate)
        {
            var current = observations;
            var index = Array.IndexOf(current, observation);
            if (index < 0) return;
            var next = new PublicationObservation[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref observations, next);
        }
    }

    private sealed class PublicationObservation(KernelEtwEventSource source, Action signal) : IDisposable
    {
        private KernelEtwEventSource? owner = source;
        internal bool Deliver()
        {
            if (Volatile.Read(ref owner) is null) return false;
            signal();
            return true;
        }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Remove(this);
    }
}
