using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Drivers;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class DriverInformationOwnerTests
{
    [Fact]
    public void ExactDevInstKeysTypesAndUtcDeclarationDateAreUsedWithoutInference()
    {
        var seen = new List<uint>();
        var instant = new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var row = WindowsDeviceDriverPropertyReader.Read(42, @"PCI\A", Query);
        Assert.Equal("INF provider", row.ProviderDeclared); Assert.Equal("2024-02-29", row.DateDeclared);
        Assert.Equal("31.0.101.5534", row.VersionDeclared);
        Assert.Equal(new uint[] { 9, 2, 3 }, seen);
        Assert.Equal(@"windows-device-driver|PCI\A", row.DeviceIdentity);
        uint Query(uint node, in WindowsDeviceDriverPropertyReader.PropertyKey key, out uint type,
            byte[] buffer, ref uint size, uint flags)
        {
            Assert.Equal(42u, node); Assert.Equal(0u, flags);
            Assert.Equal(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), key.FormatId);
            seen.Add(key.PropertyId);
            if (key.PropertyId == 9)
            { type = 0x12; var bytes = Encoding.Unicode.GetBytes("INF provider\0"); bytes.CopyTo(buffer, 0); size = (uint)bytes.Length; }
            else if (key.PropertyId == 2)
            { type = 0x10; BinaryPrimitives.WriteInt64LittleEndian(buffer, instant); size = 8; }
            else
            { type = 0x12; var bytes = Encoding.Unicode.GetBytes("31.0.101.5534\0"); bytes.CopyTo(buffer, 0); size = (uint)bytes.Length; }
            return 0;
        }
    }

    [Fact]
    public void MissingWrongTypeMalformedFiletimeAndBoundedStringsAreNull()
    {
        var valid = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(valid, new DateTime(2006, 6, 21, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        Assert.Equal("2006-06-21", WindowsDeviceDriverPropertyReader.DecodeDate(0x10, valid, 8));
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeDate(0x12, valid, 8));
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeDate(0x10, valid, 7));
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeDate(0x10, new byte[8], 8));
        BinaryPrimitives.WriteInt64LittleEndian(valid, long.MaxValue);
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeDate(0x10, valid, 8));
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeString(0x12, [0, 0xD8, 0, 0], 4));
        Assert.Null(WindowsDeviceDriverPropertyReader.DecodeString(0x12, Encoding.Unicode.GetBytes("a\nb\0"), 8));
        var calls = 0;
        var row = WindowsDeviceDriverPropertyReader.Read(1, "A", Query);
        Assert.Null(row.ProviderDeclared); Assert.Null(row.DateDeclared); Assert.Null(row.VersionDeclared); Assert.Equal(3, calls);
        uint Query(uint node, in WindowsDeviceDriverPropertyReader.PropertyKey key, out uint type,
            byte[] buffer, ref uint size, uint flags)
        { calls++; type = 0x12; size = 9000; return 26; }
    }

    [Fact]
    public async Task SingleOriginalOwnerSharesPublicationAndRetainsOnCancelRevokeThenClearsFailureAndRemovesEmpty()
    {
        var reads = 0; var next = Native(new DeviceTopologyDriverInformation(@"PCI\A", "Provider", "2024-02-29") { VersionDeclared = "31.0.101.5534" });
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation(() => { reads++; return next; });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        var fields = (await backend.DiscoverAsync(default)).Fields;
        Assert.Equal(3, fields.Length); Assert.Equal(1, reads);
        var date = fields.Single(field => field.Field.FieldName == "driver_all_date_declared").Field;
        var version = fields.Single(field => field.Field.FieldName == "driver_all_version_declared").Field;
        Assert.Equal("ISO8601-date", backend.Catalog.Require(date).Definition.Unit);
        Assert.False(backend.Catalog.Require(date).Write.Supported);
        Assert.Equal("text", backend.Catalog.Require(version).Definition.Unit);
        Assert.False(backend.Catalog.Require(version).Write.Supported);
        var previous = owner.ReadState();
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([date, version]);
        using var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]);
        next = Native(new DeviceTopologyDriverInformation(@"PCI\A", "Changed", "2025-01-01") { VersionDeclared = "32.0.101.6078" });
        Assert.False((await borrow.CollectAsync([date], new Permit(false))).Committed);
        Assert.Same(previous, owner.ReadState());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync([date], new Permit(), cancellation.Token));
        Assert.Same(previous, owner.ReadState());
        var result = await borrow.CollectAsync([date, version], new Permit()); Assert.True(result.Committed);
        var dateReading = result.Readings.Single(reading => reading.Field.FieldName == "driver_all_date_declared");
        var versionReading = result.Readings.Single(reading => reading.Field.FieldName == "driver_all_version_declared");
        Assert.Equal("2025-01-01", dateReading.Value!.Text);
        Assert.Equal("32.0.101.6078", versionReading.Value!.Text);
        Assert.Equal(dateReading, backend.ReadCached(date));
        Assert.Equal(versionReading, backend.ReadCached(version));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await publication.WaitAsync(timeout.Token);
        next = new(new Dictionary<string, DeviceTopologyNativeDevice>(), "access denied");
        var failed = (await backend.CollectAsync([version], default)).Single();
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status); Assert.Null(failed.Value); Assert.Null(failed.ObservedAt);
        Assert.Equal(3, backend.Catalog.Fields.Length);
        next = Native(); _ = await owner.CaptureInformationAsync(default);
        Assert.Empty(backend.Catalog.Fields);
        // Previously bound exact fields project retirement unavailable without re-requiring live catalog.
        var retired = await borrow.CollectAsync([date, version], new Permit());
        Assert.True(retired.Committed); Assert.All(retired.Readings, reading => Assert.Null(reading.Value));
    }

    [Fact]
    public void ProductProjectionIncludesOnlyExactUniqueBindingsAndPersistedValuesAreNotLive()
    {
        var rows = new[] { new DeviceTopologyDriverInformation(@"pci\a-b", "A", "2024-01-01") { VersionDeclared = "1.2.3" },
            new DeviceTopologyDriverInformation(@"PCI\A_B", "B", "2024-02-02") { VersionDeclared = "4.5.6" } };
        var snapshot = new DeviceTopologySnapshot(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []) { DriverInformation = rows };
        var live = DeviceTopologyInformationProjection.Create(snapshot, snapshot.CapturedAt);
        Assert.Contains(live.Devices, device => device.DeviceIdentity == @"windows-device-driver|PCI\A-B");
        Assert.Contains(live.Devices, device => device.DeviceIdentity == @"windows-device-driver|PCI\A_B");
        var persisted = DeviceTopologyInformationProjection.Create(snapshot, null);
        Assert.All(persisted.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
        var duplicate = WindowsInformationFieldProjection.Drivers([rows[0], rows[0]], snapshot.CapturedAt);
        Assert.Empty(duplicate.Devices);
    }

    [Fact]
    public async Task ExistingNativeSubscriptionUsesSameDriverCurrentAndStopLeavesDirectCaptureAvailable()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation(() => Native(new DeviceTopologyDriverInformation(@"PCI\A", "Provider", "2024-02-29") { VersionDeclared = "31.0.101.5534" }));
        var backend = owner.CreateBackend(); var field = (await backend.DiscoverAsync(default)).Fields.Single(candidate => candidate.Field.FieldName == "driver_all_version_declared").Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected worker failure", failure.Error), TimeSpan.FromMilliseconds(1));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("driver", [field], TimeSpan.FromMilliseconds(5), timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var delivered = Assert.Single(stream.Current);
        var current = backend.ReadCached(field);
        Assert.NotNull(delivered.ObservedAt);
        Assert.True(current.ObservedAt >= delivered.ObservedAt);
        Assert.Equal(delivered with { ObservedAt = current.ObservedAt }, current);
        await stream.DisposeAsync(); await composition.StopAsync();
        Assert.Equal(FieldObservationStatus.Current, (await backend.CollectAsync([field], default)).Single().Status);
    }

    [Fact]
    public async Task TerminalDisposeDrainsOriginalGateAndLateQueuedCaptureRejectsWithoutSemaphoreHang()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = DeviceTopologyCaptureOwner.CreateDriverInformation(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return Native(new DeviceTopologyDriverInformation(@"PCI\A", "Provider", "2024-01-01"));
        });
        var first = Task.Run(async () => await owner.CaptureInformationAsync(default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var queued = owner.CaptureInformationAsync(default).AsTask();
        var dispose = Task.Run(owner.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { _ = owner.CreateBackend(); return false; }
                catch (ObjectDisposedException) { return true; }
            }, TimeSpan.FromSeconds(5)));
            Assert.False(dispose.IsCompleted);
        }
        finally { release.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => owner.CreateBackend());
        owner.Dispose();
    }

    [Fact]
    public void ProviderUsesOriginalOneBoundedResizeAndDateFailureDoesNotEraseProvider()
    {
        var calls = new List<(uint Key, uint Size)>();
        var text = new string('P', 300);
        var row = WindowsDeviceDriverPropertyReader.Read(7, "A", Query);
        Assert.Equal(text, row.ProviderDeclared); Assert.Null(row.DateDeclared); Assert.Equal("31.0.101.5534", row.VersionDeclared);
        Assert.Equal(new[] { (9u, 512u), (9u, 602u), (2u, 8u), (3u, 512u), (3u, 602u) }, calls);
        uint Query(uint node, in WindowsDeviceDriverPropertyReader.PropertyKey key, out uint type,
            byte[] buffer, ref uint size, uint flags)
        {
            calls.Add((key.PropertyId, size)); type = 0x12;
            if (key.PropertyId == 2) return 5;
            if (size == 512) { size = 602; return 26; }
            var value = key.PropertyId == 9 ? text : "31.0.101.5534";
            Encoding.Unicode.GetBytes(value + "\0").CopyTo(buffer, 0); size = (uint)Encoding.Unicode.GetByteCount(value + "\0"); return 0;
        }
    }

    [Fact]
    public void VersionPropertyFailureLeavesOtherDeclarationsAndVersionUnavailable()
    {
        var instant = new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var row = WindowsDeviceDriverPropertyReader.Read(7, "A", Query);
        Assert.Equal("INF provider", row.ProviderDeclared);
        Assert.Equal("2024-02-29", row.DateDeclared);
        Assert.Null(row.VersionDeclared);
        uint Query(uint node, in WindowsDeviceDriverPropertyReader.PropertyKey key, out uint type,
            byte[] buffer, ref uint size, uint flags)
        {
            if (key.PropertyId == 3) { type = 0; size = 0; return 5; }
            if (key.PropertyId == 9)
            { type = 0x12; var bytes = Encoding.Unicode.GetBytes("INF provider\0"); bytes.CopyTo(buffer, 0); size = (uint)bytes.Length; }
            else
            { type = 0x10; BinaryPrimitives.WriteInt64LittleEndian(buffer, instant); size = 8; }
            return 0;
        }
    }

    private static DeviceTopologyNativeSnapshot Native(params DeviceTopologyDriverInformation[] rows)
        => new(rows.ToDictionary(row => row.DeviceInstanceId, row => new DeviceTopologyNativeDevice(row.DeviceInstanceId,
            null, null, null, null, "class-key", null, null, null, null, [], [], [], null, null, null)
            { DriverInformation = row }, StringComparer.OrdinalIgnoreCase), null);
    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
}


