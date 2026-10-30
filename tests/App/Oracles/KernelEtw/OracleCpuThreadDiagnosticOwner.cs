using System.Globalization;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Domain.CpuTopology;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal sealed class OracleCpuThreadDiagnosticOwner : IDisposable
{
    private readonly IOracleThreadDiagnosticSource source;
    internal OracleCpuThreadDiagnosticOwner(IOracleThreadDiagnosticSource? source = null)
        => this.source = source ?? new OracleWindowsThreadDiagnosticSource();
    private bool stopped;
    public void Dispose() { lock (diagnosticGate) { stopped = true; DisposeDiagnosticRequests(); ClearDiagnosticSource(); } }
    private readonly object diagnosticGate = new();
    private readonly HashSet<ThreadDiagnosticRequest> diagnosticRequests = [];
    private OracleKernelEtwEventSource? diagnosticSource;
    private Func<bool>? diagnosticSourceIsCurrent;
    private Action<ThreadTraceData>? diagnosticThreadStarted, diagnosticThreadEnded;
    private long diagnosticNativeReads;
    internal long DiagnosticNativeReads => Interlocked.Read(ref diagnosticNativeReads);
    internal int DiagnosticRequestCount { get { lock (diagnosticGate) return diagnosticRequests.Count; } }
    internal void BindDiagnosticSource(OracleKernelEtwEventSource source, long generation, Func<bool> isCurrent)
    {
        lock (diagnosticGate)
        {
            ClearDiagnosticSource();
            diagnosticSource = source; diagnosticSourceIsCurrent = isCurrent;
            foreach (var request in diagnosticRequests.ToArray()) request.BindManaged();
            AttachDiagnosticEvents();
        }
    }
    private void AttachDiagnosticEvents()
    {
        if (diagnosticRequests.Count == 0 || diagnosticSource is null || diagnosticThreadStarted is not null) return;
        diagnosticThreadStarted = data =>
        {
            lock (diagnosticGate)
                if (diagnosticSourceIsCurrent?.Invoke() == true) ObserveDiagnosticThread(data.ProcessID, data.ThreadID);
        };
        diagnosticThreadEnded = data =>
        {
            lock (diagnosticGate)
                if (diagnosticSourceIsCurrent?.Invoke() == true)
                    foreach (var request in diagnosticRequests)
                        if (request.ProcessId == data.ProcessID) request.ObserveThreadEnd(data.ThreadID);
        };
        diagnosticSource.ThreadStartGroup += diagnosticThreadStarted;
        diagnosticSource.ThreadEndGroup += diagnosticThreadEnded;
    }
    internal void ClearDiagnosticSource()
    {
        lock (diagnosticGate)
        {
            if (diagnosticThreadStarted is not null && diagnosticSource is not null)
                diagnosticSource.ThreadStartGroup -= diagnosticThreadStarted;
            if (diagnosticThreadEnded is not null && diagnosticSource is not null)
                diagnosticSource.ThreadEndGroup -= diagnosticThreadEnded;
            diagnosticThreadStarted = diagnosticThreadEnded = null; diagnosticSource = null; diagnosticSourceIsCurrent = null;
        }
    }

    // Caller owns residency demand; registering metadata alone starts no ETW worker.
    public ThreadDiagnosticRequest RequestThreadDiagnostics(int processId, long processCreationFileTime100Ns)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (processCreationFileTime100Ns <= 0) throw new ArgumentOutOfRangeException(nameof(processCreationFileTime100Ns));
        lock (diagnosticGate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var request = new ThreadDiagnosticRequest(this, processId, processCreationFileTime100Ns);
            diagnosticRequests.Add(request);
            AttachDiagnosticEvents();
            request.MarkReady();
            return request;
        }
    }
    private void ObserveDiagnosticThread(int pid, int tid)
    {
        lock (diagnosticGate)
            foreach (var request in diagnosticRequests)
                if (request.ProcessId == pid) request.ObserveThreadStart(tid);
    }
    private void DisposeDiagnosticRequests()
    {
        lock (diagnosticGate)
            foreach (var request in diagnosticRequests.ToArray()) request.Dispose();
    }

    public sealed class ThreadDiagnosticRequest : IDisposable
    {
        private readonly OracleCpuThreadDiagnosticOwner owner;
        private readonly IOracleThreadDiagnosticSource api;
        private readonly Dictionary<(int Id, long Birth), Entry> entries = [];
        private readonly Dictionary<int, int> failures = [];
        private readonly HashSet<int> endedThreadIds = [], reusedThreadIds = [];
        private IReadOnlyList<WindowsProcessLoadedModule> modules = [];
        private IntPtr process;
        private int? processNativeError;
        private bool managedInitialized;
        private bool disposed;
        public int ProcessId { get; }
        public long ProcessCreationFileTime100Ns { get; }
        public DateTimeOffset RequestedAt { get; private set; }
        internal ThreadDiagnosticRequest(OracleCpuThreadDiagnosticOwner owner, int pid, long birth)
        {
            this.owner = owner; api = owner.source; ProcessId = pid; ProcessCreationFileTime100Ns = birth; RequestedAt = api.UtcNow;
            // Before binding, the request holds identity only. Native production
            // never creates the legacy metadata owner while awaiting its source.
            if (owner.diagnosticSource is not null) BindManaged();
        }
        internal void BindManaged()
        {
            if (managedInitialized || disposed) return;
            managedInitialized = true;
            try
            {
                Interlocked.Increment(ref owner.diagnosticNativeReads);
                process = api.OpenProcess(0x1000 | 0x100000, ProcessId);
                if (process == IntPtr.Zero)
                { processNativeError = api.LastError; return; }
                if (!api.TryGetCreationFileTime(process, out var actual) || actual != (ulong)ProcessCreationFileTime100Ns)
                { processNativeError = 1168; api.CloseHandle(process); process = IntPtr.Zero; return; }
                RefreshInventory();
                var moduleHandle = api.OpenProcess(0x0400 | 0x0010, ProcessId);
                if (moduleHandle != IntPtr.Zero)
                {
                    try
                    {
                        if (api.TryGetCreationFileTime(moduleHandle, out actual) && actual == (ulong)ProcessCreationFileTime100Ns)
                            api.TryReadModules(moduleHandle, out modules);
                    }
                    finally { api.CloseHandle(moduleHandle); }
                }
            }
            catch { Dispose(); throw; }
        }
        internal void MarkReady() => RequestedAt = api.UtcNow;
        internal void ObserveThreadEnd(int tid) => endedThreadIds.Add(tid);
        internal void ObserveThreadStart(int tid)
        {
            var knownBirths = entries.Keys.Where(key => key.Id == tid).Select(key => key.Birth).ToHashSet();
            var actualBirth = ObserveThread(tid, 0, null);
            if (endedThreadIds.Contains(tid) && (!actualBirth.HasValue || !knownBirths.Contains(actualBirth.Value)))
                reusedThreadIds.Add(tid);
        }
        private void RefreshInventory()
        {
            if (process == IntPtr.Zero || !api.IsRunning(process)) return;
            if (!api.TryReadInventory(ProcessId, ProcessCreationFileTime100Ns, out var rows)) return;
            foreach (var row in rows) ObserveThread(row.ThreadId, row.CreationFileTime100Ns, row.StartAddress);
        }
        internal long? ObserveThread(int tid, long birth, ulong? start)
        {
            lock (owner.diagnosticGate)
            {
                if (disposed || process == IntPtr.Zero || !api.IsRunning(process)) return null;
                if (birth != 0 && entries.ContainsKey((tid, birth))) return birth;
                Interlocked.Increment(ref owner.diagnosticNativeReads);
                var handle = api.OpenDiagnosticThread(ProcessId, tid, birth,
                    out var creation, out var kernel, out var user, out var error);
                if (handle == IntPtr.Zero) { failures[tid] = error; return null; }
                if (!api.IsRunning(process)) { api.CloseDiagnosticThread(handle); return null; }
                if (entries.ContainsKey((tid, creation))) { api.CloseDiagnosticThread(handle); return creation; }
                var retained = false;
                try
                {
                    var readAt = api.UtcNow;
                    // A thread born inside this request has zero counters at birth. An older thread needs a baseline.
                    var bornDuringRequest = creation >= RequestedAt.UtcDateTime.ToFileTimeUtc();
                    var name = api.ReadDiagnosticName(handle);
                    var win32Start = api.ReadDiagnosticStartAddress(handle);
                    entries.Add((tid, creation), new(handle, tid, creation, name.Name, name.Error,
                        win32Start ?? start, win32Start.HasValue ? CpuThreadStartAddressSource.Win32StartAddress
                            : start.HasValue ? CpuThreadStartAddressSource.SystemProcessInformation : null,
                        bornDuringRequest ? DateTimeOffset.FromFileTime(creation) : readAt,
                        bornDuringRequest ? 0 : kernel, bornDuringRequest ? 0 : user));
                    retained = true;
                    return creation;
                }
                finally { if (!retained) api.CloseDiagnosticThread(handle); }
            }
        }
        public CpuProcessThreadWindow? ReadWindow(CpuCoreResidencySnapshot? snapshot)
        {
            lock (owner.diagnosticGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (snapshot is null || snapshot.MeasuredFrom < RequestedAt) return null;
                var source = snapshot.Processes.SingleOrDefault(row => row.ProcessId == ProcessId
                    && row.ProcessStartKey == ProcessCreationFileTime100Ns.ToString(CultureInfo.InvariantCulture));
                if (source is null) return null;
                RefreshInventory();
                var metadataById = entries.Values.GroupBy(entry => entry.Id)
                    .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.Single() : null);
                var uniqueIds = source.Threads.GroupBy(row => row.ThreadId)
                    .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet();
                var threads = source.Threads.Select(row =>
                {
                    // Residency ids are ETW lifetime identities, not native creation times. Never guess across TID reuse.
                    if (reusedThreadIds.Contains(row.ThreadId) || !uniqueIds.Contains(row.ThreadId)
                        || !metadataById.TryGetValue(row.ThreadId, out var entry) || entry is null
                        || entry.Birth > snapshot.MeasuredThrough.UtcDateTime.ToFileTimeUtc())
                        return failures.TryGetValue(row.ThreadId, out var failed) || processNativeError.HasValue
                            ? row with { Diagnostic = new(null, null, null, null, null, null, null, processNativeError ?? failed) } : row;
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        var name = api.ReadDiagnosticName(entry.Handle);
                        entry.Name = name.Name; entry.NameError = name.Error;
                    }
                    CpuThreadCounterDelta? counters = null;
                    int? error = null;
                    if (api.ReadDiagnosticTimes(entry.Handle, out var birth, out var kernel, out var user, out var exit)
                        && birth == entry.Birth && kernel >= entry.Kernel && user >= entry.User)
                    {
                        // A cached handle that had already exited before this window cannot identify this execution row.
                        if (exit != 0 && exit <= snapshot.MeasuredFrom.UtcDateTime.ToFileTimeUtc()) return row;
                        counters = new(entry.ReadFrom, api.UtcNow, kernel - entry.Kernel, user - entry.User);
                    }
                    else error = api.LastError;
                    var module = entry.Start is { } address ? modules.FirstOrDefault(module =>
                        module.ImageSizeBytes is { } size && address >= module.BaseAddress && address - module.BaseAddress < size)?.Path : null;
                    return row with { Diagnostic = new(entry.Birth, entry.Name, entry.NameError, entry.Start, entry.StartSource, module, counters, error) };
                }).ToArray();
                return new(snapshot.MeasuredFrom, snapshot.MeasuredThrough, snapshot.SessionGeneration, source with { Threads = threads });
            }
        }
        public void Dispose()
        {
            lock (owner.diagnosticGate)
            {
                if (disposed) return;
                disposed = true;
                owner.diagnosticRequests.Remove(this);
                if (owner.diagnosticRequests.Count == 0 && owner.diagnosticSource is not null && owner.diagnosticThreadStarted is not null)
                {
                    owner.diagnosticSource.ThreadStartGroup -= owner.diagnosticThreadStarted;
                    if (owner.diagnosticThreadEnded is not null) owner.diagnosticSource.ThreadEndGroup -= owner.diagnosticThreadEnded;
                    owner.diagnosticThreadStarted = owner.diagnosticThreadEnded = null;
                }
                foreach (var entry in entries.Values) api.CloseDiagnosticThread(entry.Handle);
                entries.Clear(); failures.Clear(); endedThreadIds.Clear(); reusedThreadIds.Clear(); modules = [];
                if (process != IntPtr.Zero) { api.CloseHandle(process); process = IntPtr.Zero; }
            }
        }
        private sealed class Entry(IntPtr handle, int id, long birth, string? name, int? nameError,
            ulong? start, CpuThreadStartAddressSource? startSource, DateTimeOffset readFrom, long kernel, long user)
        {
            internal readonly IntPtr Handle = handle;
            internal readonly int Id = id;
            internal readonly long Birth = birth;
            internal string? Name = name;
            internal int? NameError = nameError;
            internal readonly ulong? Start = start;
            internal readonly CpuThreadStartAddressSource? StartSource = startSource;
            internal readonly DateTimeOffset ReadFrom = readFrom;
            internal readonly long Kernel = kernel, User = user;
        }
    }
}
