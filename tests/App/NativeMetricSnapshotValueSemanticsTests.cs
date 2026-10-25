using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotValueSemanticsTests
{
    [Fact]
    public void CapacityItemsKeepRawTotalsThroughCurrentPublication()
    {
        const ulong gib = 1UL << 30;
        var frame = Frame(
            (MetricFieldCode.MemoryUsed, 8 * gib, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.MemoryTotal, 16 * gib, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.CommitUsed, 10 * gib, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.CommitLimit, 32 * gib, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.GpuMemoryUsed, 2 * gib, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.GpuMemoryTotal, 12 * gib, NativeMetricSnapshotMetricStatus.Current));
        var projected = NativeMetricSnapshotCommittedProjection.Create(frame, 1);
        using var current = new LastSuccessfulHardwareMetricSnapshot();
        current.Publish(projected);
        foreach (var snapshot in new[] { projected, current.Read()! })
        {
            AssertCapacity(snapshot, "memory.usage", 8 * gib, 16 * gib);
            AssertCapacity(snapshot, "virtualMemory.usage", 10 * gib, 32 * gib);
            AssertCapacity(snapshot, "gpu.0.vram", 2 * gib, 12 * gib);
            foreach (var binding in frame.Catalog.MetricBindings.Values.Where(static row =>
                row.FieldCode is MetricFieldCode.MemoryUsed or MetricFieldCode.CommitUsed or MetricFieldCode.GpuMemoryUsed))
            {
                if (snapshot.Items.TryGetValue(binding.MetricId, out var canonical))
                    Assert.Equal(snapshot.Items[binding.LegacyMetricId].Total, canonical.Total);
            }
        }
    }

    [Fact]
    public void MissingCapacityDoesNotEraseTheIndependentUsedValue()
    {
        var projected = NativeMetricSnapshotCommittedProjection.Create(Frame(
            (MetricFieldCode.MemoryUsed, 8, NativeMetricSnapshotMetricStatus.Current),
            (MetricFieldCode.MemoryTotal, 0, NativeMetricSnapshotMetricStatus.Unavailable)), 1);
        Assert.Null(projected.Items["memory.usage"].Total);
        Assert.Equal(new MetricInteger(8), projected.Items["memory.usage"].IntegerValue);
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(long.MinValue, true)]
    [InlineData(long.MaxValue, true)]
    [InlineData(-1L, false)]
    public void SignedCurrentValueFeedsHistoryWithoutChangingItsExactInteger(long signed, bool configureBeforePublication)
    {
        var frame = Frame((MetricFieldCode.NvmlClocksEvents, unchecked((ulong)signed), NativeMetricSnapshotMetricStatus.Current));
        var original = Assert.Single(frame.Metrics);
        var binding = frame.Catalog.MetricBindings[original.MetricHandle];
        var signedBinding = binding with
        {
            ValueKind = (uint)NativeMetricSnapshotValueKind.Signed64,
            Field = binding.Field! with { ValueKind = "signed64", Unit = "count" }
        };
        original.ValueKind = (uint)NativeMetricSnapshotValueKind.Signed64;
        frame = frame with
        {
            Metrics = [original],
            Catalog = frame.Catalog with
            {
                MetricBindings = frame.Catalog.MetricBindings.SetItem(binding.MetricHandle, signedBinding),
                RuleBindings = frame.Catalog.RuleBindings.SetItem(binding.RuleHandle, signedBinding)
            }
        };
        var projected = NativeMetricSnapshotCommittedProjection.Create(frame, 1);
        using var current = new LastSuccessfulHardwareMetricSnapshot();
        if (configureBeforePublication)
            current.ConfigureHistory([new(binding.MetricId, 3)]);
        current.Publish(projected, MetricSampleRequest.ForIds([binding.MetricId]));
        if (!configureBeforePublication)
            current.ConfigureHistory([new(binding.MetricId, 3)]);
        var snapshot = current.Read()!;
        Assert.Equal(MetricInteger.FromSigned64(signed), snapshot.Items[binding.MetricId].IntegerValue);
        Assert.Null(snapshot.Items[binding.MetricId].NumericValue);
        var sample = Assert.Single(snapshot.History[binding.MetricId]);
        Assert.Equal((double)signed, sample.Value);
        Assert.Equal(projected.CapturedAt, sample.ObservedAt);
    }

    private static void AssertCapacity(HardwareMetricSnapshot snapshot, string id, ulong used, ulong total)
    {
        var value = snapshot.Items[id];
        Assert.Equal(new MetricInteger(used), value.IntegerValue);
        Assert.Null(value.NumericValue);
        Assert.Equal((double)total, value.Total);
        Assert.Equal("B", value.Unit);
    }

    private static NativeMetricSnapshotCommittedFrame Frame(
        params (MetricFieldCode Code, ulong Value, NativeMetricSnapshotMetricStatus Status)[] values)
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new(0, "Recorded GPU", "gpu-recorded", 0xA1, 1, 1, true)], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var outputs = values.Select(entry =>
        {
            var binding = catalog.MetricBindings.Values.First(row => row.FieldCode == entry.Code);
            return new NativeMetricSnapshotMetricOutput
            {
                MetricHandle = binding.MetricHandle,
                SourceHandle = binding.SourceHandle,
                SourceGeneration = 1,
                Status = (uint)entry.Status,
                ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64,
                ValueBits = entry.Value,
                ObservedAtMilliseconds = 1_000
            };
        }).ToArray();
        return new(new NativeMetricSnapshotSnapshotHeader
        {
            CapturedAtMilliseconds = 2_000,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1
        }, [], [.. outputs], [], [], catalog);
    }
}
