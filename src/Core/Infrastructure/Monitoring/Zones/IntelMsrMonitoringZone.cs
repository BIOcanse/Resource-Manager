using ResourceManager.App.Infrastructure.Control.Writers;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class IntelMsrMonitoringZone : MonitoringSourceZone
{
    private readonly IntelMsrReader reader;
    public IntelMsrMonitoringZone(IHostEnvironment environment) : this(environment.ContentRootPath) { }
    public IntelMsrMonitoringZone(string contentRootPath) : base(MonitoringSourceZoneIds.VendorIntelMsr)
        => reader = new(() => IntelCpuHardwareBinding.Open(contentRootPath), WindowsCpuThreadCpuidReader.CreateNativeAccess);
    internal IntelMsrMonitoringZone(IntelMsrReader reader) : base(MonitoringSourceZoneIds.VendorIntelMsr) => this.reader = reader;
    internal IReadOnlyList<IntelMsrMetricRead> Read(IReadOnlyList<IntelMsrCatalogIdentity> devices,
        IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>> requested)
        => CanRead ? reader.Read(devices, requested) : [];
}
