using System.Diagnostics;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal interface IWindowsProcessInventoryReader
{
    Process[] GetProcesses();
    IReadOnlyDictionary<int, ProcessCreationRead> ReadCreations(Process[] processes, WindowsProcessInventoryBuffer buffer)
        => WindowsProcessInventoryReader.Instance.ReadCreations(processes, buffer);
}

internal sealed class WindowsProcessInventoryReader : IWindowsProcessInventoryReader
{
    internal static WindowsProcessInventoryReader Instance { get; } = new();

    private WindowsProcessInventoryReader()
    {
    }

    public Process[] GetProcesses() => Process.GetProcesses();

    public IReadOnlyDictionary<int, ProcessCreationRead> ReadCreations(Process[] processes, WindowsProcessInventoryBuffer buffer)
    {
        // Creation identities come from the kernel inventory, independently of
        // permission to open individual processes for their optional fields.
        var options = WindowsProcessDetailsOptions.Default;
        Dictionary<int, long> creations = [];
        var current = buffer.TryRead(out var data)
            && WindowsProcessSystemThreadParser.TryParseCreations(data, options, out creations);
        var result = new Dictionary<int, ProcessCreationRead>(processes.Length);
        foreach (var process in processes)
        {
            if (process.Id <= 0) continue;
            var read = !current ? new ProcessCreationRead(ProcessReadStatus.Unavailable, null)
                : creations.TryGetValue(process.Id, out var creation)
                    ? new ProcessCreationRead(creation > 0 ? ProcessReadStatus.Current : ProcessReadStatus.Unavailable, creation > 0 ? creation : null)
                    : new ProcessCreationRead(ProcessReadStatus.Exited, null);
            result.TryAdd(process.Id, read);
        }
        return result;
    }
}
