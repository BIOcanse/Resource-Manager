using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class NpuMemoryCaptureIntegrationTests
{
    [Fact]
    public async Task InvalidatedIdentityRejectsTheWholeRealNativeFrameAndTheNextPlanCanComplete()
    {
        var recording = new NpuMemoryIntegrationRecording();
        using var fixture = new NativeFixture(recording);
        var request = fixture.Request(MetricFieldCode.NpuSharedMemoryResident, MetricFieldCode.MemoryUsed);
        var first = Assert.IsType<HardwareMetricSnapshot>(await fixture.Capture(request));
        Assert.Equal("0", Npu(first, NpuMemoryIntegrationRecording.SharedResident).IntegerValue);
        var before = fixture.Workspace.ReadCommitted();
        var callbacks = fixture.Published;
        var admissions = fixture.PermitInvocations;
        recording.Invalidated = true;
        recording.SharedResidentBytes = ulong.MaxValue;
        recording.MemoryUsed = 128;
        Assert.Null(await fixture.Capture(request));
        Assert.Equal(1, fixture.Refreshes);
        Assert.Equal(callbacks, fixture.Published);
        Assert.Equal(admissions, fixture.PermitInvocations);
        var rejected = fixture.Workspace.ReadCommitted();
        Assert.Equal(before.Header.CommittedGeneration, rejected.Header.CommittedGeneration);
        Assert.Equal(before.Metrics.ToArray(), rejected.Metrics.ToArray());
        Assert.Equal("0", Npu(fixture.Current!, NpuMemoryIntegrationRecording.SharedResident).IntegerValue);
        // The refresh retains the same precise physical target. Do not reset/recreate
        // the workspace: a leaked completion_open must make this next real Plan fail.
        recording.Invalidated = false;
        fixture.RefreshCatalog();
        var recovered = Assert.IsType<HardwareMetricSnapshot>(await fixture.Capture(request));
        Assert.Equal("18446744073709551615", Npu(recovered, NpuMemoryIntegrationRecording.SharedResident).IntegerValue);
        Assert.Equal(128UL, recovered.Memory.UsedBytes);
        Assert.True(recovered.CommittedGeneration > first.CommittedGeneration);
        Assert.Equal(callbacks + 1, fixture.Published);
        Assert.Equal(3, recording.Reads);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(18446744073709551615UL)]
    public async Task DedicatedFailureKeepsSharedUnsignedBytesAndOtherSourceInTheSameCommit(ulong shared)
    {
        var recording = new NpuMemoryIntegrationRecording { DedicatedCommitted = null, SharedResidentBytes = shared };
        using var fixture = new NativeFixture(recording);
        var snapshot = Assert.IsType<HardwareMetricSnapshot>(await fixture.Capture(fixture.Request(
            MetricFieldCode.NpuDedicatedMemoryCommitted, MetricFieldCode.NpuSharedMemoryResident, MetricFieldCode.MemoryUsed)));
        var dedicated = Npu(snapshot, NpuMemoryIntegrationRecording.DedicatedCommittedName);
        Assert.Equal(SamplingObservationStatus.Unavailable, dedicated.Status);
        Assert.Null(dedicated.IntegerValue);
        var actual = Npu(snapshot, NpuMemoryIntegrationRecording.SharedResident);
        Assert.Equal(shared.ToString(System.Globalization.CultureInfo.InvariantCulture), actual.IntegerValue);
        Assert.Null(actual.NumericValue);
        Assert.Equal(SamplingObservationStatus.Current, actual.Status);
        Assert.Equal(64UL, snapshot.Memory.UsedBytes);
        Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets["memory.usage"].Status);
        Assert.Equal(1, fixture.Published);
        Assert.Equal(1, recording.MemoryReads);
        Assert.Equal(NpuMemoryFields.DedicatedCommitted | NpuMemoryFields.SharedResident,
            Assert.Single(recording.Requests).Value);
    }

    [Fact]
    public async Task CancellationAndRevokedPublicationCannotEscapeThroughTheNextNativeCommit()
    {
        var recording = new NpuMemoryIntegrationRecording();
        using var fixture = new NativeFixture(recording);
        var request = fixture.Request(MetricFieldCode.NpuSharedMemoryResident);
        var first = Assert.IsType<HardwareMetricSnapshot>(await fixture.Capture(request));
        var generation = fixture.Workspace.ReadCommitted().Header.CommittedGeneration;
        recording.SharedResidentBytes = 17;
        using var canceled = new CancellationTokenSource();
        recording.AfterRead = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Capture(request, canceled.Token));
        Assert.Same(first, fixture.Current);
        Assert.Equal(generation, fixture.Workspace.ReadCommitted().Header.CommittedGeneration);
        recording.AfterRead = null;
        recording.SharedResidentBytes = 23;
        Assert.Null(await fixture.Capture(request, allowPublish: false));
        Assert.Same(first, fixture.Current);
        Assert.Equal(generation, fixture.Workspace.ReadCommitted().Header.CommittedGeneration);
        recording.SharedResidentBytes = 31;
        var recovered = Assert.IsType<HardwareMetricSnapshot>(await fixture.Capture(request));
        Assert.Equal("31", Npu(recovered, NpuMemoryIntegrationRecording.SharedResident).IntegerValue);
        Assert.Equal(2, fixture.Published);
    }

    [Fact]
    public async Task DisposingTheOriginalBorrowDuringAReadRejectsItsLateValueAndDirectCaptureRecovers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recording = new NpuMemoryIntegrationRecording();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Resources(), new());
        var backend = owner.CreateBackend();
        var field = (await backend.DiscoverAsync(default)).Fields.Single(row =>
            row.Field.DeviceIdentity == NpuMemoryIntegrationRecording.Identity &&
            row.Field.FieldName == NpuMemoryIntegrationRecording.SharedResident).Field;
        Assert.Equal("0", Assert.Single(await backend.CollectAsync([field], default)).Value!.Integer);
        var current = owner.ReadCachedSnapshot();
        using var borrow = owner.BorrowSubscriptionCapture();
        borrow.Bind([field]);
        recording.SharedResidentBytes = 99;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        recording.AfterRead = () => { entered.TrySetResult(); resume.Wait(timeout.Token); };
        var capture = Task.Run(async () => await borrow.CollectAsync([field], new NvidiaFanFixture.Permit(), timeout.Token));
        Task? retirement = null;
        FieldCollectionResult refused;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            retirement = Task.Run(borrow.Dispose);
            // Dispose retires the borrow before waiting for the active sample to finish.
            // Bind checks that same retirement flag without waiting for SampleGate.
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { borrow.Bind([field]); }
                catch (ObjectDisposedException) { break; }
                await Task.Delay(10, timeout.Token);
            }
            resume.Set();
            refused = await capture.WaitAsync(timeout.Token);
            await retirement.WaitAsync(timeout.Token);
        }
        finally
        {
            resume.Set();
            await capture.WaitAsync(timeout.Token);
            if (retirement is not null) await retirement.WaitAsync(timeout.Token);
        }
        Assert.False(refused.Committed);
        Assert.Same(current, owner.ReadCachedSnapshot());
        Assert.Equal("0", backend.ReadCached(field).Value!.Integer);
        recording.AfterRead = null;
        recording.SharedResidentBytes = ulong.MaxValue;
        Assert.Equal("18446744073709551615", Assert.Single(await backend.CollectAsync([field], default)).Value!.Integer);
    }

    internal static DeviceMetricFieldValue Npu(HardwareMetricSnapshot snapshot, string name)
        => Assert.Single(MetricDeviceFields.Read(snapshot, NpuMemoryIntegrationRecording.Identity)).Fields.Single(field => field.Name == name);

    private sealed class NativeFixture : IDisposable
    {
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        private readonly WindowsHardwareMetricCapture capture;
        private readonly NpuMemoryIntegrationRecording recording;
        private readonly WindowsHardwareMetricResources resources;
        private bool catalogInitialized;
        internal NativeMetricSnapshotCatalogTopologySnapshot Topology { get; private set; }
        internal HardwareMetricSnapshot? Current;
        internal int Refreshes, Published, PermitInvocations;
        internal NativeFixture(NpuMemoryIntegrationRecording recording)
        {
            this.recording = recording;
            resources = recording.Resources();
            var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
                Path.Combine(AppContext.BaseDirectory, "npu-memory-native", Guid.NewGuid().ToString("N"), "current.bin"));
            Topology = recording.Topology();
            capture = new(resources, _ => NativeMetricSnapshotZoneMode.Normal, TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => Refreshes++);
            RefreshCatalog();
        }
        internal void RefreshCatalog()
        {
            Topology = recording.Topology();
            var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                HostManagerTestPlanFactory.CreatePlan().MetricSnapshot, [], [], [], Workspace.CreateCatalogHandleMap(),
                npuMemoryIdentities: Topology.NpuMemoryIdentities);
            if (catalogInitialized)
            {
                Assert.True(WindowsMetricCatalogTopologyPublication.CanRetainCatalog(Workspace.ReadCatalog(), projection));
                return;
            }
            Workspace.ReplaceCatalog(projection, DateTimeOffset.UtcNow);
            catalogInitialized = true;
        }
        internal MetricSampleRequest Request(params MetricFieldCode[] codes)
            => MetricSampleRequest.ForIds(Workspace.ReadCatalog().MetricBindings.Values.Where(binding => codes.Contains(binding.FieldCode))
                .Select(static binding => binding.MetricId));
        internal async Task<HardwareMetricSnapshot?> Capture(MetricSampleRequest request, CancellationToken token = default, bool allowPublish = true)
        {
            await capture.SampleGate.WaitAsync(token);
            try
            {
                return await capture.CaptureUnderGateAsync(request, Workspace, 1, Topology,
                    commit => { PermitInvocations++; if (!allowPublish) return false; commit(); return true; }, new(""), token,
                    onCommitted: value => { Current = value; Published++; });
            }
            finally { capture.SampleGate.Release(); }
        }
        public void Dispose() { Workspace.Dispose(); capture.SampleGate.Dispose(); resources.Dispose(); }
    }
}

internal sealed class NpuMemoryIntegrationRecording
{
    internal const string Pnp = @"ACPI\RECORDED_NPU\ONE";
    internal const string Identity = "windows-npu|" + Pnp;
    internal const string DedicatedCommittedName = "npu_all_dedicated_memory_committed";
    internal const string SharedResident = "npu_all_shared_memory_resident";
    internal ulong? DedicatedCommitted = 7;
    internal ulong? SharedResidentBytes = 0;
    internal ulong MemoryUsed = 64;
    internal bool Invalidated, EmptyDirectory;
    internal Action? AfterRead;
    private int reads, memoryReads;
    internal int Reads => Volatile.Read(ref reads);
    internal int MemoryReads => Volatile.Read(ref memoryReads);
    internal IReadOnlyDictionary<string, NpuMemoryFields> Requests = new Dictionary<string, NpuMemoryFields>();
    internal NativeMetricSnapshotCatalogTopologySnapshot Topology() => new(1,
        NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
        NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
        { NpuMemoryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, NpuMemoryIdentities = EmptyDirectory ? [] : [new(Pnp)] };
    internal IReadOnlyList<NpuMemoryRead> Read(IReadOnlyList<NpuMemoryCatalogIdentity> identities,
        IReadOnlyDictionary<string, NpuMemoryFields> fields, uint maximum, CancellationToken token)
    {
        Assert.Equal(Pnp, Assert.Single(identities).PnpInstanceId);
        Assert.Equal(Identity, Assert.Single(fields).Key);
        Assert.Equal(64U, maximum);
        Requests = fields;
        Interlocked.Increment(ref reads);
        AfterRead?.Invoke();
        token.ThrowIfCancellationRequested();
        var flags = fields[Identity];
        return [new(Identity, flags.HasFlag(NpuMemoryFields.DedicatedCommitted) ? DedicatedCommitted : null, null, null,
            flags.HasFlag(NpuMemoryFields.SharedResident) ? SharedResidentBytes : null, Invalidated)];
    }

    internal WindowsHardwareMetricResources Resources() => new()
    {
        Controls = new() { ControlReadCpuTopology = () => null! }, CpuName = "recorded", MemoryDescription = "recorded",
        ReadCpuCounters = () => throw new InvalidOperationException("unselected CPU"),
        ReadMemory = () => { Interlocked.Increment(ref memoryReads); return new(MemoryUsed, 256, MemoryUsed * 100d / 256, true, "recorded"); },
        ReadVirtualMemory = () => throw new InvalidOperationException("unselected virtual memory"),
        ReadNumaAvailable = (nodes, selected) => { Assert.Empty(nodes); Assert.Empty(selected); return new Dictionary<string, ulong>(); },
        ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
        ReadCpuFrequency = () => throw new InvalidOperationException("unselected frequency"),
        ReadGpuUsage = _ => throw new InvalidOperationException("unselected GPU usage"),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException("unselected NVML"),
        ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException("unselected NVAPI"),
        ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException("unselected ADLX"),
        ReadAmdSmu = _ => throw new InvalidOperationException("unselected SMU"),
        ReadIntelGpu = (_, _, _) => throw new InvalidOperationException("unselected IGCL"),
        ReadIntelMsr = (_, _) => throw new InvalidOperationException("unselected MSR"),
        ReadNpuMemoryIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [new(Pnp)]), ReadNpuMemory = Read,
        ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBattery = (_, _) => throw new InvalidOperationException("unselected battery"),
        ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException("unselected network"),
        ReadSystemIo = _ => throw new InvalidOperationException("unselected IO"),
        ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadStorageSensors = () => throw new InvalidOperationException("unselected storage"),
        ReadNvmeSensors = _ => throw new InvalidOperationException("unselected NVMe"),
        ReadAtaSensors = _ => throw new InvalidOperationException("unselected ATA"),
        ReadHardwareMonitor = () => throw new InvalidOperationException("unselected WMI"),
        ReadNotebookFans = () => throw new InvalidOperationException("unselected OEM"), SynchronizeNotebookDemand = _ => { }
    };
}
