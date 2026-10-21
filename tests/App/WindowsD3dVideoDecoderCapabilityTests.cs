using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsD3dVideoDecoderCapabilityTests
{
    private static readonly WindowsD3dAdapterIdentity FirstAdapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity SecondAdapter = new(0x0000_0002_0000_003B, 0x8086, 0x46A6);
    private static readonly Guid FirstProfile = new("2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041");
    private static readonly Guid SecondProfile = new("6c1ad68d-7f31-48b1-9bc1-c72f6f8b9927");

    [Fact]
    public async Task RecordedDecoderCapabilitiesKeepExactAdapterProfileAndOutputFormatIdentity()
    {
        var scope = Scope([FirstAdapter, SecondAdapter], [WindowsD3dFormat.Nv12, WindowsD3dFormat.P010, WindowsD3dFormat.Yuy2]);
        var reader = new RecordedReader
        {
            ResultFactory = (requestScope, _) => new([Row(FirstAdapter, FirstProfile,
                (WindowsD3dFormat.Nv12, true), (WindowsD3dFormat.P010, false), (WindowsD3dFormat.Yuy2, null)),
                Row(SecondAdapter, SecondProfile, (WindowsD3dFormat.Nv12, true),
                    (WindowsD3dFormat.P010, false), (WindowsD3dFormat.Yuy2, null))])
        };
        using var owner = EmptyOwner();
        var access = owner.CreateD3dCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);
        Assert.Equal(scope.Fields, Assert.Single(reader.Requests));
        var profileFields = catalog.Fields.Where(static field => field.Field.FieldName == "gpu_all_video_decoder_profile_guid").ToArray();
        var outputFields = catalog.Fields.Where(static field => field.Field.FieldName == "gpu_all_video_decoder_output_format").ToArray();
        Assert.Equal(2, profileFields.Length);
        Assert.Equal(4, outputFields.Length);

        var firstProfile = profileFields.Single(field => field.Field.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684", StringComparison.Ordinal)).Field;
        var secondProfile = profileFields.Single(field => field.Field.DeviceIdentity.Contains("luid:000000020000003B|pci:8086:46A6", StringComparison.Ordinal)).Field;
        Assert.Contains($"profile:{FirstProfile:D}", firstProfile.DeviceIdentity, StringComparison.Ordinal);
        Assert.Contains($"profile:{SecondProfile:D}", secondProfile.DeviceIdentity, StringComparison.Ordinal);
        Assert.Equal(FirstProfile.ToString("D"), access.Backend.ReadCached(firstProfile).Value?.Text);
        Assert.Equal(SecondProfile.ToString("D"), access.Backend.ReadCached(secondProfile).Value?.Text);
        Assert.Equal("text", DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid.ValueKind);
        Assert.Equal("guid", DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid.Unit);
        Assert.Equal("unsigned-integer", DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat.ValueKind);
        Assert.Equal("dxgi_format", DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat.Unit);

        foreach (var adapterAndProfile in new[] { (FirstAdapter, FirstProfile), (SecondAdapter, SecondProfile) })
        {
            var supported = outputFields.Single(field => field.Field.DeviceIdentity.Contains(
                $"luid:{adapterAndProfile.Item1.Luid:X16}|pci:{adapterAndProfile.Item1.PciVendorId:X4}:{adapterAndProfile.Item1.PciDeviceId:X4}|profile:{adapterAndProfile.Item2:D}|dxgi-format:103",
                StringComparison.Ordinal)).Field;
            Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(supported).Status);
            Assert.Equal("103", access.Backend.ReadCached(supported).Value?.Integer);
            Assert.Contains(catalog.Devices, device => device.Identity == supported.DeviceIdentity
                && device.Resource == "gpu_adapter_decoder_format");
            Assert.DoesNotContain(outputFields, field => field.Field.DeviceIdentity.Contains(
                $"profile:{adapterAndProfile.Item2:D}|dxgi-format:104", StringComparison.Ordinal));
            var unknown = outputFields.Single(field => field.Field.DeviceIdentity.Contains(
                $"profile:{adapterAndProfile.Item2:D}|dxgi-format:107", StringComparison.Ordinal)).Field;
            Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(unknown).Status);
            Assert.Null(access.Backend.ReadCached(unknown).Value);
            Assert.Null(access.Backend.ReadCached(unknown).ObservedAt);
        }
    }

    [Fact]
    public async Task FailedEnumerationUnavailablePairKnownUnsupportedAndEmptyEnumerationRemainDistinct()
    {
        var scope = Scope([FirstAdapter], [WindowsD3dFormat.Nv12]);
        var reader = new RecordedReader
        {
            ResultFactory = (_, _) => new([Row(FirstAdapter, FirstProfile, (WindowsD3dFormat.Nv12, true))])
        };
        using var owner = EmptyOwner();
        var backend = owner.CreateD3dCapabilityInformation(scope, reader).Backend;
        var catalog = await backend.DiscoverAsync(default);
        var profile = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_video_decoder_profile_guid").Field;
        var outputFormat = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_video_decoder_output_format").Field;
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(profile).Status);
        Assert.Equal("103", backend.ReadCached(outputFormat).Value?.Integer);

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], false, [])]);
        var failedProfile = Assert.Single(await backend.CollectAsync([profile], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failedProfile.Status);
        Assert.Null(failedProfile.Value);
        var failedProfileFact = owner.ReadState().InformationFields.FindField(profile);
        Assert.NotNull(failedProfileFact);
        Assert.Null(failedProfileFact.Value);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(outputFormat).Status);

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
            [new(FirstProfile)])]);
        var restoredProfile = Assert.Single(await backend.CollectAsync([profile], default));
        Assert.Equal(FieldObservationStatus.Current, restoredProfile.Status);
        Assert.Equal(FirstProfile.ToString("D"), restoredProfile.Value?.Text);

        reader.ResultFactory = (_, _) => new([Row(FirstAdapter, FirstProfile, (WindowsD3dFormat.Nv12, null))]);
        var failedFormat = Assert.Single(await backend.CollectAsync([outputFormat], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failedFormat.Status);
        Assert.Null(failedFormat.Value);
        var failedFormatFact = owner.ReadState().InformationFields.FindField(outputFormat);
        Assert.NotNull(failedFormatFact);
        Assert.Null(failedFormatFact.Value);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(profile).Status);

        reader.ResultFactory = (_, _) => new([Row(FirstAdapter, FirstProfile, (WindowsD3dFormat.Nv12, false))]);
        var unsupportedFormat = Assert.Single(await backend.CollectAsync([outputFormat], default));
        Assert.Equal(FieldObservationStatus.Unavailable, unsupportedFormat.Status);
        Assert.Null(owner.ReadState().InformationFields.FindField(outputFormat));
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(profile).Status);

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], true, [])]);
        _ = await backend.CollectAsync([profile], default);
        Assert.Null(owner.ReadState().InformationFields.FindField(profile));
        Assert.Null(owner.ReadState().InformationFields.FindField(outputFormat));
    }

    [Fact]
    public async Task DecoderQueryBoundsAreConfigurableAndMalformedProviderResultsBecomeUnavailable()
    {
        Assert.Equal((11, 12, 13, 14, 15), WindowsD3dCapabilityNativeReader.D3d11VideoDeviceVTableSlots);
        Assert.Equal((28, 100), WindowsD3dCapabilityNativeReader.D3d11VideoDecoderPayloadSizes);
        Assert.Equal((16, 20, 24), WindowsD3dCapabilityNativeReader.D3d11VideoDecoderDescriptionOffsets);
        Assert.Equal((48, 92, 96, 98), WindowsD3dCapabilityNativeReader.D3d11VideoDecoderConfigurationOffsets);
        Assert.Equal(16, Marshal.SizeOf<Guid>());
        Assert.Equal(4, Marshal.SizeOf<int>());
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderProfiles, WindowsD3dApiVersion.Direct3D12,
            [FirstAdapter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderProfiles, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter], maximumVideoDecoderProfileCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderOutputFormats, WindowsD3dApiVersion.Direct3D11,
            [FirstAdapter], [WindowsD3dFormat.Nv12], maximumVideoDecoderFormatQueryCount: 0));

        var scope = new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderProfiles | WindowsD3dCapabilityFields.DecoderOutputFormats,
            WindowsD3dApiVersion.Direct3D11, [FirstAdapter], [WindowsD3dFormat.Nv12, WindowsD3dFormat.P010],
            maximumVideoDecoderProfileCount: 1, maximumVideoDecoderFormatQueryCount: 1);
        var reader = new RecordedReader
        {
            ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
                [new(FirstProfile, false, [])])])
        };
        using var owner = EmptyOwner();
        var backend = owner.CreateD3dCapabilityInformation(scope, reader).Backend;
        var boundedCatalog = await backend.DiscoverAsync(default);
        Assert.Single(boundedCatalog.Fields);
        Assert.Equal("gpu_all_video_decoder_profile_guid", boundedCatalog.Fields.Single().Field.FieldName);

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
            [new(FirstProfile, true, [new(WindowsD3dFormat.Nv12, true), new(WindowsD3dFormat.P010, true)])])]);
        var staleProfile = boundedCatalog.Fields.Single().Field;
        var pairBoundCatalog = await backend.DiscoverAsync(default);
        Assert.Single(pairBoundCatalog.Fields);
        Assert.Null(owner.ReadState().InformationFields.FindField(staleProfile)?.Value);

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
            [new(FirstProfile, false, []), new(SecondProfile, false, [])])]);
        var profileBoundCatalog = await backend.DiscoverAsync(default);
        Assert.Single(profileBoundCatalog.Fields);
        Assert.Null(owner.ReadState().InformationFields.FindField(staleProfile)?.Value);
    }

    [Fact]
    public async Task DecoderConfigurationsUseExplicitDescriptionsAndKeepZeroFailureAndRawIndicesDistinct()
    {
        var descriptor = new WindowsD3dVideoDecoderDescriptor(FirstProfile, 1920, 1080, WindowsD3dFormat.Nv12);
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D11, [FirstAdapter]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            decoderConfigurationDescriptors: [descriptor, descriptor]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            decoderConfigurationDescriptors: [new(Guid.Empty, 1920, 1080, WindowsD3dFormat.Nv12)]));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            decoderConfigurationDescriptors: [descriptor, descriptor with { SampleWidth = 1280 }],
            maximumVideoDecoderConfigurationDescriptorCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D11, [FirstAdapter],
            decoderConfigurationDescriptors: [descriptor], maximumVideoDecoderConfigurationQueryCount: 0));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderConfigurations, WindowsD3dApiVersion.Direct3D12, [FirstAdapter],
            decoderConfigurationDescriptors: [descriptor]));

        var fields = WindowsD3dCapabilityFields.DecoderConfigurations | WindowsD3dCapabilityFields.FeatureLevel;
        var scope = new WindowsD3dCapabilityScope(fields,
            WindowsD3dApiVersion.Direct3D11, [FirstAdapter], decoderConfigurationDescriptors: [descriptor],
            maximumVideoDecoderConfigurationCount: 8, maximumVideoDecoderConfigurationQueryCount: 9);
        var reader = new RecordedReader
        {
            ResultFactory = (_, selected) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11,
                (selected & WindowsD3dCapabilityFields.FeatureLevel) != 0
                    ? WindowsD3dFeatureLevel.Level11_0 : null,
                [], null, [], [new(descriptor, true, 0, [])])])
        };
        using var owner = EmptyOwner();
        var backend = owner.CreateD3dCapabilityInformation(scope, reader).Backend;
        var catalog = await backend.DiscoverAsync(default);
        var configurationField = catalog.Fields.Single(static item =>
            item.Field.FieldName == "gpu_all_video_decoder_configuration").Field;
        var featureField = catalog.Fields.Single(static item =>
            item.Field.FieldName == "gpu_all_d3d_feature_level").Field;
        Assert.Equal("gpu_all_video_decoder_configuration", configurationField.FieldName);
        Assert.Contains("luid:000000010000002A|pci:10DE:2684", configurationField.DeviceIdentity, StringComparison.Ordinal);
        Assert.Contains($"profile:{FirstProfile:D}|sample-size:1920x1080|output-format:103", configurationField.DeviceIdentity, StringComparison.Ordinal);
        Assert.Equal("object", DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration.ValueKind);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(configurationField).Status);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(featureField).Status);
        Assert.Equal("45056", backend.ReadCached(featureField).Value?.Integer);
        using (var zero = JsonDocument.Parse(backend.ReadCached(configurationField).Value!.Structure!.Value.GetRawText()))
        {
            Assert.Equal(0U, zero.RootElement.GetProperty("configurationCount").GetUInt32());
            Assert.Empty(zero.RootElement.GetProperty("configurations").EnumerateArray());
            Assert.Equal(1920U, zero.RootElement.GetProperty("descriptor").GetProperty("sampleWidth").GetUInt32());
        }
        Assert.Equal(fields, reader.Requests.Last());

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11,
            null, [], null, [], [new(descriptor, false, null, [])])]);
        var unavailable = Assert.Single(await backend.CollectAsync([configurationField], default));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        var unavailableFact = owner.ReadState().InformationFields.FindField(configurationField);
        Assert.NotNull(unavailableFact);
        Assert.Null(unavailableFact.Value);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(featureField).Status);
        Assert.Equal("45056", backend.ReadCached(featureField).Value?.Integer);
        Assert.Equal(WindowsD3dCapabilityFields.DecoderConfigurations, reader.Requests.Last());

        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11,
            null, [], null, [], [new(descriptor, true, 9, [])])]);
        var overBound = Assert.Single(await backend.CollectAsync([configurationField], default));
        Assert.Equal(FieldObservationStatus.Unavailable, overBound.Status);
        Assert.Null(overBound.Value);
        var overBoundFact = owner.ReadState().InformationFields.FindField(configurationField);
        Assert.NotNull(overBoundFact);
        Assert.Null(overBoundFact.Value);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(featureField).Status);
        Assert.Equal("45056", backend.ReadCached(featureField).Value?.Integer);

        var raw = new WindowsD3dVideoDecoderConfig(
            new("11111111-2222-3333-4444-555555555555"),
            new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new("12345678-90ab-cdef-1234-567890abcdef"),
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0x1234, 0x5678);
        reader.ResultFactory = (_, _) => new([new(FirstAdapter, WindowsD3dApiVersion.Direct3D11,
            null, [], null, [], [new(descriptor, true, 1, [new(0, raw)])])]);
        var current = Assert.Single(await backend.CollectAsync([configurationField], default));
        Assert.Equal(FieldObservationStatus.Current, current.Status);
        using (var complete = JsonDocument.Parse(current.Value!.Structure!.Value.GetRawText()))
        {
            var root = complete.RootElement;
            Assert.Equal(1U, root.GetProperty("configurationCount").GetUInt32());
            var indexed = Assert.Single(root.GetProperty("configurations").EnumerateArray());
            Assert.Equal(0U, indexed.GetProperty("index").GetUInt32());
            var native = indexed.GetProperty("configuration");
            Assert.Equal(17, native.EnumerateObject().Count());
            Assert.Equal("11111111-2222-3333-4444-555555555555", native.GetProperty("guidConfigBitstreamEncryption").GetString());
            Assert.Equal(12U, native.GetProperty("config4GroupedCoefs").GetUInt32());
            Assert.Equal((ushort)0x1234, native.GetProperty("configMinRenderTargetBuffCount").GetUInt16());
            Assert.Equal((ushort)0x5678, native.GetProperty("configDecoderSpecific").GetUInt16());
        }
        Assert.Equal(current, backend.ReadCached(configurationField));
        Assert.Equal(WindowsD3dCapabilityFields.DecoderConfigurations, reader.Requests.Last());
    }

    private static WindowsD3dCapabilityScope Scope(IEnumerable<WindowsD3dAdapterIdentity> adapters,
        IEnumerable<WindowsD3dFormat> formats)
        => new(WindowsD3dCapabilityFields.DecoderProfiles | WindowsD3dCapabilityFields.DecoderOutputFormats,
            WindowsD3dApiVersion.Direct3D11, adapters, formats);

    private static WindowsD3dAdapterCapabilityRead Row(WindowsD3dAdapterIdentity adapter, Guid profile,
        params (WindowsD3dFormat Format, bool? Supported)[] formats)
        => new(adapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
            [new(profile, true, [.. formats.Select(static item => new WindowsD3dVideoDecoderFormatRead(item.Format, item.Supported))])]);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private sealed class RecordedReader : IWindowsD3dCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsD3dCapabilityFields> requests = new();
        public IReadOnlyList<WindowsD3dCapabilityFields> Requests => requests.ToArray();
        public Func<WindowsD3dCapabilityScope, WindowsD3dCapabilityFields, WindowsD3dCapabilityReadResult> ResultFactory { get; set; }
            = static (scope, selected) => new([.. scope.Adapters.Select(adapter => new WindowsD3dAdapterCapabilityRead(
                adapter, scope.ApiVersion, null,
                (selected & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                    ? [.. scope.Formats.Select(static format => new WindowsD3dFormatSupportRead(format, null))] : [],
                (selected & (WindowsD3dCapabilityFields.DecoderProfiles
                    | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0 ? false : null, []))]);

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            return ResultFactory(scope, selectedFields);
        }
    }
}
