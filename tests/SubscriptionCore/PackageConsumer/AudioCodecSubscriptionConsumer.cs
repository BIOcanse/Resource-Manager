using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Media;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class AudioCodecSubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var reader = new RecordedReader();
        var scope = new WindowsMediaInformationScope(WindowsMediaInformationFields.Audio | WindowsMediaInformationFields.Codec,
            [WindowsAudioDataFlow.Render, WindowsAudioDataFlow.Capture], 1,
            [WindowsMftCategory.AudioDecoder, WindowsMftCategory.AudioEncoder,
                WindowsMftCategory.VideoDecoder, WindowsMftCategory.VideoEncoder], 32,
            [WindowsDirectShowCategory.RegisteredFilters, WindowsDirectShowCategory.AudioCompressors,
                WindowsDirectShowCategory.VideoCompressors], WindowsCodecRegistryView.Registry64);
        var access = owner.CreateAudioCodecInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var renderEndpoint = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_endpoint_id_declared"
            && backend.ReadCached(field.Field).Value?.Text == "recorded-render-endpoint");
        var mix = catalog.Fields.Single(field => field.Field.FieldName == "audio_all_mix_format_configured"
            && field.Field.DeviceIdentity == renderEndpoint.Field.DeviceIdentity).Field;
        var codec = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_media_type_capability"
            && field.Field.DeviceIdentity.StartsWith("windows-mft|", StringComparison.Ordinal)).Field;
        var directShowMedia = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_media_type_capability"
            && field.Field.DeviceIdentity.StartsWith("windows-directshow|", StringComparison.Ordinal)).Field;
        var codecVersion = catalog.Fields.Single(static field => field.Field.FieldName == "codec_all_version_declared"
            && field.Field.DeviceIdentity.StartsWith("windows-directshow|", StringComparison.Ordinal)).Field;
        Require(reader.Requests.Single() == scope.Fields, "discovery used only the explicit audio/codec scope");

        var borrow = access.BorrowSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        var workerFailures = new ConcurrentQueue<string>();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => workerFailures.Enqueue($"{failure.Stage}: {failure.Error.Message}"));

        reader.FailEndpointDirectories();
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-audio-codec", [mix, codec, directShowMedia, codecVersion],
            TimeSpan.FromSeconds(1), new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator())
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "failed selected capture published a reading");
            var readings = stream.Current.ToDictionary(static reading => reading.Field);
            var unavailable = readings[mix];
            var mftData = readings[codec].Value?.Structure;
            var directShowData = readings[directShowMedia].Value?.Structure;
            Require(unavailable.Status == FieldObservationStatus.Unavailable && unavailable.Value is null,
                "failed endpoint directory clears selected value to unavailable null");
            Require(readings[codec].Status == FieldObservationStatus.Current && mftData is not null,
                "the simultaneously selected registered MFT type remains current");
            Require(readings[directShowMedia].Status == FieldObservationStatus.Current
                && directShowData is { } directShowValue
                && directShowValue.GetProperty("registrationSource").GetString() == "directshow-category-moniker",
                "the simultaneously selected DirectShow source remains current");
            Require(readings[codecVersion].Status == FieldObservationStatus.Current
                && readings[codecVersion].Value?.Text?.Contains("FileVersion=4.5.6.7", StringComparison.Ordinal) == true,
                "the exact CLSID module version remains current");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(unavailable, backend.ReadCached(mix));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[codec], backend.ReadCached(codec));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[directShowMedia], backend.ReadCached(directShowMedia));
            Require(reader.Requests.Last() == (WindowsMediaInformationFields.AudioMixFormat
                | WindowsMediaInformationFields.CodecMediaTypes | WindowsMediaInformationFields.CodecVersion),
                "subscription sampled only its four selected fields");

            var stateBeforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            var stop = composition.StopAsync();
            await stop.WaitAsync(timeout.Token);
            Require(ReferenceEquals(stateBeforeCancel, owner.ReadState()),
                "canceled in-flight read did not commit to the shared current");
            Require(!await pending.WaitAsync(timeout.Token), "stopped role-2 stream ended without a late value");
        }
        Require(workerFailures.IsEmpty, "recorded failure and cancellation were not worker faults");

        reader.Succeed();
        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            var lease = composition.Acquire("last-audio-codec-demand", [mix, codec, directShowMedia], TimeSpan.FromMilliseconds(250));
            using (lease)
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand, "explicit last demand caused a recorded capture");
                var current = backend.ReadCached(mix);
                Require(current.Status == FieldObservationStatus.Current && current.Value?.Structure is not null,
                    "successful recorded capture restored the selected mix format in the same current");
                Require(backend.ReadCached(codec).Status == FieldObservationStatus.Current,
                    "successful registered MFT capture remains on that same current");
                Require(backend.ReadCached(directShowMedia).Status == FieldObservationStatus.Current,
                    "successful DirectShow category capture remains on that same current");
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(350));
        var readsAfterLastDemand = reader.ReadCount;
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Require(reader.ReadCount == readsAfterLastDemand, "last resource demand withdrawal stopped further reads");
        await composition.StopAsync();
        Require(workerFailures.IsEmpty, "recorded subscription completed without worker faults");
        Console.WriteLine("[CODEC-AUDIO-SUBSCRIPTION] recorded role-2 audio/MFT failure/null, canceled no-commit, last-demand withdrawal, explicit Start/Stop passed; no native read.");
        Console.WriteLine("[CODEC-DIRECTSHOW-SUBSCRIPTION] recorded same-owner DirectShow registration/version with the selected render endpoint, canceled no-commit, last-demand withdrawal, explicit Start/Stop passed; no native read.");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Audio/codec subscription consumer failed: {label}.");
    }

    private sealed class RecordedReader : IWindowsMediaInformationReader
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Guid AudioMajorType = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid PcmSubType = new("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid DecoderClassId = new("3f0dc74e-3d1f-45aa-acf0-8d4cc706d851");
        private static readonly Guid DirectShowClassId = new("b438fbe7-d1f8-4a3e-a451-94d8d70caa21");
        private const string MonikerDisplayName = "@device:sw:{083863f1-70de-11d0-bd40-00a0c911ce86}\\{b438fbe7-d1f8-4a3e-a451-94d8d70caa21}";
        private const string ModulePath = @"C:\Recorded\SubscriptionCodec.dll";
        private static readonly DeviceTopologyCodecModuleVersion DirectShowVersion = new(
            WindowsCodecRegistrationSource.DirectShowCategoryMoniker, DirectShowClassId,
            WindowsCodecRegistryView.Registry64, ModulePath, ModulePath, "4.5.6.7",
            "Recorded Provider", "Recorded Subscription Codec");
        private static readonly DeviceTopologyWaveFormat Mix = new(1, 2, 48_000, 192_000, 4, 16, 0,
            null, null, null, "");
        private readonly ConcurrentQueue<WindowsMediaInformationFields> requests = new();
        private int failEndpointDirectories;
        private int blockNextRead;
        private int readCount;
        private TaskCompletionSource blockEntered = CompletedSignal();

        public IEnumerable<WindowsMediaInformationFields> Requests => requests;
        public int ReadCount => Volatile.Read(ref readCount);
        public Task BlockEntered => Volatile.Read(ref blockEntered).Task;

        public void FailEndpointDirectories() => Volatile.Write(ref failEndpointDirectories, 1);
        public void Succeed() => Volatile.Write(ref failEndpointDirectories, 0);
        public void BlockNextReadUntilCanceled()
        {
            Volatile.Write(ref blockEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref blockNextRead, 1);
        }

        public WindowsMediaInformationReadResult Read(WindowsMediaInformationScope scope,
            WindowsMediaInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            Interlocked.Increment(ref readCount);
            if (Interlocked.Exchange(ref blockNextRead, 0) != 0)
            {
                Volatile.Read(ref blockEntered).TrySetResult();
                if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The composition did not cancel the recorded pending read.");
                cancellationToken.ThrowIfCancellationRequested();
            }

            var directories = new List<WindowsMediaDirectoryRead>();
            var endpointFields = selectedFields & (WindowsMediaInformationFields.AudioEndpointId
                | WindowsMediaInformationFields.AudioDefaultRoles | WindowsMediaInformationFields.AudioMixFormat);
            if (endpointFields != WindowsMediaInformationFields.None)
            {
                foreach (var direction in scope.AudioDirections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directionCode = direction == WindowsAudioDataFlow.Render ? "render" : "capture";
                    var prefix = $"windows-audio-endpoint|{directionCode}|";
                    if (Volatile.Read(ref failEndpointDirectories) != 0)
                    {
                        directories.Add(new(prefix, false, []));
                        continue;
                    }
                    var endpoint = direction == WindowsAudioDataFlow.Render
                        ? new DeviceTopologyAudioEndpointInformation("recorded-render-endpoint", direction,
                            ImmutableArray.Create(WindowsAudioDefaultRole.Multimedia), Mix)
                        : new DeviceTopologyAudioEndpointInformation("recorded-capture-endpoint", direction,
                            ImmutableArray.Create(WindowsAudioDefaultRole.Communications), Mix);
                    directories.Add(new(prefix, true, [ProjectEndpoint(endpoint, selectedFields)]));
                }
            }
            if ((selectedFields & WindowsMediaInformationFields.AudioDriverVersion) != 0)
                directories.Add(new("windows-audio-device|", true, []));
            if ((selectedFields & WindowsMediaInformationFields.Codec) != 0)
            {
                foreach (var category in scope.CodecCategories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var code = category switch
                    {
                        WindowsMftCategory.AudioDecoder => "audio-decoder",
                        WindowsMftCategory.AudioEncoder => "audio-encoder",
                        WindowsMftCategory.VideoDecoder => "video-decoder",
                        WindowsMftCategory.VideoEncoder => "video-encoder",
                        _ => throw new ArgumentOutOfRangeException(nameof(category))
                    };
                    DeviceInformationObject[] objects = category == WindowsMftCategory.AudioDecoder
                        ? [ProjectDecoder(selectedFields)] : [];
                    directories.Add(new($"windows-mft|{code}|", true, objects));
                }
                foreach (var category in scope.DirectShowCategories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var code = DirectShowCategoryCode(category);
                    DeviceInformationObject[] objects = category == WindowsDirectShowCategory.RegisteredFilters
                        ? [ProjectDirectShow(selectedFields)] : [];
                    directories.Add(new($"windows-directshow|{code}|", true, objects));
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
            {
                var role = endpoint.DataFlow == WindowsAudioDataFlow.Render ? "multimedia" : "communications";
                fields.Add(Field(DeviceInformationFieldRegistry.AudioAllDefaultRoleActual, new[] { role }));
            }
            if ((selected & WindowsMediaInformationFields.AudioMixFormat) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.AudioAllMixFormatConfigured, endpoint.MixFormat));
            var direction = endpoint.DataFlow == WindowsAudioDataFlow.Render ? "render" : "capture";
            return new($"windows-audio-endpoint|{direction}|{endpoint.EndpointId}", "audio_endpoint", fields);
        }

        private static DeviceInformationField Field<T>(DeviceInformationDefinition definition, T value)
            => new(definition, JsonSerializer.SerializeToElement(value, Json));

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
            var classIdCode = DirectShowClassId.ToString("B").ToLowerInvariant();
            var identity = $"windows-directshow|registered-filters|{classIdCode}|{Uri.EscapeDataString(MonikerDisplayName)}";
            var fields = new List<DeviceInformationField>(2);
            if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
                fields.Add(Field(DeviceInformationFieldRegistry.CodecAllMediaTypeCapability, new
                {
                    registrationSource = "directshow-category-moniker",
                    category = "registered-filters",
                    categoryClassId = "{083863f1-70de-11d0-bd40-00a0c911ce86}",
                    classId = classIdCode,
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

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }
    }
}
