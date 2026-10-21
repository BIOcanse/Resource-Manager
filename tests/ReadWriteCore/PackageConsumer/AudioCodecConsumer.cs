using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Media;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class AudioCodecConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var reader = new RecordedReader();
        var scope = Scope();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        Require(reader.ReadCount == 1, "explicit discovery read once");
        Require(reader.Requests.Single() == scope.Fields, "discovery used the configured scope");

        var renderEndpoint = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_endpoint_id_declared"
            && backend.ReadCached(field.Field).Value?.Text == "recorded-render-endpoint");
        var mix = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_mix_format_configured"
            && field.Field.DeviceIdentity == renderEndpoint.Field.DeviceIdentity);
        var codec = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_media_type_capability"
            && field.Field.DeviceIdentity.StartsWith("windows-mft|", StringComparison.Ordinal));
        var directShowMedia = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_media_type_capability"
            && field.Field.DeviceIdentity.StartsWith("windows-directshow|", StringComparison.Ordinal));
        var directShowVersion = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_version_declared"
            && field.Field.DeviceIdentity.StartsWith("windows-directshow|", StringComparison.Ordinal));
        Require(!string.Equals(codec.Field.DeviceIdentity, directShowMedia.Field.DeviceIdentity, StringComparison.Ordinal),
            "MFT CLSID and DirectShow moniker occupy separate stable object identities");
        var countAfterDiscovery = reader.ReadCount;
        var cachedBefore = backend.ReadCached(mix.Field);
        Require(cachedBefore.Status == FieldObservationStatus.Current && cachedBefore.Value?.Structure is not null,
            "configured shared mix format is current");
        Require(reader.ReadCount == countAfterDiscovery, "cached read did not sample");

        var selected = await backend.CollectAsync([mix.Field], CancellationToken.None);
        var selectedMix = selected.Single();
        Require(selectedMix.Status == FieldObservationStatus.Current && selectedMix.Value?.Structure is not null,
            "selected mix-format capture");
        Require(reader.Requests.Last() == WindowsMediaInformationFields.AudioMixFormat,
            "selected capture requested only the canonical mix-format field");
        var countAfterSelected = reader.ReadCount;
        Require(backend.ReadCached(mix.Field) == selectedMix, "selected value is the original owner's current");
        Require(owner.ReadState().InformationFields.FindField(mix.Field) is not null,
            "selected value resides in the original topology owner");
        Require(reader.ReadCount == countAfterSelected, "cache read after selected capture did not sample");

        var versionReading = (await backend.CollectAsync([directShowVersion.Field], CancellationToken.None)).Single();
        var versionText = versionReading.Value?.Text;
        Require(versionReading.Status == FieldObservationStatus.Current && versionText is not null
            && versionText.Contains($"InprocServer32={RecordedReader.ModulePath}", StringComparison.Ordinal)
            && versionText.Contains("FileVersion=2.3.4.5", StringComparison.Ordinal),
            "selected version read carries the exact registered module path and file resource version");
        Require(reader.Requests.Last() == WindowsMediaInformationFields.CodecVersion,
            "version capture requested only the canonical version field");

        using var audioBorrow = access.BorrowSubscriptionCapture();
        var audioBinding = audioBorrow.Bind([codec.Field]).Single();
        Require(audioBinding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId]),
            "MFT subscription borrow binds the original topology collection item");
        var audioBorrowed = await audioBorrow.CollectAsync([codec.Field], new Permit());
        var audioRegisteredTypes = audioBorrowed.Readings.Single();
        Require(audioBorrowed.Committed && audioRegisteredTypes.Status == FieldObservationStatus.Current
            && audioRegisteredTypes.Value?.Structure is not null, "recorded MFT registration through the original borrow");
        Require(reader.Requests.Last() == WindowsMediaInformationFields.CodecMediaTypes,
            "MFT borrow capture requested only registered media types");
        Require(backend.ReadCached(codec.Field) == audioRegisteredTypes, "MFT borrow and direct API share the same current");
        Console.WriteLine("[CODEC-AUDIO-PACKAGE] recorded owner discovery, selected mix format, cache, MFT media types, and role-2 borrow passed; no native read.");

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = borrow.Bind([directShowMedia.Field]).Single();
        Require(binding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId]),
            "subscription borrow binds the original topology collection item");
        var borrowed = await borrow.CollectAsync([directShowMedia.Field], new Permit());
        var registeredTypes = borrowed.Readings.Single();
        var directShowData = registeredTypes.Value?.Structure;
        Require(borrowed.Committed && registeredTypes.Status == FieldObservationStatus.Current
            && directShowData is { } sourceValue
            && sourceValue.GetProperty("registrationSource").GetString() == "directshow-category-moniker",
            "recorded DirectShow source identity through the original borrow");
        Require(reader.Requests.Last() == WindowsMediaInformationFields.CodecMediaTypes,
            "borrow capture requested only registered codec media types");
        Require(backend.ReadCached(directShowMedia.Field) == registeredTypes, "borrow and direct API share the same current");

        Console.WriteLine("[CODEC-DIRECTSHOW-PACKAGE] recorded same-owner discovery, selected audio/version reads, cache, exact DirectShow moniker/CLSID, module version, and borrow passed; no native read.");
    }

    private static WindowsMediaInformationScope Scope()
        => new(WindowsMediaInformationFields.Audio | WindowsMediaInformationFields.Codec,
            [WindowsAudioDataFlow.Render, WindowsAudioDataFlow.Capture], 1,
            [WindowsMftCategory.AudioDecoder, WindowsMftCategory.AudioEncoder,
                WindowsMftCategory.VideoDecoder, WindowsMftCategory.VideoEncoder], 64,
            [WindowsDirectShowCategory.RegisteredFilters, WindowsDirectShowCategory.AudioCompressors,
                WindowsDirectShowCategory.VideoCompressors], WindowsCodecRegistryView.Registry64);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Audio/codec package consumer failed: {label}.");
    }

    private sealed class RecordedReader : IWindowsMediaInformationReader
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Guid AudioMajorType = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid PcmSubType = new("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid DecoderClassId = new("3f0dc74e-3d1f-45aa-acf0-8d4cc706d851");
        private static readonly Guid DirectShowClassId = new("2d124a0d-1f6a-43d4-a0c3-7393a06bc82a");
        private const string MonikerDisplayName = "@device:sw:{083863f1-70de-11d0-bd40-00a0c911ce86}\\{2d124a0d-1f6a-43d4-a0c3-7393a06bc82a}";
        public const string ModulePath = @"C:\Recorded\DirectShowCodec.dll";
        private static readonly DeviceTopologyCodecModuleVersion DirectShowVersion = new(
            WindowsCodecRegistrationSource.DirectShowCategoryMoniker,
            DirectShowClassId, WindowsCodecRegistryView.Registry64,
            ModulePath, ModulePath, "2.3.4.5", "Recorded Provider", "Recorded DirectShow Codec");
        private static readonly DeviceTopologyWaveFormat Mix = new(1, 2, 48_000, 192_000, 4, 16, 0,
            null, null, null, "");
        private static readonly DeviceTopologyAudioEndpointInformation[] Endpoints =
        [
            new("recorded-render-endpoint", WindowsAudioDataFlow.Render,
                ImmutableArray.Create(WindowsAudioDefaultRole.Console, WindowsAudioDefaultRole.Multimedia), Mix),
            new("recorded-capture-endpoint", WindowsAudioDataFlow.Capture,
                ImmutableArray.Create(WindowsAudioDefaultRole.Communications), Mix)
        ];

        public ConcurrentQueue<WindowsMediaInformationFields> Requests { get; } = new();
        public int ReadCount => Requests.Count;

        public WindowsMediaInformationReadResult Read(WindowsMediaInformationScope scope,
            WindowsMediaInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(selectedFields);
            var directories = new List<WindowsMediaDirectoryRead>();
            var endpointFields = selectedFields & (WindowsMediaInformationFields.AudioEndpointId
                | WindowsMediaInformationFields.AudioDefaultRoles | WindowsMediaInformationFields.AudioMixFormat);
            if (endpointFields != WindowsMediaInformationFields.None)
            {
                foreach (var direction in scope.AudioDirections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var identityPrefix = $"windows-audio-endpoint|{DirectionCode(direction)}|";
                    var objects = Endpoints.Where(endpoint => endpoint.DataFlow == direction)
                        .Select(endpoint => ProjectEndpoint(endpoint, selectedFields)).ToArray();
                    directories.Add(new(identityPrefix, true, objects));
                }
            }

            if ((selectedFields & WindowsMediaInformationFields.AudioDriverVersion) != 0)
                directories.Add(new("windows-audio-device|", true, []));

            var codecFields = selectedFields & WindowsMediaInformationFields.Codec;
            if (codecFields != WindowsMediaInformationFields.None)
            {
                foreach (var category in scope.CodecCategories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var identityPrefix = $"windows-mft|{CategoryCode(category)}|";
                    DeviceInformationObject[] objects = category == WindowsMftCategory.AudioDecoder
                        ? [ProjectDecoder(selectedFields)]
                        : [];
                    directories.Add(new(identityPrefix, true, objects));
                }
                foreach (var category in scope.DirectShowCategories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var identityPrefix = $"windows-directshow|{DirectShowCategoryCode(category)}|";
                    IReadOnlyList<DeviceInformationObject> objects = category == WindowsDirectShowCategory.RegisteredFilters
                        ? [ProjectDirectShow(selectedFields)] : [];
                    directories.Add(new(identityPrefix, true, objects));
                }
            }

            return new(directories);
        }

        private static DeviceInformationObject ProjectEndpoint(DeviceTopologyAudioEndpointInformation endpoint,
            WindowsMediaInformationFields selected)
        {
            var fields = new List<DeviceInformationField>(3);
            if ((selected & WindowsMediaInformationFields.AudioEndpointId) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.AudioAllEndpointIdDeclared, endpoint.EndpointId));
            if ((selected & WindowsMediaInformationFields.AudioDefaultRoles) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.AudioAllDefaultRoleActual,
                    endpoint.DefaultRoles!.Value.Select(RoleCode).ToArray()));
            if ((selected & WindowsMediaInformationFields.AudioMixFormat) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.AudioAllMixFormatConfigured, endpoint.MixFormat));
            var direction = DirectionCode(endpoint.DataFlow);
            return new($"windows-audio-endpoint|{direction}|{endpoint.EndpointId}", "audio_endpoint", fields);
        }

        private static DeviceInformationObject ProjectDecoder(WindowsMediaInformationFields selected)
        {
            var fields = new List<DeviceInformationField>(2);
            if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
            {
                var value = new
                {
                    category = "audio-decoder",
                    classId = DecoderClassId.ToString("B").ToLowerInvariant(),
                    input = new[] { new { majorType = AudioMajorType.ToString("D"), subType = PcmSubType.ToString("D") } },
                    output = new[] { new { majorType = AudioMajorType.ToString("D"), subType = PcmSubType.ToString("D") } }
                };
                fields.Add(Field(DeviceInformationFieldRegistry.CodecAllMediaTypeCapability, value));
            }
            if ((selected & WindowsMediaInformationFields.CodecVersion) != 0)
                fields.Add(new(DeviceInformationFieldRegistry.CodecAllVersionDeclared, null));
            return new($"windows-mft|audio-decoder|{DecoderClassId:B}".ToLowerInvariant(), "codec", fields);
        }

        private static DeviceInformationObject ProjectDirectShow(WindowsMediaInformationFields selected)
        {
            var escaped = Uri.EscapeDataString(MonikerDisplayName);
            var classIdCode = DirectShowClassId.ToString("B").ToLowerInvariant();
            var identity = $"windows-directshow|registered-filters|{classIdCode}|{escaped}";
            var fields = new List<DeviceInformationField>(2);
            if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.CodecAllMediaTypeCapability, new
                {
                    registrationSource = "directshow-category-moniker",
                    category = "registered-filters",
                    categoryClassId = "{083863f1-70de-11d0-bd40-00a0c911ce86}",
                    classId = DirectShowClassId.ToString("B").ToLowerInvariant(),
                    sourceIdentity = MonikerDisplayName,
                    friendlyName = "Recorded DirectShow Codec",
                    devicePath = (string?)null,
                    input = (object?)null,
                    output = (object?)null
                }));
            if ((selected & WindowsMediaInformationFields.CodecVersion) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.CodecAllVersionDeclared,
                    $"FileVersion={DirectShowVersion.FileVersion}; Source=directshow-category-moniker; "
                    + $"CLSID={classIdCode}; RegistryView=registry64; "
                    + $"InprocServer32={DirectShowVersion.RegisteredModulePath}; ResolvedModule={DirectShowVersion.ResolvedModulePath}; "
                    + $"CompanyName={DirectShowVersion.CompanyName}; ProductName={DirectShowVersion.ProductName}"));
            return new(identity, "codec", fields);
        }

        private static string DirectShowCategoryCode(WindowsDirectShowCategory category) => category switch
        {
            WindowsDirectShowCategory.RegisteredFilters => "registered-filters",
            WindowsDirectShowCategory.AudioCompressors => "audio-compressors",
            WindowsDirectShowCategory.VideoCompressors => "video-compressors",
            WindowsDirectShowCategory.AudioCaptureSources => "audio-capture-sources",
            WindowsDirectShowCategory.VideoCaptureSources => "video-capture-sources",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        private static DeviceInformationField Field<T>(DeviceInformationDefinition definition, T value)
            => new(definition, JsonSerializer.SerializeToElement(value, Json));

        private static string DirectionCode(WindowsAudioDataFlow direction) => direction switch
        {
            WindowsAudioDataFlow.Render => "render",
            WindowsAudioDataFlow.Capture => "capture",
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        private static string RoleCode(WindowsAudioDefaultRole role) => role switch
        {
            WindowsAudioDefaultRole.Console => "console",
            WindowsAudioDefaultRole.Multimedia => "multimedia",
            WindowsAudioDefaultRole.Communications => "communications",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        private static string CategoryCode(WindowsMftCategory category) => category switch
        {
            WindowsMftCategory.AudioDecoder => "audio-decoder",
            WindowsMftCategory.AudioEncoder => "audio-encoder",
            WindowsMftCategory.VideoDecoder => "video-decoder",
            WindowsMftCategory.VideoEncoder => "video-encoder",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
