using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsInformationOwnerTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(8, 8, 16, 2, 250_000);

    [Fact]
    public async Task ScopeIsPassiveAndImmutableAndTypedJsonPreservesPrecisionAndUnknownMetadata()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var ids = new List<uint> { 7, 8, 9 };
        var scope = new NvidiaDrsInformationRequest([new("Game", ids)], Limits);
        ids.Add(10);
        var calls = 0;
        var access = owner.CreateNvidiaDrsInformation(scope, (profiles, _, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            Assert.Equal(new uint[] { 7, 8, 9 }, profiles.Single().SettingIds);
            return new([new("Game", 0x80000007, 1, 0, 3,
                [Setting(7, 4, new NvidiaDrsQwordValue(ulong.MaxValue)),
                 Setting(8, 1, new NvidiaDrsBinaryValue([0, 255])), Setting(9, 2, null)])]);
        });
        Assert.Equal(3, access.Backend.Catalog.Fields.Length);
        Assert.All(scope.Fields, field => Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(field).Status));
        Assert.Equal(0, calls);
        var readings = await access.Backend.CollectAsync(scope.Fields, CancellationToken.None);
        var qword = Packet(readings[0]);
        Assert.Equal("18446744073709551615", qword.GetProperty("currentValue").GetProperty("value").GetString());
        Assert.Equal("qword", qword.GetProperty("currentValue").GetProperty("kind").GetString());
        Assert.Equal(2, Packet(readings[1]).GetProperty("currentValue").GetProperty("length").GetInt32());
        Assert.Equal("AP8=", Packet(readings[1]).GetProperty("currentValue").GetProperty("base64").GetString());
        Assert.Equal(2U, Packet(readings[2]).GetProperty("settingTypeRaw").GetUInt32());
        Assert.Equal(JsonValueKind.Null, Packet(readings[2]).GetProperty("currentValue").ValueKind);
        Assert.Equal(0x80000003U, qword.GetProperty("settingLocationRaw").GetUInt32());
        foreach (var reading in readings)
        {
            Assert.Equal(Packet(reading).GetRawText(), owner.ReadState().InformationFields.FindField(reading.Field)!.Value!.Value.GetRawText());
            Assert.Equal(Packet(reading).GetRawText(), Packet(access.Backend.ReadCached(reading.Field)).GetRawText());
        }
        Assert.All(owner.ReadState().InformationFields.Devices, device => Assert.Equal("gpu_driver_profile_setting", device.ObjectType));
    }

    [Fact]
    public async Task SourceFailureClearsTheSelectedCurrentAndDoesNotRetainAGoodValue()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = Scope(7); var available = true;
        var access = owner.CreateNvidiaDrsInformation(scope, (_, _, _) => available ? Snapshot(7, 1) : null);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(await access.Backend.CollectAsync(scope.Fields, CancellationToken.None)).Status);
        available = false;
        Assert.Equal(FieldObservationStatus.Unavailable, Assert.Single(await access.Backend.CollectAsync(scope.Fields, CancellationToken.None)).Status);
        Assert.Null(owner.ReadState().InformationFields.FindField(scope.Fields[0])!.Value);
    }

    [Fact]
    public async Task CancellationAndRejectedPermitLeaveTheOriginalCurrentUnchanged()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        using var cancellation = new CancellationTokenSource();
        var scope = Scope(7); var cancel = false;
        NvidiaDrsReadSource source = (_, _, _) => { if (cancel) cancellation.Cancel(); return Snapshot(7, 2); };
        var access = owner.CreateNvidiaDrsInformation(scope, source);
        _ = await access.Backend.CollectAsync(scope.Fields, CancellationToken.None);
        var before = owner.ReadState(); cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.Backend.CollectAsync(scope.Fields, cancellation.Token).AsTask());
        Assert.Same(before, owner.ReadState());
        cancel = false;
        var permission = new DeviceTopologyCapturePermission(() => true, _ => false, () => true);
        _ = await owner.CaptureNvidiaDrsInformationAsync(scope, scope, source, permission, CancellationToken.None);
        Assert.Same(before, owner.ReadState());
    }

    [Fact]
    public async Task FullDiscoveryDistinguishesUnobservedFromObservedEmptyWithoutPurgingCurrent()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = Scope(7);
        var access = owner.CreateNvidiaDrsInformation(scope, (_, _, _) => Snapshot(7, 2));
        _ = await access.Backend.CollectAsync(scope.Fields, CancellationToken.None);
        var before = owner.ReadState();
        Assert.Null(await owner.DiscoverNvidiaDrsDirectoryAsync(new(0, 0, 0, 0, 0),
            (_, _) => throw new InvalidOperationException("No database observation is permitted with zero budget.")));
        Assert.Same(before, owner.ReadState());
        var empty = await owner.DiscoverNvidiaDrsDirectoryAsync(Limits, (_, _) => new([]));
        Assert.NotNull(empty); Assert.NotNull(empty.ObservedAt); Assert.Empty(empty.Devices);
        Assert.Same(before, owner.ReadState());
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(scope.Fields[0]).Status);
        Assert.Null(await owner.DiscoverNvidiaDrsDirectoryAsync(Limits, (_, _) => null));
        Assert.Same(before, owner.ReadState());
    }

    [Fact]
    public async Task RealProfilesWithNoSettingsProduceACompleteEmptySettingDirectory()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var result = await owner.DiscoverNvidiaDrsDirectoryAsync(Limits,
            (_, _) => new([new("A", 7, 0, 0, 0, []), new("B", 7, 1, 4, 0, [])]));
        Assert.NotNull(result); Assert.NotNull(result.ObservedAt); Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task IndependentSelectionsMergeAndFailedSelectionLeavesOtherKeysUntouched()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var a = Scope(7); var b = new NvidiaDrsInformationRequest([new("Other", new uint[] { 8 })], Limits);
        var accessA = owner.CreateNvidiaDrsInformation(a, (_, _, _) => Snapshot(7, 10));
        var fail = false;
        var accessB = owner.CreateNvidiaDrsInformation(b, (_, _, _) => fail ? null
            : new([new("Other", 7, 0, 0, 1, [Setting(8, 0, new NvidiaDrsDwordValue(20))])]));
        _ = await accessA.Backend.CollectAsync(a.Fields, default);
        _ = await accessB.Backend.CollectAsync(b.Fields, default);
        var aValue = owner.ReadState().InformationFields.FindField(a.Fields[0]);
        fail = true;
        _ = await accessB.Backend.CollectAsync(b.Fields, default);
        Assert.Same(aValue, owner.ReadState().InformationFields.FindField(a.Fields[0]));
        Assert.Null(owner.ReadState().InformationFields.FindField(b.Fields[0])!.Value);
        Assert.Equal(2, owner.ReadState().InformationFields.Devices.Count);
    }

    [Fact]
    public async Task BorrowUsesOriginalCurrentAndPermitAndWithdrawalDoesNotCloseSourceOrClearCurrent()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = Scope(7); var calls = 0;
        var access = owner.CreateNvidiaDrsInformation(scope, (_, _, _) => { calls++; return Snapshot(7, (uint)calls); });
        var borrow = access.BorrowSubscriptionCapture();
        Assert.Same(access.Backend, borrow.Backend);
        Assert.Same(owner.InformationPublications, borrow.Publications);
        Assert.All(borrow.Bind(scope.Fields), binding => Assert.Equal(FieldCollectionSourceKind.SourceQuery, binding.SourceKind));
        var accepted = await borrow.CollectAsync(scope.Fields, new Permit(true));
        Assert.True(accepted.Committed);
        Assert.Equal<FieldReference>(scope.Fields, accepted.QueriedFields);
        var before = owner.ReadState();
        var rejected = await borrow.CollectAsync(scope.Fields, new Permit(false));
        Assert.False(rejected.Committed); Assert.Same(before, owner.ReadState());
        borrow.SynchronizeDemand([]); borrow.Dispose();
        Assert.Same(before, owner.ReadState());
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(scope.Fields[0]).Status);
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task OrdinaryTopologyPublicationPreservesTheOriginalDrsFields()
    {
        var policy = new DeviceTopologyCapturePolicy(new EmptyTopology(), static _ => "recorded",
            static (snapshot, _) => new(snapshot, DeviceInformationSnapshot.Empty), static (_, _) => Task.CompletedTask,
            static _ => new("recorded-failure", []), static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new EmptyAcpi(), policy);
        var scope = Scope(7); var access = owner.CreateNvidiaDrsInformation(scope, (_, _, _) => Snapshot(7, 8));
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        var before = owner.ReadState().InformationFields.FindField(scope.Fields[0]);
        await owner.CaptureInformationAsync(default);
        Assert.Same(before, owner.ReadState().InformationFields.FindField(scope.Fields[0]));
    }

    [Fact]
    public async Task BoundZonePermitRejectsDirectAndBorrowCommitAfterTheSourceWithoutFalseCommittedResult()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = Scope(7); var readable = true; var freezeDuringRead = false; var calls = 0;
        var order = new List<string>();
        var zone = new DeviceTopologyCapturePermission(() => readable, publish =>
        { order.Add("zone"); if (!readable) return false; publish(); return true; }, () => true);
        var access = owner.CreateNvidiaDrsInformation(scope, (_, _, _) =>
        { calls++; if (freezeDuringRead) readable = false; return Snapshot(7, (uint)calls); }, zone);
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        var before = owner.ReadState();
        freezeDuringRead = true;
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        Assert.Same(before, owner.ReadState());
        readable = true; order.Clear();
        using var borrow = access.BorrowSubscriptionCapture();
        var rejected = await borrow.CollectAsync(scope.Fields, new OrderedPermit(order));
        Assert.False(rejected.Committed); Assert.Same(before, owner.ReadState());
        Assert.Equal(new[] { "outer", "zone" }, order);
        // A frozen direct access is passive: no source call and no commit.
        var count = calls; _ = await access.Backend.CollectAsync(scope.Fields, default); Assert.Equal(count, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryDiscoveryRequiresAcceptedPermissionForEmptyAndNonemptyObservation(bool empty)
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var calls = 0; var active = true;
        var permission = new DeviceTopologyCapturePermission(() => active, _ => false, () => true);
        var before = owner.ReadState();
        var result = await owner.DiscoverNvidiaDrsDirectoryAsync(Limits, (_, _) =>
        { calls++; return empty ? new([]) : Snapshot(7, 1); }, permission: permission);
        Assert.Null(result); Assert.Equal(1, calls); Assert.Same(before, owner.ReadState());
        active = false;
        Assert.Null(await owner.DiscoverNvidiaDrsDirectoryAsync(Limits, (_, _) =>
            throw new InvalidOperationException("Inactive permission must reject before source."), permission: permission));
        Assert.Same(before, owner.ReadState());
        var accepted = await owner.DiscoverNvidiaDrsDirectoryAsync(Limits,
            (_, _) => empty ? new([]) : Snapshot(7, 2), permission: new(() => true, publish => { publish(); return true; }, () => true));
        Assert.NotNull(accepted);
        if (empty) Assert.Same(before, owner.ReadState());
        else Assert.NotSame(before, owner.ReadState());
    }

    private sealed class OrderedPermit(List<string> order) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { order.Add("outer"); commit(); return true; }
    }

    private sealed class Permit(bool accept) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { if (!accept) return false; commit(); return true; }
    }
    private sealed class EmptyAcpi : ResourceManager.ReadWrite.Windows.Devices.IAcpiFirmwareTableReader
    { public IReadOnlyList<ResourceManager.ReadWrite.Windows.Devices.Models.AcpiFirmwareTable>? ReadTables() => []; }
    private sealed class EmptyTopology : ResourceManager.App.Application.DeviceTopology.IDeviceTopologyReader
    {
        public ResourceManager.App.Domain.DeviceTopology.DeviceTopologySnapshot ReadSnapshot()
            => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
    }

    private static NvidiaDrsInformationRequest Scope(uint id) => new([new("Game", new[] { id })], Limits);
    private static NvidiaDrsSnapshot Snapshot(uint id, uint value) => new([new("Game", 7, 0, 0, 1,
        [Setting(id, 0, new NvidiaDrsDwordValue(value))])]);
    private static NvidiaDrsSetting Setting(uint id, uint type, NvidiaDrsCurrentValue? value)
        => new(id, "setting", type, 0x80000003, 0x80000005, 0x80000007, value);
    private static JsonElement Packet(FieldReading reading) => reading.Value!.Structure!.Value;
}
