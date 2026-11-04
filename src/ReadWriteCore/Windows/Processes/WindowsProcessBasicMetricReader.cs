using ResourceManager.App.Infrastructure.ResourceBreakdown;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed record ProcessBasicMetricRequest(int ProcessId, long CreationFileTime100Ns, ProcessMetricReadMask Mask);

public sealed record ProcessMemoryObservation(ulong WorkingSet, ulong PrivateCommit, ulong PeakPrivateCommit, ulong? PrivateWorkingSet);
public sealed record ProcessIoQueryTiming(long QueryStartTicks, long QueryReturnTicks, long TicksPerSecond);
public sealed record ProcessIoObservation(ulong ReadOperations, ulong WriteOperations, ulong OtherOperations, ulong ReadBytes, ulong WriteBytes, ulong OtherBytes)
{
    public ProcessIoQueryTiming? Timing { get; init; }
}
public sealed record ProcessBasicMetricObservation(UInt128? CpuTime100Ns, ProcessMemoryObservation? Memory, ProcessIoObservation? Io, uint? HandleCount)
{
    public long? CreationFileTime100Ns { get; init; }
    public string? ImagePath { get; init; }
    public uint? ParentPid { get; init; }
    public string? UserSid { get; init; }
    public uint? ThreadCount { get; init; }
    public string? CommandLine { get; init; }
    internal static readonly ProcessBasicMetricObservation Unavailable = new(null, null, null, null);
}

internal interface IWindowsProcessBasicMetricApi
{
    IntPtr Open(int processId);
    IntPtr Open(int processId, ProcessMetricReadMask mask) => Open(processId);
    bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user);
    ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet);
    ProcessIoObservation? Io(IntPtr handle);
    uint? HandleCount(IntPtr handle);
    uint? ThreadCount(IntPtr handle, int processId) => null;
    string? ImagePath(IntPtr handle) => null;
    string? CommandLine(IntPtr handle) => null;
    uint? ParentPid(IntPtr handle) => null;
    string? UserSid(IntPtr handle) => null;
    IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
        => new Dictionary<int, uint?>();
    bool ThreadCountLive(IntPtr handle, int pid) => true;
    bool Close(IntPtr handle);
}

/// <summary>Explicit read of the selected counters for one process creation identity.
/// Reads open, verify and release their own handle. The original process field owner
/// retains Fixed fields; this raw reader retains no per-instance state.</summary>
public sealed class WindowsProcessBasicMetricReader
{
    private readonly IWindowsProcessBasicMetricApi api;
    private readonly TimeProvider clock;
    public WindowsProcessBasicMetricReader() : this(new WindowsProcessBasicMetricNativeApi()) { }

    internal WindowsProcessBasicMetricReader(IWindowsProcessBasicMetricApi api, TimeProvider? clock = null)
    { this.api = api; this.clock = clock ?? TimeProvider.System; }

    /// <summary>Reads the current creation time for a PID without allocating a Process wrapper or observation.</summary>
    internal long? ReadCurrentCreationFileTime(int processId)
    {
        if (processId <= 0) return null;
        var handle = IntPtr.Zero;
        var released = true;
        long? creation = null;
        try
        {
            handle = api.Open(processId, ProcessMetricReadMask.StartTime);
            if (ValidHandle(handle) && api.Times(handle, out var created, out _, out _)
                && created is > 0 and <= long.MaxValue)
                creation = (long)created;
        }
        catch (Exception error) when (IsSourceFailure(error)) { }
        finally
        {
            if (ValidHandle(handle))
            {
                try { released = api.Close(handle); }
                catch (Exception error) when (IsSourceFailure(error)) { released = false; }
            }
        }
        return released ? creation : null;
    }

    public ProcessBasicMetricObservation Read(int processId, long creationFileTime, ProcessMetricReadMask mask)
        => mask.HasFlag(ProcessMetricReadMask.ThreadCount)
            ? ReadBatch([new(processId, creationFileTime, mask)])[0]
            : ReadSingle(processId, creationFileTime, mask);

    /// <summary>Reads a complete selected batch with at most one process snapshot. Handles and
    /// observations belong only to this call; no directory, baseline or current is retained.</summary>
    public IReadOnlyList<ProcessBasicMetricObservation> ReadBatch(IReadOnlyList<ProcessBasicMetricRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var prepared = new Prepared[requests.Count];
        var result = Enumerable.Repeat(ProcessBasicMetricObservation.Unavailable, requests.Count).ToArray();
        try
        {
            // All selected creation handles are established before the shared snapshot.
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i]; var item = new Prepared(request); prepared[i] = item;
                if (request.Mask == ProcessMetricReadMask.None || request.ProcessId <= 0 || request.CreationFileTime100Ns <= 0) continue;
                try
                {
                    item.Handle = api.Open(request.ProcessId, request.Mask);
                    item.Valid = ValidHandle(item.Handle) && api.Times(item.Handle, out item.Created, out item.Kernel, out item.User)
                        && item.Created == (ulong)request.CreationFileTime100Ns;
                }
                catch (Exception error) when (IsSourceFailure(error)) { }
            }
            IReadOnlyDictionary<int, uint?> threads = new Dictionary<int, uint?>();
            var threadHandles = prepared.Where(item => item.Valid && item.Request.Mask.HasFlag(ProcessMetricReadMask.ThreadCount))
                .GroupBy(item => item.Request.ProcessId).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Handle);
            if (threadHandles.Count != 0)
            {
                try { threads = api.ThreadCounts(threadHandles); }
                catch (Exception error) when (IsSourceFailure(error)) { }
            }
            for (var i = 0; i < prepared.Length; i++)
            {
                var item = prepared[i]; if (!item.Valid) continue;
                var request = item.Request;
                try
                {
                    result[i] = ReadSelected(item.Handle, request.ProcessId, request.CreationFileTime100Ns, request.Mask,
                        item.Kernel, item.User, threads.GetValueOrDefault(request.ProcessId));
                }
                catch (Exception error) when (IsSourceFailure(error)) { }
            }

        }
        finally
        {
            for (var i = 0; i < prepared.Length; i++)
            {
                var item = prepared[i]; if (item is null || !ValidHandle(item.Handle)) continue;
                try { if (!api.Close(item.Handle)) result[i] = ProcessBasicMetricObservation.Unavailable; }
                catch (Exception error) when (IsSourceFailure(error)) { result[i] = ProcessBasicMetricObservation.Unavailable; }
            }
        }
        return result;
    }
    // Retain the original single-handle hot path for every non-thread selection.
    private ProcessBasicMetricObservation ReadSingle(int processId, long creationFileTime, ProcessMetricReadMask mask)
    {
        if (mask == ProcessMetricReadMask.None || processId <= 0 || creationFileTime <= 0) return ProcessBasicMetricObservation.Unavailable;
        var handle = IntPtr.Zero; var released = true; var value = ProcessBasicMetricObservation.Unavailable;
        try
        {
            handle = api.Open(processId, mask);
            if (ValidHandle(handle) && api.Times(handle, out var created, out var kernel, out var user) && created == (ulong)creationFileTime)
                value = ReadSelected(handle, processId, creationFileTime, mask, kernel, user, null);
        }
        catch (Exception error) when (IsSourceFailure(error)) { }
        finally
        {
            if (ValidHandle(handle))
            {
                try { released = api.Close(handle); }
                catch (Exception error) when (IsSourceFailure(error)) { released = false; }
            }
        }
        return released ? value : ProcessBasicMetricObservation.Unavailable;
    }
    private ProcessBasicMetricObservation ReadSelected(IntPtr handle, int processId, long creationFileTime,
        ProcessMetricReadMask mask, ulong kernel, ulong user, uint? threadCount)
    {
        var memory = mask.HasFlag(ProcessMetricReadMask.Memory) || mask.HasFlag(ProcessMetricReadMask.PrivateWorkingSet)
            ? Consistent(ReadCategory(() => api.Memory(handle, mask.HasFlag(ProcessMetricReadMask.PrivateWorkingSet)))) : null;
        var io = mask.HasFlag(ProcessMetricReadMask.Io) ? ReadCategory(() => ReadIo(handle)) : null;
        var handles = mask.HasFlag(ProcessMetricReadMask.HandleCount) ? ReadHandleCount(handle) : null;
        var image = mask.HasFlag(ProcessMetricReadMask.ImagePath) ? ReadCategory(() => api.ImagePath(handle)) : null;
        var sid = mask.HasFlag(ProcessMetricReadMask.UserSid) ? ReadCategory(() => api.UserSid(handle)) : null;
        var commandLine = mask.HasFlag(ProcessMetricReadMask.CommandLine) ? ReadCategory(() => api.CommandLine(handle)) : null;
        var parent = mask.HasFlag(ProcessMetricReadMask.ParentPid) ? ReadParentPid(handle) : null;
        // No second creation check: the open handle pins the process object, so its PID cannot be reused and
        // its creation time cannot change after the check before these reads.
        if (mask.HasFlag(ProcessMetricReadMask.ThreadCount))
        {
            try { if (!api.ThreadCountLive(handle, processId)) threadCount = null; }
            catch (Exception error) when (IsSourceFailure(error)) { threadCount = null; }
        }
        return new(mask.HasFlag(ProcessMetricReadMask.CpuTime) ? (UInt128)kernel + user : null, memory, io, handles)
        {
            CreationFileTime100Ns = mask.HasFlag(ProcessMetricReadMask.StartTime) ? creationFileTime : null,
            ImagePath = string.IsNullOrWhiteSpace(image) ? null : image,
            CommandLine = commandLine, ParentPid = parent, ThreadCount = threadCount, UserSid = string.IsNullOrWhiteSpace(sid) ? null : sid
        };
    }
    private ProcessIoObservation? ReadIo(IntPtr handle)
    {
        var frequency = clock.TimestampFrequency;
        var start = clock.GetTimestamp();
        var value = api.Io(handle);
        var end = clock.GetTimestamp();
        // Query-return is the reference point. The single-call bracket validates
        // clock continuity; its duration is never used as the rate denominator.
        return value is null ? null : value with { Timing = frequency > 0 && frequency == clock.TimestampFrequency
            && start >= 0 && end >= start ? new(start, end, frequency) : null };
    }

    /// <summary>A private working set above the working set is not a valid reading of either source.</summary>
    internal static ProcessMemoryObservation? Consistent(ProcessMemoryObservation? memory)
        => memory is { PrivateWorkingSet: { } resident } && resident > memory.WorkingSet ? memory with { PrivateWorkingSet = null } : memory;

    private static bool ValidHandle(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);
    private sealed class Prepared(ProcessBasicMetricRequest request)
    {
        internal readonly ProcessBasicMetricRequest Request = request;
        internal IntPtr Handle; internal bool Valid; internal ulong Created, Kernel, User;
    }

    private static T? ReadCategory<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    private uint? ReadHandleCount(IntPtr handle)
    {
        try { return api.HandleCount(handle); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    private uint? ReadParentPid(IntPtr handle)
    {
        try { return api.ParentPid(handle); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    private static bool IsSourceFailure(Exception error) => error is Win32Exception or InvalidOperationException or ExternalException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException
        or UnauthorizedAccessException or ArgumentException;
}
