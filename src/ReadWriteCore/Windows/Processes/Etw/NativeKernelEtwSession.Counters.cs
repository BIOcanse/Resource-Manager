using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed unsafe partial class NativeKernelEtwSession
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterConfiguration
    {
        public ulong Token;
        public long Generation;
        public ulong KindMask;
        public uint Outputs, Flags;
        public long WindowStart, IncompleteThrough;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterPair { public ulong Received, Dropped; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CounterFrame
    {
        public fixed ulong Totals[16 * 2];
        public fixed ulong Rejected[16 * 18];
    }
    private readonly Dictionary<ulong, string> counterTokenHolders = [];
    private readonly HashSet<string> counterHolderNames = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, CounterConfiguration> counterConfigurations = [];
    private readonly HashSet<ulong> pendingCounterRetirements = [];
    private readonly Dictionary<(string Holder, uint Kind, uint Reason), CounterPair> retiredHolderCounters = [];
    private readonly Dictionary<(ulong Token, uint Kind, uint Reason), CounterPair> finalConsumerCounters = [];
    private readonly CounterPair[] finalPhysicalCounters = new CounterPair[28];
    private uint maximumCounterConsumers;
    private ulong nextCounterToken;
    private Exception? counterSnapshotFailure;
    public Exception? CounterSnapshotFailure => Volatile.Read(ref counterSnapshotFailure);
    private static readonly string[] ConsumerKinds = ["ProcessStartGroup", "ProcessEndGroup", "ThreadStartGroup", "ThreadEndGroup", "ThreadCSwitch",
        "isr", "dpc", "threaded_dpc", "timer_dpc", "ImageLoadOrRundown", "ImageUnload", "disk-transfer", "process-identity",
        "network-transfer", "renderer-invalidation", "process-start"];
    private static readonly string[] CounterReasons = ["", "generation-mismatch", "", "outside-window", "before-window", "unmapped-processor",
        "invalid-duration", "inactive-binding", "invalid-payload", "stale-binding", "incomplete-window", "capacity", "before-process-birth",
        "invalid-process", "stopped", "system-process", "closed-queue", "user-mode-image"];

    internal void InitializeCounterContract(uint maximumConsumers)
    {
        if (maximumConsumers == 0) throw new ArgumentOutOfRangeException(nameof(maximumConsumers));
        lock (gate) maximumCounterConsumers = maximumConsumers;
    }

    internal ulong GetConsumerCounterToken(string holder)
    {
        lock (gate)
        {
            ReapCounterRetirements();
            if (counterTokenHolders.Count >= maximumCounterConsumers)
                throw new InvalidOperationException("The kernel ETW consumer descriptor capacity was exceeded.");
            if (!counterHolderNames.Contains(holder) && counterHolderNames.Count >= maximumCounterConsumers)
                throw new InvalidOperationException("The kernel ETW stable diagnostic holder capacity was exceeded.");
            var token = checked(++nextCounterToken);
            counterHolderNames.Add(holder);
            counterTokenHolders.Add(token, holder);
            return token;
        }
    }

    internal Func<EtwEventKindDrops?>? CreateConsumerCounterReader(ulong token, string holder, KernelEtwNativeOutputs outputs,
        long generation, string name, string? reason)
    {
        var kindIndex = Array.IndexOf(ConsumerKinds, name);
        var reasonIndex = reason is null ? 0 : Array.IndexOf(CounterReasons, reason);
        if (kindIndex < 0 || reasonIndex < 0) return null;
        lock (gate)
        {
            ReapCounterRetirements();
            if (!counterTokenHolders.ContainsKey(token) || pendingCounterRetirements.Contains(token))
                return () => ReadHolderCounter(holder, (uint)kindIndex, (uint)reasonIndex, name);
            if (!counterConfigurations.TryGetValue(token, out var config))
                config = new() { Token = token, Generation = generation, Outputs = (uint)outputs, Flags = 8 | 4 };
            config.KindMask |= 1UL << kindIndex;
            counterConfigurations[token] = config;
            ConfigureCounter(config);
            finalConsumerCounters.TryAdd((token, (uint)kindIndex, (uint)reasonIndex), default);
        }
        return () => ReadHolderCounter(holder, (uint)kindIndex, (uint)reasonIndex, name);
    }

    internal void ConfigureConsumerAdmission(ulong token, long generation, KernelEtwNativeOutputs outputs,
        bool active, bool windowActive = true, long windowStart = 0, long incompleteThrough = 0)
    {
        lock (gate)
        {
            ReapCounterRetirements();
            if (pendingCounterRetirements.Contains(token)) return;
            if (!counterConfigurations.TryGetValue(token, out var config)) return;
            config.Generation = generation; config.Outputs = (uint)outputs;
            config.Flags = 8u | (active ? 1u : 0u) | (windowActive ? 4u : 0u);
            config.WindowStart = windowStart; config.IncompleteThrough = incompleteThrough;
            counterConfigurations[token] = config;
            ConfigureCounter(config);
        }
    }
    internal void RetireConsumerCounter(ulong token)
    {
        lock (gate)
        {
            ReapCounterRetirements();
            if (!counterTokenHolders.ContainsKey(token) || pendingCounterRetirements.Contains(token)) return;
            if (!counterConfigurations.ContainsKey(token)) { counterTokenHolders.Remove(token); return; }
            Check(CounterNative.Retire(handle, token), "retire consumer admission");
            pendingCounterRetirements.Add(token);
            FlushRunningProvider();
            ReapCounterRetirements();
        }
    }
    private void ReapCounterRetirements()
    {
        if (handle == 0 || disposed || pendingCounterRetirements.Count == 0) return;
        foreach (var token in pendingCounterRetirements.ToArray())
        {
            var result = CounterNative.TakeRetired(handle, token, out var frame, (uint)sizeof(CounterFrame));
            if (result == 3) continue;
            Check(result, "acknowledge retired consumer admission");
            var holder = counterTokenHolders[token];
            for (uint kind = 0; kind < 16; kind++)
            {
                AccumulateRetired(holder, kind, 0, new() { Received = frame.Totals[kind * 2], Dropped = frame.Totals[kind * 2 + 1] });
                for (uint reason = 1; reason < 18; reason++)
                {
                    var count = frame.Rejected[kind * 18 + reason];
                    AccumulateRetired(holder, kind, reason, new() { Received = count, Dropped = count });
                }
            }
            foreach (var key in finalConsumerCounters.Keys.Where(key => key.Token == token).ToArray()) finalConsumerCounters.Remove(key);
            counterConfigurations.Remove(token); counterTokenHolders.Remove(token); pendingCounterRetirements.Remove(token);
        }
    }
    private void AccumulateRetired(string holder, uint kind, uint reason, CounterPair pair)
    {
        if (pair.Received == 0 && pair.Dropped == 0) return;
        var key = (holder, kind, reason);
        var previous = retiredHolderCounters.GetValueOrDefault(key);
        retiredHolderCounters[key] = new() { Received = SaturatingAdd(previous.Received, pair.Received), Dropped = SaturatingAdd(previous.Dropped, pair.Dropped) };
    }
    private static ulong SaturatingAdd(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    private void ConfigureCounter(CounterConfiguration config)
    {
        ObjectDisposedException.ThrowIf(disposed || handle == 0, this);
        Check(CounterNative.Configure(handle, in config, (uint)Marshal.SizeOf<CounterConfiguration>()), "configure consumer admission");
        FlushRunningProvider();
    }
    private EtwEventKindDrops ReadConsumerCounter(ulong token, uint kind, uint reason, string name)
    {
        lock (gate)
        {
            var key = (token, kind, reason);
            var pair = finalConsumerCounters.GetValueOrDefault(key);
            if (handle != 0 && !disposed)
            {
                var result = CounterNative.QueryConsumer(handle, token, kind, reason, out var current, 16);
                if (result == 0) pair = current;
                else if (result != 3) Check(result, "read consumer admission counter");
                finalConsumerCounters[key] = pair;
            }
            return ProjectCounter(name, pair);
        }
    }
    private EtwEventKindDrops ReadHolderCounter(string holder, uint kind, uint reason, string name)
    {
        lock (gate)
        {
            ReapCounterRetirements();
            var archived = ProjectCounter(name, retiredHolderCounters.GetValueOrDefault((holder, kind, reason)));
            long received = archived.Received, dropped = archived.Dropped;
            foreach (var pair in counterTokenHolders)
            {
                if (!StringComparer.Ordinal.Equals(pair.Value, holder)) continue;
                var counts = ReadConsumerCounter(pair.Key, kind, reason, name);
                received = checked(received + counts.Received); dropped = checked(dropped + counts.Dropped);
            }
            return new(name, received, dropped);
        }
    }
    internal EtwEventKindDrops ReadPhysicalCounter(uint kind, string name)
    {
        lock (gate)
        {
            ReapCounterRetirements();
            var pair = finalPhysicalCounters[kind];
            if (handle != 0 && !disposed)
            {
                Check(CounterNative.QueryPhysical(handle, kind, out pair, 16), "read physical event counter");
                finalPhysicalCounters[kind] = pair;
            }
            return ProjectCounter(name, pair);
        }
    }
    internal void CaptureFinalCounterSnapshot()
    {
        try { CaptureFinalCounterSnapshotCore(); }
        catch (Exception error) { Volatile.Write(ref counterSnapshotFailure, error); }
    }
    private void CaptureFinalCounterSnapshotCore()
    {
        lock (gate)
        {
            if (handle == 0) return;
            ReapCounterRetirements();
            // Diagnostic reads cannot replace a ProcessTrace error, prevent
            // completion notification, or retain an otherwise released owner.
            List<Exception>? failures = null;
            for (uint kind = 0; kind < 28; kind++)
                try { ReadPhysicalCounter(kind, ""); }
                catch (Exception error) { (failures ??= []).Add(error); }
            foreach (var key in finalConsumerCounters.Keys.ToArray())
                try { ReadConsumerCounter(key.Token, key.Kind, key.Reason, ""); }
                catch (Exception error) { (failures ??= []).Add(error); }
            if (failures is not null)
                Volatile.Write(ref counterSnapshotFailure, failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }
    private static EtwEventKindDrops ProjectCounter(string name, CounterPair pair)
        => new(name, checked((long)Math.Min(pair.Received, long.MaxValue)), checked((long)Math.Min(pair.Dropped, long.MaxValue)));
    private static class CounterNative
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_configure_consumer_counter", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Configure(nint owner, in CounterConfiguration config, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_consumer_counter", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int QueryConsumer(nint owner, ulong token, uint kind, uint reason, out CounterPair pair, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_event_counter", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int QueryPhysical(nint owner, uint kind, out CounterPair pair, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_retire_consumer_counter", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Retire(nint owner, ulong token);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_take_retired_consumer_counter", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TakeRetired(nint owner, ulong token, out CounterFrame frame, uint size);
    }
}
