using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class DefaultReadWriteCollectionBorrowTests
{
    [Theory]
    [InlineData("fan_all_rotational_speed", "fan_all_speed_rpm")]
    [InlineData("fan_all_pwm_reported", "fan_all_reported_pwm_duty")]
    public void HardwareBorrowPreservesAliasAndCanonicalBindingsToTheSameCollectionItem(string alias, string canonical)
    {
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), UnusedResources(_ => { }), new());
        var definition = MetricFieldRegistry.FindByName(canonical)!;
        var binding = new MetricDeviceFieldBinding("recorded-oem-fan", definition, "recorded-fan/address", NativeMetricSnapshotSources.NotebookOemFan);
        var catalog = new HardwareMetricSnapshot(default, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>())
        { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding } };
        typeof(WindowsHardwareMetricOwner).GetField("catalogSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, catalog);
        ImmutableArray<FieldReference> fields = [new(binding.DeviceIdentity, alias), new(binding.DeviceIdentity, canonical), new(binding.DeviceIdentity, alias)];
        using var borrow = owner.BorrowSubscriptionCapture();
        var bound = borrow.Bind(fields);
        Assert.Equal(fields.ToArray(), bound.Select(static item => item.Field).ToArray());
        Assert.All(bound, item => Assert.Equal(binding.RequestAddress, Assert.Single(item.CollectionItemIds)));
        borrow.SynchronizeDemand(fields);
        var rebound = borrow.Bind(fields);
        Assert.Equal(fields.ToArray(), rebound.Select(static item => item.Field).ToArray());
        Assert.All(rebound, item => Assert.Equal(binding.RequestAddress, Assert.Single(item.CollectionItemIds)));
        borrow.SynchronizeDemand([]);
    }

    [Fact]
    public async Task ProcessBorrowIsTheSameBackendCurrentSignalAndOriginalDatasetBinding()
    {
        using var owner = ProcessOwner(new(), new());
        await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_image_path_actual", "process_all_user_sid_actual");
        using var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(owner.Backend, borrow.Backend); Assert.Same(owner.Publications, borrow.Publications);
        Assert.All(borrow.Bind(fields), binding =>
        {
            Assert.Equal(new[] { ProcessMetricDatasetIds.BasicMetrics }, binding.CollectionItemIds);
            Assert.Equal(binding.CollectionItemIds.ToArray(), binding.PublicationKeys.ToArray());
            Assert.Equal(FieldCollectionSourceKind.SourceQuery, binding.SourceKind);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publication = borrow.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        borrow.SynchronizeDemand(fields);
        var result = await borrow.CollectAsync(fields, new Permit(), timeout.Token);
        await publication.WaitAsync(timeout.Token);
        Assert.True(result.Committed);
        Assert.Equal(fields, result.QueriedFields);
        Assert.Equal(result.Readings.ToArray(), fields.Select(owner.Backend.ReadCached).ToArray());
        borrow.Dispose();
        Assert.NotNull(owner.ReadCachedSnapshot());
        await owner.Backend.CollectAsync(fields, timeout.Token);
    }

    [Fact]
    public async Task LastBorrowedNetworkDemandStopsOnlyItsLeaseAndRestartWarmsAgain()
    {
        var broker = new Broker(); using var owner = ProcessOwner(new(), broker);
        await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_network_send_bytes_attributed");
        using var first = owner.BorrowSubscriptionCapture(); using var second = owner.BorrowSubscriptionCapture();
        first.SynchronizeDemand(fields); second.SynchronizeDemand(fields);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var warming = await first.CollectAsync(fields, new Permit(), timeout.Token);
        Assert.True(warming.Committed); Assert.Equal(1, broker.Acquires);
        first.SynchronizeDemand([]); Assert.Equal(0, broker.Releases);
        second.SynchronizeDemand([]); Assert.Equal(1, broker.Releases);
        second.SynchronizeDemand(fields);
        var restarted = await second.CollectAsync(fields, new Permit(), timeout.Token);
        Assert.Equal(2, broker.Acquires);
        Assert.All(restarted.Readings, reading => Assert.Equal(FieldObservationStatus.Unavailable, reading.Status));
        Assert.Null(owner.Current.ReadPart(ProcessMetricDatasetIds.ObservedNetworkTraffic)!.CapturedAt);
        second.Dispose(); Assert.Equal(2, broker.Releases);
        Assert.False(broker.Disposed);
    }

    [Fact]
    public async Task DirectNetworkCaptureRetainsItsIntentWhenBorrowEnds()
    {
        var broker = new Broker(); using var owner = ProcessOwner(new(), broker);
        await owner.CollectAsync(["process_all_network_send_bytes_attributed"]);
        using var borrow = owner.BorrowSubscriptionCapture();
        var fields = ProcessFields(owner, "process_all_network_send_bytes_attributed");
        borrow.SynchronizeDemand(fields); borrow.SynchronizeDemand([]); borrow.Dispose();
        Assert.Equal(1, broker.Acquires); Assert.Equal(0, broker.Releases);
        owner.Dispose(); Assert.Equal(1, broker.Releases); Assert.False(broker.Disposed);
    }

    [Fact]
    public async Task RevokedPermitRejectsBlockedBorrowCommitWithoutAnotherCurrent()
    {
        var api = new Basic.Api(); using var owner = ProcessOwner(api, new());
        await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_image_path_actual");
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.SynchronizeDemand(fields);
        var before = owner.Backend.ReadCached(fields[0]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        owner.Inventory = new Inventory(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return [Process.GetCurrentProcess()]; });
        var permit = new Permit();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Task.Run(async () => await borrow.CollectAsync(fields, permit, timeout.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); api.Image = "changed"; permit.Revoke(); release.Set();
            var result = await pending.WaitAsync(timeout.Token);
            Assert.False(result.Committed);
            Assert.Equal(fields, result.QueriedFields);
            Assert.Equal(before, owner.Backend.ReadCached(fields[0]));
        }
        finally { release.Set(); await pending.WaitAsync(timeout.Token); }
    }

    [Fact]
    public async Task BorrowFailureClearsSameCurrentAndNotifiesButRevokedFailureRetainsIt()
    {
        using var owner = ProcessOwner(new(), new()); await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_image_path_actual");
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.SynchronizeDemand(fields);
        owner.Inventory = new Inventory(() => throw new InvalidOperationException("recorded inventory failure"));
        using var publication = owner.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => borrow.CollectAsync(fields, new Permit(), timeout.Token).AsTask());
        await publication.WaitAsync(timeout.Token);
        Assert.Null(owner.Backend.ReadCached(fields[0]).Value);
        var cleared = owner.Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics); var rejected = new Permit(); rejected.Revoke();
        await Assert.ThrowsAsync<InvalidOperationException>(() => borrow.CollectAsync(fields, rejected, timeout.Token).AsTask());
        Assert.Same(cleared, owner.Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics));
    }

    [Fact]
    public void HardwareBorrowSharesBackendAndWithdrawalDoesNotDisposeOwnerResources()
    {
        var releases = 0; var notebookStops = 0; var resources = UnusedResources(_ => notebookStops++);
        resources.Own(new Release(() => releases++));
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), resources, new());
        var backend = owner.CreateBackend(); using var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(backend, owner.CreateBackend()); Assert.Same(backend, borrow.Backend);
        Assert.Same(owner.Publications, borrow.Publications); Assert.Equal(0, notebookStops);
        borrow.SynchronizeDemand([]); borrow.Dispose(); Assert.Equal(0, releases);
        owner.Dispose(); Assert.Equal(1, releases);
    }

    [Fact]
    public void HardwareBorrowUsesExactSourceAndPreservesOtherNotebookBorrow()
    {
        var notebookStops = 0; using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(),
            UnusedResources(_ => notebookStops++), new());
        var field = MetricFieldRegistry.FindByName("cpu_all_cooling_fan_speed_rpm")!;
        var binding = new MetricDeviceFieldBinding("recorded-cpu", field, "cpu.fanRpm", NativeMetricSnapshotSources.NotebookOemFan);
        var catalog = new HardwareMetricSnapshot(default, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>())
            { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding } };
        typeof(WindowsHardwareMetricOwner).GetField("catalogSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, catalog);
        ImmutableArray<FieldReference> fields = [new(binding.DeviceIdentity, field.Name)];
        using var first = owner.BorrowSubscriptionCapture(); using var second = owner.BorrowSubscriptionCapture();
        Assert.Equal(new[] { "cpu.fanRpm" }, first.Bind(fields).Single().CollectionItemIds);
        first.SynchronizeDemand(fields); second.SynchronizeDemand(fields);
        first.SynchronizeDemand([]); Assert.Equal(0, notebookStops);
        second.SynchronizeDemand([]); Assert.Equal(1, notebookStops);
    }

    [Fact]
    public async Task DirectCommitNotifiesTheBorrowAndCanceledCaptureRetainsCurrent()
    {
        using var owner = ProcessOwner(new(), new()); await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_image_path_actual");
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind(fields); borrow.SynchronizeDemand(fields);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publication = borrow.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        await owner.Backend.CollectAsync(fields, timeout.Token);
        await publication.WaitAsync(timeout.Token);
        var before = owner.Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics);
        using var canceled = new CancellationTokenSource();
        owner.Inventory = new Inventory(() => { canceled.Cancel(); return [Process.GetCurrentProcess()]; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync(fields, new Permit(), canceled.Token).AsTask());
        Assert.Same(before, owner.Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics));
    }

    [Fact]
    public async Task BoundProcessIdentitySurvivesDirectoryRetirementAndPublishesUnavailable()
    {
        using var owner = ProcessOwner(new(), new()); await owner.Backend.DiscoverAsync(default);
        var fields = ProcessFields(owner, "process_all_image_path_actual"); Assert.NotEmpty(fields);
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind(fields); borrow.SynchronizeDemand(fields);
        owner.Inventory = new Inventory(static () => []);
        await owner.CollectAsync(["process_all_image_path_actual"]);
        Assert.Empty(owner.Backend.Catalog.Fields);
        borrow.SynchronizeDemand(fields);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publication = borrow.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        var result = await borrow.CollectAsync(fields, new Permit(), timeout.Token);
        await publication.WaitAsync(timeout.Token);
        Assert.True(result.Committed);
        Assert.Equal(fields.ToArray(), result.Readings.Select(static reading => reading.Field).ToArray());
        Assert.All(result.Readings, reading => { Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); });
        borrow.SynchronizeDemand([]);
    }

    [Fact]
    public void BoundHardwareMetadataSurvivesDirectoryRetirementAndWithdrawsOnlyItsDemand()
    {
        var stops = 0; using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(),
            UnusedResources(_ => stops++), new());
        var field = MetricFieldRegistry.FindByName("cpu_all_cooling_fan_speed_rpm")!;
        var binding = new MetricDeviceFieldBinding("retired-cpu", field, "cpu.fanRpm", NativeMetricSnapshotSources.NotebookOemFan);
        var catalog = new HardwareMetricSnapshot(default, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>())
            { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding } };
        var catalogField = typeof(WindowsHardwareMetricOwner).GetField("catalogSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        catalogField.SetValue(owner, catalog);
        ImmutableArray<FieldReference> fields = [new(binding.DeviceIdentity, field.Name)];
        using var first = owner.BorrowSubscriptionCapture(); using var second = owner.BorrowSubscriptionCapture();
        var original = first.Bind(fields); second.Bind(fields);
        first.SynchronizeDemand(fields); second.SynchronizeDemand(fields);
        catalogField.SetValue(owner, null); Assert.Empty(owner.CreateBackend().Catalog.Fields);
        Assert.Equal(original.Select(static item => item.Field), first.Bind(fields).Select(static item => item.Field));
        Assert.Equal(original.SelectMany(static item => item.CollectionItemIds), first.Bind(fields).SelectMany(static item => item.CollectionItemIds));
        first.SynchronizeDemand(fields); first.Dispose(); Assert.Equal(0, stops);
        second.SynchronizeDemand([]); Assert.Equal(1, stops);
    }

    [Fact]
    public async Task HardwareOriginalCaptureDirectCommitNotifiesAndSourceFailureClearsSameCurrent()
    {
        var failing = false;
        using var owner = MemoryOwner(() => failing ? throw new InvalidOperationException("recorded memory failure")
            : new MemoryMetrics(512, 1024, 50, true, "recorded"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publication = owner.Publications.Subscribe(["memory.usage"]);
        var captured = await owner.CaptureAsync(MetricSampleRequest.ForIds(["memory.usage"]), timeout.Token);
        await publication.WaitAsync(timeout.Token);
        PublishRecordedCatalog(owner);
        var field = owner.CreateBackend().Catalog.Fields.Single(item => item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]); borrow.SynchronizeDemand([field]);
        var reading = borrow.Backend.ReadCached(field);
        Assert.True(reading.Status == FieldObservationStatus.Current && reading.Value?.Number == 512d,
            $"Recorded current status={reading.Status}; memory={captured.Memory.IsUsageAvailable}/{captured.Memory.UsedBytes}; "
            + $"items={string.Join(',', captured.Items.Select(item => $"{item.Key}:{item.Value.NumericValue}"))}; "
            + $"datasets={string.Join(',', captured.Datasets.Select(item => $"{item.Key}:{item.Value.Status}"))}");
        failing = true; await Task.Delay(150, timeout.Token);
        var failedSource = await borrow.CollectAsync([field], new Permit(), timeout.Token);
        await publication.WaitAsync(timeout.Token);
        Assert.True(failedSource.Committed);
        Assert.Equal(new[] { field }, failedSource.QueriedFields);
        Assert.Null(failedSource.Readings.Single().Value);
        Assert.Null(borrow.Backend.ReadCached(field).Value);
        Assert.Same(owner.ReadCachedSnapshot(), owner.ReadCachedSnapshot());
    }

    [Fact]
    public async Task FrozenHardwareSourceCanCommitCurrentWithoutClaimingAQueryRan()
    {
        var frozen = false;
        var memoryReads = 0;
        using var owner = MemoryOwner(() =>
        {
            memoryReads++;
            return new(512, 1024, 50, true, "recorded");
        }, source => source == NativeMetricSnapshotSources.WindowsMemory && frozen
            ? NativeMetricSnapshotZoneMode.Freeze
            : NativeMetricSnapshotZoneMode.Normal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.CaptureAsync(MetricSampleRequest.ForIds(["memory.usage"]), timeout.Token);
        PublishRecordedCatalog(owner);
        var field = owner.CreateBackend().Catalog.Fields.Single(item =>
            item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        using var borrow = owner.BorrowSubscriptionCapture();
        Assert.Equal(FieldCollectionSourceKind.SourceQuery, Assert.Single(borrow.Bind([field])).SourceKind);

        frozen = true;
        var result = await borrow.CollectAsync([field], new Permit(), timeout.Token);

        Assert.True(result.Committed);
        Assert.Empty(result.QueriedFields);
        Assert.Equal(1, memoryReads);
        Assert.Equal(512d, Assert.Single(result.Readings).Value!.Number!.Value);
    }

    [Fact]
    public async Task HardwareRejectedOrCanceledOriginalCapturePreservesManagedCurrent()
    {
        CancellationTokenSource? cancelDuringRead = null;
        using var owner = MemoryOwner(() => { cancelDuringRead?.Cancel(); return new(512, 1024, 50, true, "recorded"); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.CaptureAsync(MetricSampleRequest.ForIds(["memory.usage"]), timeout.Token);
        PublishRecordedCatalog(owner);
        var field = owner.CreateBackend().Catalog.Fields.Single(item => item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]); borrow.SynchronizeDemand([field]);
        var before = owner.ReadCachedSnapshot(); var rejected = new Permit(); rejected.Revoke();
        var result = await borrow.CollectAsync([field], rejected, timeout.Token);
        Assert.False(result.Committed); Assert.Equal(new[] { field }, result.QueriedFields);
        Assert.Empty(result.Readings); Assert.Same(before, owner.ReadCachedSnapshot());
        await Task.Delay(150, timeout.Token);
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(5)); cancelDuringRead = canceled;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync([field], new Permit(), canceled.Token).AsTask());
        Assert.Same(before, owner.ReadCachedSnapshot());
    }

    // Existing friend-level raw boundary recording: the original native workspace/capture/current remain real.
    // No hardware reader, alternate publication or public test API is substituted.
    private static WindowsHardwareMetricOwner MemoryOwner(Func<MemoryMetrics> read,
        Func<string, NativeMetricSnapshotZoneMode>? modeProvider = null)
    {
        var defaults = WindowsMetricConfiguration.CreateDefault();
        var catalog = new NativeMetricSnapshotCatalogConfiguration(1, new string('A', 64), 8, 64, 32,
            [new(NativeMetricSnapshotSources.WindowsMemory, 1, 1, 100, true, 1, 11),
             new(NativeMetricSnapshotSources.WindowsGpuAdapterOrder, 2, 2, 100, true, 1, 12)],
            [MemoryRule("memory.total", "memory.total", NativeMetricSnapshotMetricKind.MemoryTotal, NativeMetricSnapshotMetricFlags.TotalValue),
             MemoryRule("memory.used", "memory.usage", NativeMetricSnapshotMetricKind.MemoryUsed, NativeMetricSnapshotMetricFlags.UsedValue)]);
        Assert.True(catalog.IsPublished, "Recorded catalog must include the original mandatory role-2 inventory source.");
        var configuration = new NativeMetricSnapshotConfiguration
        {
            AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = checked((uint)Unsafe.SizeOf<NativeMetricSnapshotConfiguration>()), Generation = 1,
            MaximumSourceCount = 8, MaximumMetricCount = 32, MaximumRuleCount = 64,
            MaximumRequestedCount = 32, MaximumObservationCount = 64, MaximumGpuAdapterCount = 8,
            MaximumPersistenceSourceCount = 8, MaximumPersistenceRuleCount = 64, MaximumPersistenceGpuCount = 8,
            SourceIndexCapacity = 16, MetricIndexCapacity = 64, RuleIndexCapacity = 128, GpuIndexCapacity = 16,
            MaximumFutureSkewMilliseconds = 30_000, ResidentByteBudget = 4 * 1024 * 1024,
            CatalogContractVersion = NativeMetricSnapshotAbi.CatalogContractVersion, ValueContractVersion = NativeMetricSnapshotAbi.ValueContractVersion,
            ObservationContractVersion = NativeMetricSnapshotAbi.ObservationContractVersion, InventoryContractVersion = NativeMetricSnapshotAbi.InventoryContractVersion,
            PersistenceContractVersion = NativeMetricSnapshotAbi.PersistenceContractVersion,
            MaximumPlanMetricCount = 32, MaximumSourceModeCount = 8, MaximumSourcePlanCount = 8, MaximumMetricPlanCount = 64,
            GpuLuidIndexCapacity = 16, GpuKeyIndexCapacity = 16
        };
        var owner = new WindowsHardwareMetricOwner(new(configuration, catalog, defaults.PdhOptions),
            UnusedResources(_ => { }, read), new(), modeProvider);
        try
        {
            var workspace = new NativeMetricSnapshotWorkspace(configuration, catalog);
            SetOwnerField(owner, "workspace", workspace); // Ownership transfers immediately; owner.Dispose handles failed setup too.
            workspace.ReplaceCatalog(NativeMetricSnapshotCatalogProjection.Create(catalog, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty), DateTimeOffset.UtcNow);
            SetOwnerField(owner, "workspaceIdentity", 1UL); SetOwnerField(owner, "topologyRefreshRequested", false);
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }
    private static NativeMetricSnapshotRuleTemplate MemoryRule(string id, string metric, NativeMetricSnapshotMetricKind kind,
        NativeMetricSnapshotMetricFlags flags) => new(id, metric, NativeMetricSnapshotSources.WindowsMemory, 1, 100, 3, (uint)kind,
        (uint)NativeMetricSnapshotScopeKind.Memory, (uint)NativeMetricSnapshotValueKind.Float64,
        1, (uint)(flags | NativeMetricSnapshotMetricFlags.Nonnegative),
        0, BitConverter.DoubleToUInt64Bits(double.MaxValue), 0, 0, 1, 22);
    private static void PublishRecordedCatalog(WindowsHardwareMetricOwner owner) => SetOwnerField(owner, "catalogSnapshot", owner.ReadCachedSnapshot());
    private static void SetOwnerField(WindowsHardwareMetricOwner owner, string name, object? value)
        => typeof(WindowsHardwareMetricOwner).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);

    private static WindowsProcessFieldOwner ProcessOwner(Basic.Api api, Broker broker)
    {
        var owner = WindowsProcessFieldOwner.CreateDefault(broker);
        owner.Inventory = new Inventory(static () => [Process.GetCurrentProcess()]); owner.BasicReader = new(api);
        return owner;
    }
    private static ImmutableArray<FieldReference> ProcessFields(WindowsProcessFieldOwner owner, params string[] names)
        => [.. owner.Backend.Catalog.Fields.Where(field => names.Contains(field.Field.FieldName)).Select(field => field.Field)];
    private sealed class Inventory(Func<Process[]> read) : IWindowsProcessInventoryReader { public Process[] GetProcesses() => read(); }
    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class Permit : IMetricPublicationPermit
    {
        private readonly object gate = new(); private bool active = true;
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1;
        public bool IsActive { get { lock (gate) return active; } }
        public void Revoke() { lock (gate) active = false; }
        public bool TryPublish(Action commit) { lock (gate) { if (!active) return false; commit(); return true; } }
    }
    private sealed class Broker : IKernelEtwSessionBroker, IDisposable
    {
        internal int Acquires, Releases; internal bool Disposed; private KernelEtwSessionContext context = new(0, DateTimeOffset.UnixEpoch);
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            context = new(++Acquires, DateTimeOffset.UtcNow); var source = new KernelEtwEventSource();
            request.Bind(source, context); source.Activate(); return new Lease(this, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new(Acquires == Releases ? "Idle" : "Running", "recorded", context.Generation, context.StartedAt,
            Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.NetworkTCPIP, Acquires - Releases, 0);
        public void Dispose() => Disposed = true;
        private sealed class Lease(Broker owner, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { source.Cancel(); owner.Releases++; } }
        }
    }
    private static WindowsHardwareMetricResources UnusedResources(Action<bool> notebook, Func<MemoryMetrics>? memory = null) => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded",
        ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = memory ?? (() => throw new InvalidOperationException()),
        ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, selected) => selected.Count == 0 ? new Dictionary<string, ulong>() : throw new InvalidOperationException(),
        ReadNumaIdentities = () => throw new InvalidOperationException(), ReadGpuInventory = () => throw new InvalidOperationException(),
        ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
        ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
        ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
        ReadBatteryIdentities = () => throw new InvalidOperationException(), ReadBattery = (_, _) => throw new InvalidOperationException(),
        ReadNetworkIdentities = () => throw new InvalidOperationException(), ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(),
        ReadSystemIo = _ => throw new InvalidOperationException(), ReadStorageIdentities = _ => throw new InvalidOperationException(),
        ReadFanIdentities = _ => throw new InvalidOperationException(), ReadStorageSensors = () => throw new InvalidOperationException(),
        ReadNvmeSensors = _ => throw new InvalidOperationException(), ReadAtaSensors = _ => throw new InvalidOperationException(),
        ReadHardwareMonitor = () => throw new InvalidOperationException(), ReadNotebookFans = () => throw new InvalidOperationException(),
        SynchronizeNotebookDemand = notebook
    };
}
