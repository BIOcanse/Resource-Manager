using ResourceManager.ReadWrite.Windows.Processes;
using static Resource_Manager_APP.Tests.KernelEtwFixtures.NativeKernelEtwTestResource;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>Scripted OS answers only. The retained C# owner still performs all baselines, joins and decisions.</summary>
internal sealed class ScriptedOracleThreadDiagnosticSource : IOracleThreadDiagnosticSource
{
    private sealed record Handle(uint Pid, long ProcessBirth, uint? Tid = null, long ThreadBirth = 0);
    private readonly Dictionary<(uint Pid, long Birth), DiagnosticProcessInput> processes = [];
    private readonly Dictionary<uint, long> currentProcesses = [];
    private readonly Dictionary<(uint Pid, long ProcessBirth, uint Tid, long Birth), DiagnosticThreadInput> threads = [];
    private readonly Dictionary<(uint Pid, long ProcessBirth, uint Tid), long> currentThreads = [];
    private readonly Dictionary<IntPtr, Handle> handles = [];
    private long nextHandle;
    public DateTimeOffset UtcNow { get; private set; }
    public int LastError { get; private set; } = 5;
    internal int OpenHandles => handles.Count;

    internal void SetClock(long filetime) => UtcNow = DateTimeOffset.FromFileTime(filetime);
    internal void Configure(long now, DiagnosticProcessInput[] processRows, DiagnosticThreadInput[] threadRows)
    {
        SetClock(now);
        foreach (var row in processRows)
        {
            if (currentProcesses.TryGetValue(row.ProcessId, out var previous) && previous != row.Birth)
                processes[(row.ProcessId, previous)] = processes[(row.ProcessId, previous)] with { Running = false };
            processes[(row.ProcessId, row.Birth)] = row;
            currentProcesses[row.ProcessId] = row.Birth;
        }
        foreach (var row in threadRows)
        {
            var processBirth = row.ProcessBirth ?? currentProcesses[row.ProcessId];
            var value = row with { ProcessBirth = processBirth };
            threads[(row.ProcessId, processBirth, row.ThreadId, row.Birth)] = value;
            currentThreads[(row.ProcessId, processBirth, row.ThreadId)] = row.Birth;
        }
    }
    private IntPtr Retain(Handle value)
    {
        var handle = new IntPtr(checked(++nextHandle));
        handles.Add(handle, value);
        return handle;
    }
    public IntPtr OpenProcess(uint access, int pid)
    {
        if (!currentProcesses.TryGetValue((uint)pid, out var birth)) return IntPtr.Zero;
        var row = processes[((uint)pid, birth)];
        if (!row.CanOpen || ((access & 0x10) != 0 && !row.CanOpenModules)) return IntPtr.Zero;
        return Retain(new((uint)pid, birth));
    }
    public bool TryGetCreationFileTime(IntPtr process, out ulong creation)
    {
        var handle = handles[process]; var value = processes[(handle.Pid, handle.ProcessBirth)];
        creation = value.CanReadBirth ? checked((ulong)value.Birth) : 0;
        return value.CanReadBirth;
    }
    public bool IsRunning(IntPtr process)
    {
        var handle = handles[process];
        return processes[(handle.Pid, handle.ProcessBirth)].Running;
    }
    public bool CloseHandle(IntPtr handle) => handles.Remove(handle);
    public bool TryReadModules(IntPtr process, out IReadOnlyList<WindowsProcessLoadedModule> modules)
    {
        var handle = handles[process];
        modules = threads.Where(pair => pair.Key.Pid == handle.Pid && pair.Key.ProcessBirth == handle.ProcessBirth)
            .Select(pair => pair.Value).Where(row => row.StartAddress.HasValue && row.ModulePath is not null)
            .Select(row => new WindowsProcessLoadedModule(row.StartAddress!.Value, row.ModulePath!) { ImageSizeBytes = 1 })
            .DistinctBy(row => (row.BaseAddress, row.Path)).ToArray();
        return true;
    }
    public bool TryReadInventory(int pid, long creation, out IReadOnlyList<OracleWindowsSystemCpuThread> rows)
    {
        rows = [];
        if (!processes.TryGetValue(((uint)pid, creation), out var process) || !process.InventoryAvailable) return false;
        rows = currentThreads.Where(pair => pair.Key.Pid == (uint)pid && pair.Key.ProcessBirth == creation)
            .Select(pair => threads[(pair.Key.Pid, pair.Key.ProcessBirth, pair.Key.Tid, pair.Value)])
            .Where(row => row.InInventory)
            .Select(row => new OracleWindowsSystemCpuThread((int)row.ThreadId, row.Birth, row.Kernel, row.User, row.StartAddress ?? 0)).ToArray();
        return true;
    }
    public IntPtr OpenDiagnosticThread(int pid, int tid, long birth, out long creation, out long kernel, out long user, out int error)
    {
        creation = kernel = user = 0; error = 5;
        if (!currentProcesses.TryGetValue((uint)pid, out var processBirth)
            || !currentThreads.TryGetValue(((uint)pid, processBirth, (uint)tid), out var threadBirth)) return IntPtr.Zero;
        var row = threads[((uint)pid, processBirth, (uint)tid, threadBirth)];
        if (!row.CanOpen || !row.CanReadTimes) { error = row.NativeError; LastError = error; return IntPtr.Zero; }
        if (birth != 0 && birth != row.Birth) { error = 1168; LastError = error; return IntPtr.Zero; }
        creation = row.Birth; kernel = row.Kernel; user = row.User; error = 0;
        return Retain(new((uint)pid, processBirth, (uint)tid, row.Birth));
    }
    private DiagnosticThreadInput Thread(IntPtr thread)
    {
        var handle = handles[thread];
        return threads[(handle.Pid, handle.ProcessBirth, handle.Tid!.Value, handle.ThreadBirth)];
    }
    public (string? Name, int? Error) ReadDiagnosticName(IntPtr thread)
    {
        var row = Thread(thread);
        if (row.DescriptionHResult is { } error && error < 0) return (null, error);
        return (string.IsNullOrEmpty(row.Description) ? null : row.Description, null);
    }
    public ulong? ReadDiagnosticStartAddress(IntPtr thread) => Thread(thread).StartAddress;
    public bool ReadDiagnosticTimes(IntPtr thread, out long creation, out long kernel, out long user, out long exit)
    {
        var row = Thread(thread);
        if (!row.CanReadTimes)
        { creation = kernel = user = exit = 0; LastError = row.NativeError; return false; }
        creation = row.Birth; kernel = row.Kernel; user = row.User; exit = row.Exit;
        return true;
    }
    public void CloseDiagnosticThread(IntPtr thread) => handles.Remove(thread);
}
