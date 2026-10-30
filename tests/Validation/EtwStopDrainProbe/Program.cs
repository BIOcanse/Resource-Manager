using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

// Only controls the unique session created by this process. No attach/orphan cleanup.
if (args.Length is < 2 or > 3 || args[0] is not ("legacy" or "drain"))
    throw new ArgumentException("Usage: EtwStopDrainProbe legacy|drain output.jsonl [delayed]");
if (TraceEventSession.IsElevated() != true) throw new InvalidOperationException("Probe must be elevated.");
using var output = new StreamWriter(args[1]) { AutoFlush = true };
var mode = args[0];
var delayed = args.Length == 3 && args[2] == "delayed";
var sessionName = $"RM-stop-drain-probe-{Environment.ProcessId}";
var poolSizeMb = 0;
var retained = new List<object>(); // Prevent finalizers from hiding missing explicit cleanup.
Record("baseline", 0, "idle", 0, 0);
var kindIndex = 0;
while (kindIndex < 2)
{
    var kind = kindIndex++ == 0 ? "kernel64" : "dxg16";
    var cycle = 1;
    while (cycle <= 5)
    {
        TraceEventSession? session = null;
        ETWTraceEventSource? source = null;
        Thread? thread = null;
        Exception? processFailure = null;
        long events = 0;
        using var entered = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var stopping = 0;
        try
        {
            session = new TraceEventSession(sessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate) { StopOnDispose = false };
            if (kind == "kernel64")
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch |
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
            else
            {
                session.BufferSizeMB = 16;
                session.EnableProvider(new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d"), TraceEventLevel.Verbose, 0);
            }
            poolSizeMb = session.BufferSizeMB;
            source = session.Source;
            source.AllEvents += _ =>
            {
                Interlocked.Increment(ref events);
                if (delayed && Volatile.Read(ref stopping) != 0)
                {
                    paused.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Probe callback was not resumed.");
                }
            };
            thread = new Thread(() =>
            {
                entered.Set();
                try { source.Process(); }
                catch (Exception failure) { processFailure = failure; }
            }) { IsBackground = true, Name = "RM stop/drain probe consumer" };
            thread.Start();
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Consumer did not start.");
            Thread.Sleep(2500);
            Record(kind, cycle, "running", events, 0);
            using var resumeTimer = new Timer(_ => resume.Set(), null, Timeout.Infinite, Timeout.Infinite);
            if (delayed)
            {
                Volatile.Write(ref stopping, 1);
                if (!paused.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("No event entered the delayed callback.");
                resumeTimer.Change(1000, Timeout.Infinite);
            }
            var timer = Stopwatch.StartNew();
            OwnedSession.Stop(sessionName);
            session.StopOnDispose = false;
            if (mode == "legacy")
            {
                source.StopProcessing();
                session.Dispose();
                if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Legacy consumer did not exit.");
            }
            else
            {
                if (!thread.Join(TimeSpan.FromSeconds(5)))
                {
                    source.StopProcessing();
                    source.Dispose();
                    throw new TimeoutException("Natural drain timed out; trace forcibly closed, release incomplete.");
                }
                source.StopProcessing();
                source.Dispose();
                session.Dispose();
            }
            if (processFailure is not null) throw new IOException("ProcessTrace failed.", processFailure);
            Record(kind, cycle, "stopped", events, timer.Elapsed.TotalMilliseconds);
            // Actual terminal Dispose closes source after the legacy Stop's join.
            source.Dispose();
            Record(kind, cycle, "disposed", events, timer.Elapsed.TotalMilliseconds);
            retained.Add(session);
            retained.Add(source);
        }
        finally
        {
            resume.Set();
            // All cleanup targets are local objects for this exact owned session.
            try { if (session?.IsActive == true) OwnedSession.Stop(sessionName); }
            finally
            {
                source?.StopProcessing();
                source?.Dispose();
                if (thread is { IsAlive: true } && !thread.Join(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Probe consumer remains alive after forced close.");
                session?.Dispose();
            }
        }
        cycle++;
    }
}
GC.KeepAlive(retained);

void Record(string kind, int cycle, string phase, long events, double stopMs)
{
    var snapshot = Memory.Snapshot();
    var loggerPool = phase == "running" ? OwnedSession.Query(sessionName) : null;
    using var process = Process.GetCurrentProcess();
    var line = JsonSerializer.Serialize(new { mode, delayed, pid = Environment.ProcessId, sessionName, kind, cycle, phase,
        privateBytes = process.PrivateMemorySize64, snapshot.Regions, snapshot.Bytes, snapshot.Exact64KiBRegions,
        poolSizeMb, logicalProcessors = Environment.ProcessorCount, loggerPool, events, stopMs });
    output.WriteLine(line);
    Console.WriteLine(line);
}

static class Memory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Mbi
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll")] static extern nuint VirtualQuery(nint address, out Mbi info, nuint size);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);
    internal static (int Regions, long Bytes, int Exact64KiBRegions) Snapshot()
    {
        // Maintenance Age-EtwBuffers.ps1: BufferSize @0 == 0x10000 and 0 < QPC @0x10 <= now.
        QueryPerformanceCounter(out var now);
        var header = new byte[64];
        nuint address = 0;
        var nodes = 0;
        var regions = 0;
        var exact = 0;
        long bytes = 0;
        while (nodes++ < 1_000_000 && VirtualQuery((nint)address, out var info, (nuint)Marshal.SizeOf<Mbi>()) != 0)
        {
            if (info.State == 0x1000 && info.Type == 0x20000 && (info.Protect & 0x101) == 0 &&
                ReadProcessMemory(-1, info.BaseAddress, header, 64, out var read) && read == 64 &&
                BitConverter.ToUInt32(header, 0) == 0x10000 &&
                BitConverter.ToInt64(header, 0x10) is var stamp && stamp > 0 && stamp <= now)
            {
                regions++;
                bytes += (long)info.RegionSize;
                if (info.RegionSize == 65536) exact++;
            }
            var next = (nuint)info.BaseAddress + info.RegionSize;
            if (next <= address) break;
            address = next;
        }
        if (nodes >= 1_000_000) throw new InvalidOperationException("VirtualQuery bound exceeded.");
        return (regions, bytes, exact);
    }
}

static class OwnedSession
{
    internal sealed record Pool(uint BufferKiB, uint MinimumBuffers, uint MaximumBuffers, uint AllocatedBuffers, uint FreeBuffers)
    {
        public long AllocatedBytes => (long)BufferKiB * 1024 * AllocatedBuffers;
    }
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern uint ControlTraceW(ulong handle, string name, byte[] properties, uint control);

    internal static void Stop(string name)
    {
        ValidateName(name);
        var properties = Properties();
        var status = ControlTraceW(0, name, properties, 1);
        if (status is not (0 or 4201)) throw new System.ComponentModel.Win32Exception((int)status);
    }

    internal static Pool Query(string name)
    {
        ValidateName(name);
        var properties = Properties();
        var status = ControlTraceW(0, name, properties, 0);
        if (status != 0) throw new System.ComponentModel.Win32Exception((int)status);
        return new(BitConverter.ToUInt32(properties, 48), BitConverter.ToUInt32(properties, 52),
            BitConverter.ToUInt32(properties, 56), BitConverter.ToUInt32(properties, 80), BitConverter.ToUInt32(properties, 84));
    }

    private static void ValidateName(string name)
    {
        if (name != $"RM-stop-drain-probe-{Environment.ProcessId}" || !Environment.Is64BitProcess)
            throw new InvalidOperationException("Only this x64 probe's own named session may be stopped.");
    }

    private static byte[] Properties()
    {
        // EVENT_TRACE_PROPERTIES is 120 bytes on x64. Match the production
        // native ControlTrace STOP without TraceEvent.Stop's global sample-rate reset.
        var properties = new byte[120 + 2048 * sizeof(char)];
        BitConverter.TryWriteBytes(properties.AsSpan(0), (uint)properties.Length);
        BitConverter.TryWriteBytes(properties.AsSpan(44), 0x20000u); // WNODE_FLAG_TRACED_GUID
        BitConverter.TryWriteBytes(properties.AsSpan(112), 120u + 1024u * sizeof(char));
        BitConverter.TryWriteBytes(properties.AsSpan(116), 120u);
        return properties;
    }
}
