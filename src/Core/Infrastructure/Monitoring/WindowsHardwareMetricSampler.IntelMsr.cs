using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsHardwareMetricSampler
{
    internal static NativeMetricSnapshotSourceCompletion CreateIntelMsrCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<IntelMsrMetricRead> values)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateIntelMsrCompletion(plan, sourcePlan, identity, capturedAt, values);

}


