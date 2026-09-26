using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal readonly record struct ResourceManagerSelfProcessResource(
    int ProcessId,
    bool IsBackend,
    bool IsNativeUi,
    bool IsNativeUiDescendant,
    ulong WorkingSetBytes);

internal static class ResourceManagerSelfProcessFamilySampler
{
    internal static IReadOnlyList<ResourceManagerSelfProcessResource> Capture(int backendProcessId)
    {
        var parentById = ReadProcessParentIds();
        var processRows = new List<ResourceManagerSelfProcessResource>();
        var rootProcessIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!TryCreateProcessResource(process, backendProcessId, out var row))
                {
                    continue;
                }

                if (row.IsBackend || row.IsNativeUi)
                {
                    rootProcessIds.Add(row.ProcessId);
                }

                processRows.Add(row);
            }
        }

        var nativeUiRootProcessIds = processRows
            .Where(static row => row.IsNativeUi)
            .Select(static row => row.ProcessId)
            .ToHashSet();

        return processRows
            .Where(row => row.IsBackend
                || row.IsNativeUi
                || IsDescendantOfAny(row.ProcessId, parentById, rootProcessIds))
            .Select(row => row with
            {
                IsNativeUiDescendant = !row.IsBackend
                    && !row.IsNativeUi
                    && IsDescendantOfAny(row.ProcessId, parentById, nativeUiRootProcessIds)
            })
            .OrderBy(static row => row.ProcessId)
            .ToArray();
    }

    private static bool TryCreateProcessResource(
        Process process,
        int backendProcessId,
        out ResourceManagerSelfProcessResource row)
    {
        row = default;
        try
        {
            var processId = process.Id;
            var name = process.ProcessName;
            row = new ResourceManagerSelfProcessResource(
                processId,
                processId == backendProcessId,
                MatchesProcessName(name, "ResourceManager.NativeUi"),
                IsNativeUiDescendant: false,
                (ulong)Math.Max(0, process.WorkingSet64));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return false;
        }
    }

    private static Dictionary<int, int> ReadProcessParentIds()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = ProcessEntry32.Create();
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return parents;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private static bool IsDescendantOfAny(
        int processId,
        IReadOnlyDictionary<int, int> parentById,
        IReadOnlySet<int> ancestorProcessIds)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (parentById.TryGetValue(current, out var parentProcessId) && parentProcessId > 0)
        {
            if (!visited.Add(parentProcessId))
            {
                return false;
            }

            if (ancestorProcessIds.Contains(parentProcessId))
            {
                return true;
            }

            current = parentProcessId;
        }

        return false;
    }

    private static bool MatchesProcessName(string processName, string expectedName)
    {
        return processName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
            || processName.Equals(Path.GetFileNameWithoutExtension(expectedName), StringComparison.OrdinalIgnoreCase);
    }
}
