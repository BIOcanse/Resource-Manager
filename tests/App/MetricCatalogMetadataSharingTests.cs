using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed class MetricCatalogMetadataSharingTests
{
    [Fact]
    public void ReadingSelectionUsesCatalogStringsAndOneIdPerPhysicalReading()
    {
        var compiled = NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot);
        var plan = compiled with
        {
            RuleTemplates = compiled.RuleTemplates.Select(static template => template with
            { SourceId = new string(template.SourceId.AsSpan()) }).ToImmutableArray()
        };
        var catalog = NativeMetricSnapshotCatalogProjection.Create(plan,
            [new(0, "GPU", "gpu-a", 0x101, 1, 2, true)], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        foreach (var binding in catalog.RuleBindings.Values)
            Assert.Same(catalog.SourceIds[binding.SourceHandle], binding.SourceId);

        var frame = new NativeMetricSnapshotCommittedFrame(
            new() { ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1 },
            catalog.Sources.Select(static source => new NativeMetricSnapshotSourceOutput
            { SourceHandle = source.SourceHandle, CapabilityGeneration = 1 }).ToImmutableArray(),
            [], [], [], catalog);
        var tracker = new MetricReadingSourceTracker();
        var selection = tracker.Select(frame, 1);
        var repeated = tracker.Select(frame, 1);
        var sharedGroups = selection.Fields.Values.GroupBy(static reading => reading.Id)
            .Where(static group => group.Count() > 1).ToArray();
        Assert.NotEmpty(sharedGroups);
        var avoidedStringBytes = 0L;
        foreach (var group in sharedGroups)
        {
            var canonical = group.First();
            foreach (var reading in group)
            {
                Assert.Same(canonical.Id, reading.Id);
                Assert.Same(canonical.Quantity, reading.Quantity);
            }
            avoidedStringBytes += (group.Count() - 1) * ((24 + canonical.Id.Length * 2 + 7) & ~7);
        }
        Assert.True(avoidedStringBytes >= 168);
        foreach (var (handle, reading) in selection.Fields)
        {
            Assert.Same(reading.Id, repeated.Fields[handle].Id);
            Assert.Equal(reading, repeated.Fields[handle]);
            var binding = catalog.MetricBindings[handle];
            if (reading.Quantity == binding.Field!.Name)
                Assert.Same(binding.Field.Name, reading.Quantity);
        }
        // A new catalog generation creates its own bounded ID ownership.
        var next = tracker.Select(frame with
        { Header = frame.Header with { CatalogGeneration = 2 } }, 1);
        foreach (var (handle, reading) in selection.Fields)
        {
            Assert.Equal(reading, next.Fields[handle]);
            Assert.NotSame(reading.Id, next.Fields[handle].Id);
        }
    }

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
            Assert.Equal(pair[0].Field!.Code, pair[1].Field!.Code);
            Assert.Same(pair[0].Field!.Unit, pair[1].Field!.Unit);
            Assert.Same(pair[0].SourceId, pair[1].SourceId);
            Assert.Equal(pair[0].Field!.ValueKind, pair[1].Field!.ValueKind);
            Assert.Equal(pair[0].Field!.Resource, pair[1].Field!.Resource);
        }
        Assert.Contains(groups, static group => group.First().Field!.Name.Length > 0);

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
        var first = NativeMetricSnapshotCommittedProjection.Create(frame: frame, workspaceIdentity: 1);
        var second = NativeMetricSnapshotCommittedProjection.Create(frame: frame, workspaceIdentity: 1);
        foreach (var group in groups)
        {
            var pair = group.OrderBy(static binding => binding.DisplayIndex).ToArray();
            var firstValue = first.Items[pair[0].MetricId];
            var siblingValue = first.Items[pair[1].MetricId];
            Assert.Same(firstValue.FieldName, siblingValue.FieldName);
            Assert.Same(firstValue.FieldName, second.Items[pair[0].MetricId].FieldName);
            Assert.Equal(firstValue.ValueKind, siblingValue.ValueKind);
            Assert.Same(firstValue.SourceId, siblingValue.SourceId);
            Assert.Same(first.FieldBindings[pair[0].MetricId].Field,
                first.FieldBindings[pair[1].MetricId].Field);
            Assert.Same(pair[0].Field, second.FieldBindings[pair[0].MetricId].Field);
        }

        // A catalog with equivalent independently constructed field declarations follows the same
        // factual projection path. Serialized public results must match.
        var uncached = catalog with { MetricBindings = catalog.MetricBindings.ToImmutableDictionary(
            static pair => pair.Key, static pair => pair.Value with
            { Field = pair.Value.Field is { } field ? field with { } : null }) };
        var expected = NativeMetricSnapshotCommittedProjection.Create(frame: frame with { Catalog = uncached }, workspaceIdentity: 1);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected.Items), JsonSerializer.SerializeToUtf8Bytes(first.Items));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Catalog(expected)),
            JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Catalog(first)));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Read(expected)),
            JsonSerializer.SerializeToUtf8Bytes(MetricDeviceFields.Read(first)));
    }
}
