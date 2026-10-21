using System.Collections.Concurrent;
using System.Collections.Immutable;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsD3dCapabilityInformationTests
{
    private static readonly WindowsD3dAdapterIdentity FirstAdapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity SecondAdapter = new(0x0000_0002_0000_003B, 0x8086, 0x46A6);

    [Fact]
    public async Task ExplicitAdapterApiAndFormatScopeUsesOriginalCurrentAndSelectedCapture()
    {
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.All,
            WindowsD3dApiVersion.Direct3D12, [FirstAdapter, SecondAdapter],
            [WindowsD3dFormat.R8G8B8A8UNorm, WindowsD3dFormat.D32Float]);
        var reader = new RecordedReader
        {
            FeatureLevels = [WindowsD3dFeatureLevel.Level12_1, WindowsD3dFeatureLevel.Level11_0]
        };
        using var owner = EmptyOwner();
        var access = owner.CreateD3dCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);
        Assert.Equal(WindowsD3dCapabilityFields.All, Assert.Single(reader.Requests));

        var firstFeature = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_d3d_feature_level"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).Field;
        var featureReading = access.Backend.ReadCached(firstFeature);
        Assert.Equal(FieldObservationStatus.Current, featureReading.Status);
        Assert.Equal("49408", featureReading.Value?.Integer);
        Assert.Contains(catalog.Devices, device => device.Identity == firstFeature.DeviceIdentity
            && device.Resource == "gpu_adapter");

        var firstFormats = catalog.Fields.Where(field => field.Field.FieldName == "gpu_all_d3d_format_support_flags"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, firstFormats.Length);
        var zeroFormat = firstFormats.Single(field => field.Field.DeviceIdentity.EndsWith("dxgi-format:40", StringComparison.Ordinal)).Field;
        var zeroReading = access.Backend.ReadCached(zeroFormat);
        Assert.Equal(FieldObservationStatus.Current, zeroReading.Status);
        Assert.Equal("0", zeroReading.Value?.Integer);
        Assert.Contains(catalog.Devices, device => device.Identity == zeroFormat.DeviceIdentity
            && device.Resource == "gpu_adapter_format");
        var packedFlags = firstFormats.Single(field => field.Field.DeviceIdentity.EndsWith("dxgi-format:28", StringComparison.Ordinal)).Field;
        Assert.Equal("281474976710689", access.Backend.ReadCached(packedFlags).Value?.Integer);

        var countAfterDiscovery = reader.Requests.Count;
        _ = access.Backend.ReadCached(packedFlags);
        Assert.Equal(countAfterDiscovery, reader.Requests.Count);

        reader.FeatureLevels = [WindowsD3dFeatureLevel.Level12_0, WindowsD3dFeatureLevel.Level11_0];
        var selectedFeature = Assert.Single(await access.Backend.CollectAsync([firstFeature], default));
        Assert.Equal(FieldObservationStatus.Current, selectedFeature.Status);
        Assert.Equal("49152", selectedFeature.Value?.Integer);
        Assert.Equal(WindowsD3dCapabilityFields.FeatureLevel, reader.Requests.Last());
        Assert.Equal("281474976710689", access.Backend.ReadCached(packedFlags).Value?.Integer);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([packedFlags]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        reader.FormatFlags[WindowsD3dFormat.R8G8B8A8UNorm] = 0;
        var borrowed = await borrow.CollectAsync([packedFlags], new Permit(), default);
        var borrowedReading = Assert.Single(borrowed.Readings);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, borrowedReading.Status);
        Assert.Equal("0", borrowedReading.Value?.Integer);
        Assert.Equal(WindowsD3dCapabilityFields.FormatSupportFlags, reader.Requests.Last());
        Assert.Equal(borrowedReading, access.Backend.ReadCached(packedFlags));
        Assert.NotNull(owner.ReadState().InformationFields.FindField(packedFlags));

        await owner.SampleTopologyAsync(null, CancellationToken.None, requireDemand: false);
        Assert.Equal("0", access.Backend.ReadCached(packedFlags).Value?.Integer);
        Assert.Equal("49152", access.Backend.ReadCached(firstFeature).Value?.Integer);
    }

    [Fact]
    public async Task ReaderAndShapeFailuresClearOnlySelectedValuesWhileCanceledOrRejectedCapturesDoNotCommit()
    {
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.All,
            WindowsD3dApiVersion.Direct3D11, [FirstAdapter], [WindowsD3dFormat.BC1UNorm]);
        var reader = new RecordedReader
        {
            FeatureLevels = [WindowsD3dFeatureLevel.Level12_1]
        };
        reader.FormatFlags[WindowsD3dFormat.BC1UNorm] = 0x0000_0001_0000_0021UL;
        using var owner = EmptyOwner();
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var feature = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_d3d_feature_level").Field;
        var format = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_d3d_format_support_flags").Field;
        var featureBefore = access.Backend.ReadCached(feature);
        var formatBefore = access.Backend.ReadCached(format);
        Assert.Equal("49408", featureBefore.Value?.Integer);
        Assert.Equal("4294967329", formatBefore.Value?.Integer);

        reader.Mode = 2;
        var beforeFailure = owner.ReadState();
        var failedFeature = Assert.Single(await access.Backend.CollectAsync([feature], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failedFeature.Status);
        Assert.Null(failedFeature.Value);
        Assert.Null(failedFeature.ObservedAt);
        Assert.NotSame(beforeFailure, owner.ReadState());
        Assert.Equal(failedFeature, access.Backend.ReadCached(feature));
        Assert.Equal(formatBefore, access.Backend.ReadCached(format));

        reader.Mode = 0;
        var restoredFeature = Assert.Single(await access.Backend.CollectAsync([feature], default));
        Assert.Equal(FieldObservationStatus.Current, restoredFeature.Status);
        Assert.Equal("49408", restoredFeature.Value?.Integer);
        Assert.Equal(formatBefore, access.Backend.ReadCached(format));

        reader.Mode = 3;
        var beforeMalformed = owner.ReadState();
        var malformedFeature = Assert.Single(await access.Backend.CollectAsync([feature], default));
        Assert.Equal(FieldObservationStatus.Unavailable, malformedFeature.Status);
        Assert.Null(malformedFeature.Value);
        Assert.Null(malformedFeature.ObservedAt);
        Assert.NotSame(beforeMalformed, owner.ReadState());
        Assert.Equal(malformedFeature, access.Backend.ReadCached(feature));
        Assert.Equal(formatBefore, access.Backend.ReadCached(format));

        reader.Mode = 0;
        restoredFeature = Assert.Single(await access.Backend.CollectAsync([feature], default));
        Assert.Equal(FieldObservationStatus.Current, restoredFeature.Status);
        var featureBeforeFormatFailure = access.Backend.ReadCached(feature);
        reader.Mode = 1;
        var unavailable = Assert.Single(await access.Backend.CollectAsync([format], default));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        Assert.Null(unavailable.ObservedAt);
        Assert.Equal(featureBeforeFormatFailure, access.Backend.ReadCached(feature));

        var beforeCancel = owner.ReadState();
        var requestCount = reader.Requests.Count;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([feature], canceled.Token));
        Assert.Same(beforeCancel, owner.ReadState());
        Assert.Equal(requestCount, reader.Requests.Count);

        using var borrow = access.BorrowSubscriptionCapture();
        beforeCancel = owner.ReadState();
        var rejected = await borrow.CollectAsync([feature], new Permit(accept: false), default);
        Assert.False(rejected.Committed);
        Assert.Same(beforeCancel, owner.ReadState());
        Assert.Equal("49408", access.Backend.ReadCached(feature).Value?.Integer);

        var retiring = new Permit();
        reader.AfterRead = retiring.Retire;
        beforeCancel = owner.ReadState();
        var retired = await borrow.CollectAsync([feature], retiring, default);
        Assert.False(retired.Committed);
        Assert.Same(beforeCancel, owner.ReadState());
        Assert.Equal("49408", access.Backend.ReadCached(feature).Value?.Integer);
    }

    [Fact]
    public void ScopeUsesConfigurableFiniteBoundsAndRejectsUnmappedRequests()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.None, WindowsD3dApiVersion.Direct3D11, [FirstAdapter]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FormatSupportFlags, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter], [(WindowsD3dFormat)777]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FeatureLevel, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter, FirstAdapter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FeatureLevel, (WindowsD3dApiVersion)99, [FirstAdapter]));

        var invalidBoundInputReads = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FeatureLevel, WindowsD3dApiVersion.Direct3D12,
            InvalidBoundInput(), maximumDxgiAdapterCount: 0));
        Assert.Equal(0, invalidBoundInputReads);

        var defaults = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.FeatureLevel,
            WindowsD3dApiVersion.Direct3D12, [FirstAdapter]);
        Assert.Equal(64, defaults.MaximumSelectedAdapterCount);
        Assert.Equal(64, defaults.MaximumSelectedFormatCount);
        Assert.Equal(512, defaults.MaximumDxgiAdapterCount);

        var adapters = AdapterIdentities(65).ToArray();
        var largerAdapterScope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.FeatureLevel,
            WindowsD3dApiVersion.Direct3D12, adapters, maximumSelectedAdapterCount: 65,
            maximumDxgiAdapterCount: 513);
        Assert.Equal(65, largerAdapterScope.Adapters.Length);
        Assert.Equal(513, largerAdapterScope.MaximumDxgiAdapterCount);

        var formats = Enum.GetValues<WindowsD3dFormat>().Take(65).ToArray();
        var largerFormatScope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.FormatSupportFlags,
            WindowsD3dApiVersion.Direct3D12, [FirstAdapter], formats, maximumSelectedFormatCount: 65,
            maximumDxgiAdapterCount: 513);
        Assert.Equal(65, largerFormatScope.Formats.Length);

        var consumedAdapters = 0;
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FeatureLevel, WindowsD3dApiVersion.Direct3D12,
            InfiniteAdapters(), maximumSelectedAdapterCount: 3));
        Assert.Equal(4, consumedAdapters);

        var consumedFormats = 0;
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.FormatSupportFlags, WindowsD3dApiVersion.Direct3D12,
            [FirstAdapter], InfiniteFormats(), maximumSelectedFormatCount: 3));
        Assert.Equal(4, consumedFormats);

        IEnumerable<WindowsD3dAdapterIdentity> InfiniteAdapters()
        {
            for (var index = 1; ; index++)
            {
                consumedAdapters++;
                yield return new((ulong)index, 0x1234, checked((ushort)(0x1000 + index)));
            }
        }

        IEnumerable<WindowsD3dFormat> InfiniteFormats()
        {
            while (true)
            {
                consumedFormats++;
                yield return WindowsD3dFormat.R8G8B8A8UNorm;
            }
        }

        IEnumerable<WindowsD3dAdapterIdentity> InvalidBoundInput()
        {
            invalidBoundInputReads++;
            yield return FirstAdapter;
        }
    }

    [Fact]
    public void D3dPayloadLayoutsAndPackedFormatFlagsMatchTheReviewedAbi()
    {
        var sizes = WindowsD3dCapabilityNativeReader.D3d12PayloadSizes;
        Assert.Equal(IntPtr.Size == 8 ? 24 : 12, sizes.FeatureLevels);
        Assert.Equal(12, sizes.FormatSupport);
        Assert.Equal(8, WindowsD3dCapabilityNativeReader.D3d11FormatSupport2PayloadSize);
        Assert.Equal(0x0000_0001_0000_0021UL,
            WindowsD3dCapabilityNativeReader.PackD3d11FormatSupport(0x21, 0x1));
        Assert.Equal(0x0001_0000_0000_0021UL,
            WindowsD3dCapabilityNativeReader.PackD3d12FormatSupport(0x21, 0x0001_0000));
        Assert.Equal((uint)WindowsD3dFeatureLevel.Level12_2, 0xC200U);
        Assert.True(WindowsD3dCapabilityScope.IsFeatureLevelAllowed(
            WindowsD3dApiVersion.Direct3D11, WindowsD3dFeatureLevel.Level12_1));
        Assert.False(WindowsD3dCapabilityScope.IsFeatureLevelAllowed(
            WindowsD3dApiVersion.Direct3D11, WindowsD3dFeatureLevel.Level12_2));
        Assert.Equal((uint)WindowsD3dFormat.R8G8B8A8UNorm, 28U);
        Assert.Equal((uint)WindowsD3dFormat.D32Float, 40U);
    }

    private static WindowsD3dCapabilityReadResult Result(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected, IReadOnlyList<WindowsD3dFeatureLevel?> levels,
        IReadOnlyDictionary<WindowsD3dFormat, ulong?> formatFlags)
    {
        var rows = scope.Adapters.Select((adapter, index) => new WindowsD3dAdapterCapabilityRead(
            adapter, scope.ApiVersion,
            (selected & WindowsD3dCapabilityFields.FeatureLevel) != 0 ? levels[index] : null,
            (selected & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                ? [.. scope.Formats.Select(format => new WindowsD3dFormatSupportRead(format, formatFlags[format]))]
                : [])).ToImmutableArray();
        return new(rows);
    }

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static IEnumerable<WindowsD3dAdapterIdentity> AdapterIdentities(int count)
    {
        for (var index = 1; index <= count; index++)
            yield return new((ulong)index, 0x1234, checked((ushort)(0x1000 + index)));
    }

    private sealed class RecordedReader : IWindowsD3dCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsD3dCapabilityFields> requests = new();
        public int Mode { get; set; }
        public IReadOnlyList<WindowsD3dCapabilityFields> Requests => requests.ToArray();
        public WindowsD3dFeatureLevel?[] FeatureLevels { get; set; } = [WindowsD3dFeatureLevel.Level12_1, WindowsD3dFeatureLevel.Level11_0];
        public Action? AfterRead { get; set; }
        public Dictionary<WindowsD3dFormat, ulong?> FormatFlags { get; } = new()
        {
            [WindowsD3dFormat.R8G8B8A8UNorm] = 0x0001_0000_0000_0021,
            [WindowsD3dFormat.D32Float] = 0
        };

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            if (Mode == 2) throw new InvalidOperationException("recorded reader failure");
            if (Mode == 3)
                return new([new(SecondAdapter, scope.ApiVersion, WindowsD3dFeatureLevel.Level12_0, [])]);
            var featureLevels = Mode == 1
                ? FeatureLevels.Select(static _ => (WindowsD3dFeatureLevel?)null).ToArray()
                : FeatureLevels;
            var formats = Mode == 1
                ? FormatFlags.Keys.ToDictionary(static format => format, static _ => (ulong?)null)
                : FormatFlags;
            var result = Result(scope, selectedFields, featureLevels, formats);
            AfterRead?.Invoke();
            return result;
        }
    }

    private sealed class Permit(bool accept = true) : IMetricPublicationPermit
    {
        private int active = 1;
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => Volatile.Read(ref active) != 0;
        public void Retire() => Volatile.Write(ref active, 0);
        public bool TryPublish(Action commit)
        {
            if (!accept || !IsActive) return false;
            commit();
            return true;
        }
    }

}
