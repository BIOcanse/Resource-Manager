using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class MetricCatalogMetadataSharingTests
{
    [Fact]
    public void SameKindDevicesShareMetadataThroughCatalogAndRepeatedPublicationWithoutChangingWireValues()
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new(0, "GPU A", "gpu-a", 0x101, 1, 1, true),
             new(1, "GPU B", "gpu-b", 0x102, 1, 1, true)], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var groups = catalog.MetricBindings.Values.Where(static binding => binding.Field is not null
                && binding.Device.Kind == "gpu_adapter")
            .GroupBy(static binding => (binding.SourceId, binding.Field!.Name)).ToArray();
        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            var pair = group.OrderBy(static binding => binding.DisplayIndex).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.NotEqual(pair[0].DeviceIdentity, pair[1].DeviceIdentity);
            Assert.Same(pair[0].Field, pair[1].Field);
            Assert.Same(pair[0].Field!.Meaning, pair[1].Field!.Meaning);
            Assert.Same(pair[0].Field!.Unit, pair[1].Field!.Unit);
            Assert.Same(pair[0].SourceId, pair[1].SourceId);
            Assert.Same(pair[0].FieldDetail, pair[1].FieldDetail);
            Assert.Same(pair[0].FieldGroup, pair[1].FieldGroup);
        }
        Assert.Contains(groups, static group => group.First().Field!.Meaning is not null);

        var frame = new NativeMetricSnapshotCommittedFrame(
            new() { CapturedAtMilliseconds = 1_700_000_000_000, ConfigurationGeneration = 1,
                CatalogGeneration = 1, CommittedGeneration = 1 }, [],
            catalog.MetricBindings.Values.Select(static binding => new NativeMetricSnapshotMetricOutput
            {
                MetricHandle = binding.MetricHandle, SourceHandle = binding.SourceHandle,
                Status = (uint)NativeMetricSnapshotMetricStatus.Current, ValueKind = binding.ValueKind,
                ValueBits = binding.ValueKind == (uint)NativeMetricSnapshotValueKind.Float64
                    ? unchecked((ulong)BitConverter.DoubleToInt64Bits(42)) : 42,
                ObservedAtMilliseconds = 1_700_000_000_000
            }).ToImmutableArray(), [], [], catalog);
        var first = NativeMetricSnapshotCommittedProjection.Create(frame, "CPU", "RAM", 1);
        var second = NativeMetricSnapshotCommittedProjection.Create(frame, "CPU", "RAM", 1);
        foreach (var group in groups)
        {
            var pair = group.OrderBy(static binding => binding.DisplayIndex).ToArray();
            var firstValue = first.Items[pair[0].MetricId];
            var siblingValue = first.Items[pair[1].MetricId];
            Assert.Same(firstValue.Detail, siblingValue.Detail);
            Assert.Same(firstValue.Detail, second.Items[pair[0].MetricId].Detail);
            Assert.Same(firstValue.Group, siblingValue.Group);
            Assert.Same(firstValue.SourceId, siblingValue.SourceId);
            Assert.Same(first.FieldBindings[pair[0].MetricId].Field,
                first.FieldBindings[pair[1].MetricId].Field);
            Assert.Same(pair[0].Field, second.FieldBindings[pair[0].MetricId].Field);
        }

        // A frame without prebuilt presentation strings follows the previous
        // per-publication construction path. Serialized public results must match.
        var uncached = catalog with { MetricBindings = catalog.MetricBindings.ToImmutableDictionary(
            static pair => pair.Key, static pair => pair.Value with
            { Field = pair.Value.Field is { } field ? field with { } : null,
                FieldDetail = null, FieldGroup = null }) };
        var expected = NativeMetricSnapshotCommittedProjection.Create(frame with { Catalog = uncached }, "CPU", "RAM", 1);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected.Items), JsonSerializer.SerializeToUtf8Bytes(first.Items));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Catalog(expected)),
            JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Catalog(first)));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Read(expected)),
            JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Read(first)));
    }
}
