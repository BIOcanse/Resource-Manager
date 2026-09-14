using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotProductionAuthorityTests
{
    [Fact]
    public void CompiledSourceIdsMapToExactMonitoringZones()
    {
        (string SourceId, string ZoneId)[] mappings =
        [
            (
                NativeMetricSnapshotSourceCatalog.WindowsCpu,
                MonitoringSourceZoneIds.WindowsCpu),
            (
                NativeMetricSnapshotSourceCatalog.WindowsMemory,
                MonitoringSourceZoneIds.WindowsMemory),
            (
                NativeMetricSnapshotSourceCatalog.WindowsVirtualMemory,
                MonitoringSourceZoneIds.WindowsVirtualMemory),
            (
                NativeMetricSnapshotSourceCatalog.WindowsGpuAdapterOrder,
                MonitoringSourceZoneIds.WindowsGpuAdapterOrder),
            (
                NativeMetricSnapshotSourceCatalog.PdhCpuFrequency,
                MonitoringSourceZoneIds.PdhCpuFrequency),
            (
                NativeMetricSnapshotSourceCatalog.PdhGpuEngine,
                MonitoringSourceZoneIds.PdhGpuEngine),
            (
                NativeMetricSnapshotSourceCatalog.NvidiaNvml,
                MonitoringSourceZoneIds.VendorNvidiaNvml),
            (
                NativeMetricSnapshotSourceCatalog.NvidiaNvapi,
                MonitoringSourceZoneIds.VendorNvidiaNvapi),
            (
                NativeMetricSnapshotSourceCatalog.AmdAdlx,
                MonitoringSourceZoneIds.VendorAmdAdlx),
            (
                NativeMetricSnapshotSourceCatalog.AmdSmu,
                MonitoringSourceZoneIds.VendorAmdSmu),
            (
                NativeMetricSnapshotSourceCatalog.WindowsStorageSensors,
                MonitoringSourceZoneIds.WindowsStorageSensors),
            (
                NativeMetricSnapshotSourceCatalog.HardwareMonitorWmi,
                MonitoringSourceZoneIds.HardwareMonitorWmi),
            (
                NativeMetricSnapshotSourceCatalog.NotebookOemFan,
                MonitoringSourceZoneIds.NotebookOemFan),
            (
                NativeMetricSnapshotSourceCatalog.PdhSystemIo,
                MonitoringSourceZoneIds.PdhSystemIo)
        ];

        Assert.Equal(14, mappings.Length);
        Assert.Equal(
            mappings.Length,
            mappings.Select(static value => value.SourceId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            mappings,
            mapping => Assert.Equal(
                mapping.ZoneId,
                NativeMetricSnapshotSourceCatalog.ResolveZoneId(
                    mapping.SourceId)));
        Assert.Throws<InvalidOperationException>(
            () => NativeMetricSnapshotSourceCatalog.ResolveZoneId(
                "unknown.source"));
    }

    [Fact]
    public void ManagedMetricSnapshotAuthorityIsPhysicallyRetired()
    {
        var monitoringRoot = Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "Monitoring");
        string[] retiredFiles =
        [
            "HardwareMetricSnapshotState.cs",
            "WindowsHardwareMetricSampler.GpuMetricSelection.cs",
            "WindowsHardwareMetricSampler.ItemProjection.cs",
            "WindowsHardwareMetricSampler.ItemProjection.Gpu.cs",
            "WindowsHardwareMetricSampler.ItemProjection.GpuSensors.cs",
            "WindowsHardwareMetricSampler.ItemProjection.PlatformSensors.cs",
            "WindowsHardwareMetricSampler.ItemProjection.SensorFormatting.cs",
            "WindowsHardwareMetricSampler.ItemProjection.SystemIo.cs",
            "WindowsHardwareMetricSampler.ProviderOwnership.cs",
            "WindowsHardwareMetricSampler.RequestPlanning.cs",
            "WindowsHardwareMetricSampler.Utilities.cs",
            "MetricProviderOwnershipPlanCompiler.cs"
        ];

        Assert.All(
            retiredFiles,
            file => Assert.False(
                File.Exists(Path.Combine(monitoringRoot, file)),
                file));

        var sampler = File.ReadAllText(Path.Combine(
            monitoringRoot,
            "WindowsHardwareMetricSampler.cs"));
        Assert.Contains(
            "HostManagerMetricSnapshotOwner",
            sampler,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HardwareMetricSnapshotState",
            sampler,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CompiledMetricProviderOwnershipPlan",
            sampler,
            StringComparison.Ordinal);
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath]
        string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var candidate = Path.GetFullPath(Path.Combine(
                sourceDirectory,
                "..",
                "Resource Manager-APP"));
            if (File.Exists(Path.Combine(
                    candidate,
                    "ResourceManager.App.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "The Resource Manager application root could not be located.");
    }
}
