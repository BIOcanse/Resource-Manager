using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class NativePdhCollectorTests
{
    [Fact]
    public void DistributedNativeCoreExportsExpectedAbiVersion()
    {
        Assert.Equal(NativeCoreAbi.Version, NativeCoreLibrary.GetPdhCollectorAbiVersion());
    }

    [Fact]
    public void DistributedNativeCollectorCreatesAndCollectsBaseline()
    {
        var api = new NativePdhApi();
        var config = new NativePdhConfig
        {
            AbiVersion = NativeCoreAbi.Version,
            StructSize = 16,
            BaselineResetIntervalMilliseconds = 5_000
        };

        Assert.Equal(NativePdhResultCode.Ok, api.Create(config, out var handle));
        try
        {
            var header = new NativePdhFrameHeader
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = 48
            };
            var result = api.Sample(handle, ref header);

            Assert.Contains(result, new[] { NativePdhResultCode.NoData, NativePdhResultCode.Ok });
            Assert.Equal(NativeCoreAbi.Version, header.AbiVersion);
            Assert.Equal(48U, header.StructSize);
            var topologyGeneration = header.TopologyGeneration;

            header = new NativePdhFrameHeader
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = 48
            };
            var warmResult = api.Sample(handle, ref header);

            Assert.Contains(warmResult, new[] { NativePdhResultCode.NoData, NativePdhResultCode.Ok });
            Assert.Equal(topologyGeneration, header.TopologyGeneration);
            if (warmResult == NativePdhResultCode.Ok)
            {
                var engineRows = new NativePdhGpuEngineRow[header.EngineRowCount];
                var memoryRows = new NativePdhGpuMemoryRow[header.MemoryRowCount];
                var copyResult = api.CopyFrame(handle, header.Sequence, engineRows, memoryRows, out var systemIo);

                Assert.Equal(NativePdhResultCode.Ok, copyResult);
                Assert.Equal(72U, systemIo.StructSize);

                var diagnostics = new NativePdhDiagnostics
                {
                    AbiVersion = NativeCoreAbi.Version,
                    StructSize = 56
                };
                Assert.Equal(NativePdhResultCode.Ok, api.GetDiagnostics(handle, ref diagnostics));
                Assert.InRange(diagnostics.CounterCount, 1U, 9U);
                Assert.Equal(header.TopologyGeneration, diagnostics.TopologyGeneration);
                Assert.True(diagnostics.CollectDurationNanoseconds > 0);
                Assert.True(diagnostics.ReadDurationNanoseconds > 0);
            }
        }
        finally
        {
            api.Destroy(handle);
        }
    }

    [Fact]
    public void DistributedNativeCollectorTreatsLongIdleGapAsANewBaseline()
    {
        const uint baselineCollectedFlag = 1U << 2;
        var api = new NativePdhApi();
        var config = new NativePdhConfig
        {
            AbiVersion = NativeCoreAbi.Version,
            StructSize = 16,
            BaselineResetIntervalMilliseconds = 1_000
        };

        Assert.Equal(NativePdhResultCode.Ok, api.Create(config, out var handle));
        try
        {
            var header = new NativePdhFrameHeader
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = 48
            };
            Assert.Equal(NativePdhResultCode.NoData, api.Sample(handle, ref header));

            Thread.Sleep(1_100);
            header = new NativePdhFrameHeader
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = 48
            };
            var resumed = api.Sample(handle, ref header);

            Assert.Equal(NativePdhResultCode.NoData, resumed);
            Assert.NotEqual(0U, header.Flags & baselineCollectedFlag);
        }
        finally
        {
            api.Destroy(handle);
        }
    }

    [Fact]
    public void AbiLayoutsMatchNativeVersionOne()
    {
        Assert.Equal(16, Marshal.SizeOf<NativePdhConfig>());
        Assert.Equal(48, Marshal.SizeOf<NativePdhFrameHeader>());
        Assert.Equal(24, Marshal.SizeOf<NativePdhGpuEngineRow>());
        Assert.Equal(24, Marshal.SizeOf<NativePdhGpuMemoryRow>());
        Assert.Equal(72, Marshal.SizeOf<NativePdhSystemIo>());
        Assert.Equal(56, Marshal.SizeOf<NativePdhDiagnostics>());
    }

    [Fact]
    public void ReadReturnsLastGoodWhenWarmSampleFails()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok, NativePdhResultCode.PdhError);
        using var collector = new NativePdhCollector(api, TimeSpan.Zero);

        var first = collector.Read(0x11);
        var second = collector.Read(0x11);

        Assert.Equal(NativePdhProviderAvailability.Available, first.Availability);
        Assert.Equal(NativePdhProviderAvailability.LastGood, second.Availability);
        Assert.Equal(NativePdhResultCode.PdhError, second.ResultCode);
        Assert.NotSame(first.Snapshot, second.Snapshot);
        Assert.Same(first.Snapshot!.EngineRows, second.Snapshot!.EngineRows);
        Assert.Same(first.Snapshot.MemoryRows, second.Snapshot.MemoryRows);
        Assert.Equal(first.Snapshot.SystemIo, second.Snapshot.SystemIo);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            second.Snapshot.GpuEngineObservation.Availability);
        Assert.Equal(
            first.Snapshot.GpuEngineObservation.ObservedAt,
            second.Snapshot.GpuEngineObservation.ObservedAt);
        Assert.Equal(
            first.Snapshot.GpuEngineObservation.Generation,
            second.Snapshot.GpuEngineObservation.Generation);
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(2, api.SampleCount);
    }

    [Fact]
    public void ReadExpiresLastGoodAfterTheBoundedGraceWindow()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok, NativePdhResultCode.PdhError);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));
        using var collector = new NativePdhCollector(
            api,
            TimeSpan.Zero,
            time,
            NativePdhCollector.DefaultLastGoodLifetime);

        var first = collector.Read(0x11);
        time.Advance(NativePdhCollector.DefaultLastGoodLifetime + TimeSpan.FromMilliseconds(1));
        var expired = collector.Read(0x11);

        Assert.Equal(NativePdhProviderAvailability.Available, first.Availability);
        Assert.Null(expired.Snapshot);
        Assert.Equal(NativePdhProviderAvailability.Unavailable, expired.Availability);
        Assert.Equal(NativePdhResultCode.PdhError, expired.ResultCode);
    }

    [Fact]
    public void ReadReportsWarmingUpWithoutBlockingForSecondSample()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.NoData);
        using var collector = new NativePdhCollector(api, TimeSpan.Zero);

        var result = collector.Read(0x22);

        Assert.Null(result.Snapshot);
        Assert.Equal(NativePdhProviderAvailability.WarmingUp, result.Availability);
        Assert.Equal(NativePdhResultCode.NoData, result.ResultCode);
        Assert.Equal(1, api.SampleCount);
    }

    [Fact]
    public void AbiMismatchIsExplicitlyUnavailableAndDoesNotCreateCollector()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok)
        {
            AbiVersion = NativeCoreAbi.Version + 1
        };
        using var collector = new NativePdhCollector(api, TimeSpan.Zero);

        var result = collector.Read();

        Assert.Null(result.Snapshot);
        Assert.Equal(NativePdhProviderAvailability.Unavailable, result.Availability);
        Assert.Equal(0, api.CreateCount);
    }

    [Fact]
    public void AdapterTopologyRefreshKeepsOneNativeCollectorHandle()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok, NativePdhResultCode.Ok);
        using var collector = new NativePdhCollector(api, TimeSpan.Zero);

        collector.Read(0x31);
        collector.Read(0x32);

        Assert.Equal(1, api.CreateCount);
        Assert.Equal(2, api.TopologyRefreshCount);
        Assert.Equal(2, api.SampleCount);
    }

    [Fact]
    public void RecreatePublishesReplacementBeforeRetiringPreviousHandle()
    {
        var initialPlan = HostManagerTestPlanFactory.CreatePlan();
        var replacementPlan = CreatePdhPlan(1, 6_000);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CreateRuntimePlan(1, initialPlan));
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok, NativePdhResultCode.Ok);
        using var collector = new NativePdhCollector(
            new HostManagerPdhCollectorRuntime(provider, deployment),
            api,
            TimeSpan.Zero,
            TimeProvider.System,
            NativePdhCollector.DefaultLastGoodLifetime);

        collector.Read(0x41);
        provider.Publish(CreateRuntimePlan(2, replacementPlan));
        collector.Read(0x41);

        Assert.Equal(new uint[] { 5_000, 6_000 }, api.CreatedBaselineResetIntervals);
        Assert.Equal(2, api.CreateCount);
        Assert.Equal(2, api.SampleCount);
        Assert.NotEqual(api.SampleHandles[0], api.SampleHandles[1]);
        Assert.Contains(api.SampleHandles[0], api.DestroyedHandles);
        Assert.DoesNotContain(api.SampleHandles[1], api.DestroyedHandles);
        Assert.Equal(2, api.TopologyRefreshCount);
        Assert.Equal(
            replacementPlan.DeploymentDigests.PdhCollector.RecreateSha256,
            deployment.Snapshot.PdhCollector.AppliedRecreateSha256);
    }

    [Fact]
    public void InitialNoDataContinuesWarmingTheCreatedHandleWithoutRecreate()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CreateRuntimePlan(1, HostManagerTestPlanFactory.CreatePlan()));
        var api = new FakeNativePdhApi(
            NativePdhResultCode.NoData,
            NativePdhResultCode.Ok);
        using var collector = new NativePdhCollector(
            new HostManagerPdhCollectorRuntime(provider, deployment),
            api,
            TimeSpan.Zero,
            TimeProvider.System,
            NativePdhCollector.DefaultLastGoodLifetime);

        var warming = collector.Read();
        var available = collector.Read();

        Assert.Equal(NativePdhProviderAvailability.WarmingUp, warming.Availability);
        Assert.Equal(NativePdhProviderAvailability.Available, available.Availability);
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(api.SampleHandles[0], api.SampleHandles[1]);
    }

    [Fact]
    public void FailedRecreateRetainsOldHandleAndRetriesOnlyAfterNewPublication()
    {
        var initialPlan = HostManagerTestPlanFactory.CreatePlan();
        var replacementPlan = CreatePdhPlan(1, 6_000);
        var retryPlan = CreatePdhPlan(2, 6_000);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CreateRuntimePlan(1, initialPlan));
        var api = new FakeNativePdhApi(
            NativePdhResultCode.Ok,
            NativePdhResultCode.Ok,
            NativePdhResultCode.Ok,
            NativePdhResultCode.Ok);
        api.CreateResults.Enqueue(NativePdhResultCode.Ok);
        api.CreateResults.Enqueue(NativePdhResultCode.PdhError);
        api.CreateResults.Enqueue(NativePdhResultCode.Ok);
        using var collector = new NativePdhCollector(
            new HostManagerPdhCollectorRuntime(provider, deployment),
            api,
            TimeSpan.Zero,
            TimeProvider.System,
            NativePdhCollector.DefaultLastGoodLifetime);

        collector.Read();
        var originalHandle = api.SampleHandles[0];
        provider.Publish(CreateRuntimePlan(2, replacementPlan));
        collector.Read();
        collector.Read();

        Assert.Equal(2, api.CreateCount);
        Assert.All(api.SampleHandles, handle => Assert.Equal(originalHandle, handle));
        Assert.DoesNotContain(originalHandle, api.DestroyedHandles);
        Assert.Equal(
            HostManagerDeploymentHealth.Failed,
            deployment.Snapshot.PdhCollector.Health);

        provider.Publish(CreateRuntimePlan(3, retryPlan));
        collector.Read();

        Assert.Equal(3, api.CreateCount);
        Assert.NotEqual(originalHandle, api.SampleHandles[^1]);
        Assert.Contains(originalHandle, api.DestroyedHandles);
    }

    [Fact]
    public void StaleRecreateSettlementDiscardsReplacementWithoutSwitchingHandles()
    {
        var initialPlan = HostManagerTestPlanFactory.CreatePlan();
        var stalePlan = CreatePdhPlan(1, 6_000);
        var currentPlan = CreatePdhPlan(2, 7_000);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CreateRuntimePlan(1, initialPlan));
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok, NativePdhResultCode.Ok);
        using var collector = new NativePdhCollector(
            new HostManagerPdhCollectorRuntime(provider, deployment),
            api,
            TimeSpan.Zero,
            TimeProvider.System,
            NativePdhCollector.DefaultLastGoodLifetime);

        collector.Read();
        var originalHandle = api.SampleHandles[0];
        api.CreateCallback = createCount =>
        {
            if (createCount == 2)
            {
                provider.Publish(CreateRuntimePlan(3, currentPlan));
            }
        };
        provider.Publish(CreateRuntimePlan(2, stalePlan));
        collector.Read();

        Assert.Equal(2, api.CreateCount);
        Assert.Equal(originalHandle, api.SampleHandles[^1]);
        Assert.DoesNotContain(originalHandle, api.DestroyedHandles);
        Assert.Contains(new IntPtr(2), api.DestroyedHandles);
        Assert.NotEqual(
            stalePlan.DeploymentDigests.PdhCollector.RecreateSha256,
            deployment.Snapshot.PdhCollector.AppliedRecreateSha256);
    }

    [Fact]
    public void ReadersProjectOnePackedFrameByAdapterAndProcess()
    {
        var luid = new AdapterLuid { HighPart = 1, LowPart = 2 };
        var packedLuid = NativePdhAdapterIdentity.Pack(luid);
        var source = new FixedSnapshotSource(new NativePdhSnapshot(
            DateTimeOffset.UtcNow,
            7,
            3,
            [
                new NativePdhGpuEngineRow { AdapterLuid = packedLuid, ProcessId = 42, EngineClass = NativePdhEngineClass.RayTracing, UsagePercent = 20 },
                new NativePdhGpuEngineRow { AdapterLuid = packedLuid, ProcessId = 42, EngineClass = NativePdhEngineClass.Compute, UsagePercent = 15 },
                new NativePdhGpuEngineRow { AdapterLuid = packedLuid, ProcessId = 84, EngineClass = NativePdhEngineClass.Other, UsagePercent = 10 }
            ],
            [
                new NativePdhGpuMemoryRow { AdapterLuid = packedLuid, ProcessId = 42, DedicatedBytes = 4096 }
            ],
            new NativePdhSystemIo
            {
                StructSize = 72,
                ValidMask = NativePdhIoValidMask.DiskActive
                    | NativePdhIoValidMask.DiskRead
                    | NativePdhIoValidMask.DiskWrite
                    | NativePdhIoValidMask.DiskQueue
                    | NativePdhIoValidMask.NetworkReceive
                    | NativePdhIoValidMask.NetworkSend
                    | NativePdhIoValidMask.NetworkUtilization
                    | NativePdhIoValidMask.NetworkBandwidth,
                DiskActivePercent = 25,
                DiskReadBytesPerSecond = 100,
                DiskWriteBytesPerSecond = 200,
                DiskQueueLength = 3,
                NetworkReceiveBytesPerSecond = 400,
                NetworkSendBytesPerSecond = 500,
                NetworkUtilizationPercent = 6,
                NetworkBandwidthBitsPerSecond = 1_000_000
            }));
        WindowsGpuAdapter[] adapters =
        [
            new(0, "GPU", 0x10de, 1, 0, luid, false, 8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
        ];

        var adapterReader = new PdhGpuEngineUsageReader(source);
        var processReader = new PdhProcessGpuReader(source);
        var ioReader = new PdhSystemIoReader(source);

        Assert.Equal(45, adapterReader.ReadUsageByAdapterIndex(adapters)[0]);
        var specialized = adapterReader.ReadSpecializedUsageByAdapterIndex(adapters)[0];
        Assert.Equal(20, specialized.RtUsagePercent);
        Assert.Equal(15, specialized.CudaUsagePercent);
        Assert.Equal(10, specialized.TotalUsagePercent);
        var processGpu = processReader.ReadBreakdownSnapshot(adapters);
        Assert.Equal(SamplingObservationStatus.Current, processGpu.Status);
        Assert.Equal(35, processGpu.GetUsagePercentByProcess(0)[42]);
        Assert.Equal(
            4096,
            processGpu.GetDedicatedMemoryBytesByProcess(0)[42]);

        var io = ioReader.Read(new PdhSystemIoReadRequest(true, true));
        Assert.Equal(25, io.Disk.ActivePercent);
        Assert.Equal(100, io.Disk.ReadBytesPerSecond);
        Assert.Equal(500, io.Network.SendBytesPerSecond);
        Assert.Equal(NativePdhProviderAvailability.Available, io.ProviderAvailability);
    }

    [Fact]
    public void ProviderLastGoodKeepsOriginalObservationTimeAndStatus()
    {
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            24,
            12,
            30,
            0,
            TimeSpan.Zero);
        var luid = new AdapterLuid
        {
            LowPart = 0x1234,
            HighPart = 0
        };
        var snapshot = new NativePdhSnapshot(
            capturedAt,
            5,
            1,
            [
                new NativePdhGpuEngineRow
                {
                    AdapterLuid = NativePdhAdapterIdentity.Pack(luid),
                    ProcessId = 42,
                    EngineClass = NativePdhEngineClass.Other,
                    UsagePercent = 25
                }
            ],
            [],
            new NativePdhSystemIo
            {
                StructSize = 72,
                ValidMask = NativePdhIoValidMask.DiskRead,
                DiskReadBytesPerSecond = 100
            });
        WindowsGpuAdapter[] adapters =
        [
            new(
                0,
                "GPU",
                0x10de,
                1,
                0,
                luid,
                false,
                8UL * 1024 * 1024 * 1024,
                WindowsGpuAdapterKind.Dedicated)
        ];
        var source = new FixedSnapshotSource(
            snapshot,
            NativePdhProviderAvailability.LastGood);

        var gpu = new PdhGpuEngineUsageReader(source)
            .ReadUsageSnapshotByAdapterIndex(adapters);
        var processGpu = new PdhProcessGpuReader(source)
            .ReadBreakdownSnapshot(adapters);
        var io = new PdhSystemIoReader(source).Read(
            new PdhSystemIoReadRequest(true, false));

        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            gpu.ProviderAvailability);
        Assert.Equal(
            SamplingObservationStatus.RetainedLastGood,
            processGpu.Status);
        Assert.Equal(capturedAt, gpu.ObservedAt);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            io.ProviderAvailability);
        Assert.Equal(capturedAt, io.ObservedAt);
    }

    [Fact]
    public void PartialFrameAdvancesOnlyCompleteDomainsAndRetainsOthers()
    {
        var api = new FakeNativePdhApi(
            NativePdhResultCode.Ok,
            NativePdhResultCode.Ok);
        api.Frames.Enqueue(FakePdhFrame.Default);
        api.Frames.Enqueue(new FakePdhFrame
        {
            Flags = NativePdhFrameFlags.HasData
                | NativePdhFrameFlags.Partial
                | NativePdhFrameFlags.DiskComplete,
            EngineRows = [],
            MemoryRows = [],
            SystemIo = new NativePdhSystemIo
            {
                StructSize = 72,
                ValidMask = NativePdhIoValidMask.DiskActive
                    | NativePdhIoValidMask.DiskRead
                    | NativePdhIoValidMask.DiskWrite
                    | NativePdhIoValidMask.DiskQueue,
                DiskActivePercent = 20,
                DiskReadBytesPerSecond = 200,
                DiskWriteBytesPerSecond = 300,
                DiskQueueLength = 4
            }
        });
        using var collector = new NativePdhCollector(
            api,
            TimeSpan.Zero);

        var first = collector.Read();
        var second = collector.Read();

        Assert.Equal(
            NativePdhProviderAvailability.Available,
            second.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.Available,
            second.Snapshot!.DiskObservation.Availability);
        Assert.Equal(2UL, second.Snapshot.DiskObservation.Generation);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            second.Snapshot.NetworkObservation.Availability);
        Assert.Equal(1UL, second.Snapshot.NetworkObservation.Generation);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            second.Snapshot.GpuEngineObservation.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            second.Snapshot.GpuMemoryObservation.Availability);
        Assert.Same(first.Snapshot!.EngineRows, second.Snapshot.EngineRows);
        Assert.Same(first.Snapshot.MemoryRows, second.Snapshot.MemoryRows);

        var source = new FixedSnapshotSource(
            second.Snapshot,
            second.Availability);
        var io = new PdhSystemIoReader(source).Read(
            new PdhSystemIoReadRequest(true, true));
        Assert.Equal(
            NativePdhProviderAvailability.Available,
            io.DiskAvailability);
        Assert.Equal(
            NativePdhProviderAvailability.LastGood,
            io.NetworkAvailability);
        Assert.Equal(200, io.Disk.ReadBytesPerSecond);
        Assert.Equal(8, io.Network.SendBytesPerSecond);
    }

    [Fact]
    public void SuccessfulEmptyGpuArraysReplacePriorRowsWithCurrentEmptyDatasets()
    {
        var api = new FakeNativePdhApi(
            NativePdhResultCode.Ok,
            NativePdhResultCode.Ok);
        api.Frames.Enqueue(FakePdhFrame.Default);
        api.Frames.Enqueue(new FakePdhFrame
        {
            EngineRows = [],
            MemoryRows = []
        });
        using var collector = new NativePdhCollector(
            api,
            TimeSpan.Zero);

        collector.Read();
        var second = collector.Read();

        Assert.Equal(
            NativePdhProviderAvailability.Available,
            second.Snapshot!.GpuEngineObservation.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.Available,
            second.Snapshot.GpuMemoryObservation.Availability);
        Assert.Equal(2UL, second.Snapshot.GpuEngineObservation.Generation);
        Assert.Empty(second.Snapshot.EngineRows);
        Assert.Empty(second.Snapshot.MemoryRows);

        var luid = new AdapterLuid { LowPart = 1 };
        WindowsGpuAdapter[] adapters =
        [
            new(
                0,
                "GPU",
                0x10de,
                1,
                0,
                luid,
                false,
                8UL * 1024 * 1024 * 1024,
                WindowsGpuAdapterKind.Dedicated)
        ];
        var source = new FixedSnapshotSource(
            second.Snapshot,
            second.Availability);
        var usage = new PdhGpuEngineUsageReader(source)
            .ReadUsageSnapshotByAdapterIndex(adapters);
        var process = new PdhProcessGpuReader(source)
            .ReadBreakdownSnapshot(adapters);
        Assert.Equal(
            NativePdhProviderAvailability.Available,
            usage.ProviderAvailability);
        Assert.Empty(usage.UsageByAdapterIndex);
        Assert.Equal(
            SamplingObservationStatus.Current,
            process.UsageStatus);
        Assert.Equal(
            SamplingObservationStatus.Current,
            process.MemoryStatus);
        Assert.Empty(process.GetUsagePercentByProcess(0));
        Assert.Empty(process.GetDedicatedMemoryBytesByProcess(0));
    }

    [Fact]
    public void LegacyPartialFrameWithoutDomainFlagsCannotPublishAnyDomain()
    {
        var api = new FakeNativePdhApi(NativePdhResultCode.Ok);
        api.Frames.Enqueue(FakePdhFrame.Default with
        {
            Flags = NativePdhFrameFlags.HasData
                | NativePdhFrameFlags.Partial
        });
        using var collector = new NativePdhCollector(
            api,
            TimeSpan.Zero);

        var read = collector.Read();

        Assert.Equal(
            NativePdhProviderAvailability.Unavailable,
            read.Availability);
        Assert.NotNull(read.Snapshot);
        Assert.Equal(
            NativePdhProviderAvailability.Unavailable,
            read.Snapshot!.DiskObservation.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.Unavailable,
            read.Snapshot.NetworkObservation.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.Unavailable,
            read.Snapshot.GpuEngineObservation.Availability);
        Assert.Equal(
            NativePdhProviderAvailability.Unavailable,
            read.Snapshot.GpuMemoryObservation.Availability);
        Assert.Empty(read.Snapshot.EngineRows);
        Assert.Empty(read.Snapshot.MemoryRows);
    }

    private sealed class FixedSnapshotSource(
        NativePdhSnapshot snapshot,
        NativePdhProviderAvailability availability =
            NativePdhProviderAvailability.Available)
        : INativePdhSnapshotSource
    {
        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
        {
            return new NativePdhReadResult(
                snapshot,
                availability,
                NativePdhResultCode.Ok,
                0);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = utcNow;
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => currentUtcNow;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan value)
        {
            currentUtcNow += value;
            timestamp += value.Ticks;
        }
    }

    private static CompiledHostManagerPlan CreatePdhPlan(
        int revisionIncrement,
        int baselineResetIntervalMilliseconds)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] =
                root["profile_revision"]!.GetValue<int>() + revisionIncrement;
            root["host_recreate"]!["pdh_collector"]![
                "baseline_reset_interval_ms"] = baselineResetIntervalMilliseconds;
        });

    private static CompiledRuntimePlan CreateRuntimePlan(
        long version,
        CompiledHostManagerPlan hostPlan)
        => CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "pdh-collector-test",
            HostManager = hostPlan
        };

    private sealed class FakeNativePdhApi(params NativePdhResultCode[] sampleResults) : INativePdhApi
    {
        private readonly Queue<NativePdhResultCode> sampleResults = new(sampleResults);
        private FakePdhFrame activeFrame = FakePdhFrame.Default;
        private ulong sequence;
        private int nextHandle;

        public uint AbiVersion { get; init; } = NativeCoreAbi.Version;

        public int CreateCount { get; private set; }

        public int SampleCount { get; private set; }

        public int TopologyRefreshCount { get; private set; }

        public Queue<NativePdhResultCode> CreateResults { get; } = new();

        public Queue<FakePdhFrame> Frames { get; } = new();

        public List<uint> CreatedBaselineResetIntervals { get; } = [];

        public List<IntPtr> DestroyedHandles { get; } = [];

        public List<IntPtr> SampleHandles { get; } = [];

        public Action<int>? CreateCallback { get; set; }

        public uint GetAbiVersion() => AbiVersion;

        public NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle)
        {
            CreateCount++;
            CreatedBaselineResetIntervals.Add(config.BaselineResetIntervalMilliseconds);
            var result = CreateResults.Count > 0
                ? CreateResults.Dequeue()
                : NativePdhResultCode.Ok;
            handle = result == NativePdhResultCode.Ok
                ? new IntPtr(++nextHandle)
                : IntPtr.Zero;
            CreateCallback?.Invoke(CreateCount);
            return result;
        }

        public void Destroy(IntPtr handle)
        {
            DestroyedHandles.Add(handle);
        }

        public NativePdhResultCode RequestTopologyRefresh(IntPtr handle)
        {
            TopologyRefreshCount++;
            return NativePdhResultCode.Ok;
        }

        public NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header)
        {
            SampleCount++;
            SampleHandles.Add(handle);
            var result = sampleResults.Count > 0 ? sampleResults.Dequeue() : NativePdhResultCode.Ok;
            header.AbiVersion = NativeCoreAbi.Version;
            header.StructSize = 48;
            header.NativeStatus = result == NativePdhResultCode.PdhError ? -1 : 0;
            if (result == NativePdhResultCode.Ok)
            {
                activeFrame = Frames.Count > 0
                    ? Frames.Dequeue()
                    : FakePdhFrame.Default;
                header.Sequence = ++sequence;
                header.Flags = (uint)activeFrame.Flags;
                header.EngineRowCount = checked((uint)activeFrame.EngineRows.Length);
                header.MemoryRowCount = checked((uint)activeFrame.MemoryRows.Length);
                header.TopologyGeneration = 1;
            }

            return result;
        }

        public NativePdhResultCode CopyFrame(
            IntPtr handle,
            ulong expectedSequence,
            NativePdhGpuEngineRow[] engineRows,
            NativePdhGpuMemoryRow[] memoryRows,
            out NativePdhSystemIo systemIo)
        {
            activeFrame.EngineRows.CopyTo(engineRows, 0);
            activeFrame.MemoryRows.CopyTo(memoryRows, 0);
            systemIo = activeFrame.SystemIo;
            return NativePdhResultCode.Ok;
        }

        public NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics)
        {
            diagnostics.AbiVersion = NativeCoreAbi.Version;
            diagnostics.StructSize = 56;
            diagnostics.TopologyGeneration = 1;
            diagnostics.CounterCount = 9;
            diagnostics.ValidValueCount = 3;
            diagnostics.CollectDurationNanoseconds = 1;
            diagnostics.ReadDurationNanoseconds = 1;
            diagnostics.FrameDurationNanoseconds = 2;
            return NativePdhResultCode.Ok;
        }
    }

    private sealed record FakePdhFrame
    {
        internal static FakePdhFrame Default { get; } = new();

        internal NativePdhFrameFlags Flags { get; init; } =
            NativePdhFrameFlags.HasData
            | NativePdhFrameFlags.AllDomainsComplete;

        internal NativePdhGpuEngineRow[] EngineRows { get; init; } =
        [
            new()
            {
                AdapterLuid = 1,
                ProcessId = 2,
                UsagePercent = 3
            }
        ];

        internal NativePdhGpuMemoryRow[] MemoryRows { get; init; } =
        [
            new()
            {
                AdapterLuid = 1,
                ProcessId = 2,
                DedicatedBytes = 4
            }
        ];

        internal NativePdhSystemIo SystemIo { get; init; } = new()
        {
            StructSize = 72,
            ValidMask = NativePdhIoValidMask.DiskActive
                | NativePdhIoValidMask.DiskRead
                | NativePdhIoValidMask.DiskWrite
                | NativePdhIoValidMask.DiskQueue
                | NativePdhIoValidMask.NetworkReceive
                | NativePdhIoValidMask.NetworkSend
                | NativePdhIoValidMask.NetworkUtilization
                | NativePdhIoValidMask.NetworkBandwidth,
            DiskActivePercent = 1,
            DiskReadBytesPerSecond = 5,
            DiskWriteBytesPerSecond = 6,
            DiskQueueLength = 1,
            NetworkReceiveBytesPerSecond = 7,
            NetworkSendBytesPerSecond = 8,
            NetworkUtilizationPercent = 9,
            NetworkBandwidthBitsPerSecond = 10
        };
    }
}
