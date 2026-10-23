using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class AmdSmuMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly AmdSmuCpuSensorReader reader;
    private readonly AmdCcdMetricResource ccd;

    public AmdSmuMonitoringZone(IHostEnvironment environment)
        : this(environment.ContentRootPath)
    {
    }

    public AmdSmuMonitoringZone(string contentRootPath)
        : base(MonitoringSourceZoneIds.VendorAmdSmu)
    {
        reader = AmdSmuHardwareBinding.CreateReader(contentRootPath);
        ccd = AmdSmuHardwareBinding.CreateCcdResource(contentRootPath);
    }

    internal CpuSensorMetrics Read(AmdSmuCpuSensorReadRequest request)
    {
        return CanRead
            ? reader.Read(request)
            : CreateFrozen();
    }

    internal AmdCcdTopologyRead ReadCcdIdentities(CpuOsTopologyInformation? topology, uint maximum)
        => CanRead ? ccd.Discover(topology, maximum)
            : new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);

    internal IReadOnlyDictionary<string, double?> ReadCcd(IReadOnlyList<AmdCcdCatalogIdentity> identities,
        IReadOnlySet<string> requested)
        => CanRead ? identities.Where(identity => requested.Contains(identity.ExactIdentityKey))
            .ToDictionary(identity => identity.ExactIdentityKey, ccd.Read, StringComparer.Ordinal)
            : new Dictionary<string, double?>(StringComparer.Ordinal);

    public void Dispose()
    {
        try { reader.Dispose(); } finally { ccd.Dispose(); }
    }

    private static CpuSensorMetrics CreateFrozen()
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD SMU / PawnIO",
                "Frozen",
                "AMD SMU CPU 传感监控源当前处于功能区冻结。"),
            null,
            null,
            null,
            null);
    }
}
