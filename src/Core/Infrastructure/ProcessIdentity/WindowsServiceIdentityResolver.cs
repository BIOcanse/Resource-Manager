using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsServiceIdentityResolver : IRuntimeServiceIdentityResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly WindowsProcessBasicMetricReader BasicMetricReader = new();
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
        var facts = new WindowsServiceReader().Read(WindowsServiceScope.Win32);
        if (!facts.Available) return ServiceSnapshot.Unavailable;
        var serviceMap = BuildServiceMap(facts.Services);
        return new ServiceSnapshot(true, serviceMap.ServicesByProcess, serviceMap.UnavailableProcessIds);
    }
    private static ServiceProcessMap BuildServiceMap(
        IReadOnlyList<WindowsServiceRecord> entries)
    {
        var result = new Dictionary<int, List<ServiceIdentity>>();
        foreach (var entry in entries)
        {
            var processId = entry.ProcessId <= int.MaxValue ? (int)entry.ProcessId : 0;
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
        => BasicMetricReader.ReadCurrentCreationFileTime(processId);

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

