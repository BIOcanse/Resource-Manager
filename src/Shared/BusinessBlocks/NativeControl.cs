using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Explicit creation limits. Production supplies these from its freedom points.</summary>
public sealed record NativeControlOptions
{
    public required uint Profile { get; init; }
    public required int ShutdownItemUid { get; init; }
    public int WorkerCount { get; init; } = 4;
    public int HistoryCapacity { get; init; } = 128;
    public int CompletionCapacity { get; init; } = 128;
    public int ReaderCapacity { get; init; } = 16;
    public int ReaderBytes { get; init; } = 4096;
    public int WorkerArenaBytes { get; init; } = 65536;
    public int ReasonTextCapacity { get; init; } = 256;
}

/// <summary>Host shell only: native code owns scheduling and Boolean publications.</summary>
public sealed partial class NativeControl : IBusinessBlockRuntime, IDisposable
{
    // The shell must be able to report its longest lifecycle reason even when construction fails.
    public const int MinimumReasonTextCapacity = 29; // UTF-8 bytes: "native-control" + "creation-failed".
    private readonly ControlHandle handle;
    private readonly ILogger logger;
    private readonly NativeControlOptions options;
    private GCHandle context;
    private readonly ConcurrentDictionary<(int, ulong), Run> runs = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> completions = new();
    private readonly object readersSync = new();
    private readonly object schedulerShutdownSync = new();
    private readonly object disposeSync = new();
    private readonly Dictionary<ulong, Func<BusinessBlockReason, Task>> readers = new();
    private readonly List<GCHandle> readFunctions = new();
    private Task? schedulerShutdownTask;
    private Task? disposeTask;
    private Func<int, IBusinessBlock>? create;
    private Func<long> planVersion = static () => 0;
    private long nextToken;
    private int disposed;
    private bool activated;
    private StateAuthority? authority;
    [ThreadStatic] private static Publication? publication;
    public BusinessBlockGraph Graph { get; }
    public StateAuthority Authority => authority ?? throw new InvalidOperationException("The host state directory has not been attached.");
    internal ControlHandle Handle => handle;
    public long CurrentPlanVersion => planVersion();

    /// <summary>Reads the native tree's exact runnable UID list before creating its controller.</summary>
    public static unsafe int[] GetProfileHostBlockUids(uint profile)
    {
        if (AbiVersion() != 0x0002_0000) throw new InvalidOperationException("The native controller ABI version does not match.");
        var result = ProfileHostBlocksNative(profile, null, 0, out var needed);
        if (result != 5) Check(result); // BufferTooSmall is the size-query result.
        var nativeUids = new uint[checked((int)needed)];
        fixed (uint* buffer = nativeUids)
        {
            Check(ProfileHostBlocksNative(profile, buffer, (nuint)nativeUids.Length, out var written));
            if (written != needed) throw new InvalidOperationException("The native profile UID count changed during the query.");
        }
        return nativeUids.Select(uid => checked((int)uid)).ToArray();
    }

    public unsafe NativeControl(IEnumerable<int> hostUids, NativeControlOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(hostUids);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReasonTextCapacity, MinimumReasonTextCapacity, nameof(options.ReasonTextCapacity));
        this.options = options;
        this.logger = logger;
        if (AbiVersion() != 0x0002_0000) throw new InvalidOperationException("The native controller ABI version does not match.");
        var ids = hostUids.Select(id => checked((uint)id)).ToArray();
        context = GCHandle.Alloc(this);
        try
        {
            var callbacks = new HostCallbacks { Context = GCHandle.ToIntPtr(context), Start = &OnStart, Stop = &OnStop, Complete = &OnComplete, Log = &OnLog, ItemChanged = &OnItemChanged };
            fixed (uint* blocks = ids)
            {
                var config = new Config
                {
                    StructSize = (uint)sizeof(Config), Profile = options.Profile,
                    WorkerCount = checked((uint)options.WorkerCount), HistoryCapacity = checked((uint)options.HistoryCapacity),
                    CompletionCapacity = checked((uint)options.CompletionCapacity), ReaderCapacity = checked((uint)options.ReaderCapacity),
                    ReaderBytes = checked((uint)options.ReaderBytes), WorkerArenaBytes = checked((uint)options.WorkerArenaBytes),
                    ReasonTextCapacity = checked((uint)options.ReasonTextCapacity),
                    ShutdownItemUid = checked((uint)options.ShutdownItemUid), HostBlocks = blocks, HostBlockCount = (uint)ids.Length
                };
                Check(Create(in config, in callbacks, out var raw));
                handle = new(raw);
            }
            var snapshot = SnapshotJson();
            var program = snapshot.GetProperty("program").GetInt32();
            Graph = new(program, snapshot.GetProperty("blocks").EnumerateArray()
                .Where(row => row.GetProperty("uid").GetInt32() != program)
                .Select(row => new BusinessBlockNode(row.GetProperty("uid").GetInt32(), row.GetProperty("parent").GetInt32(),
                    row.GetProperty("inputs").EnumerateArray().Select(input => input.GetInt32()).ToArray())));
        }
        catch
        {
            if (handle is not null && !handle.IsInvalid)
            {
                // No run has been dispatched. A failed shell construction still releases native tables.
                var why = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "native-control", "creation-failed", 0);
                try { ShutdownAsync(why).GetAwaiter().GetResult(); } catch { }
                handle.Dispose();
            }
            context.Free();
            throw;
        }
    }
    public StateAuthority CreateAuthority(IEnumerable<StateItemDeclaration> declarations, IEnumerable<int> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var declaredBlocks = blocks.ToHashSet();
        var actualBlocks = Graph.BlockIds.Prepend(Graph.Program).ToArray();
        if (actualBlocks.Any(uid => !declaredBlocks.Contains(uid)))
            throw new ArgumentException("The host declarations omit blocks in the selected native tree.", nameof(blocks));
        // A catalog can include blocks used only by another startup profile. Keys mirror this created tree.
        var value = new StateAuthority(declarations, actualBlocks, this);
        AttachAuthority(value);
        return value;
    }
    internal void AttachAuthority(StateAuthority value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Runtime, this)) throw new ArgumentException("The state directory must belong to this native controller.", nameof(value));
        if (authority is not null && !ReferenceEquals(authority, value)) throw new InvalidOperationException("A native controller has exactly one host state directory.");
        authority = value;
    }
    public void Activate(Func<int, IBusinessBlock> runFactory, Func<long>? currentPlanVersion = null)
    {
        ArgumentNullException.ThrowIfNull(runFactory);
        if (activated) throw new InvalidOperationException("The controller is already active.");
        create = runFactory;
        planVersion = currentPlanVersion ?? (static () => 0);
        activated = true;
        try { Check(ActivateNative(handle)); }
        catch { activated = false; throw; }
    }
    internal nint Claim(StateItemKey key, StateItemStorage storage)
    {
        ThrowIfDisposed();
        nint claim;
        Check(storage == StateItemStorage.Own ? ClaimWrite(handle, Key(key), out claim) : ClaimCell(handle, Key(key), out claim));
        return claim;
    }
    internal StateRead<bool> ReadBool(StateItemKey key)
    {
        Check(ReadNative(handle, Key(key), out var read));
        return read switch
        {
            0 => StateRead<bool>.NoValue(NoValueReason.NotPublished), 1 => StateRead<bool>.NoValue(NoValueReason.OwnerStopped),
            2 => StateRead<bool>.NoValue(NoValueReason.ReadFailed), 3 => StateRead<bool>.FromValue(false), 4 => StateRead<bool>.FromValue(true),
            _ => throw new InvalidOperationException("The native controller returned an invalid Boolean publication.")
        };
    }
    internal unsafe nint ClaimReadFunction(StateItemKey key, Func<StateRead<bool>> read)
    {
        ThrowIfDisposed();
        var callback = GCHandle.Alloc(read);
        try
        {
            var function = new ReadFunction { Context = GCHandle.ToIntPtr(callback), Read = &OnReadFunction };
            Check(ClaimReadFunctionNative(handle, Key(key), in function, out var claim));
            lock (readFunctions) readFunctions.Add(callback);
            return claim;
        }
        catch { callback.Free(); throw; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnReadFunction(nint context, FunctionRead* output)
    {
        try
        {
            var read = ((Func<StateRead<bool>>)GCHandle.FromIntPtr(context).Target!)();
            *output = new() { Kind = read.HasValue ? 3u : (uint)read.NoValueReason!.Value, Value = read.HasValue && read.Value ? 1u : 0u };
        }
        catch { *output = new() { Kind = 2 }; }
    }
    internal unsafe Task NotifyChanged(nint claim, BusinessBlockReason reason)
        => Wake(reason, (h, why, token) => NotifyChangedNative(h, claim, why, token));
    internal unsafe Task PublishBool(nint claim, StateRead<bool> value, BusinessBlockReason why)
        => Wake(why, (h, reason, token) => Publish(h, claim, value.HasValue ? value.Value ? 4u : 3u : (uint)value.NoValueReason!.Value, reason, token));
    internal unsafe Task PublishPresence(nint claim, NoValueReason? absence, BusinessBlockReason why)
        => Wake(why, (h, reason, token) => PublishPresenceNative(h, claim, absence is null ? 0u : (uint)absence.Value + 1, reason, token));
    internal unsafe Task Bind(nint claim, BusinessBlockReason why) => Wake(why, (h, reason, token) => BindNative(h, claim, reason, token));
    internal unsafe Task Unbind(nint claim, BusinessBlockReason why) => Wake(why, (h, reason, token) => UnbindNative(h, claim, reason, token));
    public unsafe Task ShutdownAsync(BusinessBlockReason why) => Wake(why, static (h, reason, token) => Shutdown(h, reason, token));
    internal Task RequestSchedulerShutdownAsync()
    {
        lock (schedulerShutdownSync)
        {
            if (schedulerShutdownTask is not null) return schedulerShutdownTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            schedulerShutdownTask = completion.Task;
            try
            {
                var shutdown = ShutdownAsync(new(BusinessBlockRequestKind.Configuration,
                    Graph.Program.ToString(System.Globalization.CultureInfo.InvariantCulture), "dispose", 0));
                _ = CompleteSchedulerShutdownAsync(shutdown, completion);
            }
            catch (Exception error) { completion.TrySetException(error); }
            return schedulerShutdownTask;
        }
    }
    private static async Task CompleteSchedulerShutdownAsync(Task shutdown, TaskCompletionSource completion)
    {
        try { await shutdown.ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    public BusinessBlockState ReadState(int id) { ThrowIfDisposed(); _ = Graph.Get(id); Check(ReadStateNative(handle, (uint)id, out var state)); return (BusinessBlockState)state; }
    internal unsafe IDisposable Subscribe(StateItemKey key, string holder, Func<BusinessBlockReason, Task> reader)
    {
        ThrowIfDisposed(); ArgumentException.ThrowIfNullOrWhiteSpace(holder); ArgumentNullException.ThrowIfNull(reader);
        var bytes = Encoding.UTF8.GetBytes(holder);
        lock (readersSync) fixed (byte* text = bytes)
        {
            Check(RegisterReader(handle, Key(key), text, checked((uint)bytes.Length), out var id));
            readers.Add(id, reader);
            return new ReaderRegistration(this, id);
        }
    }
    private void Unregister(ulong id)
    {
        lock (readersSync) { if (!readers.Remove(id)) return; Check(UnregisterReader(handle, id)); }
    }
    private sealed class ReaderRegistration(NativeControl owner, ulong id) : IDisposable
    {
        private int removed;
        public void Dispose() { if (Interlocked.Exchange(ref removed, 1) == 0) owner.Unregister(id); }
    }
    private unsafe delegate int Mutation(ControlHandle handle, ReasonView* reason, ulong token);
    private unsafe Task Wake(BusinessBlockReason why, Mutation mutation)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(why);
        if (!Enum.IsDefined(why.Kind) || string.IsNullOrWhiteSpace(why.Owner) || string.IsNullOrWhiteSpace(why.ConfigurationOrDependency))
            throw new ArgumentException("An update needs its kind, owner and cause.", nameof(why));
        var token = activated ? checked((ulong)Interlocked.Increment(ref nextToken)) : 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (token != 0) completions.TryAdd(token, completion); else completion.SetResult();
        var outer = publication;
        var current = new Publication(this);
        publication = current;
        try
        {
            var ownerBytes = Encoding.UTF8.GetBytes(why.Owner); var causeBytes = Encoding.UTF8.GetBytes(why.ConfigurationOrDependency);
            fixed (byte* owner = ownerBytes) fixed (byte* cause = causeBytes)
            {
                var reason = new ReasonView { Kind = (uint)why.Kind, Owner = owner, OwnerLength = (nuint)ownerBytes.Length,
                    Cause = cause, CauseLength = (nuint)causeBytes.Length, PlanVersion = unchecked((ulong)why.PlanVersion) };
                Check(mutation(handle, &reason, token));
            }
        }
        catch { if (token != 0) completions.TryRemove(token, out _); throw; }
        finally { publication = outer; }
        return current.Tasks is { Count: > 0 } tasks ? Task.WhenAll(tasks.Prepend(completion.Task)) : completion.Task;
    }
    private sealed class Publication(NativeControl owner)
    {
        internal readonly NativeControl Owner = owner;
        internal List<Task>? Tasks;
        internal void Add(Task task) => (Tasks ??= []).Add(task);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
