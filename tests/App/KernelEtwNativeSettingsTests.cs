using System.Collections.Frozen;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwNativeSettingsTests
{
    [Fact]
    public void SdkBuildProjectionExactlyMatchesTheProductDeclarationForEveryNativeField()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var cpu = new CpuResidencyCaptureConfiguration(plan.CpuCoreResidency.ObservationWindowMilliseconds,
            plan.CpuCoreResidency.ExecutionTimeSource.MaximumActivePairs,
            new Dictionary<int, CpuResidencyCoreAccounting>
            {
                [0] = new("core", "", 2, 0.5), [1] = new("core", "", 2, 0.5)
            }.ToFrozenDictionary(), "fixture", plan.CpuCoreResidency.ExecutionTimeSource.BucketWidthMilliseconds);
        var expected = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!, plan.CpuCoreResidency, 2);
        var selected = ReadWriteKernelEtwSettings.CreateConfiguration(cpu);
        var json = new JsonSerializerOptions { IncludeFields = true };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected, json), JsonSerializer.SerializeToUtf8Bytes(selected, json));
        selected.ByteCap = 1;
        Assert.Equal(expected.ByteCap, ReadWriteKernelEtwSettings.CreateConfiguration(cpu).ByteCap);
        Assert.DoesNotContain(typeof(WindowsProcessFieldOwner).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "ResourceManager");
    }

    [Fact]
    public void DeclaredCapsFlowThroughPublishedPlanIntoTheNativeConfiguration()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]!;
            value["region_bytes"] = 100663296;
            value["maximum_network_events"] = 131072;
            value["publication_banks"] = 4;
        });
        var config = KernelEtwNativeSettings.CreateConfiguration(changed.KernelEtwLimits!, changed.CpuCoreResidency, 16);
        Assert.Equal(100663296UL, config.ByteCap);
        Assert.Equal(131072U, config.MaximumNetworkEvents);
        Assert.Equal(4U, config.PublicationBanks);
        Assert.Equal(16U, config.Residency.MaximumProcessors);
        Assert.Equal(5000U, config.Residency.WindowMilliseconds);
        Assert.Equal(50U, config.Residency.BucketMilliseconds);
        Assert.Equal(baseline.KernelEtwLimits, KernelEtwNativeSettings.ProductDefaults);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
    }

    [Fact]
    public void NonDividingDiskBucketFlowsExactlyFromTheSelectedPointIntoNativeConfiguration()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]!;
            value["disk_bucket_milliseconds"] = 73;
            value["maximum_disk_pairs"] = 32;
        });

        Assert.True(changed.KernelEtwLimits!.IsPublished);
        Assert.NotEqual(0L, changed.KernelEtwLimits.IoRetentionMilliseconds % changed.KernelEtwLimits.DiskBucketMilliseconds);
        Assert.Equal(baseline.KernelEtwLimits! with { DiskBucketMilliseconds = 73, MaximumDiskPairs = 32 }, changed.KernelEtwLimits);
        var config = KernelEtwNativeSettings.CreateConfiguration(changed.KernelEtwLimits, changed.CpuCoreResidency, 16);
        Assert.Equal(73L, config.DiskBucketMilliseconds);
        Assert.Equal(32U, config.MaximumDiskPairs);
        Assert.Equal(baseline.KernelEtwLimits.RegionBytes, config.ByteCap);
        Assert.Equal(baseline.KernelEtwLimits.DiskBytes, config.DiskByteCap);
    }

    [Theory]
    [InlineData("region_bytes", 1)]
    [InlineData("maximum_cells", 0)]
    [InlineData("publication_banks", 1)]
    [InlineData("io_retention_milliseconds", 100)]
    [InlineData("disk_bucket_milliseconds", 0)]
    [InlineData("disk_bucket_milliseconds", -1)]
    public void InvalidOwnerCapsFailCompilationInsteadOfSubstitutingDefaults(string field, int value)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]![field] = value));
    }
}
