using System.Diagnostics;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal interface IWindowsProcessInventoryReader
{
    Process[] GetProcesses();
    IReadOnlyDictionary<int, ProcessCreationRead> ReadCreations(Process[] processes, WindowsProcessInventoryBuffer buffer)
        => WindowsProcessInventoryReader.Instance.ReadCreations(processes, buffer);
    WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
    {
        var processes = GetProcesses();
        try
        {
            var entries = WindowsProcessInventoryReader.ReadEntries(processes, buffer);
            return new(processes.Select(process => new WindowsProcessInventoryEntry(process.Id,
                entries[process.Id].Creation, entries[process.Id].ParentProcessId, null, process)).ToArray(), processes);
        }
        catch
        {
            foreach (var process in processes) try { process.Dispose(); } catch { }
            throw;
        }
    }
}

/// <summary>One process of an inventory capture. <paramref name="Counters"/> is present exactly when the entry
/// comes from a kernel process table record; a Process-object inventory has none.</summary>
internal sealed record WindowsProcessInventoryEntry(int Id, ProcessCreationRead Creation, int ParentProcessId,
    string? Name, Process? Source = null, WindowsProcessTableCounters? Counters = null)
{
    internal string ProcessName => Source?.ProcessName ?? Name ?? string.Empty;
}

/// <summary>Dynamic counters of one SystemProcessInformation record. They come from the same kernel call and
/// record as the entry's creation time, so they need no handle and no separate identity check.
/// CPU time is KernelTime + UserTime; commit is PagefileUsage (PROCESS_MEMORY_COUNTERS_EX PrivateUsage);
/// peak commit is PeakPagefileUsage; private working set is WorkingSetPrivateSize; IO is the record's
/// IO_COUNTERS, timed by the table query bracket; thread count is NumberOfThreads within the parser bound.</summary>
internal readonly record struct WindowsProcessTableCounters(
    ulong KernelTime100Ns, ulong UserTime100Ns, ulong WorkingSet, ulong PrivateCommit, ulong PeakPrivateCommit,
    ulong PrivateWorkingSet, ulong ReadOperations, ulong WriteOperations, ulong OtherOperations,
    ulong ReadBytes, ulong WriteBytes, ulong OtherBytes, uint HandleCount, uint? ThreadCount, ProcessIoQueryTiming? IoTiming)
{
    /// <summary>The read-mask categories a table record answers.</summary>
    internal const ProcessMetricReadMask Fields = ProcessMetricReadMask.CpuTime | ProcessMetricReadMask.Memory
        | ProcessMetricReadMask.PrivateWorkingSet | ProcessMetricReadMask.Io | ProcessMetricReadMask.HandleCount
        | ProcessMetricReadMask.ThreadCount;

    /// <summary>Returns <paramref name="reading"/> with every requested table category set from this record and
    /// every unrequested one empty, as a handle read of the same mask would.</summary>
    internal ProcessBasicMetricObservation Fill(ProcessMetricReadMask requested, ProcessBasicMetricObservation reading)
    {
        if ((requested & Fields) == 0) return reading;
        var memory = (requested & (ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet)) != 0
            ? WindowsProcessBasicMetricReader.Consistent(new(WorkingSet, PrivateCommit, PeakPrivateCommit,
                requested.HasFlag(ProcessMetricReadMask.PrivateWorkingSet) ? PrivateWorkingSet : null))
            : null;
        return reading with
        {
            CpuTime100Ns = requested.HasFlag(ProcessMetricReadMask.CpuTime) ? (UInt128)KernelTime100Ns + UserTime100Ns : null,
            Memory = memory,
            Io = requested.HasFlag(ProcessMetricReadMask.Io)
                ? new(ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes) { Timing = IoTiming }
                : null,
            HandleCount = requested.HasFlag(ProcessMetricReadMask.HandleCount) ? HandleCount : null,
            ThreadCount = requested.HasFlag(ProcessMetricReadMask.ThreadCount) ? ThreadCount : null
        };
    }
}

internal sealed class WindowsProcessInventoryCapture(WindowsProcessInventoryEntry[] processes, Process[]? ownedProcesses = null) : IDisposable
{
    internal WindowsProcessInventoryEntry[] Processes { get; } = processes;
    public void Dispose()
    {
        if (ownedProcesses is null) return;
        foreach (var process in ownedProcesses) try { process.Dispose(); } catch { }
    }
}

internal sealed class WindowsProcessInventoryReader : IWindowsProcessInventoryReader
{
    internal static WindowsProcessInventoryReader Instance { get; } = new();

    private WindowsProcessInventoryReader()
    {
    }

    public Process[] GetProcesses() => Process.GetProcesses();

    public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
    {
        if (!buffer.TryRead(out var data, out var timing)
            || !WindowsProcessSystemThreadParser.TryParseInventory(data, WindowsProcessDetailsOptions.Default,
                timing is { } query ? new(query.QueryStartTicks, query.QueryReturnTicks, query.TicksPerSecond) : null, out var processes))
            throw new InvalidOperationException("The kernel process inventory is unavailable.");
        return new(processes);
    }

    public IReadOnlyDictionary<int, ProcessCreationRead> ReadCreations(Process[] processes, WindowsProcessInventoryBuffer buffer)
        => ReadEntries(processes, buffer).ToDictionary(static pair => pair.Key, static pair => pair.Value.Creation);

    internal static IReadOnlyDictionary<int, WindowsProcessInventoryEntry> ReadEntries(Process[] processes, WindowsProcessInventoryBuffer buffer)
    {
        // Creation identities come from the kernel inventory, independently of
        // permission to open individual processes for their optional fields.
        var options = WindowsProcessDetailsOptions.Default;
        WindowsProcessInventoryEntry[] rows = [];
        // Only creations and parents are kept here, so the records' counters need no IO timing.
        var current = buffer.TryRead(out var data, out _)
            && WindowsProcessSystemThreadParser.TryParseInventory(data, options, null, out rows);
        var entries = rows.ToDictionary(static row => row.Id);
        var result = new Dictionary<int, WindowsProcessInventoryEntry>(processes.Length);
        foreach (var process in processes)
        {
            var read = !current ? new ProcessCreationRead(ProcessReadStatus.Unavailable, null)
                : entries.TryGetValue(process.Id, out var entry)
                    ? entry.Creation
                    : new ProcessCreationRead(ProcessReadStatus.Exited, null);
            result.TryAdd(process.Id, new(process.Id, read, entries.GetValueOrDefault(process.Id)?.ParentProcessId ?? 0, null));
        }
        return result;
    }
}
