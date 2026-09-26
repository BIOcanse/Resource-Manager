using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class PdhGpuEngineUsageReader
{
    private readonly INativePdhSnapshotSource snapshotSource;

    public PdhGpuEngineUsageReader()
        : this(UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    internal PdhGpuEngineUsageReader(INativePdhSnapshotSource snapshotSource)
    {
        this.snapshotSource = snapshotSource;
    }

    internal NativePdhProviderAvailability ProviderAvailability { get; private set; } = NativePdhProviderAvailability.NotRequested;

    public IReadOnlyDictionary<int, double> ReadUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
        => ReadUsageSnapshotByAdapterIndex(adapters).UsageByAdapterIndex;

    internal PdhGpuEngineUsageRead ReadUsageSnapshotByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var adapterByLuid = CreateAdapterIndex(adapters);
        if (adapterByLuid.Count == 0)
        {
            ProviderAvailability = NativePdhProviderAvailability.NotRequested;
            return PdhGpuEngineUsageRead.NotRequested;
        }

        var read = snapshotSource.Read(NativePdhAdapterIdentity.ComputeTopologyFingerprint(adapters));
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
        foreach (var row in read.Snapshot.EngineRows)
        {
            if (!adapterByLuid.TryGetValue(row.AdapterLuid, out var adapterIndex))
            {
                continue;
            }

            result[adapterIndex] = result.GetValueOrDefault(adapterIndex) + Sanitize(row.UsagePercent);
        }

        return new PdhGpuEngineUsageRead(
            result.ToDictionary(
                static item => item.Key,
                static item => Math.Clamp(item.Value, 0, 100)),
            ProviderAvailability,
            observation?.ObservedAt);
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

        var read = snapshotSource.Read(NativePdhAdapterIdentity.ComputeTopologyFingerprint(adapters));
        var observation = read.Snapshot?.GpuEngineObservation;
        ProviderAvailability = EffectiveDomainAvailability(
            observation?.Availability,
            read.Availability);
        if (read.Snapshot is null || !HasPayload(ProviderAvailability))
        {
            return new Dictionary<int, GpuEngineSpecializedUsage>();
        }

        var result = new Dictionary<int, GpuEngineSpecializedUsage>();
        foreach (var row in read.Snapshot.EngineRows)
        {
            if (!adapterByLuid.TryGetValue(row.AdapterLuid, out var adapterIndex))
            {
                continue;
            }

            var current = result.GetValueOrDefault(adapterIndex);
            var usage = Sanitize(row.UsagePercent);
            current = row.EngineClass switch
            {
                NativePdhEngineClass.RayTracing => current with { RtUsagePercent = current.RtUsagePercent + usage },
                NativePdhEngineClass.Compute => current with { CudaUsagePercent = current.CudaUsagePercent + usage },
                _ => current with { TotalUsagePercent = current.TotalUsagePercent + usage }
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
            .GroupBy(static adapter => NativePdhAdapterIdentity.Pack(adapter.Luid))
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

internal sealed record PdhGpuEngineUsageRead(
    IReadOnlyDictionary<int, double> UsageByAdapterIndex,
    NativePdhProviderAvailability ProviderAvailability,
    DateTimeOffset? ObservedAt)
{
    internal static PdhGpuEngineUsageRead NotRequested { get; } = new(
        new Dictionary<int, double>(),
        NativePdhProviderAvailability.NotRequested,
        null);
}

internal readonly record struct GpuEngineSpecializedUsage(
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
