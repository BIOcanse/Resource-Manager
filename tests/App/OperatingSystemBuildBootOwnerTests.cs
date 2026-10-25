using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class OperatingSystemBuildBootOwnerTests
{
    [Fact]
    public void StrictDmtfOffsetMicrosecondsAndBuildTextArePreservedWithoutLocalTimezoneGuess()
    {
        Assert.Equal("26100", WindowsOperatingSystemDeclarationReader.DecodeBuild("26100"));
        Assert.Equal("00026100", WindowsOperatingSystemDeclarationReader.DecodeBuild("00026100"));
        Assert.Null(WindowsOperatingSystemDeclarationReader.DecodeBuild(26100));
        Assert.Null(WindowsOperatingSystemDeclarationReader.DecodeBuild("26100.1234"));
        Assert.Null(WindowsOperatingSystemDeclarationReader.DecodeBuild(" 26100"));
        var boot = WindowsOperatingSystemDeclarationReader.DecodeBootTime("20240229120000.123456+330");
        Assert.Equal("2024-02-29T06:30:00.1234560+00:00", boot!.Value.ToString("O", CultureInfo.InvariantCulture));
        Assert.Equal(boot, WindowsOperatingSystemDeclarationReader.DecodeBootTime("20240229013000.123456-300"));
        foreach (var invalid in new[] { "20240229120000.******+330", "20240229120000.123456+***", "20240229120000.123456:000",
            "20230229120000.123456+000", "20240229120000.123456+999", "00010101000000.000000+840", "20240229120000.123456" })
            Assert.Null(WindowsOperatingSystemDeclarationReader.DecodeBootTime(invalid));
    }

    [Fact]
    public void ExplicitReaderQueriesOnlySelectedColumnsOnceAndRejectsAmbiguousSingleton()
    {
        var queries = new List<string>();
        var multiple = false;
        var reader = new WindowsOperatingSystemDeclarationReader(TimeSpan.FromMilliseconds(1234), (scope, query, timeout) =>
        {
            Assert.Equal(@"root\CIMV2", scope); Assert.Equal(TimeSpan.FromMilliseconds(1234), timeout); queries.Add(query);
            var row = new Dictionary<string, object?> { ["BuildNumber"] = "26100", ["LastBootUpTime"] = "20240229120000.123456+330" };
            return new(multiple ? [row, row] : [row], null);
        });
        Assert.Empty(queries); _ = reader.Read(WindowsOperatingSystemInformationFields.Uptime); Assert.Empty(queries);
        Assert.Equal("26100", reader.Read(WindowsOperatingSystemInformationFields.Build).BuildDeclared);
        Assert.Equal("SELECT BuildNumber FROM Win32_OperatingSystem", queries[0]);
        Assert.NotNull(reader.Read(WindowsOperatingSystemInformationFields.BootTime).BootTimeActual);
        Assert.Equal("SELECT LastBootUpTime FROM Win32_OperatingSystem", queries[1]);
        multiple = true; var missing = reader.Read(WindowsOperatingSystemInformationFields.All);
        Assert.Null(missing.BuildDeclared); Assert.Null(missing.BootTimeActual); Assert.Equal(3, queries.Count);
    }

    [Fact]
    public async Task SelectedCaptureMergesOnlyRequestedValuesAndTimesAndFailureDoesNotClearUptime()
    {
        var uptimeReads = 0; var queries = new List<string>(); var denied = false;
        var clock = new Clock();
        var reader = new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, query, _) =>
        {
            queries.Add(query);
            return denied ? new([], "Access denied") : new([new() { ["BuildNumber"] = "26100", ["LastBootUpTime"] = "20240229120000.123456+330" }], null);
        });
        using var owner = new WindowsOperatingSystemInformationOwner(() => { uptimeReads++; return new(TimeSpan.FromSeconds(7), 7); }, reader, clock);
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Empty(queries); Assert.Equal(0, uptimeReads);
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(3, catalog.Fields.Count(field => field.Field.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineIdentity));
        var uptime = catalog.Fields.Single(field => field.Field.FieldName == "os_all_uptime_actual").Field;
        var build = catalog.Fields.Single(field => field.Field.FieldName == "os_all_build_declared").Field;
        var boot = catalog.Fields.Single(field => field.Field.FieldName == "os_all_boot_time_actual").Field;
        var oldUptime = backend.ReadCached(uptime); var oldBoot = backend.ReadCached(boot);
        clock.Now = clock.Now.AddHours(1);
        denied = true;
        var failed = (await backend.CollectAsync([build], default)).Single();
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status); Assert.Null(failed.Value); Assert.Null(failed.ObservedAt);
        Assert.Equal(oldUptime, backend.ReadCached(uptime)); Assert.Equal(oldBoot, backend.ReadCached(boot)); Assert.Equal(1, uptimeReads);
        Assert.Equal("SELECT BuildNumber FROM Win32_OperatingSystem", queries.Last());
        denied = false; clock.Now = clock.Now.AddHours(1);
        var updated = (await backend.CollectAsync([boot], default)).Single();
        Assert.Equal(clock.Now, updated.ObservedAt); Assert.Equal(oldUptime, backend.ReadCached(uptime)); Assert.Equal(1, uptimeReads);
        Assert.Equal("SELECT LastBootUpTime FROM Win32_OperatingSystem", queries.Last());
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(build).Status);
        var queryCount = queries.Count; _ = backend.Catalog; _ = backend.ReadCached(boot); Assert.Equal(queryCount, queries.Count);
    }

    [Fact]
    public async Task BorrowSharesOriginalCurrentAndRevokedOrCanceledCaptureRetainsCompletePublication()
    {
        var text = "26100";
        using var owner = new WindowsOperatingSystemInformationOwner(() => new(TimeSpan.FromSeconds(7), 7),
            new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, _, _) => new([new() { ["BuildNumber"] = text, ["LastBootUpTime"] = "20240229120000.123456+330" }], null)));
        var backend = owner.CreateBackend(); var fields = (await backend.DiscoverAsync(default)).Fields;
        var build = fields.Single(field => field.Field.FieldName == "os_all_build_declared").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([build]);
        var previous = owner.ReadInformation(); text = "26200";
        Assert.False((await borrow.CollectAsync([build], new Permit(false))).Committed); Assert.Same(previous, owner.ReadInformation());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync([build], new Permit(), canceled.Token));
        Assert.Same(previous, owner.ReadInformation());
        using var publication = borrow.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        var committed = await borrow.CollectAsync([build], new Permit()); Assert.True(committed.Committed);
        Assert.Equal("26200", committed.Readings.Single().Value!.Text); Assert.Equal(committed.Readings.Single(), backend.ReadCached(build));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await publication.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ExistingNativeCompositionStreamsRealSameOwnerBackendWithoutStartingAnotherOsWorker()
    {
        using var owner = new WindowsOperatingSystemInformationOwner(() => new(TimeSpan.FromSeconds(7), 7),
            new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, _, _) => new([new() { ["BuildNumber"] = "26100", ["LastBootUpTime"] = "20240229120000.123456+330" }], null)), new Clock());
        var backend = owner.CreateBackend(); var field = (await backend.DiscoverAsync(default)).Fields.Single(field => field.Field.FieldName == "os_all_boot_time_actual").Field;
        var config = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in config, failure => throw new InvalidOperationException("Unexpected worker failure", failure.Error), TimeSpan.Zero);
        await composition.StartAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("boot", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); Assert.Equal(stream.Current.Single(), backend.ReadCached(field));
        await composition.StopAsync(); Assert.False(await stream.MoveNextAsync());
        Assert.Equal(FieldObservationStatus.Current, (await backend.CollectAsync([field], default)).Single().Status);
    }

    [Fact]
    public async Task CancellationAfterWmiReadAndAtomicRetirementRejectTheWholeSelectedCommit()
    {
        Action? duringQuery = null;
        using var owner = new WindowsOperatingSystemInformationOwner(() => new(TimeSpan.FromSeconds(7), 7),
            new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, _, _) =>
            {
                duringQuery?.Invoke();
                return new([new() { ["BuildNumber"] = "26100", ["LastBootUpTime"] = "20240229120000.123456+330" }], null);
            }));
        var backend = owner.CreateBackend(); var build = (await backend.DiscoverAsync(default)).Fields.Single(field => field.Field.FieldName == "os_all_build_declared").Field;
        var previous = owner.ReadInformation();
        using var canceled = new CancellationTokenSource(); duringQuery = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([build], canceled.Token));
        Assert.Same(previous, owner.ReadInformation());
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([build]);
        var active = true;
        duringQuery = () => active = false;
        var permit = new MutablePermit(() => active);
        Assert.False((await borrow.CollectAsync([build], permit)).Committed);
        Assert.Same(previous, owner.ReadInformation());
    }

    [Fact]
    public async Task HostSelectedDirectAndOriginalRoleTwoSourceViewsUseOneOsItemAndMergedActiveFields()
    {
        var uptimeReads = 0; var queries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var os = new WindowsOperatingSystemInformationOwner(() => { Interlocked.Increment(ref uptimeReads); return new(TimeSpan.FromSeconds(7), 7); },
            new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, query, _) => { queries.Enqueue(query); return new([new() { ["BuildNumber"] = "26100", ["LastBootUpTime"] = "20240229120000.123456+330" }], null); }));
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = new DeviceTopologySnapshotProvider(new Topology(),
            new Store(), new(), sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance,
            new Acpi(), operatingSystemInventory: os);
        await using var cpu = new CpuTopologyProviderTestFixture();
        var api = new ProcessBasicFieldCollectorTests.Api(); using var process = ProcessBasicFieldCollectorTests.Fixture(api);
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!), provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        await provider.CaptureOperatingSystemInformationAsync(default);
        var catalog = host.Reads.Catalog;
        var build = catalog.Fields.Single(field => field.Field.FieldName == "os_all_build_declared").Field;
        var boot = catalog.Fields.Single(field => field.Field.FieldName == "os_all_boot_time_actual").Field;
        var direct = (await host.Reads.CollectAsync([build], default)).Single();
        Assert.Equal("SELECT BuildNumber FROM Win32_OperatingSystem", queries.Last()); Assert.Equal(1, uptimeReads);
        Assert.Equal(direct, host.Reads.ReadCached(build));
        var slow = TimeSpan.FromMinutes(1);
        using var slowBoot = host.Subscriptions.Acquire("slow-boot", [boot], slow);
        using var fastBuild = host.Subscriptions.Acquire("fast-build", [build], TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publication = os.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        var next = publication.WaitAsync(timeout.Token).AsTask();
        await provider.StartAsync(timeout.Token);
        try { await next; Assert.Equal("SELECT BuildNumber, LastBootUpTime FROM Win32_OperatingSystem", queries.Last()); Assert.Equal(1, uptimeReads); }
        finally { await provider.StopAsync(default); }
        Assert.Same(os, provider.OperatingSystemInventory);
        _ = await os.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Build);
    }

    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active; public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; } }
    private sealed class MutablePermit(Func<bool> active) : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active(); public bool TryPublish(Action commit) { if (!active()) return false; commit(); return true; } }
    private sealed class Topology : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => throw new InvalidOperationException("Unexpected topology capture."); }
    private sealed class Acpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("Unexpected ACPI capture."); }
    private sealed class Store : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken token) => throw new InvalidOperationException("Unexpected journal write.");
    }
}

