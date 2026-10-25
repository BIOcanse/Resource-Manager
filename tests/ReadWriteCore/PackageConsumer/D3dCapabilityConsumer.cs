using ResourceManager.App.Domain.Metrics;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class D3dCapabilityConsumer
{
    private static readonly WindowsD3dVideoDecoderDescriptor Descriptor =
        new(RecordedProvider.ProfileGuid, 1920, 1080, WindowsD3dFormat.Nv12);
    private static readonly WindowsD3dVideoProcessorDescriptor ProcessorDescriptor = new(
        WindowsD3dVideoFrameFormat.Progressive, new(30, 1), 1920, 1080,
        new(30, 1), 1920, 1080, WindowsD3dVideoUsage.PlaybackNormal);

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var adapter = new WindowsD3dAdapterIdentity(0x0000_0001_0000_002A, 0x10DE, 0x2684);
        var selectedFields = WindowsD3dCapabilityFields.All | WindowsD3dCapabilityFields.DecoderProfiles
            | WindowsD3dCapabilityFields.DecoderOutputFormats | WindowsD3dCapabilityFields.DecoderConfigurations
            | WindowsD3dCapabilityFields.VideoProcessorCaps | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps;
        var scope = new WindowsD3dCapabilityScope(selectedFields,
            WindowsD3dApiVersion.Direct3D11, [adapter], [WindowsD3dFormat.Nv12, WindowsD3dFormat.P010],
            decoderConfigurationDescriptors: [Descriptor], videoProcessorDescriptors: [ProcessorDescriptor]);
        var reader = new RecordedProvider();
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var feature = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_d3d_feature_level").Field;
        var format = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_d3d_format_support_flags"
            && field.Field.DeviceIdentity == $"windows-d3d-adapter|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}|dxgi-format:{(uint)WindowsD3dFormat.Nv12}").Field;
        var profile = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_video_decoder_profile_guid").Field;
        var outputFormat = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_video_decoder_output_format").Field;
        var decoderConfiguration = catalog.Fields.Single(static field =>
            field.Field.FieldName == "gpu_all_video_decoder_configuration").Field;
        var processorCaps = catalog.Fields.Single(static field =>
            field.Field.FieldName == "gpu_all_video_processor_caps").Field;
        var rateConversionCaps = catalog.Fields.Single(static field =>
            field.Field.FieldName == "gpu_all_video_processor_rate_conversion_caps").Field;
        Require(feature.DeviceIdentity.Contains("d3d11|luid:000000010000002A|pci:10DE:2684", StringComparison.Ordinal),
            "public feature identity retains exact API, LUID, and PCI pair");
        Require(backend.ReadCached(feature).Value?.Integer == MetricInteger.Parse("45056"), "recorded current D3D11 feature-level code");
        Require(backend.ReadCached(format).Value?.Integer == MetricInteger.Parse("281474976710689"), "recorded 64-bit D3D11 format flags");
        Require(profile.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684|profile:2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041", StringComparison.Ordinal)
            && backend.ReadCached(profile).Value?.Text == RecordedProvider.ProfileGuid.ToString("D"),
            "profile GUID remains scoped to the exact adapter");
        Require(outputFormat.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684|profile:2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041|dxgi-format:103", StringComparison.Ordinal)
            && backend.ReadCached(outputFormat).Value?.Integer == MetricInteger.Parse("103"),
            "supported output format remains scoped to the exact adapter and decoder profile");
        Require(decoderConfiguration.DeviceIdentity.Contains("luid:000000010000002A|pci:10DE:2684"
                + "|profile:2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041|sample-size:1920x1080|output-format:103",
                StringComparison.Ordinal), "configuration identity retains the exact adapter and descriptor");
        using (var configuration = JsonDocument.Parse(backend.ReadCached(decoderConfiguration).Value!.Structure!.Value.GetRawText()))
        {
            Require(configuration.RootElement.GetProperty("configurationCount").GetUInt32() == 1
                && configuration.RootElement.GetProperty("configurations")[0].GetProperty("index").GetUInt32() == 0,
                "recorded configuration count and zero-based index remain current");
        }
        Require(processorCaps.DeviceIdentity.Contains("input-frame-format:0|input-rate:30/1|input-size:1920x1080|output-rate:30/1|output-size:1920x1080|usage:0", StringComparison.Ordinal),
            "processor identity retains all caller-supplied content fields");
        using (var caps = JsonDocument.Parse(backend.ReadCached(processorCaps).Value!.Structure!.Value.GetRawText()))
        {
            Require(caps.RootElement.GetProperty("caps").EnumerateObject().Count() == 9
                && caps.RootElement.GetProperty("caps").GetProperty("rateConversionCapsCount").GetUInt32() == 2,
                "all unsigned processor caps members are current");
        }
        using (var groups = JsonDocument.Parse(backend.ReadCached(rateConversionCaps).Value!.Structure!.Value.GetRawText()))
        {
            var caps = groups.RootElement.GetProperty("rateConversionCaps");
            Require(groups.RootElement.GetProperty("rateConversionCapsCount").GetUInt32() == 2
                && caps.GetArrayLength() == 2 && caps[1].GetProperty("index").GetUInt32() == 1
                && caps[1].GetProperty("caps").EnumerateObject().Count() == 5,
                "complete zero-based rate-conversion groups preserve all native members");
        }
        Require(catalog.Fields.All(static field => field.Field.FieldName != "gpu_all_video_decoder_output_format"
            || !field.Field.DeviceIdentity.EndsWith("dxgi-format:104", StringComparison.Ordinal)),
            "successful false CheckVideoDecoderFormat is omitted as unsupported");
        Require(reader.Requests.Single() == selectedFields, "explicit discovery scope");

        var readsBeforeCache = reader.Requests.Count;
        _ = backend.ReadCached(feature);
        _ = backend.ReadCached(format);
        Require(reader.Requests.Count == readsBeforeCache, "cached current reads do not sample");

        var selected = (await backend.CollectAsync([feature], CancellationToken.None)).Single();
        Require(selected.Status == FieldObservationStatus.Current && selected.Value?.Integer == MetricInteger.Parse("45056"),
            "selected public feature-level collection");
        Require(reader.Requests.Last() == WindowsD3dCapabilityFields.FeatureLevel,
            "selected API requested only the feature-level field");

        var selectedConfiguration = (await backend.CollectAsync([decoderConfiguration], CancellationToken.None)).Single();
        Require(selectedConfiguration.Status == FieldObservationStatus.Current
            && reader.Requests.Last() == WindowsD3dCapabilityFields.DecoderConfigurations,
            "explicit configuration selection queries only the configuration group");
        var selectedProcessorCaps = (await backend.CollectAsync([processorCaps], CancellationToken.None)).Single();
        Require(selectedProcessorCaps.Status == FieldObservationStatus.Current
            && reader.Requests.Last() == WindowsD3dCapabilityFields.VideoProcessorCaps,
            "explicit processor caps selection queries only that public group");
        var selectedRateCaps = (await backend.CollectAsync([rateConversionCaps], CancellationToken.None)).Single();
        Require(selectedRateCaps.Status == FieldObservationStatus.Current
            && reader.Requests.Last() == WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps,
            "explicit rate-conversion selection queries its independent public group");
        var requestsBeforeCachedConfiguration = reader.Requests.Count;
        _ = backend.ReadCached(decoderConfiguration);
        Require(reader.Requests.Count == requestsBeforeCachedConfiguration, "cached configurations do not query the source");

        using var borrow = access.BorrowSubscriptionCapture();
        var bindings = borrow.Bind([outputFormat, decoderConfiguration, processorCaps, rateConversionCaps]);
        Require(bindings.All(binding => binding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId])),
            "role-2 borrow binds the original topology item");
        var borrowed = await borrow.CollectAsync([outputFormat, decoderConfiguration, processorCaps, rateConversionCaps], new Permit(), CancellationToken.None);
        var borrowedConfiguration = borrowed.Readings.Single(reading => reading.Field == decoderConfiguration);
        Require(borrowed.Committed && borrowedConfiguration.Status == FieldObservationStatus.Current,
            "role-2 public format capture commits through the same owner");
        Require(backend.ReadCached(decoderConfiguration) == borrowedConfiguration, "borrow and public cache share one current");
        Require(backend.ReadCached(processorCaps).Status == FieldObservationStatus.Current
            && backend.ReadCached(rateConversionCaps).Status == FieldObservationStatus.Current,
            "role-2 borrow and public reads share processor capability current");
        Require(owner.ReadState().InformationFields.FindField(decoderConfiguration) is not null,
            "D3D capability resides in the original topology current");
        await RunRaytracingAsync(owner, adapter);
        Console.WriteLine("[D3D-CAPABILITY-PACKAGE] recorded exact adapter/API/processor descriptor discovery, selected fields, cached current, and role-2 borrow passed; no D3D native read.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunRaytracingAsync(DeviceTopologyCaptureOwner owner, WindowsD3dAdapterIdentity adapter)
    {
        var reader = new RecordedRaytracingProvider();
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.RaytracingTier,
            WindowsD3dApiVersion.Direct3D12, [adapter]);
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        Require(reader.Calls == 0, "raytracing factory is inert");
        var field = (await access.Backend.DiscoverAsync(default)).Fields.Single().Field;
        Require(field.FieldName == "gpu_all_d3d12_raytracing_tier"
            && field.DeviceIdentity.Contains($"d3d12|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}", StringComparison.Ordinal),
            "raytracing field has exact API/LUID/PCI identity");
        Require(access.Backend.ReadCached(field).Value?.Integer == MetricInteger.Parse("0"), "successful zero is reported no-raytracing");
        var calls = reader.Calls;
        Require(owner.ReadState().InformationFields.FindField(field)?.Value?.GetString() == "0", "same owner current contains zero");
        Require(calls == reader.Calls, "cached raytracing reads do not query");
        reader.Tier = uint.MaxValue;
        var current = (await access.Backend.CollectAsync([field], default)).Single();
        Require(current.Value?.Integer == MetricInteger.Parse("4294967295"), "future raw tier retains UInt32 without narrowing");
        reader.Tier = null;
        current = (await access.Backend.CollectAsync([field], default)).Single();
        Require(current.Status == FieldObservationStatus.Unavailable && current.Value is null && current.ObservedAt is null,
            "source failure clears selected tier rather than becoming tier zero");
        reader.Tier = 11;
        using var borrow = access.BorrowSubscriptionCapture();
        var published = await borrow.CollectAsync([field], new Permit(), default);
        Require(published.Committed && published.Readings.Single().Value?.Integer == MetricInteger.Parse("11")
            && published.Readings.Single() == access.Backend.ReadCached(field), "public borrow and direct API use the same raytracing current");
        Require(reader.LastFields == WindowsD3dCapabilityFields.RaytracingTier, "only selected raytracing query group");
        Console.WriteLine("[D3D12-RAYTRACING-PACKAGE] recorded zero/raw-UInt32/failure/recovery and original-owner borrow; no native GPU access.");
    }

    private sealed class RecordedRaytracingProvider : IWindowsD3dCapabilityReader
    {
        public uint? Tier { get; set; } = 0;
        public int Calls { get; private set; }
        public WindowsD3dCapabilityFields LastFields { get; private set; }
        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; LastFields = selectedFields;
            return new([new(scope.Adapters.Single(), scope.ApiVersion, null, []) { RaytracingTier = Tier }]);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"D3D capability package consumer failed: {label}.");
    }

    private sealed class RecordedProvider : IWindowsD3dCapabilityReader
    {
        internal static readonly Guid ProfileGuid = new("2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041");
        private readonly List<WindowsD3dCapabilityFields> requests = [];
        private readonly object gate = new();
        public IReadOnlyList<WindowsD3dCapabilityFields> Requests { get { lock (gate) return requests.ToArray(); } }

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) requests.Add(selectedFields);
            return new([.. scope.Adapters.Select(adapter => new WindowsD3dAdapterCapabilityRead(
                adapter, scope.ApiVersion,
                (selectedFields & WindowsD3dCapabilityFields.FeatureLevel) != 0
                    ? WindowsD3dFeatureLevel.Level11_0 : null,
                (selectedFields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                    ? [.. scope.Formats.Select(static format => new WindowsD3dFormatSupportRead(format, 0x0001_0000_0000_0021))]
                    : [],
                (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                    | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0 ? true : null,
                (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                    | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0
                    ? [new WindowsD3dVideoDecoderProfileRead(ProfileGuid,
                        (selectedFields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0 ? true : null,
                        (selectedFields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0
                            ? [new(WindowsD3dFormat.Nv12, true), new(WindowsD3dFormat.P010, false)] : [])]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0
                    ? [new WindowsD3dVideoDecoderConfigurationsRead(Descriptor, true, 1,
                        [new(0, new WindowsD3dVideoDecoderConfig(Guid.Empty, Guid.Empty, Guid.Empty,
                            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0))])]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0
                    ? [new WindowsD3dVideoProcessorCapsRead(ProcessorDescriptor, true,
                        new(1, 2, 3, 4, 5, 6, 2, 8, 9))]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0
                    ? [new WindowsD3dVideoProcessorRateConversionCapsRead(ProcessorDescriptor, true, 2,
                        [new(0, new(1, 2, 3, 4, 5)), new(1, new(6, 7, 8, 9, 10))])]
                    : []))]);
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
