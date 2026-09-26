using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class AmdSmuMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly AmdSmuCpuSensorReader reader;

    public AmdSmuMonitoringZone(IHostEnvironment environment)
        : this(environment.ContentRootPath)
    {
    }

    public AmdSmuMonitoringZone(string contentRootPath)
        : base(MonitoringSourceZoneIds.VendorAmdSmu)
    {
        reader = new AmdSmuCpuSensorReader(contentRootPath);
    }

    internal CpuSensorMetrics Read(AmdSmuCpuSensorReadRequest request)
    {
        return CanRead
            ? reader.Read(request)
            : CreateFrozen();
    }

    public void Dispose()
    {
        reader.Dispose();
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
