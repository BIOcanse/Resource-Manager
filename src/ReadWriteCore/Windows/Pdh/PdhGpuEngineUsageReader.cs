using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native.Pdh;

namespace ResourceManager.ReadWrite.Windows.Pdh;

public sealed class PdhGpuEngineUsageReader
{
    private readonly INativePdhSnapshotSource snapshotSource;

    public PdhGpuEngineUsageReader()
        : this(UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    public PdhGpuEngineUsageReader(INativePdhSnapshotSource snapshotSource)
    {
        this.snapshotSource = snapshotSource;
    }

    public NativePdhProviderAvailability ProviderAvailability { get; private set; } = NativePdhProviderAvailability.NotRequested;

    public IReadOnlyDictionary<int, double> ReadUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
        => ReadUsageSnapshotByAdapterIndex(adapters).UsageByAdapterIndex;

    public PdhGpuEngineUsageRead ReadUsageSnapshotByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var adapterByLuid = CreateAdapterIndex(adapters);
        if (adapterByLuid.Count == 0)
        {
            ProviderAvailability = NativePdhProviderAvailability.NotRequested;
            return PdhGpuEngineUsageRead.NotRequested;
        }

        var read = snapshotSource.Read(WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters));
        var observation = read.Snapshot?.GpuEngineObservation;
        ProviderAvailability = EffectiveDomainAvailability(
            observation?.Availability,
            read.Availability);
        if (read.Snapshot is null || !HasPayload(ProviderAvailability))
        {
            return new PdhGpuEngineUsageRead(
                new Dictionary<int, double>(),
                ProviderAvailability,
                null);
        }

        var result = new Dictionary<int, double>();
        foreach (var pair in NativePdhGpuUsage.BusiestEngines(read.Snapshot.EngineRows))
        {
            if (!adapterByLuid.TryGetValue(pair.Key, out var adapterIndex))
            {
                continue;
            }

            result[adapterIndex] = pair.Value.Value;
        }

        var exactEngineUsage = NativePdhGpuUsage.ByExactEngine(
            read.Snapshot.EngineRows.Where(row => adapterByLuid.ContainsKey(row.AdapterLuid)));

        return new PdhGpuEngineUsageRead(
            result.ToDictionary(
                static item => item.Key,
                static item => Math.Clamp(item.Value, 0, 100)),
            ProviderAvailability,
            observation?.ObservedAt)
        {
            UsageByEngine = exactEngineUsage
        };
    }

    public IReadOnlyDictionary<int, GpuEngineSpecializedUsage> ReadSpecializedUsageByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var adapterByLuid = CreateAdapterIndex(adapters);
        if (adapterByLuid.Count == 0)
        {
            ProviderAvailability = NativePdhProviderAvailability.NotRequested;
            return new Dictionary<int, GpuEngineSpecializedUsage>();
        }

        var read = snapshotSource.Read(WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters));
        var observation = read.Snapshot?.GpuEngineObservation;
        ProviderAvailability = EffectiveDomainAvailability(
            observation?.Availability,
            read.Availability);
        if (read.Snapshot is null || !HasPayload(ProviderAvailability))
        {
            return new Dictionary<int, GpuEngineSpecializedUsage>();
        }

        var result = new Dictionary<int, GpuEngineSpecializedUsage>();
        foreach (var rows in read.Snapshot.EngineRows.GroupBy(NativePdhGpuUsage.EngineOf))
        {
            var row = rows.First();
            if (!adapterByLuid.TryGetValue(row.AdapterLuid, out var adapterIndex))
            {
                continue;
            }

            var current = result.GetValueOrDefault(adapterIndex);
            var usage = Math.Clamp(rows.Sum(row => Sanitize(row.UsagePercent)), 0, 100);
            current = row.EngineClass switch
            {
                NativePdhEngineClass.RayTracing => current with { RtUsagePercent = Math.Max(current.RtUsagePercent, usage) },
                NativePdhEngineClass.Compute => current with { CudaUsagePercent = Math.Max(current.CudaUsagePercent, usage) },
                _ => current with { TotalUsagePercent = Math.Max(current.TotalUsagePercent, usage) }
            };
            result[adapterIndex] = current;
        }

        return result.ToDictionary(
            static item => item.Key,
            static item => item.Value.Sanitize());
    }

    private static Dictionary<ulong, int> CreateAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return adapters
            .Where(static adapter => !adapter.IsSoftware && !IsZeroLuid(adapter.Luid))
            .GroupBy(static adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid))
            .ToDictionary(static group => group.Key, static group => group.First().Index);
    }

    private static bool IsZeroLuid(AdapterLuid luid)
    {
        return luid.LowPart == 0 && luid.HighPart == 0;
    }

    private static double Sanitize(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }

    private static bool HasPayload(
        NativePdhProviderAvailability availability)
        => availability is NativePdhProviderAvailability.Available
            or NativePdhProviderAvailability.LastGood;

    private static NativePdhProviderAvailability MissingDomainAvailability(
        NativePdhProviderAvailability availability)
        => availability == NativePdhProviderAvailability.NotRequested
            ? NativePdhProviderAvailability.NotRequested
            : NativePdhProviderAvailability.Unavailable;

    private static NativePdhProviderAvailability EffectiveDomainAvailability(
        NativePdhProviderAvailability? domain,
        NativePdhProviderAvailability aggregate)
        => aggregate == NativePdhProviderAvailability.LastGood
            && domain == NativePdhProviderAvailability.Available
                ? NativePdhProviderAvailability.LastGood
                : domain ?? MissingDomainAvailability(aggregate);
}

public sealed record PdhGpuEngineUsageRead(
    IReadOnlyDictionary<int, double> UsageByAdapterIndex,
    NativePdhProviderAvailability ProviderAvailability,
    DateTimeOffset? ObservedAt)
{
    public IReadOnlyDictionary<NativePdhGpuEngineIdentity, double> UsageByEngine { get; init; } =
        new Dictionary<NativePdhGpuEngineIdentity, double>();

    public static PdhGpuEngineUsageRead NotRequested { get; } = new(
        new Dictionary<int, double>(),
        NativePdhProviderAvailability.NotRequested,
        null);
}

public readonly record struct GpuEngineSpecializedUsage(
    double RtUsagePercent,
    double CudaUsagePercent,
    double TotalUsagePercent)
{
    public GpuEngineSpecializedUsage Sanitize()
    {
        return new GpuEngineSpecializedUsage(
            Math.Clamp(RtUsagePercent, 0, 100),
            Math.Clamp(CudaUsagePercent, 0, 100),
            Math.Clamp(TotalUsagePercent, 0, 100));
    }
}
