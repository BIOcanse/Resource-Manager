using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal readonly record struct ResourceManagerSelfProcessResource(
    int ProcessId,
    bool IsBackend,
    bool IsPackageClient,
    bool IsPackageClientDescendant,
    ulong WorkingSetBytes,
    DateTimeOffset StartedAt);

internal static class ResourceManagerSelfProcessFamilySampler
{
    internal static IReadOnlyList<ResourceManagerSelfProcessResource> Capture(int backendProcessId)
    {
        var parents = ReadProcessParentIds();
        var rows = new List<ResourceManagerSelfProcessResource>();
        var catalog = PackageRuntimeCatalog.Current;
        var imageBuffer = new StringBuilder(512);
        return ProcessCollectionOwner.Use(Process.GetProcesses(), processes =>
        {
            foreach (var process in processes)
            {
                if (TryCreateProcessResource(process, backendProcessId, catalog, imageBuffer, out var row)) rows.Add(row);
            }
            var identities = rows.ToDictionary(row => row.ProcessId, row => new ProcessAncestryIdentity(
                row.ProcessId, parents.GetValueOrDefault(row.ProcessId), row.StartedAt.UtcDateTime.ToFileTimeUtc()));
            var roots = rows.Where(row => row.IsBackend || row.IsPackageClient).Select(row => row.ProcessId).ToHashSet();
            var clientRoots = rows.Where(row => row.IsPackageClient).Select(row => row.ProcessId).ToHashSet();
            var family = ProcessAncestry.FindFamily(identities, roots);
            var clientFamily = ProcessAncestry.FindFamily(identities, clientRoots);
            return rows.Where(row => family.Contains(row.ProcessId))
                .Select(row => row with { IsPackageClientDescendant = !row.IsBackend && !row.IsPackageClient && clientFamily.Contains(row.ProcessId) })
                .OrderBy(row => row.ProcessId).ToArray();
        });
    }

    private static bool TryCreateProcessResource(Process process, int backendProcessId,
        PackageRuntimeCatalog catalog, StringBuilder image, out ResourceManagerSelfProcessResource row)
    {
        row = default;
        var handle = IntPtr.Zero;
        try
        {
            var processId = process.Id;
            handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero || !NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
            var startedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(checked((long)created.ToUInt64())));
            var isPackageClient = false;
            if (processId != backendProcessId && catalog.MayBeInfrastructureImageName(process.ProcessName))
            {
                // Read the image through the already-open process handle first. Token and
                // canonical-file attestation is only needed for an admitted package image.
                image.Clear();
                var length = image.Capacity;
                var found = NativeMethods.QueryFullProcessImageName(handle, 0, image, ref length);
                if (!found && Marshal.GetLastWin32Error() == 122 && image.Capacity < 32768)
                {
                    image.EnsureCapacity(32768);
                    length = image.Capacity;
                    found = NativeMethods.QueryFullProcessImageName(handle, 0, image, ref length);
                }
                if (found && catalog.IsInfrastructureImage(image.ToString()))
                {
                    var identity = WindowsNamedPipeCallerAttester.ReadProcessIdentity(checked((uint)processId));
                    isPackageClient = identity.CreatedUtcTicks == startedAt.UtcTicks
                        && catalog.IsInfrastructureImage(identity.CanonicalExecutablePath);
                }
            }
            row = new(processId, processId == backendProcessId, isPackageClient, false,
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
