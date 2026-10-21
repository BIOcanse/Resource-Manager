using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Media;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsMediaInformationOwnerTests
{
    private static readonly Guid AudioDecoderClassId = new("11223344-5566-7788-99AA-BBCCDDEEFF00");
    private static readonly DeviceTopologyRegisteredMediaType RegisteredPcm = new(
        new Guid("73647561-0000-0010-8000-00AA00389B71"), new Guid("00000001-0000-0010-8000-00AA00389B71"));

    [Fact]
    public async Task ExplicitDiscoveryAndSelectedCollectionUseOneOwnerCurrentAndExactStaticFields()
    {
        var scope = Scope();
        var audio = new DeviceTopologyAudioEndpointInformation("endpoint-render-1", WindowsAudioDataFlow.Render,
            [WindowsAudioDefaultRole.Console, WindowsAudioDefaultRole.Multimedia], WaveFormat(48000));
        var audioDevice = new DeviceTopologyAudioDeviceInformation("device-instance-1", "1.2.3.4");
        var transform = Mft(WindowsMftCategory.AudioDecoder, AudioDecoderClassId);
        var reader = new RecordedReader((actualScope, selected, token)
            => Recorded(actualScope, selected, token, [audio], [transform], [audioDevice]));
        var nativeReads = 0;
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation(() =>
        {
            nativeReads++;
            return new(new Dictionary<string, DeviceTopologyNativeDevice>(), null);
        });
        var access = owner.CreateAudioCodecInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);
        Assert.Equal(0, nativeReads);
        Assert.Equal(scope.Fields, Assert.Single(reader.Requests));

        var endpointId = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_endpoint_id_declared");
        Assert.Equal("windows-audio-endpoint|render|endpoint-render-1", endpointId.Field.DeviceIdentity);
        Assert.Equal("endpoint-render-1", access.Backend.ReadCached(endpointId.Field).Value!.Text);
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(endpointId.Field).Status);

        var driverVersion = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_driver_version_declared");
        Assert.Equal("windows-audio-device|device-instance-1", driverVersion.Field.DeviceIdentity);
        Assert.Equal("1.2.3.4", access.Backend.ReadCached(driverVersion.Field).Value!.Text);
        Assert.Contains(catalog.Devices, device => device.Identity == driverVersion.Field.DeviceIdentity
            && device.Resource == "audio_device");

        var roles = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_default_role_actual");
        Assert.Equal(JsonValueKind.Array, access.Backend.ReadCached(roles.Field).Value!.Structure!.Value.ValueKind);
        var roleCodes = access.Backend.ReadCached(roles.Field).Value!.Structure!.Value.EnumerateArray()
            .Select(static item => item.GetString()).ToArray();
        Assert.Equal(new[] { "console", "multimedia" }, roleCodes);

        var codec = catalog.Fields.Single(field => field.Field.FieldName == "codec_all_media_type_capability");
        Assert.Equal($"windows-mft|audio-decoder|{{{AudioDecoderClassId:D}}}", codec.Field.DeviceIdentity);
        var codecValue = access.Backend.ReadCached(codec.Field).Value!.Structure!.Value;
        Assert.Equal("audio-decoder", codecValue.GetProperty("category").GetString());
        Assert.Equal(RegisteredPcm.MajorType.ToString("D"),
            codecValue.GetProperty("input")[0].GetProperty("majorType").GetString());
        Assert.Equal(RegisteredPcm.SubType.ToString("D"),
            codecValue.GetProperty("input")[0].GetProperty("subType").GetString());

        var version = catalog.Fields.Single(field => field.Field.FieldName == "codec_all_version_declared");
        Assert.True(version.Read.Supported);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(version.Field).Status);
        Assert.Null(access.Backend.ReadCached(version.Field).Value);

        var requestsBeforeCachedReads = reader.Requests.Count;
        _ = access.Backend.Catalog;
        _ = access.Backend.ReadCached(codec.Field);
        Assert.Equal(requestsBeforeCachedReads, reader.Requests.Count);

        var ownerBackend = owner.CreateBackend();
        Assert.Equal(access.Backend.ReadCached(endpointId.Field), ownerBackend.ReadCached(endpointId.Field));
        await owner.CaptureInformationAsync(default);
        Assert.Equal("endpoint-render-1", access.Backend.ReadCached(endpointId.Field).Value!.Text);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == endpointId.Field.DeviceIdentity);
    }

    [Fact]
    public async Task FailedFirstDiscoveryIsNotAnEmptyCatalogAndTheSameOwnerCanRetry()
    {
        var scope = Scope(WindowsMediaInformationFields.Audio);
        var endpoint = new DeviceTopologyAudioEndpointInformation("render-one", WindowsAudioDataFlow.Render,
            [WindowsAudioDefaultRole.Multimedia], WaveFormat(48000));
        var mode = 0;
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            DeviceTopologyAudioEndpointInformation[] endpoints = mode == 2 ? [endpoint] : [];
            var result = RecordedReader.Result(actualScope, selected, endpoints, []);
            return mode == 0
                ? result with { Directories = result.Directories.Select(static directory => directory with
                    { DirectorySucceeded = false, Objects = [] }).ToArray() }
                : result;
        });
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));
        Assert.Contains("windows-audio-endpoint|render|", failure.Message);
        Assert.Empty(owner.ReadState().InformationFields.Devices);
        Assert.Empty(access.Backend.Catalog.Fields);

        mode = 1;
        var empty = await access.Backend.DiscoverAsync(default);
        Assert.Empty(empty.Fields);
        Assert.Empty(owner.ReadState().InformationFields.Devices);

        mode = 2;
        var recovered = await access.Backend.DiscoverAsync(default);
        var recoveredMix = Assert.Single(recovered.Fields,
            field => field.Field.FieldName == "audio_all_mix_format_configured");
        Assert.Equal(3, reader.Requests.Count);
        Assert.Equal("windows-audio-endpoint|render|render-one", recoveredMix.Field.DeviceIdentity);
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(recoveredMix.Field).Status);
    }

    [Fact]
    public async Task DirectShowFirstDirectoryFailureIsNotEmptyAndSameOwnerCanRetry()
    {
        var scope = new WindowsMediaInformationScope(WindowsMediaInformationFields.CodecMediaTypes
                | WindowsMediaInformationFields.CodecVersion,
            [], 0, [], 32, [WindowsDirectShowCategory.RegisteredFilters], WindowsCodecRegistryView.Registry64);
        var entry = DirectShow(WindowsDirectShowCategory.RegisteredFilters, AudioDecoderClassId,
            "@device:sw:{083863f1-70de-11d0-bd40-00a0c911ce86}\\{11223344-5566-7788-99aa-bbccddeeff00}");
        var mode = 0;
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            var result = RecordedReader.Result(actualScope, selected, [], [], directShow: mode == 2 ? [entry] : []);
            return mode == 0
                ? result with { Directories = result.Directories.Select(static directory => directory with
                    { DirectorySucceeded = false, Objects = [] }).ToArray() }
                : result;
        });
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));
        Assert.Contains("windows-directshow|registered-filters|", failure.Message);
        Assert.Empty(owner.ReadState().InformationFields.Devices);
        Assert.Empty(access.Backend.Catalog.Fields);

        mode = 1;
        Assert.Empty((await access.Backend.DiscoverAsync(default)).Fields);
        Assert.Empty(owner.ReadState().InformationFields.Devices);

        mode = 2;
        var recovered = await access.Backend.DiscoverAsync(default);
        var media = Assert.Single(recovered.Fields, field => field.Field.FieldName == "codec_all_media_type_capability");
        var value = access.Backend.ReadCached(media.Field).Value!.Structure!.Value;
        Assert.Equal("directshow-category-moniker", value.GetProperty("registrationSource").GetString());
        Assert.Equal(AudioDecoderClassId.ToString("B").ToLowerInvariant(), value.GetProperty("classId").GetString());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("input").ValueKind);
        Assert.Equal(3, reader.Requests.Count);
        Assert.StartsWith("windows-directshow|registered-filters|", media.Field.DeviceIdentity, StringComparison.Ordinal);
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(media.Field).Status);
        var unavailableVersion = Assert.Single(recovered.Fields,
            field => field.Field.FieldName == "codec_all_version_declared");
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(unavailableVersion.Field).Status);
        Assert.Null(access.Backend.ReadCached(unavailableVersion.Field).Value);
    }

    [Fact]
    public async Task DirectShowSelectedFailureClearsOnlyVersionAndEmptySuccessRetiresMoniker()
    {
        var scope = new WindowsMediaInformationScope(WindowsMediaInformationFields.CodecMediaTypes
                | WindowsMediaInformationFields.CodecVersion,
            [], 0, [], 32, [WindowsDirectShowCategory.AudioCompressors], WindowsCodecRegistryView.Registry64);
        var entry = DirectShow(WindowsDirectShowCategory.AudioCompressors, AudioDecoderClassId,
            "@device:sw:{33d9a761-90c8-11d0-bd43-00a0c911ce86}\\{11223344-5566-7788-99aa-bbccddeeff00}",
            passiveTypes: ImmutableArray.Create(RegisteredPcm),
            version: ModuleVersion(WindowsCodecRegistrationSource.DirectShowCategoryMoniker, AudioDecoderClassId, "1.2.3.4"));
        var directorySucceeded = true;
        var entries = new List<DeviceTopologyRegisteredDirectShowInformation> { entry };
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            var result = RecordedReader.Result(actualScope, selected, [], [], directShow: entries);
            return result with { Directories = result.Directories.Select(directory => directory with
                { DirectorySucceeded = directorySucceeded, Objects = directorySucceeded ? directory.Objects : [] }).ToArray() };
        });
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var media = Assert.Single(catalog.Fields, field => field.Field.FieldName == "codec_all_media_type_capability").Field;
        var version = Assert.Single(catalog.Fields, field => field.Field.FieldName == "codec_all_version_declared").Field;
        var mediaBefore = access.Backend.ReadCached(media);
        var mediaTime = mediaBefore.ObservedAt;
        Assert.Equal(RegisteredPcm.MajorType.ToString("D"),
            mediaBefore.Value!.Structure!.Value.GetProperty("input")[0].GetProperty("majorType").GetString());
        var versionValue = access.Backend.ReadCached(version).Value!.Text!;
        Assert.Contains("Source=directshow-category-moniker", versionValue);
        Assert.Contains("RegistryView=registry64", versionValue);

        directorySucceeded = false;
        var failedVersion = Assert.Single(await access.Backend.CollectAsync([version], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failedVersion.Status);
        Assert.Null(failedVersion.Value);
        Assert.Null(failedVersion.ObservedAt);
        Assert.Equal(mediaBefore, access.Backend.ReadCached(media));
        Assert.Equal(mediaTime, access.Backend.ReadCached(media).ObservedAt);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == media.DeviceIdentity);

        directorySucceeded = true;
        entries.Clear();
        _ = await access.Backend.DiscoverAsync(default);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == media.DeviceIdentity);
    }

    [Fact]
    public void DirectShowMediaTypesAndVersionKeepSourceAndRegistryEvidenceSeparateFromMft()
    {
        var moduleVersion = ModuleVersion(WindowsCodecRegistrationSource.DirectShowCategoryMoniker,
            AudioDecoderClassId, "1.2.3.4");
        var directShow = DirectShow(WindowsDirectShowCategory.RegisteredFilters, AudioDecoderClassId,
            "@device:sw:{083863f1-70de-11d0-bd40-00a0c911ce86}\\{11223344-5566-7788-99aa-bbccddeeff00}",
            version: moduleVersion);
        var scope = new WindowsMediaInformationScope(WindowsMediaInformationFields.CodecMediaTypes
                | WindowsMediaInformationFields.CodecVersion,
            [], 0, [WindowsMftCategory.AudioDecoder], 32,
            [WindowsDirectShowCategory.RegisteredFilters], WindowsCodecRegistryView.Registry64);
        var directShowObject = WindowsInformationFieldProjection.ProjectDirectShow(directShow, scope.Fields, scope.CodecRegistryView);
        var mftObject = WindowsInformationFieldProjection.ProjectMft(
            Mft(WindowsMftCategory.AudioDecoder, AudioDecoderClassId) with
            { Version = ModuleVersion(WindowsCodecRegistrationSource.MediaFoundationMft, AudioDecoderClassId, "9.8.7.6") },
            scope.Fields, scope.CodecRegistryView);

        Assert.NotEqual(directShowObject.DeviceIdentity, mftObject.DeviceIdentity);
        Assert.StartsWith("windows-directshow|registered-filters|", directShowObject.DeviceIdentity, StringComparison.Ordinal);
        Assert.StartsWith("windows-mft|audio-decoder|", mftObject.DeviceIdentity, StringComparison.Ordinal);
        var media = Assert.Single(directShowObject.Fields,
            field => field.Definition.Name == "codec_all_media_type_capability").Value!.Value;
        Assert.Equal(directShow.MonikerDisplayName, media.GetProperty("sourceIdentity").GetString());
        Assert.Equal(JsonValueKind.Null, media.GetProperty("input").ValueKind);
        var version = Assert.Single(directShowObject.Fields,
            field => field.Definition.Name == "codec_all_version_declared").Value!.Value.GetString()!;
        Assert.Contains("Source=directshow-category-moniker", version);
        Assert.Contains("RegistryView=registry64", version);
        Assert.Contains(@"InprocServer32=C:\Recorded\Codec.dll", version);
        Assert.Contains(@"ResolvedModule=C:\Recorded\Codec.dll", version);
        Assert.Contains("FileVersion=1.2.3.4", version);
        var mftVersion = Assert.Single(mftObject.Fields,
            field => field.Definition.Name == "codec_all_version_declared").Value!.Value.GetString()!;
        Assert.Contains("Source=media-foundation-mft", mftVersion);
        Assert.Contains("FileVersion=9.8.7.6", mftVersion);
    }

    [Fact]
    public void CodecVersionEvidenceMustMatchTheObservedSourceClassAndRegistryView()
    {
        var scope = new WindowsMediaInformationScope(WindowsMediaInformationFields.CodecVersion,
            [], 0, [WindowsMftCategory.AudioDecoder], 32,
            [WindowsDirectShowCategory.RegisteredFilters], WindowsCodecRegistryView.Registry64);
        var wrongSource = ModuleVersion(WindowsCodecRegistrationSource.DirectShowCategoryMoniker,
            AudioDecoderClassId, "1.2.3.4");
        var wrongClassId = ModuleVersion(WindowsCodecRegistrationSource.MediaFoundationMft,
            Guid.NewGuid(), "1.2.3.4");
        var wrongView = new DeviceTopologyCodecModuleVersion(WindowsCodecRegistrationSource.MediaFoundationMft,
            AudioDecoderClassId, WindowsCodecRegistryView.Registry32, @"C:\Recorded\Codec.dll",
            @"C:\Recorded\Codec.dll", "1.2.3.4", null, null);

        Assert.Throws<InvalidDataException>(() => WindowsInformationFieldProjection.ProjectMft(
            Mft(WindowsMftCategory.AudioDecoder, AudioDecoderClassId) with { Version = wrongSource },
            scope.Fields, scope.CodecRegistryView));
        Assert.Throws<InvalidDataException>(() => WindowsInformationFieldProjection.ProjectMft(
            Mft(WindowsMftCategory.AudioDecoder, AudioDecoderClassId) with { Version = wrongClassId },
            scope.Fields, scope.CodecRegistryView));
        Assert.Throws<InvalidDataException>(() => WindowsInformationFieldProjection.ProjectMft(
            Mft(WindowsMftCategory.AudioDecoder, AudioDecoderClassId) with { Version = wrongView },
            scope.Fields, scope.CodecRegistryView));
        Assert.Throws<InvalidDataException>(() => WindowsInformationFieldProjection.ProjectDirectShow(
            DirectShow(WindowsDirectShowCategory.RegisteredFilters, AudioDecoderClassId,
                "@device:recorded", version: ModuleVersion(WindowsCodecRegistrationSource.MediaFoundationMft,
                    AudioDecoderClassId, "1.2.3.4")), scope.Fields, scope.CodecRegistryView));
    }

    [Fact]
    public async Task SelectedFieldFailurePreservesDirectoryAndUnselectedValuesThenEmptySuccessRetiresIt()
    {
        var scope = Scope(WindowsMediaInformationFields.Audio);
        var original = new DeviceTopologyAudioEndpointInformation("render-one", WindowsAudioDataFlow.Render,
            [WindowsAudioDefaultRole.Console], WaveFormat(44100));
        var capture = new DeviceTopologyAudioEndpointInformation("capture-one", WindowsAudioDataFlow.Capture,
            [WindowsAudioDefaultRole.Communications], WaveFormat(16000));
        var readerState = new ReaderState();
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            token.ThrowIfCancellationRequested();
            var result = RecordedReader.Result(actualScope, selected, readerState.Endpoints, [], readerState.AudioDevices);
            return result with
            {
                Directories = result.Directories.Select(directory =>
                    directory.IdentityPrefix == WindowsInformationFieldProjection.AudioIdentityPrefix(WindowsAudioDataFlow.Render)
                        ? directory with { DirectorySucceeded = readerState.RenderDirectorySucceeded,
                            Objects = readerState.RenderDirectorySucceeded ? directory.Objects : [] }
                        : directory)
                    .ToArray()
            };
        });
        readerState.Set(original, capture);
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var renderIdentity = WindowsInformationFieldProjection.AudioIdentity(WindowsAudioDataFlow.Render, "render-one");
        var mix = catalog.Fields.Single(field => field.Field.DeviceIdentity == renderIdentity
            && field.Field.FieldName == "audio_all_mix_format_configured").Field;
        var endpointId = new FieldReference(renderIdentity, "audio_all_endpoint_id_declared");
        var role = new FieldReference(renderIdentity, "audio_all_default_role_actual");
        var endpointIdObservedAt = access.Backend.ReadCached(endpointId).ObservedAt;
        var roleObservedAt = access.Backend.ReadCached(role).ObservedAt;

        readerState.Set(original with { MixFormat = WaveFormat(96000) }, capture);
        var selected = (await access.Backend.CollectAsync([mix], default)).Single();
        Assert.Equal(FieldObservationStatus.Current, selected.Status);
        Assert.NotNull(selected.ObservedAt);
        Assert.Equal(scope.Fields & WindowsMediaInformationFields.AudioMixFormat, reader.Requests.Last());
        Assert.Equal("render-one", access.Backend.ReadCached(endpointId).Value!.Text);
        Assert.Equal(endpointIdObservedAt, access.Backend.ReadCached(endpointId).ObservedAt);
        Assert.Equal("console", access.Backend.ReadCached(role).Value!.Structure!.Value[0].GetString());
        Assert.Equal(roleObservedAt, access.Backend.ReadCached(role).ObservedAt);
        Assert.Equal(96000, access.Backend.ReadCached(mix).Value!.Structure!.Value.GetProperty("samplesPerSecond").GetInt32());

        var beforeFailure = owner.ReadState();
        readerState.RenderDirectorySucceeded = false;
        readerState.Set(original with { MixFormat = WaveFormat(192000) }, capture);
        var failed = (await access.Backend.CollectAsync([mix], default)).Single();
        Assert.NotSame(beforeFailure, owner.ReadState());
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);
        Assert.Null(failed.ObservedAt);
        Assert.Equal("render-one", access.Backend.ReadCached(endpointId).Value!.Text);
        Assert.Equal(endpointIdObservedAt, access.Backend.ReadCached(endpointId).ObservedAt);
        Assert.Equal("console", access.Backend.ReadCached(role).Value!.Structure!.Value[0].GetString());
        Assert.Equal(roleObservedAt, access.Backend.ReadCached(role).ObservedAt);

        using var borrow = access.BorrowSubscriptionCapture();
        _ = borrow.Bind([mix]);
        readerState.RenderDirectorySucceeded = true;
        readerState.Set(capture);
        var retired = await borrow.CollectAsync([mix], new Permit());
        Assert.True(retired.Committed);
        Assert.Equal(FieldObservationStatus.Unavailable, Assert.Single(retired.Readings).Status);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == renderIdentity);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == WindowsInformationFieldProjection.AudioIdentity(WindowsAudioDataFlow.Capture, "capture-one"));
    }

    [Fact]
    public async Task ConnectedAudioDeviceVersionHasItsOwnIdentityAndEmptySuccessRetiresIt()
    {
        var scope = Scope(WindowsMediaInformationFields.AudioDriverVersion);
        var device = new DeviceTopologyAudioDeviceInformation("SWD\\MMDEVAPI\\adapter-1", "10.20.30.40");
        var state = new ReaderState();
        state.SetAudioDevices(device);
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            token.ThrowIfCancellationRequested();
            var result = RecordedReader.Result(actualScope, selected, [], [], state.AudioDevices);
            return result with
            {
                Directories = result.Directories
                    .Select(directory => directory with { DirectorySucceeded = state.AudioDeviceDirectorySucceeded,
                        Objects = state.AudioDeviceDirectorySucceeded ? directory.Objects : [] })
                    .ToArray()
            };
        });
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var capability = Assert.Single(catalog.Fields);
        Assert.Equal("audio_all_driver_version_declared", capability.Field.FieldName);
        Assert.Equal("windows-audio-device|SWD\\MMDEVAPI\\adapter-1", capability.Field.DeviceIdentity);
        Assert.Equal("audio_device", Assert.Single(catalog.Devices).Resource);

        state.AudioDeviceDirectorySucceeded = false;
        var failed = Assert.Single(await access.Backend.CollectAsync([capability.Field], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);
        Assert.Null(failed.ObservedAt);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            item => item.DeviceIdentity == capability.Field.DeviceIdentity);

        state.AudioDeviceDirectorySucceeded = true;
        state.SetAudioDevices();
        _ = await access.Backend.DiscoverAsync(default);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            item => item.DeviceIdentity == capability.Field.DeviceIdentity);
    }

    [Fact]
    public async Task RevokedPermitAndCancellationPublishNothingThroughTheOriginalBorrow()
    {
        var scope = Scope(WindowsMediaInformationFields.AudioMixFormat);
        var endpoint = new DeviceTopologyAudioEndpointInformation("render-one", WindowsAudioDataFlow.Render,
            MixFormat: WaveFormat(48000));
        var reader = new RecordedReader((actualScope, selected, token)
            => RecordedReader.Result(actualScope, selected, [endpoint], []));
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var field = catalog.Fields.Single().Field;
        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([field]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));

        var original = owner.ReadState();
        var rejected = await borrow.CollectAsync([field], new Permit(active: false));
        Assert.False(rejected.Committed);
        Assert.Same(original, owner.ReadState());

        using var entered = new ManualResetEventSlim();
        reader.BeforeReturn = token =>
        {
            entered.Set();
            while (!token.IsCancellationRequested) Thread.Sleep(1);
            token.ThrowIfCancellationRequested();
        };
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(async () => await borrow.CollectAsync([field], new Permit(), cancellation.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Same(original, owner.ReadState());
    }

    [Fact]
    public async Task CancellationDuringExternalDirectoryMergeDoesNotReplaceOriginalCurrent()
    {
        var scope = Scope(WindowsMediaInformationFields.AudioMixFormat);
        var endpoint = new DeviceTopologyAudioEndpointInformation("render-one", WindowsAudioDataFlow.Render,
            MixFormat: WaveFormat(48000));
        CancellationTokenSource? cancelDuringMerge = null;
        var reader = new RecordedReader((actualScope, selected, token) =>
        {
            var result = RecordedReader.Result(actualScope, selected, [endpoint], []);
            return cancelDuringMerge is { } cancellation
                ? result with { Directories = result.Directories.Select(directory => directory with
                    { Objects = new CancelOnEnumerateList(directory.Objects, cancellation) }).ToArray() }
                : result;
        });
        using var owner = EmptyOwner();
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var field = Assert.Single(catalog.Fields).Field;
        var original = owner.ReadState();

        using var cancellation = new CancellationTokenSource();
        cancelDuringMerge = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([field], cancellation.Token));
        Assert.Same(original, owner.ReadState());
    }

    [Fact]
    public void WaveFormatParserRetainsTheSharedMixFormatAndRejectsTruncatedExtensibleData()
    {
        var bytes = new byte[40];
        WriteUInt16(bytes, 0, 0xFFFE);
        WriteUInt16(bytes, 2, 2);
        WriteUInt32(bytes, 4, 48000);
        WriteUInt32(bytes, 8, 192000);
        WriteUInt16(bytes, 12, 4);
        WriteUInt16(bytes, 14, 16);
        WriteUInt16(bytes, 16, 22);
        WriteUInt16(bytes, 18, 16);
        WriteUInt32(bytes, 20, 3);
        var subFormat = new Guid("00000001-0000-0010-8000-00AA00389B71");
        subFormat.TryWriteBytes(bytes.AsSpan(24, 16));

        var parsed = WindowsMediaInformationNativeReader.ParseWaveFormat(bytes);
        Assert.NotNull(parsed);
        Assert.Equal(48000u, parsed.SamplesPerSecond);
        Assert.Equal((ushort)16, parsed.ValidBitsPerSample);
        Assert.Equal(3u, parsed.ChannelMask);
        Assert.Equal(subFormat, parsed.SubFormat);
        Assert.Null(WindowsMediaInformationNativeReader.ParseWaveFormat(bytes[..39]));
    }

    [Fact]
    public void MftEnumUsesItsReservedZeroFlagsParameter()
        => Assert.Equal(0U, WindowsMediaInformationNativeReader.MftEnumReservedFlags);

    [Fact]
    public void DirectShowCategoriesUseTheWindowsSdkCategoryGuids()
    {
        Assert.Equal(new Guid("083863F1-70DE-11D0-BD40-00A0C911CE86"),
            WindowsMediaInformationScope.DirectShowCategoryId(WindowsDirectShowCategory.RegisteredFilters));
        Assert.Equal(new Guid("33D9A761-90C8-11D0-BD43-00A0C911CE86"),
            WindowsMediaInformationScope.DirectShowCategoryId(WindowsDirectShowCategory.AudioCompressors));
        Assert.Equal(new Guid("33D9A760-90C8-11D0-BD43-00A0C911CE86"),
            WindowsMediaInformationScope.DirectShowCategoryId(WindowsDirectShowCategory.VideoCompressors));
        Assert.Equal(new Guid("33D9A762-90C8-11D0-BD43-00A0C911CE86"),
            WindowsMediaInformationScope.DirectShowCategoryId(WindowsDirectShowCategory.AudioCaptureSources));
        Assert.Equal(new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86"),
            WindowsMediaInformationScope.DirectShowCategoryId(WindowsDirectShowCategory.VideoCaptureSources));
    }

    private static WindowsMediaInformationScope Scope(WindowsMediaInformationFields fields = WindowsMediaInformationFields.All,
        IEnumerable<WindowsDirectShowCategory>? directShowCategories = null)
        => new(fields,
            (fields & WindowsMediaInformationFields.Audio) != 0
                ? [WindowsAudioDataFlow.Render, WindowsAudioDataFlow.Capture] : [],
            1,
            (fields & WindowsMediaInformationFields.Codec) != 0
                ? [WindowsMftCategory.AudioDecoder, WindowsMftCategory.AudioEncoder,
                    WindowsMftCategory.VideoDecoder, WindowsMftCategory.VideoEncoder] : [],
            64, directShowCategories, WindowsCodecRegistryView.Registry64);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static DeviceTopologyWaveFormat WaveFormat(uint sampleRate)
        => new(1, 2, sampleRate, sampleRate * 4, 4, 16, 0, null, null, null, "");

    private static DeviceTopologyRegisteredMftInformation Mft(WindowsMftCategory category, Guid classId)
        => new(category, classId, [RegisteredPcm], [RegisteredPcm]);

    private static DeviceTopologyRegisteredDirectShowInformation DirectShow(WindowsDirectShowCategory category,
        Guid classId, string monikerDisplayName,
        ImmutableArray<DeviceTopologyRegisteredMediaType>? passiveTypes = null,
        DeviceTopologyCodecModuleVersion? version = null)
        => new(category, monikerDisplayName, classId, "Recorded DirectShow filter", @"\\?\recorded-device",
            passiveTypes, passiveTypes, version);

    private static DeviceTopologyCodecModuleVersion ModuleVersion(WindowsCodecRegistrationSource source,
        Guid classId, string fileVersion)
        => new(source, classId, WindowsCodecRegistryView.Registry64,
            @"C:\Recorded\Codec.dll", @"C:\Recorded\Codec.dll", fileVersion, "Recorded Provider", "Recorded Codec");

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        for (var index = 0; index < sizeof(uint); index++) bytes[offset + index] = (byte)(value >> (8 * index));
    }

    private sealed class ReaderState
    {
        private DeviceTopologyAudioEndpointInformation[] render = [];
        private DeviceTopologyAudioEndpointInformation[] capture = [];
        private DeviceTopologyAudioDeviceInformation[] audioDevices = [];
        public bool RenderDirectorySucceeded { get; set; } = true;
        public bool AudioDeviceDirectorySucceeded { get; set; } = true;
        public IReadOnlyList<DeviceTopologyAudioEndpointInformation> Endpoints => [.. render, .. capture];
        public IReadOnlyList<DeviceTopologyAudioDeviceInformation> AudioDevices => audioDevices;
        public void SetAudioDevices(params DeviceTopologyAudioDeviceInformation[] devices) => audioDevices = devices;
        public void Set(params DeviceTopologyAudioEndpointInformation[] endpoints)
        {
            render = endpoints.Where(static endpoint => endpoint.DataFlow == WindowsAudioDataFlow.Render).ToArray();
            capture = endpoints.Where(static endpoint => endpoint.DataFlow == WindowsAudioDataFlow.Capture).ToArray();
        }
    }

    private sealed class RecordedReader(
        Func<WindowsMediaInformationScope, WindowsMediaInformationFields, CancellationToken, WindowsMediaInformationReadResult> read)
        : IWindowsMediaInformationReader
    {
        public ConcurrentQueue<WindowsMediaInformationFields> Requests { get; } = new();
        public Action<CancellationToken>? BeforeReturn { get; set; }

        public WindowsMediaInformationReadResult Read(WindowsMediaInformationScope scope,
            WindowsMediaInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(selectedFields);
            BeforeReturn?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return read(scope, selectedFields, cancellationToken);
        }

        public static WindowsMediaInformationReadResult Result(WindowsMediaInformationScope scope,
            WindowsMediaInformationFields selected, IReadOnlyList<DeviceTopologyAudioEndpointInformation> audio,
            IReadOnlyList<DeviceTopologyRegisteredMftInformation> transforms,
            IReadOnlyList<DeviceTopologyAudioDeviceInformation>? audioDevices = null,
            IReadOnlyList<DeviceTopologyRegisteredDirectShowInformation>? directShow = null)
        {
            var directories = new List<WindowsMediaDirectoryRead>();
            if ((selected & WindowsMediaInformationScope.AudioEndpointFields) != 0)
                foreach (var direction in scope.AudioDirections)
                    directories.Add(new(WindowsInformationFieldProjection.AudioIdentityPrefix(direction), true,
                        [.. audio.Where(endpoint => endpoint.DataFlow == direction)
                            .Select(endpoint => WindowsInformationFieldProjection.ProjectAudio(endpoint, selected))]));
            if ((selected & WindowsMediaInformationFields.AudioDriverVersion) != 0)
                directories.Add(new(WindowsInformationFieldProjection.AudioDeviceIdentityPrefix, true,
                    [.. (audioDevices ?? []).Select(device => WindowsInformationFieldProjection.ProjectAudioDevice(device, selected))]));
            if ((selected & WindowsMediaInformationFields.Codec) != 0)
            {
                foreach (var category in scope.CodecCategories)
                    directories.Add(new(WindowsInformationFieldProjection.MftIdentityPrefix(category), true,
                        [.. transforms.Where(transform => transform.Category == category)
                            .Select(transform => WindowsInformationFieldProjection.ProjectMft(transform, selected,
                                scope.CodecRegistryView))]));
                foreach (var category in scope.DirectShowCategories)
                    directories.Add(new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), true,
                        [.. (directShow ?? []).Where(filter => filter.Category == category)
                            .Select(filter => WindowsInformationFieldProjection.ProjectDirectShow(filter, selected,
                                scope.CodecRegistryView))]));
            }
            return new(directories);
        }
    }

    private static WindowsMediaInformationReadResult Recorded(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selected, CancellationToken token,
        IReadOnlyList<DeviceTopologyAudioEndpointInformation> audio,
        IReadOnlyList<DeviceTopologyRegisteredMftInformation> transforms,
        IReadOnlyList<DeviceTopologyAudioDeviceInformation>? audioDevices = null,
        IReadOnlyList<DeviceTopologyRegisteredDirectShowInformation>? directShow = null)
    {
        token.ThrowIfCancellationRequested();
        return RecordedReader.Result(scope, selected, audio, transforms, audioDevices, directShow);
    }

    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }

    private sealed class CancelOnEnumerateList(IReadOnlyList<DeviceInformationObject> items,
        CancellationTokenSource cancellation) : IReadOnlyList<DeviceInformationObject>
    {
        public int Count => items.Count;
        public DeviceInformationObject this[int index] => items[index];
        public IEnumerator<DeviceInformationObject> GetEnumerator()
        { cancellation.Cancel(); return items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

