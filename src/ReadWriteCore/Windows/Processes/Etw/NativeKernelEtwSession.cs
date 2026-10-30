using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>One shared native owner and its retained allocation. Reads borrow it without acquiring a session hold.</summary>
public sealed unsafe partial class NativeKernelEtwSession : IKernelEtwNativeRuntimeOperations,
    IKernelEtwNativeReadDrain, IKernelEtwNativeProcessingCompletion, IKernelEtwNativeOutputControl
{
    private readonly object gate = new();
    private readonly IKernelEtwRegionLease? region;
    private readonly NativeKernelEtwHostAllocator? allocator;
    private readonly IKernelEtwNativeSessionControl nativeControl;
    private nint handle;
    private TraceEventSession? provider;
    private KernelEtwSessionKeywordControl? keywordControl;
    private int readers;
    private bool providerStopped;
    private bool traceOpened;
    private bool traceClosed;
    private bool providerDisposed;
    private bool disposed;
    private bool realtime;
    private GCHandle publicationRoot;
    private bool publicationCallbackRegistered;
    private Action[] publicationSignals = [];
    private readonly Action<Exception>? reportPublicationFailure;
    private Exception? publicationCallbackFailure;
    private readonly TaskCompletionSource processingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? processingFailure;
    /// <summary>Whether this owner opened a realtime provider, rather than a recorded or controlled source.</summary>
    public bool IsRealtime => realtime;
    /// <summary>Completes when the native flow and its observer exit; inspect ProcessingFailure for failure.</summary>
    public Task ProcessingCompletion => processingCompletion.Task;
    public Exception? ProcessingFailure => Volatile.Read(ref processingFailure);
    public Exception? PublicationCallbackFailure => Volatile.Read(ref publicationCallbackFailure);

    void IKernelEtwNativeProcessingCompletion.OnProcessingCompleted(Exception? failure)
    {
        CaptureFinalCounterSnapshot();
        Volatile.Write(ref processingFailure, failure);
        processingCompletion.TrySetResult();
    }
    public DateTimeOffset PublicationOriginUtc { get; private set; }
    public long PublicationOriginTickMilliseconds { get; private set; }

    public DateTimeOffset GetPublicationTimestamp(long throughTickMilliseconds)
        => PublicationOriginUtc.AddMilliseconds(checked(throughTickMilliseconds - PublicationOriginTickMilliseconds));

    /// <summary>Queues a publication on the event owner and flushes the owned provider to wake an idle trace.</summary>
    public void RequestRefresh(long tickMilliseconds)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || handle == 0 || traceClosed || (realtime && providerStopped), this);
            Check(Native.RequestRefresh(handle, tickMilliseconds), "request shared publication");
            provider?.Flush();
        }
    }

    internal NativeKernelEtwSession(IKernelEtwRegionLease region, Action<Exception>? reportPublicationFailure = null,
        IKernelEtwNativeSessionControl? nativeControl = null)
    {
        this.region = region;
        this.reportPublicationFailure = reportPublicationFailure;
        this.nativeControl = nativeControl ?? new WindowsKernelEtwNativeSessionControl();
    }

    internal NativeKernelEtwSession(NativeKernelEtwHostAllocator allocator,
        Action<Exception>? reportPublicationFailure = null, IKernelEtwNativeSessionControl? nativeControl = null)
    {
        this.allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        this.reportPublicationFailure = reportPublicationFailure;
        this.nativeControl = nativeControl ?? new WindowsKernelEtwNativeSessionControl();
    }

    public KernelEtwAllocatorUsage? AllocationUsage => allocator?.GetUsage();

    public void UpdateOutputs(KernelEtwNativeOutputs outputs)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || handle == 0 || traceClosed, this);
            Check(Native.SetOutputs(handle, (uint)outputs), "update held native outputs");
            // This wakes the existing consumer to apply its queued resource
            // transition. It does not create a worker or a kernel-session hold.
            FlushRunningProvider();
        }
    }

    // providerStopped is set only after this owner's own StopSession stopped
    // its session or proved it absent. A FLUSH by that name can then only
    // report 4201; the ending trace delivers its final buffers without a wake.
    private void FlushRunningProvider()
    {
        if (!providerStopped) provider?.Flush();
    }

    internal void Initialize(string name, NativeKernelEtwSessionConfig config,
        KernelTraceEventParser.Keywords keywords, Action<string>? beforeCreate, DateTimeOffset publicationOriginUtc,
        bool realtime = true)
    {
        this.realtime = realtime;
        PublicationOriginUtc = publicationOriginUtc;
        PublicationOriginTickMilliseconds = config.StartedTickMilliseconds;
        if (allocator is not null)
        {
            var callbacks = allocator.Callbacks;
            Check(Native.CreateWithAllocator(in config, in callbacks, out handle), "create output-scoped shared owner");
        }
        else
        {
            // Explicit controlled/legacy bounded fixtures retain this ABI.
            // The production runtime factory requires the scoped provider.
            var length = checked((nuint)config.ByteCap);
            if (region is null || region.Pointer == 0 || region.Length < length)
                throw new InvalidOperationException("The retained kernel ETW region is smaller than its declared owner capacity.");
            Check(Native.Create(in config, region.Pointer, length, out handle), "create shared owner");
            region.Complete();
        }
        InitializeCounterContract(config.MaximumCommands);
        if (realtime)
        {
            beforeCreate?.Invoke(name);
            provider = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = false };
            provider.EnableKernelProvider(keywords);
            keywordControl = new(name, nativeControl);
        }
        else providerStopped = true;
        // A failed OpenTrace can still retain a handle that requires explicit
        // cleanup; always probe close after attempting the native open.
        traceOpened = true;
        fixed (char* namePointer = name)
            Check(Native.Open(handle, namePointer, checked((uint)name.Length), realtime ? 1u : 0u), "open shared trace");
        publicationRoot = GCHandle.Alloc(this);
        Check(Native.SetPublicationCallback(handle, &SignalPublication, GCHandle.ToIntPtr(publicationRoot)), "register publication signal");
        publicationCallbackRegistered = true;
    }

    /// <summary>Signals an existing worker once per publication. Handlers must not read or convert event payloads here.</summary>
    public IDisposable ObservePublications(Action signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || traceClosed, this);
            Volatile.Write(ref publicationSignals, [.. publicationSignals, signal]);
            return new PublicationObservation(this, signal);
        }
    }

    private sealed class PublicationObservation(NativeKernelEtwSession session, Action signal) : IDisposable
    {
        private NativeKernelEtwSession? owner = session;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.gate)
            {
                var signals = current.publicationSignals;
                var index = Array.IndexOf(signals, signal);
                if (index < 0) return;
                var next = new Action[signals.Length - 1];
                Array.Copy(signals, 0, next, 0, index);
                Array.Copy(signals, index + 1, next, index, signals.Length - index - 1);
                Volatile.Write(ref current.publicationSignals, next);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SignalPublication(nint context)
    {
        NativeKernelEtwSession? owner = null;
        try
        {
            owner = (NativeKernelEtwSession?)GCHandle.FromIntPtr(context).Target;
            if (owner is null) return;
            foreach (var signal in Volatile.Read(ref owner.publicationSignals))
            {
                try { signal(); }
                catch (Exception failure) { owner.RecordPublicationFailure(failure); }
            }
        }
        catch (Exception failure) { owner?.RecordPublicationFailure(failure); }
    }

    private void RecordPublicationFailure(Exception failure)
    {
        Volatile.Write(ref publicationCallbackFailure, failure);
        try { reportPublicationFailure?.Invoke(failure); }
        catch (Exception reportingFailure)
        { Volatile.Write(ref publicationCallbackFailure, new AggregateException(failure, reportingFailure)); }
    }

    /// <summary>The lease protects native backing while a command or published-view access is in flight.</summary>
    public ReadLease AcquireReadLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || handle == 0 || traceClosed, this);
            readers = checked(readers + 1);
            return new(this, handle);
        }
    }

    public sealed class ReadLease : IDisposable
    {
        private NativeKernelEtwSession? session;
        public nint OwnerHandle { get; }
        internal ReadLease(NativeKernelEtwSession session, nint handle) { this.session = session; OwnerHandle = handle; }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref session, null);
            if (owner is null) return;
            lock (owner.gate)
            {
                if (--owner.readers == 0) Monitor.PulseAll(owner.gate);
            }
        }
    }

    bool IKernelEtwNativeReadDrain.WaitForReaders(TimeSpan timeout)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            while (readers != 0)
            {
                var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout
                    : timeout - System.Diagnostics.Stopwatch.GetElapsedTime(began);
                if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero) return false;
                if (!Monitor.Wait(gate, remaining)) return readers == 0;
            }
            return true;
        }
    }

    public int EventsLost
    {
        get
        {
            lock (gate)
                return handle == 0 ? 0 : checked((int)Math.Min((ulong)int.MaxValue, Native.EventsLost(handle)));
        }
    }

    void IKernelEtwNativeRuntimeOperations.Process()
    {
        // ProcessTrace is the only mutator of event state; never hold a managed monitor over it.
        nint owner;
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); owner = handle; }
        var result = Native.Process(owner);
        if (result != 0 && ReadFailureDetail() is { } failure)
            throw new InvalidOperationException($"Native kernel ETW could not process shared trace (result {failure.Result}, error {failure.ErrorName}, record {failure.Records}, QPC {failure.Qpc}).");
        Check(result, "process shared trace");
    }

    void IKernelEtwNativeRuntimeOperations.StopProvider()
    {
        lock (gate)
        {
            if (providerStopped) return;
            try
            {
                // Enable can create the provider before failing. Capture its
                // owned native identity lazily during explicit cleanup; the
                // TraceEvent Stop flag cannot prove a denied stop succeeded.
                if (keywordControl is null && provider is not null)
                    keywordControl = new(provider.SessionName, nativeControl);
                keywordControl?.StopSession();
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 4201)
            {
                // The initial identity query can prove the partial session
                // absent before a keyword controller exists.
            }
            providerStopped = true;
            if (provider is not null) provider.StopOnDispose = false;
        }
    }

    void IKernelEtwNativeRuntimeOperations.UpdateKeywords(KernelTraceEventParser.Keywords keywords)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Recorded inputs have no provider to reconfigure. Their logical
            // holder union still updates in place through the dispatcher.
            if (!realtime) return;
            if (providerStopped || keywordControl is null) throw new InvalidOperationException("The owned provider is not running.");
            keywordControl.UpdateKeywords(keywords);
        }
    }

    void IKernelEtwNativeRuntimeOperations.UnblockProcessing()
    {
        lock (gate)
        {
            if (!providerStopped) throw new InvalidOperationException("Stop the provider before draining its trace.");
            if (traceOpened && !traceClosed)
            {
                if (realtime) Check(Native.Stop(handle), "unblock shared trace");
                // File processing can only be closed after it returns. Request
                // cancellation at a buffer boundary; the runtime joins before
                // CloseTrace releases the retained consumer and callback state.
                else Native.RequestStop(handle);
            }
        }
    }

    void IKernelEtwNativeRuntimeOperations.CloseTrace()
    {
        lock (gate)
        {
            if (traceClosed) return;
            if (traceOpened) Check(Native.CloseTrace(handle), "close shared trace");
            traceClosed = true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!providerStopped || !traceClosed) throw new InvalidOperationException("The provider and consumer must be stopped before releasing native state.");
            if (readers != 0) throw new InvalidOperationException("Native kernel ETW read references remain outstanding.");
            if (!providerDisposed) { provider?.Dispose(); providerDisposed = true; }
            if (handle != 0) CaptureFinalCounterSnapshot();
            // Processing was joined by the runtime before CloseTrace. Unregister
            // before dropping the GC root; no callback can outlive its target.
            if (publicationCallbackRegistered)
            {
                Check(Native.SetPublicationCallback(handle, null, 0), "unregister publication signal");
                publicationCallbackRegistered = false;
            }
            if (publicationRoot.IsAllocated) publicationRoot.Free();
            if (handle != 0) { Check(Native.Destroy(handle), "destroy shared owner"); handle = 0; }
            // A failed ledger release remains retryable after successful native destruction.
            region?.Dispose();
            allocator?.Dispose();
            processingCompletion.TrySetResult();
            Volatile.Write(ref publicationSignals, []);
            disposed = true;
        }
    }

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Native kernel ETW could not {operation} (result {result}).");
    }

    internal static class Native
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_create_in_region", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(in NativeKernelEtwSessionConfig config, nint memory, nuint length, out nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_create_with_allocator", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateWithAllocator(in NativeKernelEtwSessionConfig config,
            in NativeKernelEtwHostAllocatorCallbacks callbacks, out nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_outputs", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetOutputs(nint handle, uint outputs);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open(nint handle, char* name, uint length, uint realtime);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_process", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Process(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_stop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Stop(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_request_stop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RequestStop(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_close_trace", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CloseTrace(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Destroy(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_events_lost", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong EventsLost(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_request_refresh", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RequestRefresh(nint handle, long tickMilliseconds);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_publication_callback", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetPublicationCallback(nint handle, delegate* unmanaged[Cdecl]<nint, void> callback, nint context);
    }
}
