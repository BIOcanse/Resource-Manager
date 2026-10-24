using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaVideoMetricsRecordedTests
{
    private const string SourceId = "nvidia.nvml";
    private static readonly (MetricFieldCode Code, string Name, string Unit)[] VideoFields =
    [
        (MetricFieldCode.NvmlEncoderUtilization, "gpu_nvidia_encoder_busy_percent", "%"),
        (MetricFieldCode.NvmlEncoderPeriod, "gpu_nvidia_encoder_sampling_period_microseconds", "µs"),
        (MetricFieldCode.NvmlDecoderUtilization, "gpu_nvidia_decoder_busy_percent", "%"),
        (MetricFieldCode.NvmlDecoderPeriod, "gpu_nvidia_decoder_sampling_period_microseconds", "µs")
    ];
    private static readonly HashSet<MetricFieldCode> VideoCodes = VideoFields.Select(static field => field.Code).ToHashSet();
    private static readonly string[] VideoNames = VideoFields.Select(static field => field.Name).ToArray();

    [Fact]
    public void NvmlReaderQueriesOnlySelectedEngineFunctionsAndPreservesTheirUnits()
    {
        var requested = new HashSet<MetricFieldCode>
        {
            MetricFieldCode.NvmlEncoderUtilization,
            MetricFieldCode.NvmlDecoderPeriod
        };
        var encoderCalls = 0;
        var decoderCalls = 0;

        var readings = NvidiaNvmlReader.ReadAdditionalMetrics(
            (IntPtr)42,
            requested,
            encoderUtilizationQuery: Encoder,
            decoderUtilizationQuery: Decoder);

        Assert.Equal(1, encoderCalls);
        Assert.Equal(1, decoderCalls);
        Assert.True(requested.SetEquals(readings.Values.Keys));
        Assert.Equal(43d, readings.Values[MetricFieldCode.NvmlEncoderUtilization]);
        Assert.Equal(1_750d, readings.Values[MetricFieldCode.NvmlDecoderPeriod]);
        Assert.Equal("%", Definition(MetricFieldCode.NvmlEncoderUtilization).Unit);
        Assert.Equal("µs", Definition(MetricFieldCode.NvmlDecoderPeriod).Unit);

        int Encoder(IntPtr device, out uint percent, out uint period)
        {
            Assert.Equal((IntPtr)42, device);
            encoderCalls++;
            percent = 43;
            period = 2_300;
            return 0;
        }

        int Decoder(IntPtr device, out uint percent, out uint period)
        {
            Assert.Equal((IntPtr)42, device);
            decoderCalls++;
            percent = 71;
            period = 1_750;
            return 0;
        }
    }

    [Fact]
    public void MissingExportAndFailedEngineStatusReturnNullForOnlyThatEngine()
    {
        var encoderRequest = new HashSet<MetricFieldCode>
        {
            MetricFieldCode.NvmlEncoderUtilization,
            MetricFieldCode.NvmlEncoderPeriod
        };
        var missingEncoder = NvidiaNvmlReader.ReadAdditionalMetrics(
            (IntPtr)42,
            encoderRequest,
            encoderUtilizationQuery: MissingExport,
            decoderUtilizationQuery: UnexpectedDecoder);
        Assert.Null(missingEncoder.Values[MetricFieldCode.NvmlEncoderUtilization]);
        Assert.Null(missingEncoder.Values[MetricFieldCode.NvmlEncoderPeriod]);
        Assert.Empty(missingEncoder.Integers);

        var decoderRequest = new HashSet<MetricFieldCode>
        {
            MetricFieldCode.NvmlDecoderUtilization,
            MetricFieldCode.NvmlDecoderPeriod
        };
        var failedDecoder = NvidiaNvmlReader.ReadAdditionalMetrics(
            (IntPtr)42,
            decoderRequest,
            encoderUtilizationQuery: UnexpectedEncoder,
            decoderUtilizationQuery: FailedDecoder);
        Assert.Null(failedDecoder.Values[MetricFieldCode.NvmlDecoderUtilization]);
        Assert.Null(failedDecoder.Values[MetricFieldCode.NvmlDecoderPeriod]);

        int MissingExport(IntPtr device, out uint percent, out uint period)
        {
            percent = 99;
            period = 9_999;
            throw new EntryPointNotFoundException("recorded NVML engine export is absent");
        }

        int FailedDecoder(IntPtr device, out uint percent, out uint period)
        {
            percent = 88;
            period = 8_888;
            return 3;
        }

        int UnexpectedEncoder(IntPtr device, out uint percent, out uint period)
            => throw new InvalidOperationException("Unselected encoder function was queried.");

        int UnexpectedDecoder(IntPtr device, out uint percent, out uint period)
            => throw new InvalidOperationException("Unselected decoder function was queried.");
    }

    [Fact]
    public async Task SelectedCapturePublishesFourFieldsAndClearsWrongDeviceAndFailedReadValues()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var root = Path.Combine(Path.GetTempPath(), $"nvml-video-{Guid.NewGuid():N}");
        var adapters = new WindowsGpuAdapter[]
        {
            NvidiaFanFixture.Adapter(9, 0xA1, NvidiaFanFixture.ParentA) with { Name = "NVIDIA A" },
            NvidiaFanFixture.Adapter(3, 0xB1, NvidiaFanFixture.ParentB) with { Name = "NVIDIA B" }
        };
        var inventory = new WindowsGpuAdapterInventoryRead(
            SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 1, adapters);
        var capturedTopology = ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsHardwareMetricTopologySource.CaptureGpuIdentities(inventory);
        Assert.Null(capturedTopology.FailureCode);
        var inventoryZone = new WindowsGpuAdapterOrderMonitoringZone(() => inventory);
        using var owner = new HostManagerMetricSnapshotOwner(
            slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(
                1,
                NativeMetricSnapshotCatalogTopologyStatus.Complete,
                capturedTopology.Rows,
                NativeMetricSnapshotCatalogTopologyStatus.Complete,
                [],
                NativeMetricSnapshotCatalogTopologyStatus.Complete,
                [])),
            new TestEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);

        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
        {
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        }

        var device = dashboard.Current!.FieldBindings.Values.Single(binding =>
            binding.Field.Code == MetricFieldCode.NvmlEncoderUtilization
            && binding.DeviceIdentity == capturedTopology.Rows.Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey).DeviceIdentity;
        var phase = 0;
        var readerCalls = 0;
        var encoderCalls = 0;
        var decoderCalls = 0;
        var zone = new NvidiaNvmlMonitoringZone((request, current, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(inventory, current);
            Assert.True(request.DisplayIndexes!.SetEquals([9]));
            Assert.True(VideoCodes.SetEquals(request.AdditionalMetrics));
            Assert.False(request.IncludeUsage || request.IncludeGraphicsClocks || request.IncludeMemory
                || request.IncludeMemoryClock || request.IncludePowerUsage || request.IncludePowerLimit
                || request.IncludeTemperature || request.IncludeFan || request.IncludeElectricalState);
            Interlocked.Increment(ref readerCalls);

            var currentPhase = Volatile.Read(ref phase);
            if (currentPhase == 3)
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            var readings = NvidiaNvmlReader.ReadAdditionalMetrics(
                (IntPtr)42,
                request.AdditionalMetrics,
                encoderUtilizationQuery: Encoder,
                decoderUtilizationQuery: Decoder);
            var returnedIndex = currentPhase == 2 ? 3 : 9;
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([Gpu(returnedIndex, readings.Values)]);

            int Encoder(IntPtr nativeDevice, out uint percent, out uint period)
            {
                Assert.Equal((IntPtr)42, nativeDevice);
                Interlocked.Increment(ref encoderCalls);
                if (currentPhase == 4)
                {
                    percent = 0;
                    period = 0;
                }
                else if (currentPhase == 1)
                {
                    percent = 48;
                    period = 2_900;
                }
                else if (currentPhase == 2)
                {
                    percent = 99;
                    period = 9_900;
                }
                else
                {
                    percent = 63;
                    period = 2_500;
                }
                return 0;
            }

            int Decoder(IntPtr nativeDevice, out uint percent, out uint period)
            {
                Assert.Equal((IntPtr)42, nativeDevice);
                Interlocked.Increment(ref decoderCalls);
                if (currentPhase == 1)
                {
                    percent = 99;
                    period = 9_999;
                    throw new EntryPointNotFoundException("recorded decoder export is absent");
                }
                if (currentPhase == 2)
                {
                    percent = 98;
                    period = 9_800;
                }
                else if (currentPhase == 4)
                {
                    percent = 0;
                    period = 0;
                }
                else
                {
                    percent = 21;
                    period = 1_750;
                }
                return 0;
            }
        });
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory = new WindowsMemoryMonitoringZone(
            _ => throw new InvalidOperationException("NVML video fields do not read NUMA memory."),
            () => throw new InvalidOperationException("NVML video fields do not read memory."));
        using var sampler = new WindowsHardwareMetricSampler(
            new WindowsCpuMonitoringZone(),
            memory,
            null!,
            inventoryZone,
            null!,
            null!,
            null!,
            zone,
            null!,
            null!,
            null!,
            new AmdIntegratedGpuSensorRoute(),
            null!,
            owner,
            dashboard,
            registry,
            slots.Owner,
            slots.Provider,
            NullLogger<WindowsHardwareMetricSampler>.Instance);

        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, device, VideoNames);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, readerCalls);
        var warm = await sampler.GetSnapshotAsync(request, timeout.Token);
        Assert.All(ReadFields(warm, device), value => Assert.Null(value.NumericValue));
        Assert.Equal(0, readerCalls);

        await using var stream = sampler.SubscribeAsync(
            "nvml-video-recorded",
            request,
            TimeSpan.FromSeconds(1),
            timeout.Token).GetAsyncEnumerator(timeout.Token);

        // An empty whole-source result marks NVML unavailable for the existing
        // five-second cooldown, so exercise valid zero before that terminal phase.
        var phases = new[] { 0, 1, 2, 4, 3 };
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var currentPhase = phases[phaseIndex];
            Volatile.Write(ref phase, currentPhase);
            var next = stream.MoveNextAsync().AsTask();
            var captured = await sampler.CaptureSnapshotAsync(request, timeout.Token);
            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(phaseIndex + 1, readerCalls);

            var expected = currentPhase switch
            {
                0 => new Dictionary<MetricFieldCode, double?>
                {
                    [MetricFieldCode.NvmlEncoderUtilization] = 63,
                    [MetricFieldCode.NvmlEncoderPeriod] = 2_500,
                    [MetricFieldCode.NvmlDecoderUtilization] = 21,
                    [MetricFieldCode.NvmlDecoderPeriod] = 1_750
                },
                1 => new Dictionary<MetricFieldCode, double?>
                {
                    [MetricFieldCode.NvmlEncoderUtilization] = 48,
                    [MetricFieldCode.NvmlEncoderPeriod] = 2_900,
                    [MetricFieldCode.NvmlDecoderUtilization] = null,
                    [MetricFieldCode.NvmlDecoderPeriod] = null
                },
                4 => VideoCodes.ToDictionary(static code => code, static _ => (double?)0),
                _ => VideoCodes.ToDictionary(static code => code, static _ => (double?)null)
            };
            AssertVideoFields(captured, stream.Current, device, expected);
            AssertVideoFields(captured, sampler.ReadLatest(request)!, device, expected);
            AssertVideoFields(captured, await sampler.GetSnapshotAsync(request, timeout.Token), device, expected);
        }

        Assert.Equal(4, encoderCalls);
        Assert.Equal(4, decoderCalls);
    }

    private static void AssertVideoFields(
        HardwareMetricSnapshot captured,
        HardwareMetricSnapshot delivered,
        string device,
        IReadOnlyDictionary<MetricFieldCode, double?> expected)
    {
        var capturedFields = ReadFields(captured, device).ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var deliveredFields = ReadFields(delivered, device).ToDictionary(static field => field.Name, StringComparer.Ordinal);
        Assert.Equal(VideoFields.Length, capturedFields.Count);
        Assert.Equal(VideoFields.Length, deliveredFields.Count);
        foreach (var (code, name, unit) in VideoFields)
        {
            var definition = Definition(code);
            Assert.Equal(name, definition.Name);
            Assert.Equal(unit, definition.Unit);
            var field = capturedFields[name];
            var deliveredField = deliveredFields[name];
            Assert.Equal(unit, field.Unit);
            Assert.Equal("float64", field.ValueKind);
            Assert.Equal(SourceId, field.SourceId);
            Assert.Equal(expected[code], field.NumericValue);
            Assert.Equal(field, deliveredField);
            Assert.Equal(expected[code] is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
        }
    }

    private static DeviceMetricFieldValue[] ReadFields(HardwareMetricSnapshot snapshot, string device)
        => Assert.Single(MetricDeviceFields.Read(snapshot, device, VideoNames.ToHashSet(StringComparer.Ordinal))).Fields.ToArray();

    private static MetricFieldDefinition Definition(MetricFieldCode code)
        => Assert.Single(MetricFieldRegistry.Definitions, definition => definition.Code == code);

    private static GpuMetrics Gpu(int index, IReadOnlyDictionary<MetricFieldCode, double?> readings)
        => new(index, "recorded NVIDIA", 0, 0, 0, 0, 0, 0, 0, 0, null!)
        {
            AdditionalReadings = readings
        };

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
