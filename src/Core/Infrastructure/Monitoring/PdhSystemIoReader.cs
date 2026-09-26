using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class PdhSystemIoReader
{
    private readonly INativePdhSnapshotSource snapshotSource;

    public PdhSystemIoReader()
        : this(UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    internal PdhSystemIoReader(INativePdhSnapshotSource snapshotSource)
    {
        this.snapshotSource = snapshotSource;
    }

    public PdhSystemIoMetrics Read(PdhSystemIoReadRequest request)
    {
        if (!request.IncludeDisk && !request.IncludeNetwork)
        {
            return new PdhSystemIoMetrics(PdhDiskMetrics.NotRequested, PdhNetworkMetrics.NotRequested)
            {
                ProviderAvailability = NativePdhProviderAvailability.NotRequested,
                ObservedAt = null,
                DiskAvailability = NativePdhProviderAvailability.NotRequested,
                NetworkAvailability = NativePdhProviderAvailability.NotRequested
            };
        }

        var read = snapshotSource.Read();
        var snapshot = read.Snapshot;
        var io = snapshot?.SystemIo;
        return new PdhSystemIoMetrics(
            request.IncludeDisk ? CreateDiskMetrics(io) : PdhDiskMetrics.NotRequested,
            request.IncludeNetwork ? CreateNetworkMetrics(io) : PdhNetworkMetrics.NotRequested)
        {
            ProviderAvailability = read.Availability,
            ObservedAt = snapshot?.CapturedAt,
            DiskAvailability = request.IncludeDisk
                ? EffectiveDomainAvailability(
                    snapshot?.DiskObservation.Availability,
                    read.Availability)
                : NativePdhProviderAvailability.NotRequested,
            DiskObservedAt = request.IncludeDisk
                ? snapshot?.DiskObservation.ObservedAt
                : null,
            NetworkAvailability = request.IncludeNetwork
                ? EffectiveDomainAvailability(
                    snapshot?.NetworkObservation.Availability,
                    read.Availability)
                : NativePdhProviderAvailability.NotRequested,
            NetworkObservedAt = request.IncludeNetwork
                ? snapshot?.NetworkObservation.ObservedAt
                : null
        };
    }

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

    private static PdhDiskMetrics CreateDiskMetrics(NativePdhSystemIo? io)
    {
        if (io is null)
        {
            return PdhDiskMetrics.NotRequested;
        }

        var value = io.Value;
        return new PdhDiskMetrics(
            ReadValue(value, NativePdhIoValidMask.DiskActive, value.DiskActivePercent, clampPercent: true),
            ReadValue(value, NativePdhIoValidMask.DiskRead, value.DiskReadBytesPerSecond),
            ReadValue(value, NativePdhIoValidMask.DiskWrite, value.DiskWriteBytesPerSecond),
            ReadValue(value, NativePdhIoValidMask.DiskQueue, value.DiskQueueLength));
    }

    private static PdhNetworkMetrics CreateNetworkMetrics(NativePdhSystemIo? io)
    {
        if (io is null)
        {
            return PdhNetworkMetrics.NotRequested;
        }

        var value = io.Value;
        return new PdhNetworkMetrics(
            ReadValue(value, NativePdhIoValidMask.NetworkReceive, value.NetworkReceiveBytesPerSecond),
            ReadValue(value, NativePdhIoValidMask.NetworkSend, value.NetworkSendBytesPerSecond),
            ReadValue(value, NativePdhIoValidMask.NetworkUtilization, value.NetworkUtilizationPercent, clampPercent: true),
            ReadValue(value, NativePdhIoValidMask.NetworkBandwidth, value.NetworkBandwidthBitsPerSecond));
    }

    private static double ReadValue(
        NativePdhSystemIo io,
        NativePdhIoValidMask requiredMask,
        double value,
        bool clampPercent = false)
    {
        if ((io.ValidMask & requiredMask) == 0 || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }

        return clampPercent ? Math.Clamp(value, 0, 100) : value;
    }
}

internal sealed record PdhSystemIoReadRequest(bool IncludeDisk, bool IncludeNetwork);

internal sealed record PdhSystemIoMetrics(PdhDiskMetrics Disk, PdhNetworkMetrics Network)
{
    public NativePdhProviderAvailability ProviderAvailability { get; init; } = NativePdhProviderAvailability.NotRequested;

    public DateTimeOffset? ObservedAt { get; init; }

    public NativePdhProviderAvailability DiskAvailability { get; init; } =
        NativePdhProviderAvailability.NotRequested;

    public DateTimeOffset? DiskObservedAt { get; init; }

    public NativePdhProviderAvailability NetworkAvailability { get; init; } =
        NativePdhProviderAvailability.NotRequested;

    public DateTimeOffset? NetworkObservedAt { get; init; }
}

internal sealed record PdhDiskMetrics(
    double ActivePercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double QueueLength)
{
    public static PdhDiskMetrics NotRequested { get; } = new(0, 0, 0, 0);
}

internal sealed record PdhNetworkMetrics(
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    double UtilizationPercent,
    double BandwidthBitsPerSecond)
{
    public static PdhNetworkMetrics NotRequested { get; } = new(0, 0, 0, 0);
}
