using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal readonly record struct ResourceManagerSelfProcessResource(
    int ProcessId,
    bool IsBackend,
    bool IsNativeUi,
    bool IsNativeUiDescendant,
    ulong WorkingSetBytes,
    DateTimeOffset StartedAt);

internal static class ResourceManagerSelfProcessFamilySampler
{
    internal static IReadOnlyList<ResourceManagerSelfProcessResource> Capture(int backendProcessId)
    {
        var parents = ReadProcessParentIds();
        var rows = new List<ResourceManagerSelfProcessResource>();
        var expectedUiPath = NativeUiLaunchHostedService.ResolveNativeUiPath();
        return ProcessCollectionOwner.Use(Process.GetProcesses(), processes =>
        {
            foreach (var process in processes)
            {
                if (TryCreateProcessResource(process, backendProcessId, expectedUiPath, out var row)) rows.Add(row);
            }
            var identities = rows.ToDictionary(row => row.ProcessId, row => new ProcessAncestryIdentity(
                row.ProcessId, parents.GetValueOrDefault(row.ProcessId), row.StartedAt.UtcDateTime.ToFileTimeUtc()));
            var roots = rows.Where(row => row.IsBackend || row.IsNativeUi).Select(row => row.ProcessId).ToHashSet();
            var uiRoots = rows.Where(row => row.IsNativeUi).Select(row => row.ProcessId).ToHashSet();
            var family = ProcessAncestry.FindFamily(identities, roots);
            var uiFamily = ProcessAncestry.FindFamily(identities, uiRoots);
            return rows.Where(row => family.Contains(row.ProcessId))
                .Select(row => row with { IsNativeUiDescendant = !row.IsBackend && !row.IsNativeUi && uiFamily.Contains(row.ProcessId) })
                .OrderBy(row => row.ProcessId).ToArray();
        });
    }

    private static bool TryCreateProcessResource(Process process, int backendProcessId,
        string expectedUiPath, out ResourceManagerSelfProcessResource row)
    {
        row = default;
        var handle = IntPtr.Zero;
        try
        {
            var processId = process.Id;
            handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero || !NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
            var startedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(checked((long)created.ToUInt64())));
            var isNativeUi = false;
            if (process.ProcessName.Equals("ResourceManager.NativeUi", StringComparison.OrdinalIgnoreCase))
            {
                var identity = WindowsNamedPipeCallerAttester.ReadProcessIdentity(checked((uint)processId));
                isNativeUi = identity.CreatedUtcTicks == startedAt.UtcTicks
                    && IsNativeUiImage(identity.CanonicalExecutablePath, expectedUiPath);
            }
            row = new(processId, processId == backendProcessId, isNativeUi, false,
                (ulong)Math.Max(0, process.WorkingSet64), startedAt);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception
                                   or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally { if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle); }
    }

    internal static bool IsNativeUiImage(string actualPath, string expectedPath)
        => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath));

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

}
