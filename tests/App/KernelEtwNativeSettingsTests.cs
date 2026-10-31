using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            }.ToFrozenDictionary(), "fixture");
        var expected = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!, plan.CpuCoreResidency, 2);
        var selected = ReadWriteKernelEtwSettings.CreateConfiguration(cpu);
        var json = new JsonSerializerOptions { IncludeFields = true, Converters = { new NativePointerConverter() } };
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
        Assert.Equal(baseline.KernelEtwLimits, KernelEtwNativeSettings.ProductDefaults);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
    }

    [Fact]
    public void InterruptBucketsAndNetworkReserveFlowFromTheSelectedPointIntoNativeConfiguration()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var defaults = KernelEtwNativeSettings.CreateConfiguration(baseline.KernelEtwLimits!, baseline.CpuCoreResidency, 2);
        // The declared defaults are the former native literals.
        Assert.Equal((10000u, 6u, 16u, 256u), (defaults.Interrupts.BucketMilliseconds, defaults.Interrupts.BucketCount,
            defaults.Interrupts.ReportedDrivers, defaults.NetworkInitialEvents));
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]!;
            value["interrupt_bucket_milliseconds"] = 5000;
            value["interrupt_bucket_count"] = 12;
            value["interrupt_reported_drivers"] = 8;
            value["network_initial_events"] = 64;
        });
        var native = KernelEtwNativeSettings.CreateConfiguration(changed.KernelEtwLimits!, changed.CpuCoreResidency, 2);
        Assert.Equal((5000u, 12u, 8u, 64u), (native.Interrupts.BucketMilliseconds, native.Interrupts.BucketCount,
            native.Interrupts.ReportedDrivers, native.NetworkInitialEvents));
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
    }

    [Fact]
    public void InterruptWindowThatOverflowsNativeClockIsRejectedBeforePublication()
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]!;
            value["interrupt_bucket_milliseconds"] = 4000000000u;
            value["interrupt_bucket_count"] = 4000000000u;
        }));

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

    [Fact]
    public void DeclaredSessionBufferPoolFlowsIntoTheKernelSessionInsteadOfTheTraceEventDefault()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var declared = KernelEtwNativeSettings.CreateSessionBuffers(baseline.KernelEtwLimits!);
        Assert.Equal((64, 64, 4), (declared.BufferKib, declared.MinimumBuffers, declared.PoolMib));
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]!;
            value["session_buffer_kib"] = 32;
            value["session_minimum_buffers"] = 96;
        });
        var buffers = KernelEtwNativeSettings.CreateSessionBuffers(changed.KernelEtwLimits!);
        Assert.Equal((32, 96, 3), (buffers.BufferKib, buffers.MinimumBuffers, buffers.PoolMib));
        // The pool is a managed session setting; the native owner configuration is unchanged.
        var json = new JsonSerializerOptions { IncludeFields = true, Converters = { new NativePointerConverter() } };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(KernelEtwNativeSettings.CreateConfiguration(baseline.KernelEtwLimits!, baseline.CpuCoreResidency, 2), json),
            JsonSerializer.SerializeToUtf8Bytes(KernelEtwNativeSettings.CreateConfiguration(changed.KernelEtwLimits!, changed.CpuCoreResidency, 2), json));
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EtwSessionBuffers(64, 3));
    }

    [Theory]
    [InlineData("region_bytes", 1)]
    [InlineData("maximum_threads", 0)]
    [InlineData("publication_banks", 1)]
    [InlineData("io_retention_milliseconds", 100)]
    [InlineData("disk_bucket_milliseconds", 0)]
    [InlineData("disk_bucket_milliseconds", -1)]
    [InlineData("interrupt_bucket_milliseconds", 0)]
    [InlineData("interrupt_bucket_milliseconds", 1500)] // Interrupt slices are whole seconds.
    [InlineData("interrupt_bucket_count", 0)]
    [InlineData("interrupt_reported_drivers", 0)]
    [InlineData("network_initial_events", 0)]
    [InlineData("session_buffer_kib", 0)]
    [InlineData("session_minimum_buffers", 0)]
    [InlineData("session_minimum_buffers", 3)] // 3 x 64 KiB is not whole MiB.
    public void InvalidOwnerCapsFailCompilationInsteadOfSubstitutingDefaults(string field, int value)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.KernelEtwLimits)["value"]![field] = value));
    }

    // The residency config's processor pointer is a native field too (the shared owner passes none).
    private sealed class NativePointerConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => (nint)reader.GetInt64();
        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options) => writer.WriteNumberValue((long)value);
    }
}
