using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsServiceIdentityResolver : IRuntimeServiceIdentityResolver
{
    private static readonly TimeSpan CacheLifetime = ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.ServiceIdentityCacheLifetime;
    private static readonly WindowsProcessBasicMetricReader BasicMetricReader = new();
    private readonly object gate = new();
    private readonly Func<IReadOnlyList<WindowsServiceRecord>?> readServices;
    private readonly Func<int, long?> readProcessStartKey;
    private readonly TimeProvider clock;
    private ServiceSnapshot cachedSnapshot = ServiceSnapshot.Unavailable;
    private DateTimeOffset cachedAt;

    public WindowsServiceIdentityResolver()
        : this(ReadServiceEntries, TryReadProcessStartKey, TimeProvider.System) { }

    internal WindowsServiceIdentityResolver(Func<IReadOnlyList<WindowsServiceRecord>?> readServices,
        Func<int, long?> readProcessStartKey, TimeProvider clock)
    {
        this.readServices = readServices;
        this.readProcessStartKey = readProcessStartKey;
        this.clock = clock;
    }

    // The service owner publishes one immutable membership/name snapshot. Its
    // existing five-second acquisition stays Sampled; attribution compares the
    // published snapshot once per batch instead of probing it for each PID.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Service membership/name snapshot published")]
    public object InputVersion { get { lock (gate) return cachedSnapshot; } }

    [ResourceManager.ReadWrite.RuntimeReadCache]
    public object CaptureInputVersion() => GetServicesByProcess();

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
        => Observe(process, GetServicesByProcess());

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process, object inputVersion)
    {
        var snapshot = inputVersion as ServiceSnapshot
            ?? throw new ArgumentException("Supply the service snapshot captured for this batch.", nameof(inputVersion));
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

        return processServices.Observation;
    }

    private static RuntimeAttributionObservation CreateObservation(IReadOnlyList<ServiceIdentity> services)
    {
        var orderedServices = services
            .OrderBy(static service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedServices.Length == 1)
        {
            var service = orderedServices[0];
            return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
                WindowsServiceSoftwareIds.ForService(service.ServiceName),
                string.IsNullOrWhiteSpace(service.DisplayName) ? service.ServiceName : service.DisplayName,
                SoftwareKinds.WindowsService,
                SoftwareDisplayKinds.WindowsService,
                []));
        }

        return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
            WindowsServiceSoftwareIds.ForServiceGroup(orderedServices.Select(static service => service.ServiceName).ToArray()),
            // 名字为空：前端按分组标识出「Windows 服务」，数量由 ProcessCount 单独给出。
            string.Empty,
            SoftwareKinds.WindowsService,
            SoftwareDisplayKinds.WindowsService,
            []));
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private ServiceSnapshot GetServicesByProcess()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (now - cachedAt < CacheLifetime)
            {
                return cachedSnapshot;
            }

            var next = ReadServicesByProcess();
            if (!SameSnapshot(cachedSnapshot, next)) cachedSnapshot = next;
            cachedAt = now;
            return cachedSnapshot;
        }
    }

    private static bool SameSnapshot(ServiceSnapshot previous, ServiceSnapshot current)
    {
        if (previous.Available != current.Available || previous.ServicesByProcess.Count != current.ServicesByProcess.Count
            || !previous.UnavailableProcessIds.SetEquals(current.UnavailableProcessIds)) return false;
        foreach (var (pid, service) in previous.ServicesByProcess)
        {
            if (!current.ServicesByProcess.TryGetValue(pid, out var next) || service.StartKey != next.StartKey
                || service.Observation.Status != next.Observation.Status
                || service.Observation.Attribution?.Id != next.Observation.Attribution?.Id
                || service.Observation.Attribution?.Name != next.Observation.Attribution?.Name) return false;
        }
        // CreateObservation owns all other service payload fields as constants.
        return true;
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private static IReadOnlyList<WindowsServiceRecord>? ReadServiceEntries()
    {
        var facts = new WindowsServiceReader().Read(WindowsServiceScope.Win32);
        return facts.Available ? facts.Services : null;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private ServiceSnapshot ReadServicesByProcess()
    {
        var entries = readServices();
        if (entries is null) return ServiceSnapshot.Unavailable;
        var serviceMap = BuildServiceMap(entries);
        return new ServiceSnapshot(true, serviceMap.ServicesByProcess, serviceMap.UnavailableProcessIds);
    }
    private ServiceProcessMap BuildServiceMap(
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
            if (readProcessStartKey(item.Key) is not { } startKey)
            {
                unavailable.Add(item.Key);
                continue;
            }
            exact[item.Key] = new ServiceProcessIdentity(
                startKey,
                CreateObservation(item.Value));
        }
        return new ServiceProcessMap(exact, unavailable);
    }

    private static long? TryReadProcessStartKey(int processId)
        => BasicMetricReader.ReadCurrentCreationFileTime(processId);

    private sealed record ServiceIdentity(string ServiceName, string DisplayName);

    private sealed record ServiceProcessIdentity(
        long StartKey,
        RuntimeAttributionObservation Observation);

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

