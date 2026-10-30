using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.KernelEtwFixtures;

/// <summary>Real recorded ProcessTrace flow with the production owner/factory. No managed kernel callbacks.</summary>
internal sealed unsafe class NativeKernelEtwRecordedTestResource : IKernelEtwSessionBroker, IDisposable
{
    internal sealed record RecordedMetadata(int ProcessorCount, long QpcFrequency, long HeaderQpc);
    private readonly object gate = new();
    private readonly string path;
    private readonly bool startWhenContextSwitchHeld;
    private readonly KernelEtwSessionOwner owner;
    private readonly BoundedKernelEtwRegionProvider regions;
    private readonly Dictionary<long, KernelTraceEventParser.Keywords> holders = [];
    private NativeKernelEtwSession? session;
    private CompletedTraceStatus? finalStatus;
    private Exception? failure;
    private PublicationBarrier? publicationBarrier;
    private long sequence;
    private bool disposed;
    internal RecordedMetadata Metadata { get; }
    internal NativeKernelEtwSessionConfig Configuration { get; }
    internal int ProcessorCount => Metadata.ProcessorCount;
    internal long QpcFrequency => Metadata.QpcFrequency;
    // A reader run owns metadata; duration acquisitions only change the live union.
    internal int SubscribeCount { get; private set; }
    internal int DurationCount { get { lock (gate) return holders.Values.Count(value => value.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch)); } }
    internal KernelTraceEventParser.Keywords BoundKeywords => owner.GetSnapshot().EnabledKeywords;
    internal bool IsRunning => owner.GetSnapshot().IsRunning;
    internal NativeKernelEtwSession Session => Volatile.Read(ref session) ?? throw new InvalidOperationException("Acquire a recorded resource hold first.");
    internal Task ProcessingCompletion => Session.ProcessingCompletion;
    internal long ContextSwitchCount => Volatile.Read(ref finalStatus) is { } value ? checked((long)value.Value.Switches) : 0;
    internal KernelEtwRegionAllocationUsage RegionUsage => regions.GetUsage();

    internal NativeKernelEtwRecordedTestResource(string path, Func<RecordedMetadata, NativeKernelEtwSessionConfig> configure,
        bool startWhenContextSwitchHeld = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configure);
        if (!File.Exists(path)) throw new FileNotFoundException("Recorded test input does not exist.", path);
        this.path = path;
        this.startWhenContextSwitchHeld = startWhenContextSwitchHeld;
        // This independent Windows SDK pass identifies the actual raw
        // EventTrace/Header before the native replay opens. It never uses a
        // first image/ordinary event or TraceEvent dispatch as the clock anchor.
        var raw = KernelEtwZigInterruptEquivalenceTests.ReadRawClock(path);
        if (raw.HeaderRecord == 0 || raw.HeaderQpc < 0)
            throw new InvalidDataException("Recorded input has no raw EventTrace/Header QPC.");
        // TraceEvent is used only to inspect normalized OpenTrace header facts;
        // no parser is attached and its Process method is never called.
        using (var header = new ETWTraceEventSource(path))
        {
            var property = typeof(TraceEventSource).GetProperty("QPCFreq", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(TraceEventSource).FullName, "QPCFreq");
            var frequency = property.GetValue(header) is long value && value > 0 ? value
                : throw new InvalidDataException("Recorded header has no positive QPC frequency.");
            if (header.NumberOfProcessors <= 0) throw new InvalidDataException("Recorded header has no processors.");
            if (raw.Frequency != frequency) throw new InvalidDataException("Independent raw/header frequencies differ.");
            Metadata = new(header.NumberOfProcessors, frequency, raw.HeaderQpc);
        }
        Configuration = configure(Metadata);
        if (Configuration.Residency.QpcFrequency != QpcFrequency || Configuration.Residency.MaximumProcessors < ProcessorCount)
            throw new ArgumentException("The explicit recorded configuration does not cover its actual header frequency/topology.", nameof(configure));
        regions = new(checked((nuint)Configuration.ByteCap));
        var nativeFactory = new NativeKernelEtwSessionRuntimeFactory(regions, () => Configuration, static () => KernelEtwNativeSettings.CreateSessionBuffers(KernelEtwNativeSettings.ProductDefaults));
        owner = new(new RecordedFactory(this, nativeFactory));
    }

    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var id = checked(++sequence);
            holders.Add(id, request.Keywords);
            try
            {
                var lease = owner.Acquire(request);
                if (!request.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch)) SubscribeCount++;
                return new Lease(this, id, lease);
            }
            catch { holders.Remove(id); throw; }
        }
    }
    public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
    internal IDisposable ObservePublicationsAfterSignals(Action barrier)
    {
        ArgumentNullException.ThrowIfNull(barrier);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (publicationBarrier is not null)
                throw new InvalidOperationException("The recorded test resource already owns its publication barrier.");
            return publicationBarrier = new(Session, barrier, error => Volatile.Write(ref failure, error));
        }
    }
    internal void ThrowIfProcessingFailed()
    {
        var error = Volatile.Read(ref failure) ?? Volatile.Read(ref session)?.ProcessingFailure;
        if (error is not null) throw new InvalidOperationException("Recorded native processing failed.", error);
        var snapshot = owner.GetSnapshot();
        if (session is null && snapshot.State == "Unavailable") throw new InvalidOperationException(snapshot.Message);
    }
    internal string Describe()
    {
        var snapshot = owner.GetSnapshot();
        return $"native recorded state={snapshot.State}; message={snapshot.Message}; generation={snapshot.Generation}; "
            + $"holders={snapshot.SubscriptionCount}; switchesAfterCompletion={ContextSwitchCount}; "
            + $"frequency={QpcFrequency}; processors={ProcessorCount}; completed={session?.ProcessingCompletion.IsCompleted}.";
    }
    private void ObserveSession(NativeKernelEtwSession value)
    {
        if (ReferenceEquals(session, value)) return;
        if (value.IsRealtime) throw new InvalidDataException("Recorded fixture received a realtime session.");
        using var borrow = value.AcquireReadLease();
        Check(Native.Clock(borrow.OwnerHandle, out var clock, checked((uint)sizeof(TraceClock))), "read recorded native clock");
        Check(Native.Status(borrow.OwnerHandle, out var status, checked((uint)sizeof(TraceStatus))), "read recorded native header");
        if (clock.Frequency != QpcFrequency || status.ProcessorCount != ProcessorCount || (clock.Flags & 1) != 0)
            throw new InvalidDataException("Native OpenTrace header differs from the cold recorded metadata.");
        Check(Native.SetClock(borrow.OwnerHandle, Metadata.HeaderQpc, Configuration.StartedTickMilliseconds), "set explicit recorded raw-header tick origin");
        typeof(NativeKernelEtwSession).GetProperty(nameof(NativeKernelEtwSession.PublicationOriginUtc))!
            .SetValue(value, DateTimeOffset.FromFileTime(clock.Filetime).ToUniversalTime());
        typeof(NativeKernelEtwSession).GetProperty(nameof(NativeKernelEtwSession.PublicationOriginTickMilliseconds))!
            .SetValue(value, Configuration.StartedTickMilliseconds);
        Volatile.Write(ref failure, null);
        Volatile.Write(ref finalStatus, null);
        Volatile.Write(ref session, value);
    }
    private void ProcessingStopped(Exception? error)
    {
        Volatile.Write(ref failure, error);
        try
        {
            using var borrow = Session.AcquireReadLease();
            Check(Native.Status(borrow.OwnerHandle, out var status, checked((uint)sizeof(TraceStatus))), "read completed native trace status");
            Check(Native.Clock(borrow.OwnerHandle, out var clock, checked((uint)sizeof(TraceClock))), "verify completed raw-header clock");
            if ((clock.Flags & 3) != 2 || clock.HeaderQpc != Metadata.HeaderQpc || clock.Frequency != Metadata.QpcFrequency)
                throw new InvalidDataException("Native replay header differs from the independent recorded raw header.");
            Volatile.Write(ref finalStatus, new(status));
        }
        catch (Exception statusError)
        { Volatile.Write(ref failure, error is null ? statusError : new AggregateException(error, statusError)); }
    }
    private void Return(long id, IKernelEtwSubscription lease)
    {
        lock (gate) { holders.Remove(id); lease.Dispose(); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            owner.Dispose();
            if (regions.GetUsage().CurrentBytes != 0) throw new InvalidOperationException("Recorded resource backing was not released.");
            // Native Dispose has unregistered the chain after the actual join.
            // The caller-owned context and fixed native state can now be freed.
            publicationBarrier?.ReleaseBacking();
            holders.Clear(); disposed = true;
        }
    }
    private sealed class Lease(NativeKernelEtwRecordedTestResource owner, long id, IKernelEtwSubscription lease) : IKernelEtwSubscription
    {
        private NativeKernelEtwRecordedTestResource? owner = owner;
        public KernelEtwSessionSnapshot GetSnapshot() => lease.GetSnapshot();
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Return(id, lease);
    }
    private sealed class RecordedFactory(NativeKernelEtwRecordedTestResource owner, NativeKernelEtwSessionRuntimeFactory native)
        : IKernelEtwSessionRuntimeFactory
    {
        public void ValidateRequest(KernelEtwSubscriptionRequest request) => native.ValidateRequest(request);
        public bool TryGetAvailability(out string state, out string message)
        {
            state = OperatingSystem.IsWindows() ? "Available" : "Unavailable";
            message = "Existing recorded native ETL; no provider or elevation required.";
            return OperatingSystem.IsWindows();
        }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords, KernelEtwSessionContext context,
            IReadOnlyList<KernelEtwSubscriptionRegistration> requests)
        {
            var wrapped = requests.Select(request => request with { Bind = (source, current) =>
                { owner.ObserveSession(source.NativeSession ?? throw new InvalidDataException("Recorded native binding has no session.")); request.Bind(source, current); } }).ToArray();
            var runtime = native.CreateRecorded(owner.path, context, wrapped);
            return new DeferredRecordedRuntime(owner, runtime, requests);
        }
    }
    private sealed class DeferredRecordedRuntime(NativeKernelEtwRecordedTestResource owner, IKernelEtwSessionRuntime native,
        IReadOnlyList<KernelEtwSubscriptionRegistration> initial) : IKernelEtwSessionRuntime
    {
        private Action<Exception?>? completed;
        private bool started, stopping;
        private bool durationHeld = initial.Any(value => value.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
        public bool IsProcessing => !stopping && completed is not null && (!started || native.IsProcessing);
        public int EventsLost => native.EventsLost;
        public void StartProcessing(Action<Exception?> callback)
        {
            if (completed is not null) throw new InvalidOperationException("Recorded flow was already requested.");
            completed = callback;
            StartIfRequested();
        }
        private void StartIfRequested()
        {
            var callback = completed;
            if (started || stopping || callback is null || (owner.startWhenContextSwitchHeld && !durationHeld)) return;
            started = true;
            native.StartProcessing(error => { owner.ProcessingStopped(error); callback(Volatile.Read(ref owner.failure) ?? error); });
        }
        public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> requests, KernelEtwSessionContext context)
        {
            native.UpdateSubscriptions(requests.Select(request => request with { Bind = (source, current) =>
                { owner.ObserveSession(source.NativeSession ?? throw new InvalidDataException("Recorded native binding has no session.")); request.Bind(source, current); } }).ToArray(), context);
            durationHeld = requests.Any(value => value.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            StartIfRequested();
        }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout) { stopping = true; return native.Stop(timeout); }
        public void Dispose() { stopping = true; native.Dispose(); }
    }
    private static void Check(int result, string action)
    { if (result != 0) throw new InvalidOperationException($"Could not {action}: native result {result}."); }
    [StructLayout(LayoutKind.Sequential)] private struct TraceClock
    { public long Frequency, HeaderQpc, QpcBefore, QpcAfter, Filetime; public uint Flags, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceStatus
    {
        public uint WindowsStatus; public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost; public long Frequency; public uint ProcessorCount, BufferSize;
    }
    private sealed record CompletedTraceStatus(TraceStatus Value);
    [StructLayout(LayoutKind.Sequential)] private struct PublicationChain
    { public nint OriginalCallback, OriginalContext, BarrierCallback, BarrierContext; }
    private sealed class PublicationBarrier : IDisposable
    {
        private readonly BoundedKernelEtwRegionProvider regions = new(checked((nuint)sizeof(PublicationChain)));
        private readonly Action<Exception> reportFailure;
        private IKernelEtwRegionLease? backing;
        private GCHandle root;
        private Action? barrier;
        internal PublicationBarrier(NativeKernelEtwSession session, Action barrier, Action<Exception> reportFailure)
        {
            this.barrier = barrier;
            this.reportFailure = reportFailure;
            try
            {
                backing = regions.Allocate("recorded-publication-barrier", checked((nuint)sizeof(PublicationChain)));
                backing.Complete();
                root = GCHandle.Alloc(this);
                using var borrow = session.AcquireReadLease();
                Check(Native.ChainPublication(borrow.OwnerHandle, backing.Pointer, &AfterSignals, GCHandle.ToIntPtr(root)),
                    "install pre-process recorded publication barrier");
            }
            catch
            {
                if (root.IsAllocated) root.Free();
                backing?.Dispose(); backing = null;
                throw;
            }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void AfterSignals(nint context)
        {
            PublicationBarrier? owner = null;
            try
            {
                owner = (PublicationBarrier?)GCHandle.FromIntPtr(context).Target;
                if (owner is not null) Volatile.Read(ref owner.barrier)?.Invoke();
            }
            catch (Exception error)
            {
                try { owner?.reportFailure(error); } catch { }
            }
        }
        public void Dispose() => Volatile.Write(ref barrier, null);
        internal void ReleaseBacking()
        {
            Dispose();
            backing?.Dispose(); backing = null;
            if (root.IsAllocated) root.Free();
            if (regions.GetUsage().CurrentBytes != 0)
                throw new InvalidOperationException("Recorded publication barrier backing was not released.");
        }
    }
    private static class Native
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_clock", CallingConvention = CallingConvention.Cdecl)] internal static extern int Clock(nint owner, out TraceClock clock, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_status", CallingConvention = CallingConvention.Cdecl)] internal static extern int Status(nint owner, out TraceStatus status, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_replay_clock", CallingConvention = CallingConvention.Cdecl)] internal static extern int SetClock(nint owner, long qpc, long tick);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_chain_publication", CallingConvention = CallingConvention.Cdecl)] internal static extern int ChainPublication(nint owner, nint chain, delegate* unmanaged[Cdecl]<nint, void> barrier, nint context);
    }
}
