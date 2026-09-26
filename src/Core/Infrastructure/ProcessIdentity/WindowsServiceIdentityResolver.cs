using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsServiceIdentityResolver : IRuntimeServiceIdentityResolver
{
    private const int ErrorMoreData = 234;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private ServiceSnapshot cachedSnapshot = ServiceSnapshot.Unavailable;
    private DateTimeOffset cachedAt;

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
    {
        var snapshot = GetServicesByProcess();
        if (!snapshot.Available)
        {
            return RuntimeAttributionObservation.Unavailable;
        }
        if (process.StartKey is not { } startKey)
        {
            return RuntimeAttributionObservation.Unavailable;
        }
        if (!snapshot.ServicesByProcess.TryGetValue(
                process.ProcessId,
                out var processServices))
        {
            return snapshot.UnavailableProcessIds.Contains(process.ProcessId)
                ? RuntimeAttributionObservation.Unavailable
                : RuntimeAttributionObservation.NoMatch;
        }
        if (processServices.StartKey != startKey)
        {
            return RuntimeAttributionObservation.Unavailable;
        }

        var services = processServices.Services;
        var orderedServices = services
            .OrderBy(static service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedServices.Length == 1)
        {
            var service = orderedServices[0];
            return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
                $"windows-service:{NormalizeId(service.ServiceName)}",
                string.IsNullOrWhiteSpace(service.DisplayName) ? service.ServiceName : service.DisplayName,
                SoftwareKinds.WindowsService,
                SoftwareDisplayKinds.WindowsService,
                []));
        }

        var identityText = string.Join('|', orderedServices.Take(8).Select(static service => service.ServiceName));
        return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
            $"windows-service-group:{NormalizeId(identityText)}-{orderedServices.Length}",
            // 名字为空：前端按分组标识出「Windows 服务」，数量由 ProcessCount 单独给出。
            string.Empty,
            SoftwareKinds.WindowsService,
            SoftwareDisplayKinds.WindowsService,
            []));
    }

    private ServiceSnapshot GetServicesByProcess()
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (now - cachedAt < CacheLifetime)
            {
                return cachedSnapshot;
            }

            cachedSnapshot = ReadServicesByProcess();
            cachedAt = now;
            return cachedSnapshot;
        }
    }

    private static ServiceSnapshot ReadServicesByProcess()
    {
        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerEnumerateService);
        if (manager == IntPtr.Zero || manager == new IntPtr(-1))
        {
            return ServiceSnapshot.Unavailable;
        }

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;
            _ = NativeMethods.EnumServicesStatusEx(
                manager,
                NativeMethods.ScEnumProcessInfo,
                NativeMethods.ServiceWin32,
                NativeMethods.ServiceStateAll,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            var error = Marshal.GetLastWin32Error();
            if (bytesNeeded == 0 || error != ErrorMoreData)
            {
                return ServiceSnapshot.Unavailable;
            }

            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (!NativeMethods.EnumServicesStatusEx(
                    manager,
                    NativeMethods.ScEnumProcessInfo,
                    NativeMethods.ServiceWin32,
                    NativeMethods.ServiceStateAll,
                    buffer,
                    bytesNeeded,
                    out _,
                    out servicesReturned,
                    ref resumeHandle,
                    null))
                {
                    return ServiceSnapshot.Unavailable;
                }

                var serviceMap = BuildServiceMap(buffer, servicesReturned);
                return new ServiceSnapshot(
                    true,
                    serviceMap.ServicesByProcess,
                    serviceMap.UnavailableProcessIds);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or ArgumentException)
        {
            return ServiceSnapshot.Unavailable;
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    private static ServiceProcessMap BuildServiceMap(
        IntPtr buffer,
        uint servicesReturned)
    {
        var result = new Dictionary<int, List<ServiceIdentity>>();
        var entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();
        for (var index = 0; index < servicesReturned; index++)
        {
            var entryPointer = IntPtr.Add(buffer, index * entrySize);
            var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(entryPointer);
            var processId = (int)entry.ServiceStatusProcess.ProcessId;
            if (processId <= 0 || string.IsNullOrWhiteSpace(entry.ServiceName))
            {
                continue;
            }

            if (!result.TryGetValue(processId, out var services))
            {
                services = [];
                result[processId] = services;
            }

            services.Add(new ServiceIdentity(entry.ServiceName, entry.DisplayName));
        }

        var exact = new Dictionary<int, ServiceProcessIdentity>();
        var unavailable = new HashSet<int>();
        foreach (var item in result)
        {
            if (TryReadProcessStartKey(item.Key) is not { } startKey)
            {
                unavailable.Add(item.Key);
                continue;
            }
            exact[item.Key] = new ServiceProcessIdentity(
                startKey,
                item.Value.ToArray());
        }
        return new ServiceProcessMap(exact, unavailable);
    }

    private static long? TryReadProcessStartKey(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return process.StartTime.ToFileTimeUtc();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }

    private sealed record ServiceIdentity(string ServiceName, string DisplayName);

    private sealed record ServiceProcessIdentity(
        long StartKey,
        IReadOnlyList<ServiceIdentity> Services);

    private sealed record ServiceProcessMap(
        IReadOnlyDictionary<int, ServiceProcessIdentity> ServicesByProcess,
        IReadOnlySet<int> UnavailableProcessIds);

    private sealed record ServiceSnapshot(
        bool Available,
        IReadOnlyDictionary<int, ServiceProcessIdentity> ServicesByProcess,
        IReadOnlySet<int> UnavailableProcessIds)
    {
        internal static ServiceSnapshot Unavailable { get; } =
            new(
                false,
                new Dictionary<int, ServiceProcessIdentity>(),
                new HashSet<int>());
    }
}
