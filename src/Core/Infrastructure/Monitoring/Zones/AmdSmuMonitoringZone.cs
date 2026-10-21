using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;

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
        reader = AmdSmuHardwareBinding.CreateReader(contentRootPath);
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
