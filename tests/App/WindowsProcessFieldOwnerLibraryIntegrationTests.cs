using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsProcessFieldOwnerLibraryIntegrationTests
{
    [Fact]
    public async Task DefaultOwnerCatalogUsesTheSdkCaptureAndCurrentWithoutAcquiringEtw()
    {
        var broker = new Broker(); var api = new Basic.Api();
        using var owner = Owner(api, broker);
        Assert.Null(owner.ReadCachedSnapshot()); Assert.Equal(0, api.Opens);
        Assert.Equal(typeof(ProcessMetricFieldRegistry).Assembly, typeof(WindowsProcessFieldOwner).Assembly);
        Assert.Equal(typeof(WindowsProcessFieldOwner).Assembly, typeof(ProcessFieldCurrentState).Assembly);
        var catalog = await owner.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Single(catalog.Devices); Assert.Equal(30, catalog.Fields.Length);
        Assert.All(catalog.Fields, field => Assert.False(field.Write.Supported));
        var opens = api.Opens;
        var cpu = catalog.Fields.Single(field => field.Definition.Name == "process_all_cpu_usage_actual").Field;
        Assert.Null(owner.Backend.ReadCached(cpu).Value);
        foreach (var field in catalog.Fields) owner.Backend.ReadCached(field.Field);
        Assert.Equal(opens, api.Opens); Assert.Equal(0, broker.Acquires);
    }

    [Fact]
    public async Task SelectedImageOnlyReadsTheOriginalCategoryAndDoesNotAdvanceCpuBaseline()
    {
        var broker = new Broker(); var api = new Basic.Api();
        using var owner = Owner(api, broker);
        var first = await owner.CollectAsync(["process_all_image_path_actual"]);
        Assert.Equal(api.Image, first.Processes.Single().Values.Single().StringValue);
        Assert.Equal(0, api.MemoryCalls); Assert.Equal(0, api.IoCalls); Assert.Equal(0, api.SidCalls); Assert.Equal(0, api.ParentCalls);
        Assert.Null(first.Sources.Single().WindowMilliseconds); Assert.Equal(0, broker.Acquires);
        var cpu = await owner.CollectAsync(["process_all_cpu_usage_actual"]);
        Assert.Null(cpu.Processes.Single().Values.Single().NumericValue);
    }

    [Fact]
    public async Task RecordedCpuZeroAndPublicationUseTheSameCurrentWithSeparateSourceWindows()
    {
        var api = new Basic.Api(); using var owner = Owner(api, new());
        var now = DateTimeOffset.UnixEpoch; owner.CaptureClock = () => now;
        using var signal = owner.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await owner.CollectAsync(["process_all_cpu_usage_actual", "process_all_image_path_actual"], timeout.Token);
        await signal.WaitAsync(timeout.Token);
        Assert.Null(first.Processes.Single().Values.Single(value => value.Field == "process_all_cpu_usage_actual").NumericValue);
        now += TimeSpan.FromSeconds(1);
        var second = await owner.CollectAsync(["process_all_cpu_usage_actual", "process_all_image_path_actual"], timeout.Token);
        await signal.WaitAsync(timeout.Token);
        Assert.Equal(0d, second.Processes.Single().Values.Single(value => value.Field == "process_all_cpu_usage_actual").NumericValue);
        Assert.Null(second.Sources.Single(source => source.SourceId == "windows.process.query").WindowMilliseconds);
        Assert.Equal(1000, second.Sources.Single(source => source.SourceId == "windows.process.cpu-interval").WindowMilliseconds);
        Assert.Equal(second.Processes.Single().Values, owner.ReadCachedSnapshot()!.Processes.Single().Values);
        Assert.NotSame(owner.DirectCpu, owner.HostedCpu);
    }

    [Fact]
    public async Task FailedInventoryClearsOriginalTextValuesWhileKeepingConfirmedDirectoryIdentity()
    {
        var api = new Basic.Api(); using var owner = Owner(api, new());
        var catalog = await owner.Backend.DiscoverAsync(CancellationToken.None);
        var field = catalog.Fields.Single(value => value.Definition.Name == "process_all_image_path_actual").Field;
        Assert.NotNull(owner.Backend.ReadCached(field).Value!.Text);
        owner.Inventory = new Inventory(() => throw new InvalidOperationException("recorded inventory failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Backend.CollectAsync([field], CancellationToken.None).AsTask());
        Assert.Null(owner.Backend.ReadCached(field).Value);
        Assert.Equal(30, owner.Backend.Catalog.Fields.Length);
        Assert.Equal(field.DeviceIdentity, owner.Backend.Catalog.Devices.Single().Identity);
    }

    [Fact]
    public async Task CancelledInFlightCaptureDoesNotCommitOrClearLastCurrent()
    {
        var api = new Basic.Api(); using var owner = Owner(api, new());
        var first = await owner.CollectAsync(["process_all_image_path_actual"]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        owner.Inventory = new Inventory(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return [Process.GetCurrentProcess()]; });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Task.Run(() => owner.CollectAsync(["process_all_image_path_actual"], cancellation.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); api.Image = "changed"; cancellation.Cancel(); release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(first.Processes.Single().Values.Single().StringValue, owner.ReadCachedSnapshot()!.Processes.Single().Values.Single().StringValue);
        }
        finally { release.Set(); try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task RetiredOwnerRejectsInFlightCommitButReturnsItsActualCaptureAndRetainsDisplay()
    {
        var api = new Basic.Api(); using var owner = Owner(api, new());
        var first = await owner.CollectAsync(["process_all_working_set_actual"]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        owner.Inventory = new Inventory(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return [Process.GetCurrentProcess()]; });
        var pending = Task.Run(() => owner.CollectAsync(["process_all_working_set_actual"]));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); api.MemoryValue = api.MemoryValue! with { WorkingSet = 17 }; owner.Dispose(); release.Set();
            var captured = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(MetricInteger.Parse("17"), captured.Processes.Single().Values.Single().IntegerValue);
            Assert.Equal(first.Processes.Single().Values.Single().IntegerValue, owner.ReadCachedSnapshot()!.Processes.Single().Values.Single().IntegerValue);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task WorkspacePermitRejectionAndPreparedSlotPreserveTheOriginalVisibleCurrent()
    {
        var api = new Basic.Api(); using var owner = Owner(api, new());
        var first = await owner.CollectAsync(["process_all_image_path_actual"]);
        api.Image = "changed";
        await owner.CollectAsync(["process_all_image_path_actual"], permit: new RejectedPermit());
        Assert.Equal(first.Processes.Single().Values.Single().StringValue, owner.ReadCachedSnapshot()!.Processes.Single().Values.Single().StringValue);
        var prepared = owner.Current.PrepareCopy(); prepared.Fail(["process_all_image_path_actual"]);
        Assert.NotNull(owner.ReadCachedSnapshot()!.Processes.Single().Values.Single().StringValue);
        owner.Current.CommitPrepared(prepared);
        Assert.Null(owner.ReadCachedSnapshot()!.Processes.Single().Values.Single().StringValue);
    }

    [Fact]
    public async Task FourNetworkFieldsUseTheActualSdkWindowOwnerAndOnlyAcquireOnSelection()
    {
        var broker = new Broker(); var api = new Basic.Api(); using var owner = Owner(api, broker);
        await owner.CollectAsync(["process_all_image_path_actual"]);
        Assert.Equal(0, broker.Acquires);
        var names = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic).Select(field => field.Name).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(stopped.CapturedAt); Assert.Equal(0, broker.Acquires);
        owner.StartNetworkCapture();
        var warming = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(warming.CapturedAt); Assert.Equal(1, broker.Acquires);
        broker.PublishAfterWindow();
        var complete = await owner.CollectAsync(names, timeout.Token);
        Assert.Equal(2000, complete.WindowMilliseconds);
        Assert.All(complete.Processes.Single().Values, value => Assert.True(value.IntegerValue == new MetricInteger(0) || value.NumericValue == 0));
        Assert.Equal(1, broker.Acquires);
        owner.StopNetworkCapture(); Assert.Equal(1, broker.Releases);
        var afterStop = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(afterStop.CapturedAt); Assert.Equal(1, broker.Acquires);
        owner.StartNetworkCapture();
        var restarted = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(restarted.CapturedAt); Assert.Equal(2, broker.Acquires);
        owner.Dispose(); Assert.Equal(2, broker.Releases);
    }

    [Fact]
    public void HostedOwnerRejectsStandaloneNetworkLifecycle()
    {
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        Assert.Throws<InvalidOperationException>(() => owner.StartNetworkCapture());
        Assert.Throws<InvalidOperationException>(() => owner.StopNetworkCapture());
    }

    [Fact]
    public async Task TwoDiskFieldsReadTheSdkWindowOnlyAfterExplicitCaptureStart()
    {
        var broker = new Broker();
        using var owner = Owner(new Basic.Api(), broker);
        var names = ProcessMetricFieldRegistry.Definitions
            .Where(field => field.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo)
            .Select(field => field.Name).ToArray();
        Assert.Equal(2, names.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(stopped.CapturedAt); Assert.Equal(0, broker.Acquires);
        owner.StartDiskCapture();
        var warming = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(warming.CapturedAt); Assert.Equal(1, broker.Acquires);
        broker.PublishAfterWindow();
        var complete = await owner.CollectAsync(names, timeout.Token);
        Assert.NotNull(complete.CapturedAt); Assert.Equal(2000, complete.WindowMilliseconds);
        Assert.Equal(names, complete.Fields.Select(field => field.Name).ToArray());
        owner.StopDiskCapture(); Assert.Equal(1, broker.Releases);
        var afterStop = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(afterStop.CapturedAt); Assert.Equal(1, broker.Acquires);
        owner.StartDiskCapture();
        var restarted = await owner.CollectAsync(names, timeout.Token);
        Assert.Null(restarted.CapturedAt); Assert.Equal(2, broker.Acquires);
        owner.Dispose(); Assert.Equal(2, broker.Releases);
    }

    [Fact]
    public void HostedOwnerRejectsStandaloneDiskLifecycle()
    {
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        Assert.Throws<InvalidOperationException>(() => owner.StartDiskCapture());
        Assert.Throws<InvalidOperationException>(() => owner.StopDiskCapture());
    }

    [Fact]
    public void HostCanonicalDatasetPayloadIsTheSameSdkSlotAndFailureDoesNotCreateAnotherCurrent()
    {
        var host = new HostedResourcePublicationState();
        Assert.Same(host.Resource.ProcessFieldState, host.ProcessOwner.Current);
        var definition = ProcessMetricFieldRegistry.Find("process_all_image_path_actual")!;
        var field = new ProcessMetricFieldValue(definition.Name, null, null) { StringValue = "recorded" };
        var sample = new ProcessMetricFieldSnapshot(DateTimeOffset.UnixEpoch, null, [definition],
            [new(41, "100", [field])]) { Sources = [new(definition.SourceId, DateTimeOffset.UnixEpoch, null)] };
        var request = Basic.Request(definition.Name);
        host.Resource.ApplyDirect(request, new(DateTimeOffset.UnixEpoch, []) { ProcessFields = sample },
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Same(host.Resource.ReadProcessFieldPart(definition.DatasetId), host.ProcessOwner.Current.ReadPart(definition.DatasetId));
        var other = new ProcessMetricFieldSnapshot(DateTimeOffset.UnixEpoch, 2000, [], []);
        host.ProcessOwner.Current.SetPart(ProcessMetricDatasetIds.ObservedNetworkTraffic, other);
        var sid = ProcessMetricFieldRegistry.Find("process_all_user_sid_actual")!;
        var broader = sample with { Fields = [definition, sid], Processes = [new(41, "100", [field, new(sid.Name, null, null) { StringValue = "recorded SID" }])] };
        host.Resource.ApplyDirect(Basic.Request(definition.Name, sid.Name), new(DateTimeOffset.UnixEpoch, []) { ProcessFields = broader },
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        host.Resource.ApplyDirect(request, new(DateTimeOffset.UnixEpoch, []) { ProcessFields = sample },
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Single(host.ProcessOwner.Current.ReadPart(definition.DatasetId)!.Fields);
        Assert.Same(other, host.ProcessOwner.Current.ReadPart(ProcessMetricDatasetIds.ObservedNetworkTraffic));
        host.Resource.MarkFailed(request, DateTimeOffset.UnixEpoch, new InvalidOperationException("recorded failure"));
        Assert.Null(host.ProcessOwner.Current.ReadPart(definition.DatasetId)!.Processes.Single().Values.Single().StringValue);
        Assert.Same(other, host.ProcessOwner.Current.ReadPart(ProcessMetricDatasetIds.ObservedNetworkTraffic));
        host.ProcessOwner.Dispose();
    }

    private static WindowsProcessFieldOwner Owner(Basic.Api api, Broker broker)
    {
        var owner = WindowsProcessFieldOwner.CreateDefault(broker);
        owner.Inventory = new Inventory(static () => [Process.GetCurrentProcess()]);
        owner.BasicReader = new(api);
        return owner;
    }
    private sealed class Inventory(Func<Process[]> read) : IWindowsProcessInventoryReader { public Process[] GetProcesses() => read(); }
    private sealed class RejectedPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
    private sealed class Broker : ProcessFieldEtwTestBroker { internal Broker() : base(17) { } }
}
