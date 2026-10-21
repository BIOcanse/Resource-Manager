using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class D3dCapabilitySubscriptionConsumer
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
        var reader = new RecordedProvider { Mode = RecordedMode.Current };
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
        Require(reader.Requests.Single() == selectedFields, "initial exact selected scope");
        reader.Mode = RecordedMode.Unavailable;

        var borrow = access.BorrowSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        var workerFailures = new ConcurrentQueue<string>();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => workerFailures.Enqueue($"{failure.Stage}: {failure.Error.Message}"), TimeSpan.FromMilliseconds(10));

        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-d3d-capabilities",
            [feature, format, profile, outputFormat, decoderConfiguration, processorCaps, rateConversionCaps],
            TimeSpan.FromSeconds(1), new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator())
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "unavailable selected read was delivered");
            var readings = stream.Current.ToDictionary(static reading => reading.Field);
            Require(readings[feature].Status == FieldObservationStatus.Unavailable && readings[feature].Value is null,
                "unsupported feature-level field is explicitly unavailable");
            Require(readings[format].Status == FieldObservationStatus.Unavailable && readings[format].Value is null,
                "unsupported selected format field is explicitly unavailable");
            Require(readings[profile].Status == FieldObservationStatus.Unavailable && readings[profile].Value is null,
                "failed profile enumeration invalidates the prior selected profile value");
            Require(readings[outputFormat].Status == FieldObservationStatus.Unavailable && readings[outputFormat].Value is null,
                "failed profile enumeration invalidates the prior selected output-format value");
            Require(readings[decoderConfiguration].Status == FieldObservationStatus.Unavailable
                && readings[decoderConfiguration].Value is null,
                "failed explicit decoder configuration query is unavailable, not a zero-result list");
            Require(readings[processorCaps].Status == FieldObservationStatus.Unavailable
                && readings[processorCaps].Value is null,
                "failed processor caps query is unavailable");
            Require(readings[rateConversionCaps].Status == FieldObservationStatus.Unavailable
                && readings[rateConversionCaps].Value is null,
                "failed rate-conversion query is unavailable, not a zero-group result");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[feature], backend.ReadCached(feature));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[format], backend.ReadCached(format));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[profile], backend.ReadCached(profile));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[outputFormat], backend.ReadCached(outputFormat));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[decoderConfiguration], backend.ReadCached(decoderConfiguration));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[processorCaps], backend.ReadCached(processorCaps));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[rateConversionCaps], backend.ReadCached(rateConversionCaps));
            Require(reader.Requests.Last() == selectedFields,
                "subscription selected only the requested canonical D3D and decoder fields");

            var stateBeforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            await composition.StopAsync().WaitAsync(timeout.Token);
            Require(ReferenceEquals(stateBeforeCancel, owner.ReadState()),
                "canceled native-role read did not commit to the shared current");
            Require(!await pending.WaitAsync(timeout.Token), "stopped role-2 stream ended without a late value");
        }
        Require(workerFailures.IsEmpty, "recorded unsupported result and cancellation were not worker faults");

        reader.Mode = RecordedMode.Current;
        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            var lease = composition.Acquire("last-recorded-d3d-demand",
                [feature, format, profile, outputFormat, decoderConfiguration, processorCaps, rateConversionCaps],
                TimeSpan.FromMilliseconds(250));
            using (lease)
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand, "last explicit demand caused a recorded capture");
                Require(backend.ReadCached(feature).Status == FieldObservationStatus.Current
                    && backend.ReadCached(feature).Value?.Integer == "45056",
                    "successful D3D11 feature-level value is current in the same owner");
                Require(backend.ReadCached(format).Status == FieldObservationStatus.Current
                    && backend.ReadCached(format).Value?.Integer == "281474976710689",
                    "successful D3D11 format flags preserve the exact packed 64-bit value");
                Require(backend.ReadCached(profile).Status == FieldObservationStatus.Current
                    && backend.ReadCached(profile).Value?.Text == RecordedProvider.ProfileGuid.ToString("D"),
                    "successful decoder profile is current in the same owner");
                Require(backend.ReadCached(outputFormat).Status == FieldObservationStatus.Current
                    && backend.ReadCached(outputFormat).Value?.Integer == "103",
                    "successful decoder output format is current in the same owner");
                var configCurrent = backend.ReadCached(decoderConfiguration);
                Require(configCurrent.Status == FieldObservationStatus.Current
                    && owner.ReadState().InformationFields.FindField(decoderConfiguration) is not null,
                    "successful decoder configuration is current in the original owner");
                using var configJson = JsonDocument.Parse(configCurrent.Value!.Structure!.Value.GetRawText());
                Require(configJson.RootElement.GetProperty("configurationCount").GetUInt32() == 1
                    && configJson.RootElement.GetProperty("configurations")[0].GetProperty("index").GetUInt32() == 0,
                    "subscription observes the indexed configuration from the original current");
                var processorCurrent = backend.ReadCached(processorCaps);
                Require(processorCurrent.Status == FieldObservationStatus.Current
                    && owner.ReadState().InformationFields.FindField(processorCaps) is not null,
                    "successful processor caps are current in the original owner");
                using var processorJson = JsonDocument.Parse(processorCurrent.Value!.Structure!.Value.GetRawText());
                Require(processorJson.RootElement.GetProperty("caps").EnumerateObject().Count() == 9,
                    "subscription retains all processor caps members");
                var rateCurrent = backend.ReadCached(rateConversionCaps);
                using var rateJson = JsonDocument.Parse(rateCurrent.Value!.Structure!.Value.GetRawText());
                Require(rateCurrent.Status == FieldObservationStatus.Current
                    && rateJson.RootElement.GetProperty("rateConversionCapsCount").GetUInt32() == 2
                    && rateJson.RootElement.GetProperty("rateConversionCaps").GetArrayLength() == 2,
                    "subscription observes complete indexed rate groups from the original current");
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(350));
        var readsAfterLastDemand = reader.ReadCount;
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Require(reader.ReadCount == readsAfterLastDemand, "last resource demand withdrawal stopped further reads");
        await composition.StopAsync();
        Require(workerFailures.IsEmpty, "recorded role-2 D3D subscription completed without worker faults");
        await RunRaytracingAsync(owner, adapter);
        Console.WriteLine("[D3D-CAPABILITY-SUBSCRIPTION] recorded unavailable fields, processor descriptor groups, canceled no-commit, successful same-owner reads, last-demand withdrawal, explicit Start/Stop passed; no D3D native read.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunRaytracingAsync(DeviceTopologyCaptureOwner owner, WindowsD3dAdapterIdentity adapter)
    {
        var reader = new RecordedRaytracingProvider();
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.RaytracingTier,
            WindowsD3dApiVersion.Direct3D12, [adapter]);
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        var field = (await access.Backend.DiscoverAsync(default)).Fields.Single().Field;
        Require(field.FieldName == "gpu_all_d3d12_raytracing_tier" && access.Backend.ReadCached(field).Value?.Integer == "0",
            "raytracing successful-zero discovered through the actual package API");
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        var failures = new ConcurrentQueue<string>();
        await using var composition = new NativeFieldSubscriptionComposition(access.BorrowSubscriptionCapture(), settings,
            in configuration, failure => failures.Enqueue(failure.Error.Message), TimeSpan.FromMilliseconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (uint? tier in new uint?[] { 0, null, uint.MaxValue, 12 })
        {
            reader.Tier = tier;
            await composition.StartAsync();
            try
            {
                await using var stream = composition.SubscribeAsync("recorded-d3d12-raytracing", [field], TimeSpan.FromSeconds(1),
                    new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator();
                Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "raytracing subscription produced a reading");
                var observed = stream.Current.Single();
                Require(tier is { } value
                    ? observed.Status == FieldObservationStatus.Current && observed.Value?.Integer == value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : observed.Status == FieldObservationStatus.Unavailable && observed.Value is null && observed.ObservedAt is null,
                    "raytracing role-2 preserves zero, unavailable, raw UInt32 and restored tier");
                FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(observed, access.Backend.ReadCached(field));
                Require(reader.LastFields == WindowsD3dCapabilityFields.RaytracingTier, "role-2 selects only tier");
            }
            finally { await composition.StopAsync().WaitAsync(timeout.Token); }
        }
        Require(failures.IsEmpty, "raytracing role-2 produced no worker faults");
        Require(owner.ReadState().InformationFields.FindField(field)?.Value?.GetString() == "12",
            "stopped composition leaves its last value in the original owner");
        Console.WriteLine("[D3D12-RAYTRACING-SUBSCRIPTION] recorded zero/unavailable/raw-UInt32/recovery via original owner; no native GPU access.");
    }

    private sealed class RecordedRaytracingProvider : IWindowsD3dCapabilityReader
    {
        private long tier = 0;
        public uint? Tier { get { var value = Interlocked.Read(ref tier); return value < 0 ? null : (uint)value; }
            set => Interlocked.Exchange(ref tier, value is { } current ? current : -1L); }
        public WindowsD3dCapabilityFields LastFields { get; private set; }
        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LastFields = selectedFields;
            return new([new(scope.Adapters.Single(), scope.ApiVersion, null, []) { RaytracingTier = Tier }]);
        }
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
        if (!condition) throw new InvalidOperationException($"D3D capability subscription consumer failed: {label}.");
    }

    private enum RecordedMode { Unavailable, Current }

    private sealed class RecordedProvider : IWindowsD3dCapabilityReader
    {
        internal static readonly Guid ProfileGuid = new("2f7f9f9e-1a4c-4b72-8a8d-0c5c032d5041");
        private readonly ConcurrentQueue<WindowsD3dCapabilityFields> requests = new();
        private int blockNextRead;
        private TaskCompletionSource blockEntered = CompletedSignal();
        private int readCount;
        public RecordedMode Mode { get; set; }
        public IReadOnlyList<WindowsD3dCapabilityFields> Requests => requests.ToArray();
        public int ReadCount => Volatile.Read(ref readCount);
        public Task BlockEntered => Volatile.Read(ref blockEntered).Task;

        public void BlockNextReadUntilCanceled()
        {
            Volatile.Write(ref blockEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref blockNextRead, 1);
        }

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            Interlocked.Increment(ref readCount);
            if (Interlocked.Exchange(ref blockNextRead, 0) != 0)
            {
                Volatile.Read(ref blockEntered).TrySetResult();
                if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The subscription composition did not cancel its recorded pending read.");
                cancellationToken.ThrowIfCancellationRequested();
            }

            var available = Mode == RecordedMode.Current;
            return new([.. scope.Adapters.Select(adapter => new WindowsD3dAdapterCapabilityRead(
                adapter, scope.ApiVersion,
                (selectedFields & WindowsD3dCapabilityFields.FeatureLevel) != 0 && available
                    ? WindowsD3dFeatureLevel.Level11_0 : null,
                (selectedFields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                    ? [.. scope.Formats.Select(format => new WindowsD3dFormatSupportRead(format,
                        available ? 0x0001_0000_0000_0021 : null))]
                    : [],
                (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                    | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0 ? available : null,
                (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                    | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0 && available
                    ? [new WindowsD3dVideoDecoderProfileRead(ProfileGuid,
                        (selectedFields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0 ? true : null,
                        (selectedFields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0
                            ? [new(WindowsD3dFormat.Nv12, true), new(WindowsD3dFormat.P010, false)] : [])]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0
                    ? available
                        ? [new WindowsD3dVideoDecoderConfigurationsRead(Descriptor, true, 1,
                            [new(0, new WindowsD3dVideoDecoderConfig(Guid.Empty, Guid.Empty, Guid.Empty,
                                1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0))])]
                        : [new WindowsD3dVideoDecoderConfigurationsRead(Descriptor, false, null, [])]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0
                    ? [new WindowsD3dVideoProcessorCapsRead(ProcessorDescriptor, available,
                        available ? new(1, 2, 3, 4, 5, 6, 2, 8, 9) : null)]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0
                    ? available
                        ? [new WindowsD3dVideoProcessorRateConversionCapsRead(ProcessorDescriptor, true, 2,
                            [new(0, new(1, 2, 3, 4, 5)), new(1, new(6, 7, 8, 9, 10))])]
                        : [new WindowsD3dVideoProcessorRateConversionCapsRead(ProcessorDescriptor, false, null, [])]
                    : []))]);
        }

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }
    }
}
