using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class SamplingOutputLedgerProvidersTests
{
    [Fact]
    public Task ProductionWorkersUseTheSharedLedgerForRealPlansAndDisposeClosesAllSixOutputs()
        => ExerciseProductionWorkersAsync(blockFirstTopology: false);

    [Fact]
    public Task BlockedFiniteTopologyPreparationExplainsAnUnplannedMetricRoleAndIsJoinedBeforeRestart()
        => ExerciseProductionWorkersAsync(blockFirstTopology: true);

    private static async Task ExerciseProductionWorkersAsync(bool blockFirstTopology)
    {
        using var topologyEntered = new ManualResetEventSlim(false);
        using var releaseTopology = new ManualResetEventSlim(false);
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var cpuSampler = new NoCaptureCpuSampler();
        var deviceReader = new NoCaptureDeviceTopologyReader();
        var store = new NoWriteTopologyStore();
        var etwBroker = new NoCaptureKernelEtwBroker();
        var catalogTopology = MetricSnapshotOwnerTestFixture.Topology([]);
        var catalogTopologyDiscoveries = 0;
        var catalogTopologySource = new HostManagerMetricSnapshotCatalogTopologySource(() =>
        {
            var discovery = Interlocked.Increment(ref catalogTopologyDiscoveries);
            if (blockFirstTopology && discovery == 1)
            {
                topologyEntered.Set();
                if (!releaseTopology.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The test did not release its finite topology preparation.");
            }
            return catalogTopology;
        });
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Production configures the API pipeline before the app composition, including UIDs 3 and 4.
                LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
                services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
                var declared = services.Select(row => row.ImplementationInstance)
                    .OfType<BusinessBlockRegistration>();
                var nativeUids = NativeControl.GetProfileHostBlockUids(3);
                var bound = BackendNativeControlFactory
                    .WithWrappers(declared, nativeUids).Select(row => row.Id).ToHashSet();
                Assert.True(bound.SetEquals(nativeUids),
                    $"Missing host UIDs: {string.Join(',', nativeUids.Except(bound))}; extra host UIDs: {string.Join(',', bound.Except(nativeUids))}");
                // Keep the production factories and normal host environment while
                // replacing sources with fixtures that reject unexpected sampling.
                services.AddSingleton(sampling.Deployment);
                services.AddSingleton(sampling.Provider);
                services.AddSingleton<ResourceManager.App.Application.RuntimeSpecialization.IRuntimePlanProvider>(sampling.Provider);
                services.AddSingleton(sampling.Owner);
                services.AddSingleton<ICpuTopologySampler>(cpuSampler);
                services.AddSingleton<IDeviceTopologyReader>(deviceReader);
                services.AddSingleton<IDeviceTopologySnapshotStore>(store);
                services.AddSingleton<IKernelEtwSessionBroker>(etwBroker);
                // Hold() prepares the real metric workspace before its first Plan.
                // Isolate inventory discovery here, while keeping the production
                // owner factory, native planner and shared ledger/output storage.
                services.AddSingleton(catalogTopologySource);
                services.AddSingleton<ResourceManager.ReadWrite.Windows.Services.WindowsServiceInformationOwner>();
                services.AddSingleton<ResourceManager.ReadWrite.Windows.SystemInformation.WindowsOperatingSystemInformationOwner>();
                Assert.Single(services.Where(row => row.ServiceType == typeof(ResourceManagerLedgerOwner)));
                foreach (var type in ProviderTypes)
                {
                    var descriptor = Assert.Single(services.Where(row => row.ServiceType == type));
                    Assert.NotNull(descriptor.ImplementationFactory);
                }
            })
            .Build();
        var provider = host.Services;
        Assert.Same(catalogTopologySource, provider.GetRequiredService<HostManagerMetricSnapshotCatalogTopologySource>());
        Assert.Same(sampling.Deployment, provider.GetRequiredService<HostManagerDeploymentState>());
        Assert.Same(sampling.Provider, provider.GetRequiredService<RuntimePlanProvider>());
        Assert.Same(sampling.Provider,
            provider.GetRequiredService<ResourceManager.App.Application.RuntimeSpecialization.IRuntimePlanProvider>());
        var ledger = provider.GetRequiredService<ResourceManagerLedgerOwner>();
        var roleBytes = new Dictionary<int, ulong>();
        foreach (var roleId in new[] { 1, 2, 4, 5, 6, 7 })
        {
            using var role = sampling.Owner.Acquire(roleId);
            roleBytes.Add(roleId, 0); // Empty real output rows remain ordinary storage.
        }
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        var metric = provider.GetRequiredService<WindowsHardwareMetricSampler>();
        Assert.Same(metric, provider.GetRequiredService<IMetricSampler>());
        var writers = provider.GetServices<IControlWriter>().ToArray();
        Assert.Single(writers.OfType<FanControlWriter>());
        Assert.Same(provider.GetRequiredService<IntelCpuControlWriter>(), Assert.Single(writers.OfType<IntelCpuControlWriter>()));
        Assert.Same(provider.GetRequiredService<AmdCpuControlWriter>(), Assert.Single(writers.OfType<AmdCpuControlWriter>()));
        Assert.Same(provider.GetRequiredService<AmdDesktopGpuControlWriter>(), Assert.Single(writers.OfType<AmdDesktopGpuControlWriter>()));
        Assert.Same(provider.GetRequiredService<IntegratedGpuControlWriter>(), Assert.Single(writers.OfType<IntegratedGpuControlWriter>()));
        var device = provider.GetRequiredService<DeviceTopologySnapshotProvider>();
        Assert.Same(provider.GetRequiredService<ResourceManager.App.Infrastructure.Software.WindowsInstalledSoftwareInventory>(), device.SoftwareInventory);
        Assert.Same(provider.GetRequiredService<ResourceManager.ReadWrite.Windows.Services.WindowsServiceInformationOwner>(), device.ServicesInventory);
        Assert.Same(provider.GetRequiredService<ResourceManager.ReadWrite.Windows.SystemInformation.WindowsOperatingSystemInformationOwner>(), device.OperatingSystemInventory);
        _ = device.CreateNvidiaDrsInformation(new ResourceManager.ReadWrite.Vendors.Nvidia.NvidiaDrsInformationRequest(
            [new("Game", new uint[] { 7 })], new(1, 1, 1, 1, 20_000)));
        var gpu = provider.GetRequiredService<GpuTelemetryWorkerSupervisor>();
        var interrupts = provider.GetRequiredService<EtwSystemInterruptSnapshotSource>();
        var residency = provider.GetRequiredService<EtwCpuCoreResidencyReader>();
        var topology = provider.GetRequiredService<CpuTopologySnapshotProvider>();

        await metric.StartAsync(CancellationToken.None);
        await device.StartAsync(CancellationToken.None);
        await gpu.StartAsync(CancellationToken.None);
        await interrupts.StartAsync(CancellationToken.None);
        await residency.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var expectedBytes = roleBytes.Values.Aggregate(0UL, static (total, bytes) => checked(total + bytes));
        var workers = new Dictionary<int, Func<Task?>>
        {
            [1] = metric.CaptureSamplingWorkerTask,
            [2] = device.CaptureSamplingWorkerTask,
            [4] = gpu.CaptureSamplingWorkerTask,
            [5] = () => interrupts.ExecuteTask,
            [6] = () => residency.ExecuteTask,
            [7] = () => topology.ExecuteTask
        };
        if (blockFirstTopology)
        {
            try
            {
                await BusinessBlockTestSupport.Until(() => topologyEntered.IsSet);
                await BusinessBlockTestSupport.Until(() => workers.Keys.Where(role => role != 1)
                    .All(role => ReadPlanSnapshot(sampling, role).LastPlanEpoch != 0));
                Assert.Equal(0UL, ReadPlanSnapshot(sampling, 1).LastPlanEpoch);
                Assert.Equal(1, Volatile.Read(ref catalogTopologyDiscoveries));
                var stopping = metric.StopAsync(CancellationToken.None);
                Assert.False(stopping.IsCompleted); // Finite preparation owns the entry until it returns.
                releaseTopology.Set();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                await metric.StartAsync(CancellationToken.None);
            }
            finally { releaseTopology.Set(); }
        }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var observations = new Dictionary<int, NativeSamplingSubscriptionSnapshotHeader>();
            try
            {
                while (true)
                {
                    // Observe every role, rather than short-circuiting on the first
                    // unplanned role and losing the phase that actually stalled.
                    foreach (var role in workers.Keys)
                    {
                        observations[role] = ReadPlanSnapshot(sampling, role);
                        if (workers[role]() is { IsFaulted: true } failed) await failed;
                    }
                    if (observations.Values.All(observation => observation.LastPlanEpoch != 0)) break;
                    await Task.Delay(10, deadline.Token);
                }
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
            {
                ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
                var roles = string.Join("; ", observations.Select(pair =>
                    $"role={pair.Key} task={workers[pair.Key]()?.Status.ToString() ?? "absent"} "
                    + $"generation={pair.Value.ConfigurationGeneration} revision={pair.Value.StateRevision} "
                    + $"operation={pair.Value.LastOperationEpoch} plan={pair.Value.LastPlanEpoch} "
                    + $"sources={pair.Value.ActiveSourceCount} items={pair.Value.KnownItemCount}"));
                throw new TimeoutException($"Six production workers did not all plan within 5 seconds. {roles}. "
                    + $"catalog topology discoveries={Volatile.Read(ref catalogTopologyDiscoveries)}; "
                    + $"kernel leases={etwBroker.SubscribeCount}/{etwBroker.ReleaseCount}; "
                    + $"captures cpu/device={cpuSampler.CaptureCount}/{deviceReader.ReadCount}; "
                    + $"threadpool threads/pending/availableWorkers/availableIo="
                    + $"{ThreadPool.ThreadCount}/{ThreadPool.PendingWorkItemCount}/{availableWorkers}/{availableIo}.", error);
            }
        }

        await metric.StopAsync(CancellationToken.None);
        await device.StopAsync(CancellationToken.None);
        await gpu.StopAsync(CancellationToken.None);
        await interrupts.StopAsync(CancellationToken.None);
        await residency.StopAsync(CancellationToken.None);
        await topology.StopAsync(CancellationToken.None);
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(blockFirstTopology ? 2 : 1, Volatile.Read(ref catalogTopologyDiscoveries));
        Assert.Equal(0, cpuSampler.CaptureCount);
        Assert.Equal(0, deviceReader.ReadCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(2, etwBroker.SubscribeCount); // Interrupts and CPU residency each hold their resource on entry.
        Assert.Equal(etwBroker.SubscribeCount, etwBroker.ReleaseCount);

        metric.Dispose();
        expectedBytes -= roleBytes[1];
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        device.Dispose();
        expectedBytes -= roleBytes[2];
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        device.Dispose();
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => device.AcquireInformationSubscription(
            "after-dispose", TimeSpan.FromSeconds(1)));
        gpu.Dispose();
        expectedBytes -= roleBytes[4];
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        interrupts.Dispose();
        expectedBytes -= roleBytes[5];
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        residency.Dispose();
        expectedBytes -= roleBytes[6];
        Assert.Equal(expectedBytes, ledger.ReadTable().RegisteredBytes);
        topology.Dispose();
        expectedBytes -= roleBytes[7];
        Assert.Equal(0UL, expectedBytes);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task GpuDisposeDrainsItsCaptureAndRetriesTheSameFailedOutputClose()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var ledger = new ResourceManagerLedgerOwner();
        using var captureEntered = new ManualResetEventSlim(false);
        using var captureExited = new ManualResetEventSlim(false);
        var stores = new List<SamplingOutputScratchTests.RecordedScratch>();
        var capture = new Func<GpuTelemetryWorkerRequest, CancellationToken, Task<GpuTelemetryWorkerSnapshot>>(
            async (_, cancellationToken) =>
            {
                captureEntered.Set();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The recorded capture only exits through cancellation.");
                }
                finally
                {
                    captureExited.Set();
                }
            });
        var factory = SamplingOutputScratchTests.Factory(ledger, stores, store =>
        {
            store.FailCloseOnce = true;
            store.Closing = () => Assert.True(captureExited.IsSet,
                "The output resource cannot close while the capture worker is still running.");
        });
        using var source = new GpuTelemetryWorkerSupervisor(sampling.Owner,
            NullLogger<GpuTelemetryWorkerSupervisor>.Instance, capture, factory);
        using var enumerationCancellation = new CancellationTokenSource();
        await source.StartAsync(CancellationToken.None);
        await using var subscription = source.SubscribeAsync("drain-and-close",
            GpuTelemetryWorkerRequest.Create(["gpu.0.compute"]), TimeSpan.FromMilliseconds(100),
            enumerationCancellation.Token).GetAsyncEnumerator();
        var move = subscription.MoveNextAsync().AsTask();
        try
        {
            Assert.True(captureEntered.Wait(TimeSpan.FromSeconds(5)));
            using var role = sampling.Owner.Acquire(4);
            var layout = SamplingOutputScratchTests.RealLayout(1, 1, 1);
            var store = Assert.Single(stores);
            Assert.Equal(layout.StorageLength, store.Length);
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);

            Assert.Throws<IOException>(() => source.Dispose());
            Assert.True(captureExited.IsSet);
            Assert.False(store.Closed);
            Assert.Equal(1, store.CloseCalls);
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);

            enumerationCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await move);
            source.Dispose();
            Assert.True(store.Closed);
            Assert.Equal(2, store.CloseCalls);
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            source.Dispose();
            Assert.Equal(2, store.CloseCalls);
        }
        finally
        {
            enumerationCancellation.Cancel();
            // A failing assertion must still join MoveNext before disposing the async iterator.
            try { await move; }
            catch (OperationCanceledException) when (enumerationCancellation.IsCancellationRequested) { }
        }
    }

    private static NativeSamplingSubscriptionSnapshotHeader ReadPlanSnapshot(HostManagerSamplingSubscriptionTestFixture sampling, int roleId)
    {
        using var role = sampling.Owner.Acquire(roleId);
        var cap = role.Session.Capacity;
        var header = new NativeSamplingSubscriptionSnapshotHeader
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()
        };
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, role.Session.Snapshot(ref header,
            new NativeSamplingSubscriptionSourceView[checked((int)cap.SourceCapacity)],
            new NativeSamplingSubscriptionItemState[checked((int)cap.ItemCapacity)]));
        return header;
    }

    private static readonly Type[] ProviderTypes =
    [
        typeof(WindowsHardwareMetricSampler),
        typeof(DeviceTopologySnapshotProvider),
        typeof(GpuTelemetryWorkerSupervisor),
        typeof(EtwSystemInterruptSnapshotSource),
        typeof(EtwCpuCoreResidencyReader),
        typeof(CpuTopologySnapshotProvider)
    ];

    private sealed class NoCaptureCpuSampler : ICpuTopologySampler
    {
        private int captureCount;
        internal int CaptureCount => Volatile.Read(ref captureCount);
        public ResourceManager.App.Domain.CpuTopology.CpuTopologySnapshot CaptureSnapshot()
        {
            Interlocked.Increment(ref captureCount);
            throw new InvalidOperationException("No CPU topology capture is expected without demand.");
        }
        public ResourceManager.App.Domain.CpuTopology.CpuTopologySnapshot CaptureTopology()
            => throw new InvalidOperationException("No CPU topology capture is expected without demand.");
        public void ResetUsageBaseline() { }
    }

    private sealed class NoCaptureDeviceTopologyReader : IDeviceTopologyReader
    {
        internal int ReadCount { get; private set; }
        public ResourceManager.App.Domain.DeviceTopology.DeviceTopologySnapshot ReadSnapshot()
        {
            ReadCount++;
            throw new InvalidOperationException("No device topology read is expected without demand.");
        }
    }

    private sealed class NoWriteTopologyStore : IDeviceTopologySnapshotStore
    {
        internal int SaveCount { get; private set; }
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken cancellationToken)
        {
            SaveCount++;
            throw new InvalidOperationException("No topology persistence is expected without demand.");
        }
    }

    private sealed class NoCaptureKernelEtwBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        private int releaseCount;
        internal int SubscribeCount => Volatile.Read(ref subscribeCount);
        internal int ReleaseCount => Volatile.Read(ref releaseCount);
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            return new Lease(this);
        }
        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Unavailable", "Recorded idle resource; no native event stream is started.", 0,
                DateTimeOffset.MinValue, 0, SubscribeCount - ReleaseCount, 0);
        private sealed class Lease(NoCaptureKernelEtwBroker owner) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.releaseCount); }
        }
    }
}

internal sealed class SamplingOutputCapacityBatch : IDisposable
{
    private readonly List<IDisposable> resources = [];
    private readonly List<IDisposable> liveUses = [];

    internal static ulong FillRemainingRoleOutputs(ResourceManagerLedgerOwner owner,
        SamplingOutputCapacityBatch batch)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        ulong total = 0;
        foreach (var roleId in new[] { 1, 2, 4, 5, 6, 7 })
        {
            var role = Assert.Single(plan.SamplingSubscription.Recreate.Roles,
                row => row.RoleId == roleId);
            // A representative real working set, independent of configured admission maxima.
            var length = SamplingOutputScratchTests.RealLayout(64, 2, 128).StorageLength;
            for (var slot = 0; slot < 2; slot++)
            {
                var resource = owner.CreateScratch(length);
                batch.resources.Add(resource);
                var lease = resource.Acquire();
                lease.Complete();
                batch.liveUses.Add(lease);
                total = checked(total + length);
            }
        }
        return total;
    }

    public void Dispose()
    {
        for (var index = liveUses.Count - 1; index >= 0; index--) liveUses[index].Dispose();
        for (var index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
        liveUses.Clear();
        resources.Clear();
    }
}
