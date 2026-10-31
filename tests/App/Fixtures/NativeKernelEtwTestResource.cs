using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.KernelEtwFixtures;

/// <summary>Test-only controlled flow over the real bounded shared native owner. No provider or timer.
/// IO identities are scripted; thread diagnostics explicitly select recorded, Windows or scripted input.</summary>
internal sealed unsafe partial class NativeKernelEtwTestResource : IKernelEtwSessionBroker, IDisposable
{
    internal enum DiagnosticSourceMode { Recorded = 0, LiveWindows = 1, Scripted = 2 }
    private readonly BoundedKernelEtwRegionProvider regions;
    private readonly KernelEtwSessionOwner broker;
    private readonly Factory factory;
    private readonly DateTimeOffset originUtc;
    private readonly DiagnosticSourceMode diagnosticSource;
    private readonly long qpcOrigin;
    private ControlledRuntime? current;
    private bool disposed;
    internal NativeKernelEtwSessionConfig Configuration { get; }
    internal Dictionary<uint, ScriptedIdentity> Identities { get; } = [];
    internal List<KernelTraceEventParser.Keywords> KeywordUpdates { get; } = [];
    internal List<Exception> CallbackFailures { get; } = [];
    internal Exception? KeywordUpdateFailure { get; set; }
    internal long Tick { get; private set; }
    internal long Generation => GetSnapshot().Generation;
    internal DateTimeOffset StartedAt => GetSnapshot().StartedAt;
    internal long QpcFrequency => Configuration.Residency.QpcFrequency;
    internal bool IsRealtime => false;
    internal int Creations { get; private set; }
    internal int Stops { get; private set; }
    internal int ActiveIdentityHandles { get; private set; }
    internal NativeKernelEtwSession Session => current?.Session ?? throw new InvalidOperationException("Acquire a test run hold before borrowing its native owner.");
    internal string DiagnosticName => current?.DiagnosticName ?? throw new InvalidOperationException("Acquire a test run hold before reading its diagnostic name.");
    internal IKernelEtwSessionRuntimeFactory RuntimeFactory => factory;
    internal KernelEtwRegionAllocationUsage RegionUsage => regions.GetUsage();

    // Test declarations only: deliberately small, explicit bounds, independent of production defaults.
    internal static NativeKernelEtwSessionConfig SmallConfiguration(long? qpcFrequency = null, uint maximumProcessors = 64,
        uint windowMilliseconds = 1000, long startedTick = 0)
    {
        var config = new NativeKernelEtwSessionConfig
        {
            ByteCap = 32 * 1024 * 1024,
            DiskByteCap = 4 * 1024 * 1024, NetworkByteCap = 4 * 1024 * 1024,
            FileByteCap = 4 * 1024 * 1024, HistoryByteCap = 4 * 1024 * 1024,
            Residency = new() { QpcFrequency = qpcFrequency ?? Stopwatch.Frequency, WindowMilliseconds = windowMilliseconds,
                MaximumPairs = 32,
                MaximumProcesses = 64, MaximumThreads = 128, MaximumProcessors = maximumProcessors,
                MaximumNameBytes = 4096, ByteCap = 4 * 1024 * 1024 },
            Interrupts = new() { MaximumModules = 16, MaximumPathUnits = 1024, MaximumIdentities = 64,
                MaximumSlices = 128, MaximumSliceDrivers = 256, ByteCap = 4 * 1024 * 1024 },
            MaximumDiskPairs = 32, MaximumDisks = 16, MaximumCachedProcesses = 32,
            MaximumNetworkEvents = 256, MaximumNetworkProcesses = 64, MaximumFileEntries = 64,
            MaximumFileVersions = 256, MaximumHistoryEntries = 128, MaximumPathUnits = 1024,
            MaximumNamespaceEntries = 32, MaximumCommands = 128, MaximumCommandNameBytes = 4096,
            PublicationBanks = 3, WindowMilliseconds = 2000, RetentionMilliseconds = 8000,
            DiskBucketMilliseconds = 100, StartedTickMilliseconds = startedTick,
            MaximumPendingEvents = 128, MaximumPendingPathUnits = 4096, MaximumRendererProcesses = 64
        };
        config.StampAbi(new(1, DateTimeOffset.UnixEpoch.AddMilliseconds(startedTick)));
        return config;
    }

    internal NativeKernelEtwTestResource(NativeKernelEtwSessionConfig configuration, DateTimeOffset? originUtc = null,
        DiagnosticSourceMode diagnosticSource = DiagnosticSourceMode.Recorded, long qpcOrigin = 0)
    {
        Configuration = configuration;
        Tick = configuration.StartedTickMilliseconds;
        this.originUtc = originUtc ?? DateTimeOffset.UnixEpoch.AddMilliseconds(Tick);
        if (!Enum.IsDefined(diagnosticSource)) throw new ArgumentOutOfRangeException(nameof(diagnosticSource));
        if (qpcOrigin < 0) throw new ArgumentOutOfRangeException(nameof(qpcOrigin));
        this.diagnosticSource = diagnosticSource;
        this.qpcOrigin = qpcOrigin;
        regions = new(checked((nuint)configuration.ByteCap));
        factory = new(this);
        broker = new(factory);
    }

    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request) => broker.Acquire(request);
    public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
    internal void Reconcile() => broker.Reconcile();
    internal void Rebind()
    {
        current?.Complete(null);
        broker.Reconcile();
    }

    internal void Submit(params Input[] inputs)
    {
        if (inputs.Any(input => input.Kind is 3 or 4 or 5 or 6 or 7))
            throw new ArgumentException("Use lifecycle/CSwitch helpers so the actual shared native hooks execute.", nameof(inputs));
        using var read = Session.AcquireReadLease();
        fixed (Input* values = inputs) Check(Native.Submit(read.OwnerHandle, values, checked((uint)inputs.Length)), "submit controlled input");
    }
    private void Named(Input input, string? name, bool contextSwitch = false, bool resolveThreads = false)
    {
        byte[] bytes = name is null ? [] : Encoding.UTF8.GetBytes(name);
        fixed (byte* text = bytes)
        {
            input.Name = (nint)text; input.NameLength = checked((uint)bytes.Length);
            if (contextSwitch)
                Check(Native.CSwitch(current!.Wrapper, Tick, ref input, resolveThreads ? 1u : 0u), "deliver controlled context switch");
            else Submit(input); // The native staging queue copies the name before this pin returns.
        }
    }
    internal void SeedProcess(int pid, long birth, string name, int eventsLost = 0)
        => Named(new() { Kind = 11, Flags = 1, ProcessId = pid, Start = birth, Qpc = eventsLost }, name);
    internal void SeedThread(int pid, long birth, int tid, int eventsLost = 0)
        => Submit(new Input { Kind = 12, ProcessId = pid, Start = birth, ThreadId = tid, Qpc = eventsLost });
    internal void ProcessStart(long qpc, DateTimeOffset at, int pid, ulong unique, long birth, string name)
        => ProcessLifecycle(new() { Kind = 3, Flags = 1, Qpc = qpc, TimeTicks = at.UtcTicks, ProcessId = pid, Unique = unique, Start = birth }, name);
    internal void ProcessEnd(long qpc, DateTimeOffset at, int pid, ulong unique)
        => ProcessLifecycle(new() { Kind = 4, Qpc = qpc, TimeTicks = at.UtcTicks, ProcessId = pid, Unique = unique }, null);
    private void ProcessLifecycle(Input input, string? name)
    {
        byte[] bytes = name is null ? [] : Encoding.UTF8.GetBytes(name);
        fixed (byte* text = bytes)
        {
            input.Name = (nint)text; input.NameLength = checked((uint)bytes.Length);
            Check(Native.Process(current!.Wrapper, Tick, ref input), "deliver controlled process lifecycle");
        }
    }
    internal void ThreadStart(long qpc, DateTimeOffset at, int pid, int tid)
        => ThreadLifecycle(new Input { Kind = 5, Qpc = qpc, TimeTicks = at.UtcTicks, ProcessId = pid, ThreadId = tid });
    internal void ThreadEnd(long qpc, DateTimeOffset at, int pid, int tid)
        => ThreadLifecycle(new Input { Kind = 6, Qpc = qpc, TimeTicks = at.UtcTicks, ProcessId = pid, ThreadId = tid });
    private void ThreadLifecycle(Input input)
        => Check(Native.Thread(current!.Wrapper, Tick, ref input), "deliver controlled thread lifecycle");
    // Processor is the raw event-header index; the real native processor hook maps it to a published ID.
    internal void CSwitch(long qpc, DateTimeOffset at, int processor, int oldPid, int oldTid, int pid, int tid, string? name = null)
        => Named(new() { Kind = 7, Qpc = qpc, TimeTicks = at.UtcTicks, Processor = processor, OldProcessId = oldPid,
            OldThreadId = oldTid, ProcessId = pid, ThreadId = tid }, name, contextSwitch: true);
    internal void CSwitchFromThreadIds(long qpc, DateTimeOffset at, int rawProcessorIndex, int oldTid, int tid)
        => Named(new() { Kind = 7, Qpc = qpc, TimeTicks = at.UtcTicks, Processor = rawProcessorIndex,
            OldThreadId = oldTid, ThreadId = tid }, null, contextSwitch: true, resolveThreads: true);

    internal void Publish(long tick)
    {
        Tick = tick;
        using var read = Session.AcquireReadLease();
        current!.Publish(tick, originUtc.AddMilliseconds(tick - Configuration.StartedTickMilliseconds).UtcDateTime.ToFileTimeUtc());
    }
    internal DiagnosticUsageSnapshot DiagnosticUsage()
    {
        using var read = Session.AcquireReadLease();
        Check(Native.DiagnosticUsage(read.OwnerHandle, out var value), "read published diagnostic usage");
        return new(value.NativeReads, value.OpenHandles, value.PeakHandles);
    }
    internal uint PendingDiagnosticSamples
    {
        get
        {
            using var read = Session.AcquireReadLease();
            Check(Native.PendingDiagnosticSamples(read.OwnerHandle, out var count), "read pending diagnostic commands");
            return count;
        }
    }
    internal readonly record struct DiagnosticUsageSnapshot(ulong NativeReads, ulong OpenHandles, ulong PeakHandles);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct ScriptedInputAllocationUsage(ulong CurrentBytes, ulong PeakBytes, ulong ByteCap, ulong FailedAllocations);
    internal ScriptedInputAllocationUsage? ScriptedInputUsage()
    {
        using var read = Session.AcquireReadLease();
        var result = Native.DiagnosticInputUsage(current!.Wrapper, out var usage);
        if (result == 3) return null; // Recorded/Windows sources own no scripted input allocation.
        Check(result, "read scripted input allocation usage");
        return usage;
    }
    internal sealed record DiagnosticProcessInput(uint ProcessId, long Birth, bool Running = true,
        bool CanOpen = true, bool CanOpenModules = true, bool CanReadBirth = true, bool InventoryAvailable = true);
    internal sealed record DiagnosticThreadInput(uint ProcessId, uint ThreadId, long Birth, long Kernel = 0, long User = 0,
        long Exit = 0, string? Description = null, int? DescriptionHResult = null, ulong? StartAddress = null,
        string? ModulePath = null, bool CanOpen = true, bool CanReadTimes = true, bool InInventory = true, int NativeError = 5,
        long? ProcessBirth = null);
    internal void ConfigureDiagnosticScript(long nowFiletime, DiagnosticProcessInput[] processes, DiagnosticThreadInput[] threads)
    {
        if (diagnosticSource != DiagnosticSourceMode.Scripted) throw new InvalidOperationException("Select the explicit scripted diagnostic source at construction.");
        using var read = Session.AcquireReadLease();
        Check(Native.DiagnosticClock(current!.Wrapper, nowFiletime), "set scripted diagnostic clock");
        foreach (var process in processes)
        {
            var row = new DiagnosticProcessRow { ProcessId = process.ProcessId, Birth = process.Birth,
                Flags = (process.CanOpen ? 1u : 0u) | (process.Running ? 2u : 0u) | (process.CanOpenModules ? 4u : 0u)
                    | (process.CanReadBirth ? 8u : 0u) | (process.InventoryAvailable ? 16u : 0u) };
            Check(Native.DiagnosticProcess(current.Wrapper, ref row), "set scripted process metadata");
        }
        foreach (var thread in threads)
        {
            fixed (char* description = thread.Description)
            fixed (char* module = thread.ModulePath)
            {
                var row = new DiagnosticThreadRow { ProcessId = thread.ProcessId, ThreadId = thread.ThreadId, Birth = thread.Birth,
                    Exit = thread.Exit, Kernel = thread.Kernel, User = thread.User, StartAddress = thread.StartAddress ?? 0,
                    Description = (nint)description, DescriptionLength = checked((uint)(thread.Description?.Length ?? 0)),
                    Module = (nint)module, ModuleLength = checked((uint)(thread.ModulePath?.Length ?? 0)),
                    DescriptionHResult = thread.DescriptionHResult ?? 0, NativeError = thread.NativeError,
                    ProcessBirth = thread.ProcessBirth ?? 0,
                    Flags = (thread.CanOpen ? 1u : 0u) | (thread.CanReadTimes ? 2u : 0u) | (thread.Description is not null ? 4u : 0u)
                        | (thread.DescriptionHResult is not null ? 8u : 0u) | (thread.StartAddress is not null ? 16u : 0u)
                        | (thread.ModulePath is not null ? 32u : 0u) | (thread.InInventory ? 64u : 0u) };
                Check(Native.DiagnosticThread(current!.Wrapper, ref row), "set scripted thread metadata");
            }
        }
    }
    internal void Raw(Guid provider, byte opcode, byte version, byte pointerSize, long qpc, ushort rawProcessorIndex,
        uint headerProcessId, uint headerThreadId, ReadOnlySpan<byte> payload)
    {
        using var read = Session.AcquireReadLease();
        Tick = checked(Configuration.StartedTickMilliseconds + (long)(((Int128)qpc - qpcOrigin) * 1000 / QpcFrequency));
        fixed (byte* bytes = payload)
        {
            var result = Native.Raw(current!.Wrapper, provider, opcode, version, pointerSize, qpc, rawProcessorIndex,
                headerProcessId, headerThreadId, bytes, checked((uint)payload.Length));
            if (result != 0) current!.Complete(new InvalidOperationException($"Raw native processing failed with result {result}."));
            Check(result, "deliver raw native record");
        }
    }
    internal void Disk(long tick, uint pid, int? disk, uint bytes, bool write, ulong? filetime = null)
    { Tick = tick; Check(Native.Disk(current!.Wrapper, tick, pid, disk ?? -1, bytes, write ? 1u : 0u, filetime ?? 0), "deliver controlled disk transfer"); }
    internal void Network(long tick, uint pid, uint bytes, bool send, bool udp, IPAddress address, ulong? filetime = null)
    {
        Tick = tick; var bytesAddress = address.GetAddressBytes();
        fixed (byte* pointer = bytesAddress)
            Check(Native.Network(current!.Wrapper, tick, pid, bytes, send ? 1u : 0u, udp ? 1u : 0u,
                pointer, checked((uint)bytesAddress.Length), filetime ?? 0), "deliver controlled network transfer");
    }
    internal void Image(long tick, ulong address, ulong length, string? path, bool unload = false)
    {
        Tick = tick;
        fixed (char* text = path)
            Check(Native.Image(current!.Wrapper, tick, address, length, text, checked((uint)(path?.Length ?? 0)), unload ? 1u : 0u), "deliver controlled image metadata");
    }
    internal void Interrupt(long tick, uint kind, double durationMilliseconds, ulong routine)
    { Tick = tick; Check(Native.Interrupt(current!.Wrapper, tick, kind, durationMilliseconds, routine), "deliver controlled interrupt"); }
    internal void Loss(long tick, ulong cumulative)
    { Tick = tick; Check(Native.Loss(current!.Wrapper, tick, cumulative), "deliver controlled cumulative event loss"); }
    internal void Fail(int result)
    {
        Check(Native.Fail(current!.Wrapper, result), "fail controlled native owner");
        current!.Complete(new InvalidOperationException($"Controlled native processing failed with result {result}."));
    }
    // Deliberate test interleaving: native failure is visible before the
    // ProcessTrace completion callback updates the managed resource snapshot.
    internal void RecordNativeFailureWithoutCompletion(int result)
        => Check(Native.Fail(current!.Wrapper, result), "latch native failure before managed completion");

    public void Dispose()
    {
        if (disposed) return;
        broker.Dispose();
        if (current is { Released: false })
        {
            var release = current.Stop(TimeSpan.Zero);
            if (!release.IsReleased) throw new InvalidOperationException(string.Join(" | ", release.Failures));
        }
        if (ActiveIdentityHandles != 0 || regions.GetUsage().CurrentBytes != 0)
            throw new InvalidOperationException("The controlled native fixture retained identity handles or region backing.");
        disposed = true;
    }

    private sealed class Factory(NativeKernelEtwTestResource owner) : IKernelEtwSessionRuntimeFactory
    {
        public void ValidateRequest(KernelEtwSubscriptionRequest request)
            => NativeKernelEtwSessionRuntime.ValidateNativeOutputs(request.Id, request.NativeOutputs);
        public bool TryGetAvailability(out string state, out string message) { state = "Available"; message = "controlled native fixture"; return true; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords, KernelEtwSessionContext context,
            IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            if (owner.current is { Released: false }) throw new InvalidOperationException("A prior test generation is still retained.");
            var next = new ControlledRuntime(owner, name, context);
            owner.current = next;
            try { next.Bind(subscriptions, context); owner.Creations++; return next; }
            catch { next.Dispose(); throw; }
        }
    }

    private sealed class ControlledRuntime : IKernelEtwSessionRuntime
    {
        private readonly NativeKernelEtwTestResource owner;
        private readonly IKernelEtwRegionLease memory;
        private readonly KernelEtwSubscriptionDispatcher dispatcher;
        private readonly Open open;
        private readonly Pid pid;
        private readonly Birth birth;
        private readonly Close close;
        private readonly Signal signal;
        private Action<Exception?>? completed;
        private EtwEventDropSession? eventDrops;
        private bool closing, callbackRemoved;
        internal nint Wrapper { get; private set; }
        internal NativeKernelEtwSession Session { get; }
        internal string DiagnosticName { get; }
        internal bool Released { get; private set; }
        public bool IsProcessing { get; private set; }
        public int EventsLost => Session.EventsLost;

        internal ControlledRuntime(NativeKernelEtwTestResource owner, string name, KernelEtwSessionContext context)
        {
            this.owner = owner;
            // Controlled resources are independent concurrent flows. The real
            // broker's process-wide session name cannot identify all of them
            // in the process-wide diagnostic catalog.
            DiagnosticName = $"{name}-controlled-{Guid.NewGuid():N}";
            var config = owner.Configuration;
            config.StampAbi(context);
            memory = owner.regions.Allocate("controlled-kernel-etw-generation", checked((nuint)config.ByteCap));
            open = (_, process) =>
            {
                if (!owner.Identities.TryGetValue(process, out var identity) || !identity.CanOpen) return 0;
                if (owner.ActiveIdentityHandles >= config.MaximumCachedProcesses) return 0;
                var handle = GCHandle.Alloc(new IdentityLease(process, identity));
                owner.ActiveIdentityHandles++; identity.Opens++;
                return GCHandle.ToIntPtr(handle);
            };
            pid = (_, handle) => ((IdentityLease)GCHandle.FromIntPtr(handle).Target!).ProcessId;
            birth = (_, handle) => ((IdentityLease)GCHandle.FromIntPtr(handle).Target!).ReadBirth();
            close = (_, handle) =>
            {
                var root = GCHandle.FromIntPtr(handle); ((IdentityLease)root.Target!).Identity.Closes++;
                root.Free(); owner.ActiveIdentityHandles--;
            };
            signal = _ =>
            {
                try
                {
                    foreach (var callback in (Action[])Field("publicationSignals").GetValue(Session)!)
                        try { callback(); } catch (Exception error) { owner.CallbackFailures.Add(error); }
                }
                catch (Exception error) { owner.CallbackFailures.Add(error); }
            };
            var callbacks = new IdentityCallbacks { Open = Marshal.GetFunctionPointerForDelegate(open), Pid = Marshal.GetFunctionPointerForDelegate(pid),
                Birth = Marshal.GetFunctionPointerForDelegate(birth), Close = Marshal.GetFunctionPointerForDelegate(close) };
            nint nativeOwner = 0;
            try
            {
                Check(Native.Create(ref config, memory.Pointer, checked((nuint)config.ByteCap), ref callbacks, out var wrapper, out nativeOwner), "create controlled shared owner");
                Wrapper = wrapper;
                Check(Native.Initialize(Wrapper, owner.qpcOrigin, owner.originUtc.UtcDateTime.ToFileTimeUtc(), (uint)owner.diagnosticSource), "initialize controlled native trace/source");
                memory.Complete();
                Session = new(new BorrowedRegion(memory.Pointer, memory.Length));
                Field("handle").SetValue(Session, nativeOwner);
                Session.InitializeCounterContract(config.MaximumCommands);
                Property("PublicationOriginUtc").SetValue(Session, owner.originUtc);
                Property("PublicationOriginTickMilliseconds").SetValue(Session, config.StartedTickMilliseconds);
                dispatcher = new(Session);
                Check(Native.Callback(nativeOwner, Marshal.GetFunctionPointerForDelegate(signal), 0), "bind controlled publication signal");
            }
            catch
            {
                if (nativeOwner != 0) Check(Native.CloseTrace(nativeOwner), "close failed controlled trace");
                if (Wrapper != 0) Check(Native.Destroy(Wrapper), "release failed controlled owner");
                memory.Dispose(); throw;
            }
        }
        internal void Bind(IReadOnlyList<KernelEtwSubscriptionRegistration> requests, KernelEtwSessionContext context)
            => dispatcher.InitializeSubscriptions(requests, context);
        public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> requests, KernelEtwSessionContext context)
            => dispatcher.UpdateSubscriptions(requests, context, value =>
            {
                if (owner.KeywordUpdateFailure is { } failure) throw failure;
                owner.KeywordUpdates.Add(value);
            });
        public void StartProcessing(Action<Exception?> completion)
        {
            eventDrops = dispatcher.RegisterDiagnostics(DiagnosticName, () => Session.EventsLost);
            completed = completion; IsProcessing = true;
        }
        internal void Complete(Exception? error)
        {
            if (!IsProcessing) return;
            IsProcessing = false;
            eventDrops?.Complete();
            ((IKernelEtwNativeProcessingCompletion)Session).OnProcessingCompleted(error);
            completed?.Invoke(error);
        }
        internal void Publish(long tick, long captured)
        {
            if (closing || Released) throw new ObjectDisposedException(nameof(ControlledRuntime));
            if (Session.ProcessingFailure is { } failure) throw new InvalidOperationException("Controlled processing has failed.", failure);
            Check(Native.Publish(Wrapper, tick, captured, out _), "publish controlled native bank");
            if (owner.CallbackFailures.Count != 0) throw new AggregateException(owner.CallbackFailures);
        }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout)
        {
            if (Released) return KernelEtwSessionStopResult.Released;
            if (!closing)
            {
                owner.Stops++; closing = true; dispatcher.CancelAll(); Complete(null);
                Field("traceClosed").SetValue(Session, true); // Prevent new borrows before draining retained readers.
            }
            if (!((IKernelEtwNativeReadDrain)Session).WaitForReaders(timeout))
                return new(true, false, true, ["controlled native read references remain retained"]);
            var nativeOwner = (nint)Field("handle").GetValue(Session)!;
            if (!callbackRemoved) { Check(Native.Callback(nativeOwner, 0, 0), "remove controlled signal"); callbackRemoved = true; }
            Check(Native.CloseTrace(nativeOwner), "close controlled native trace");
            var result = Native.Destroy(Wrapper);
            if (result != 0) return new(true, false, true, [$"controlled shared destroy returned {result}"]);
            Wrapper = 0; Field("handle").SetValue(Session, (nint)0); Field("disposed").SetValue(Session, true);
            dispatcher.ConfirmNativeRelease();
            Field("publicationSignals").SetValue(Session, Array.Empty<Action>());
            if (!Session.ProcessingCompletion.IsCompleted)
                ((IKernelEtwNativeProcessingCompletion)Session).OnProcessingCompleted(null);
            memory.Dispose(); Released = true;
            GC.KeepAlive(open); GC.KeepAlive(pid); GC.KeepAlive(birth); GC.KeepAlive(close); GC.KeepAlive(signal);
            return KernelEtwSessionStopResult.Released;
        }
        public void Dispose()
        {
            var result = Stop(TimeSpan.Zero);
            if (!result.IsReleased) throw new InvalidOperationException(string.Join(" | ", result.Failures));
        }
    }

    internal sealed class ScriptedIdentity(ulong birth, bool changeOnVerification = false)
    {
        internal ulong Birth = birth;
        internal bool CanOpen = true, CanReadBirth = true;
        internal uint? ReportedPid;
        internal int Opens, Reads, Closes;
        internal readonly bool ChangeOnVerification = changeOnVerification;
    }
    private sealed class IdentityLease(uint pid, ScriptedIdentity identity)
    {
        private int reads;
        internal ScriptedIdentity Identity => identity;
        internal uint ProcessId => identity.ReportedPid ?? pid;
        internal ulong ReadBirth()
        {
            identity.Reads++; reads++;
            return !identity.CanReadBirth ? 0 : identity.Birth + (identity.ChangeOnVerification && reads > 1 ? 1UL : 0UL);
        }
    }
    private sealed class BorrowedRegion(nint pointer, nuint length) : IKernelEtwRegionLease
    { public nint Pointer => pointer; public nuint Length => length; public void Complete() { } public void Dispose() { } }
    private static FieldInfo Field(string name) => typeof(NativeKernelEtwSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NativeKernelEtwSession).FullName, name);
    private static PropertyInfo Property(string name) => typeof(NativeKernelEtwSession).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(typeof(NativeKernelEtwSession).FullName, name);
    private static void Check(int result, string operation) { if (result != 0) throw new InvalidOperationException($"{operation}: native result {result}"); }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Kind, Flags;
        public long Qpc, TimeTicks;
        public int ProcessId, ThreadId, OldProcessId, OldThreadId, Processor;
        public uint Reserved;
        public ulong Unique;
        public long Start;
        public nint Name;
        public uint NameLength, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityCallbacks { public nint Context, Open, Pid, Birth, Close; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDiagnosticUsage { public ulong NativeReads, OpenHandles, PeakHandles; }
    [StructLayout(LayoutKind.Sequential)] private struct DiagnosticProcessRow { public uint ProcessId, Flags; public long Birth; }
    [StructLayout(LayoutKind.Sequential)] private struct DiagnosticThreadRow
    {
        public uint ProcessId, ThreadId, Flags; public int NativeError;
        public long Birth, Exit, Kernel, User; public ulong StartAddress;
        public nint Description, Module; public uint DescriptionLength, ModuleLength; public int DescriptionHResult; public uint Reserved;
        public long ProcessBirth;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoView
    {
        public long CapturedAt, StartTick, ThroughTick, Window;
        public uint DiskComplete, NetworkComplete, ProcessCount, DiskRowCount, DiskCount, NetworkCount;
        public nint Processes, Disks, DiskNumbers, Networks;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Open(nint context, uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Pid(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Birth(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Signal(nint context);
    private static class Native
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(ref NativeKernelEtwSessionConfig config, nint memory, nuint length, ref IdentityCallbacks callbacks, out nint wrapper, out nint owner);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern int Destroy(nint wrapper);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_snapshot", CallingConvention = CallingConvention.Cdecl)] internal static extern int Publish(nint wrapper, long tick, long captured, out IoView view);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_fail", CallingConvention = CallingConvention.Cdecl)] internal static extern int Fail(nint wrapper, int result);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_submit", CallingConvention = CallingConvention.Cdecl)] internal static extern int Submit(nint owner, Input* input, uint count);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_publication_callback", CallingConvention = CallingConvention.Cdecl)] internal static extern int Callback(nint owner, nint callback, nint context);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_close_trace", CallingConvention = CallingConvention.Cdecl)] internal static extern int CloseTrace(nint owner);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_initialize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Initialize(nint wrapper, long qpcOrigin, long filetimeOrigin, uint diagnosticSource);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_raw", CallingConvention = CallingConvention.Cdecl)] internal static extern int Raw(nint wrapper, Guid provider, byte opcode, byte version, byte pointerSize, long qpc, ushort processor, uint pid, uint tid, byte* payload, uint length);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_diagnostic_usage", CallingConvention = CallingConvention.Cdecl)] internal static extern int DiagnosticUsage(nint owner, out NativeDiagnosticUsage usage);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_diagnostic_clock", CallingConvention = CallingConvention.Cdecl)] internal static extern int DiagnosticClock(nint wrapper, long nowFiletime);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_diagnostic_process", CallingConvention = CallingConvention.Cdecl)] internal static extern int DiagnosticProcess(nint wrapper, ref DiagnosticProcessRow process);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_diagnostic_thread", CallingConvention = CallingConvention.Cdecl)] internal static extern int DiagnosticThread(nint wrapper, ref DiagnosticThreadRow thread);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_pending_diagnostic_samples", CallingConvention = CallingConvention.Cdecl)] internal static extern int PendingDiagnosticSamples(nint owner, out uint count);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_diagnostic_input_usage", CallingConvention = CallingConvention.Cdecl)] internal static extern int DiagnosticInputUsage(nint wrapper, out ScriptedInputAllocationUsage usage);
        // The following normalized adapters are test-only draft additions, listed in the manifest.
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_disk", CallingConvention = CallingConvention.Cdecl)] internal static extern int Disk(nint wrapper, long tick, uint pid, int disk, uint bytes, uint write, ulong filetime);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_network", CallingConvention = CallingConvention.Cdecl)] internal static extern int Network(nint wrapper, long tick, uint pid, uint bytes, uint send, uint udp, byte* address, uint length, ulong filetime);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int Image(nint wrapper, long tick, ulong address, ulong length, char* path, uint pathLength, uint unload);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_interrupt", CallingConvention = CallingConvention.Cdecl)] internal static extern int Interrupt(nint wrapper, long tick, uint kind, double duration, ulong routine);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_loss", CallingConvention = CallingConvention.Cdecl)] internal static extern int Loss(nint wrapper, long tick, ulong cumulative);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_thread", CallingConvention = CallingConvention.Cdecl)] internal static extern int Thread(nint wrapper, long tick, ref Input input);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_process", CallingConvention = CallingConvention.Cdecl)] internal static extern int Process(nint wrapper, long tick, ref Input input);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_cswitch", CallingConvention = CallingConvention.Cdecl)] internal static extern int CSwitch(nint wrapper, long tick, ref Input input, uint resolveThreads);
    }
}
