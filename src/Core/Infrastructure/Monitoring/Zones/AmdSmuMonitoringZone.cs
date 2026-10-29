using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class AmdSmuMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly AmdSmuCpuSensorReader reader;
    private readonly AmdSmuCcdBinding ccd;

    public AmdSmuMonitoringZone(IHostEnvironment environment, IManagementInventoryRevisionOwner inventoryRevisionOwner)
        : this(environment.ContentRootPath, () => inventoryRevisionOwner.Current.ComponentRevision)
    {
    }

    /// <summary>Standalone composition without a component manager. Recreate after package changes.</summary>
    public AmdSmuMonitoringZone(string contentRootPath)
        : this(contentRootPath, static () => "0")
    {
    }

    private AmdSmuMonitoringZone(string contentRootPath, Func<string> readComponentRevision)
        : this(AmdSmuHardwareBinding.CreateReader(contentRootPath),
            AmdSmuHardwareBinding.CreateCcdResource(contentRootPath, readComponentRevision))
    {
    }

    internal AmdSmuMonitoringZone(AmdSmuCpuSensorReader reader, AmdSmuCcdBinding ccd)
        : base(MonitoringSourceZoneIds.VendorAmdSmu)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.ccd = ccd ?? throw new ArgumentNullException(nameof(ccd));
    }

    internal CpuSensorMetrics Read(AmdSmuCpuSensorReadRequest request)
    {
        lock (gate)
            return CanRead ? reader.Read(request) : CreateFrozen();
    }

    internal AmdCcdTopologyRead ReadCcdIdentities(CpuOsTopologyInformation? topology, uint maximum)
    {
        lock (gate)
            return CanRead ? ccd.Discover(topology, maximum)
                : new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    }

    internal IReadOnlyDictionary<string, double?> ReadCcd(IReadOnlyList<AmdCcdCatalogIdentity> identities,
        IReadOnlySet<string> requested)
    {
        lock (gate)
            return CanRead ? identities.Where(identity => requested.Contains(identity.ExactIdentityKey))
                .ToDictionary(identity => identity.ExactIdentityKey, ccd.Read, StringComparer.Ordinal)
                : new Dictionary<string, double?>(StringComparer.Ordinal);
    }

    public override void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (gate)
        {
            base.ApplyMode(mode);
            if (mode == ResourceManagerComputeZoneMode.Freeze)
                try { reader.ReleaseSession(); } finally { ccd.ReleaseSession(); }
        }
    }

    public void Dispose()
    {
        lock (gate)
            try { reader.Dispose(); } finally { ccd.Dispose(); }
    }

    private static CpuSensorMetrics CreateFrozen()
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD SMU / PawnIO",
                HardwareSensorProviderStates.Frozen,
                "AMD SMU CPU 传感监控源当前处于功能区冻结。"),
            null,
            null,
            null,
            null);
    }
}
