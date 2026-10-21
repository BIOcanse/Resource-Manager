using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsD3dVideoProcessorCapabilityTests
{
    private static readonly WindowsD3dAdapterIdentity FirstAdapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity SecondAdapter = new(0x0000_0002_0000_003B, 0x8086, 0x46A6);
    private static readonly WindowsD3dVideoProcessorDescriptor Descriptor = new(
        WindowsD3dVideoFrameFormat.InterlacedTopFieldFirst, new(60, 2), 1920, 1080,
        new(30, 1), 1280, 720, WindowsD3dVideoUsage.OptimalQuality);

    [Fact]
    public async Task ExplicitContentDescriptorProjectsCompleteCapsAndZeroGroupsToTheOriginalCurrent()
    {
        Assert.Equal(WindowsD3dCapabilityFields.FeatureLevel | WindowsD3dCapabilityFields.FormatSupportFlags,
            WindowsD3dCapabilityFields.All);
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.VideoProcessorCaps, WindowsD3dApiVersion.Direct3D11, [FirstAdapter]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.VideoProcessorCaps, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            videoProcessorDescriptors: [Descriptor, Descriptor]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dRational(30, 0));
        Assert.Equal(new WindowsD3dRational(0, 0), default(WindowsD3dRational));
        var zeroRate = new WindowsD3dRational(0, 15);
        Assert.Equal(0U, zeroRate.Numerator);
        Assert.Equal(15U, zeroRate.Denominator);
        _ = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.VideoProcessorCaps,
            WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            videoProcessorDescriptors: [Descriptor with
            {
                InputFrameRate = default,
                OutputFrameRate = new(0, 15)
            }]);
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.VideoProcessorCaps, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            videoProcessorDescriptors: [Descriptor with { InputFrameFormat = (WindowsD3dVideoFrameFormat)99 }]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.VideoProcessorCaps, WindowsD3dApiVersion.Direct3D12, [FirstAdapter],
            videoProcessorDescriptors: [Descriptor]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter], videoProcessorDescriptors: [Descriptor],
            maximumVideoProcessorRateConversionCapsQueryCount: 0));

        var selected = WindowsD3dCapabilityFields.VideoProcessorCaps
            | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps;
        var scope = new WindowsD3dCapabilityScope(selected, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter, SecondAdapter], videoProcessorDescriptors: [Descriptor],
            maximumVideoProcessorRateConversionCapsQueryCount: 4);
        var reader = new RecordedReader
        {
            ResultFactory = (_, selectedFields) => new(
            [
                CreateRow(FirstAdapter, selectedFields, true, new(uint.MaxValue, 2, 3, 4, 5, 6, 0, 8, 9),
                    true, 0, []),
                CreateRow(SecondAdapter, selectedFields, true, new(7, 8, 9, 10, 11, 12, 1, 13, 14),
                    true, 1, [new(0, new(15, 16, 17, 18, uint.MaxValue))])
            ])
        };
        using var owner = EmptyOwner();
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(selected, Assert.Single(reader.Requests));
        Assert.Equal(4, catalog.Fields.Length);

        var capsFields = catalog.Fields.Where(static field =>
            field.Field.FieldName == "gpu_all_video_processor_caps").ToArray();
        var rateFields = catalog.Fields.Where(static field =>
            field.Field.FieldName == "gpu_all_video_processor_rate_conversion_caps").ToArray();
        Assert.Equal(2, capsFields.Length);
        Assert.Equal(2, rateFields.Length);
        var firstCaps = capsFields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684", StringComparison.Ordinal)).Field;
        var secondCaps = capsFields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000020000003B|pci:8086:46A6", StringComparison.Ordinal)).Field;
        var firstRate = rateFields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684", StringComparison.Ordinal)).Field;
        var secondRate = rateFields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000020000003B|pci:8086:46A6", StringComparison.Ordinal)).Field;
        Assert.Contains("input-frame-format:1|input-rate:60/2|input-size:1920x1080|output-rate:30/1|output-size:1280x720|usage:2",
            firstCaps.DeviceIdentity, StringComparison.Ordinal);
        Assert.NotEqual(firstCaps.DeviceIdentity, secondCaps.DeviceIdentity);
        Assert.Equal("object", DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps.ValueKind);
        Assert.Equal("d3d11_video_processor_caps", DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps.Unit);
        Assert.Equal("object", DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps.ValueKind);

        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(firstCaps).Status);
        using (var caps = JsonDocument.Parse(backend.ReadCached(firstCaps).Value!.Structure!.Value.GetRawText()))
        {
            var native = caps.RootElement.GetProperty("caps");
            Assert.Equal(9, native.EnumerateObject().Count());
            Assert.Equal(uint.MaxValue, native.GetProperty("deviceCaps").GetUInt32());
            Assert.Equal(2U, native.GetProperty("featureCaps").GetUInt32());
            Assert.Equal(3U, native.GetProperty("filterCaps").GetUInt32());
            Assert.Equal(4U, native.GetProperty("inputFormatCaps").GetUInt32());
            Assert.Equal(5U, native.GetProperty("autoStreamCaps").GetUInt32());
            Assert.Equal(6U, native.GetProperty("stereoCaps").GetUInt32());
            Assert.Equal(0U, native.GetProperty("rateConversionCapsCount").GetUInt32());
            Assert.Equal(8U, native.GetProperty("maxInputStreams").GetUInt32());
            Assert.Equal(9U, native.GetProperty("maxStreamStates").GetUInt32());
        }
        using (var zeroGroups = JsonDocument.Parse(backend.ReadCached(firstRate).Value!.Structure!.Value.GetRawText()))
        {
            Assert.Equal(0U, zeroGroups.RootElement.GetProperty("rateConversionCapsCount").GetUInt32());
            Assert.Empty(zeroGroups.RootElement.GetProperty("rateConversionCaps").EnumerateArray());
        }
        using (var recordedGroup = JsonDocument.Parse(backend.ReadCached(secondRate).Value!.Structure!.Value.GetRawText()))
        {
            var group = Assert.Single(recordedGroup.RootElement.GetProperty("rateConversionCaps").EnumerateArray());
            Assert.Equal(0U, group.GetProperty("index").GetUInt32());
            Assert.Equal(5, group.GetProperty("caps").EnumerateObject().Count());
            Assert.Equal(15U, group.GetProperty("caps").GetProperty("pastFrames").GetUInt32());
            Assert.Equal(16U, group.GetProperty("caps").GetProperty("futureFrames").GetUInt32());
            Assert.Equal(17U, group.GetProperty("caps").GetProperty("processorCaps").GetUInt32());
            Assert.Equal(18U, group.GetProperty("caps").GetProperty(JsonNamingPolicy.CamelCase.ConvertName(
                nameof(WindowsD3dRateConversionCapsValue.ITelecineCaps))).GetUInt32());
            Assert.Equal(uint.MaxValue, group.GetProperty("caps").GetProperty("customRateCount").GetUInt32());
        }
        Assert.Equal(WindowsD3dCapabilityFields.All, WindowsD3dCapabilityFields.FeatureLevel | WindowsD3dCapabilityFields.FormatSupportFlags);

        var requestsBeforeCache = reader.Requests.Count;
        _ = backend.ReadCached(firstRate);
        Assert.Equal(requestsBeforeCache, reader.Requests.Count);

        reader.ResultFactory = (request, selectedFields) => CreateResult(request, selectedFields,
            capsComplete: true, rateComplete: false, count: 1, groups: []);
        var failedReadings = await backend.CollectAsync([firstCaps, firstRate], default);
        var failedRate = failedReadings.Single(reading => reading.Field == firstRate);
        var successfulCaps = failedReadings.Single(reading => reading.Field == firstCaps);
        Assert.Equal(FieldObservationStatus.Unavailable, failedRate.Status);
        Assert.Null(failedRate.Value);
        Assert.Equal(FieldObservationStatus.Current, successfulCaps.Status);
        Assert.NotNull(successfulCaps.Value?.Structure);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(firstRate));
        Assert.Null(owner.ReadState().InformationFields.FindField(firstRate)!.Value);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(firstCaps).Status);
        Assert.Equal(WindowsD3dCapabilityFields.VideoProcessorCaps
            | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps, reader.Requests.Last());

        reader.ResultFactory = (request, selectedFields) => CreateResult(request, selectedFields,
            capsComplete: false, rateComplete: true, count: 0, groups: []);
        var failedCaps = Assert.Single(await backend.CollectAsync([firstCaps], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failedCaps.Status);
        Assert.Null(failedCaps.Value);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(firstCaps));
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(firstCaps).Status);

        using var borrow = access.BorrowSubscriptionCapture();
        var borrowed = await borrow.CollectAsync([secondRate], new Permit(), default);
        Assert.True(borrowed.Committed);
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(borrow.Bind([secondRate])).CollectionItemIds.Single());
        Assert.Equal(backend.ReadCached(secondRate), borrowed.Readings.Single());
        Assert.NotNull(owner.ReadState().InformationFields.FindField(secondRate));
    }

    [Fact]
    public async Task ProcessorRateGroupBoundInvalidatesTheWholeDescriptorWithoutPartialPublication()
    {
        var selected = WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps;
        var scope = new WindowsD3dCapabilityScope(selected, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter, SecondAdapter], videoProcessorDescriptors: [Descriptor],
            maximumVideoProcessorRateConversionCapsQueryCount: 1);
        var reader = new RecordedReader
        {
            ResultFactory = (_, selectedFields) => new(
            [
                CreateRow(FirstAdapter, selectedFields, false, null,
                    true, 1, [new(0, new(1, 2, 3, 4, 5))]),
                CreateRow(SecondAdapter, selectedFields, false, null,
                    false, 1, [])
            ])
        };
        using var owner = EmptyOwner();
        var backend = owner.CreateD3dCapabilityInformation(scope, reader).Backend;
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(2, catalog.Fields.Length);
        var first = catalog.Fields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).Field;
        var second = catalog.Fields.Single(item => item.Field.DeviceIdentity.Contains("luid:000000020000003B", StringComparison.Ordinal)).Field;
        Assert.Equal("gpu_all_video_processor_rate_conversion_caps", second.FieldName);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(first).Status);
        using (var firstResult = JsonDocument.Parse(backend.ReadCached(first).Value!.Structure!.Value.GetRawText()))
        {
            Assert.Equal(1U, firstResult.RootElement.GetProperty("rateConversionCapsCount").GetUInt32());
            Assert.Single(firstResult.RootElement.GetProperty("rateConversionCaps").EnumerateArray());
        }
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(second).Status);
        Assert.Null(backend.ReadCached(second).Value);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(second));
        Assert.Equal(selected, reader.Requests.Last());

        Assert.Equal((10, 9, 10), WindowsD3dCapabilityNativeReader.D3d11VideoProcessorVTableSlots);
        Assert.Equal((40, 36, 20), WindowsD3dCapabilityNativeReader.D3d11VideoProcessorPayloadSizes);
        Assert.Equal((0, 4, 12, 16, 20, 28, 32, 36), WindowsD3dCapabilityNativeReader.D3d11VideoProcessorContentDescriptorOffsets);
        Assert.Equal((0, 4, 8, 12, 16, 20, 24, 28, 32), WindowsD3dCapabilityNativeReader.D3d11VideoProcessorCapsOffsets);
        Assert.Equal((0, 4, 8, 12, 16), WindowsD3dCapabilityNativeReader.D3d11VideoProcessorRateConversionCapsOffsets);
    }

    private static WindowsD3dCapabilityReadResult CreateResult(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected, bool capsComplete, bool rateComplete,
        uint? count, ImmutableArray<WindowsD3dIndexedRateConversionCapsRead> groups)
        => new([.. scope.Adapters.Select(adapter => CreateRow(adapter, selected, capsComplete,
            capsComplete ? new(adapter == FirstAdapter ? uint.MaxValue : 7, 2, 3, 4, 5, 6, count ?? 0, 8, 9) : null,
            rateComplete, count, groups))]);

    private static WindowsD3dAdapterCapabilityRead CreateRow(WindowsD3dAdapterIdentity adapter,
        WindowsD3dCapabilityFields selected, bool capsComplete, WindowsD3dVideoProcessorCapsValue? caps,
        bool rateComplete, uint? count, ImmutableArray<WindowsD3dIndexedRateConversionCapsRead> groups)
        => new(adapter, WindowsD3dApiVersion.Direct3D11, null, [], null, [], [],
            (selected & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0
                ? [new WindowsD3dVideoProcessorCapsRead(Descriptor, capsComplete, caps)]
                : [],
            (selected & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0
                ? [new WindowsD3dVideoProcessorRateConversionCapsRead(Descriptor, rateComplete, count,
                    rateComplete ? groups : [])]
                : []);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private sealed class RecordedReader : IWindowsD3dCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsD3dCapabilityFields> requests = new();
        public IReadOnlyList<WindowsD3dCapabilityFields> Requests => requests.ToArray();
        public Func<WindowsD3dCapabilityScope, WindowsD3dCapabilityFields, WindowsD3dCapabilityReadResult> ResultFactory { get; set; }
            = static (_, _) => throw new InvalidOperationException("A recorded D3D processor result must be configured.");

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            return ResultFactory(scope, selectedFields);
        }
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
