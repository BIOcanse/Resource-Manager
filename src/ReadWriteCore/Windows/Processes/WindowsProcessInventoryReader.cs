using System.Diagnostics;
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

internal sealed record WindowsProcessInventoryEntry(int Id, ProcessCreationRead Creation, int ParentProcessId,
    string? Name, Process? Source = null, uint? ThreadCount = null)
{
    internal string ProcessName => Source?.ProcessName ?? Name ?? string.Empty;
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
        if (!buffer.TryRead(out var data)
            || !WindowsProcessSystemThreadParser.TryParseInventory(data, WindowsProcessDetailsOptions.Default, out var processes))
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
        var current = buffer.TryRead(out var data)
            && WindowsProcessSystemThreadParser.TryParseInventory(data, options, out rows);
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
