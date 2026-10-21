using System.Buffers.Binary;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuDieInformationTests
{
    [Fact]
    public void NativeDieRelationshipsPreserveFullGroupsAndWholePackageMembership()
    {
        IReadOnlyList<CpuOsPhysicalCore> cores = [new("a", 0, true, [1, 3]), new("b", 0, false, [135])];
        IReadOnlyList<CpuOsPackage> packages = [new("package-real", [135, 3, 1])];
        var dies = WindowsCpuOsTopologyReader.ParseDieInformation([.. Die((2, 128)), .. Die((0, 10))], cores, packages);
        Assert.Equal(new[] { "windows-cpu-die|0:1,0:3", "windows-cpu-die|2:7" }, dies.Select(static die => die.DeviceIdentity));
        Assert.Equal(new[] { 1, 3 }, dies[0].LogicalProcessorIds); Assert.Equal(new[] { 135 }, dies[1].LogicalProcessorIds);
        Assert.All(dies, static die => Assert.Equal("package-real", die.PackageDeviceIdentity));
        var crossGroup = Assert.Single(WindowsCpuOsTopologyReader.ParseDieInformation(Die((2, 128), (0, 10)), cores, packages));
        Assert.Equal("windows-cpu-die|0:1,0:3,2:7", crossGroup.DeviceIdentity);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("wrong-relationship")]
    [InlineData("zero-groups")]
    [InlineData("overlap")]
    [InlineData("split-core")]
    [InlineData("missing-core")]
    [InlineData("extra-core")]
    [InlineData("duplicate-core")]
    [InlineData("split-package")]
    [InlineData("missing-package")]
    [InlineData("extra-package")]
    [InlineData("duplicate-package")]
    public void InvalidDieMembershipDoesNotBecomeAPublishedPartialIdentity(string defect)
    {
        IReadOnlyList<CpuOsPhysicalCore>? cores = [new("a", 0, true, [1, 3]), new("b", 0, false, [135])];
        IReadOnlyList<CpuOsPackage>? packages = [new("p", [1, 3, 135])];
        var data = Die((0, 10), (2, 128));
        switch (defect)
        {
            case "truncated": data = data[..^1]; break;
            case "wrong-relationship": U32(data, 0, 3); break;
            case "zero-groups": U16(data, 30, 0); break;
            case "overlap": data = [.. Die((0, 10), (2, 128)), .. Die((0, 2))]; break;
            case "split-core": data = [.. Die((0, 2)), .. Die((0, 8), (2, 128))]; break;
            case "missing-core": cores = [new("a", 0, true, [1, 3])]; break;
            case "extra-core": cores = [.. cores!, new("c", 0, false, [9])]; break;
            case "duplicate-core": cores = [.. cores!, new("c", 0, false, [135])]; break;
            case "split-package": packages = [new("p", [1, 3]), new("q", [135])]; break;
            case "missing-package": packages = [new("p", [1, 3])]; break;
            case "extra-package": packages = [new("p", [1, 3, 135, 9])]; break;
            case "duplicate-package": packages = [new("p", [1, 3, 135]), new("q", [135])]; break;
        }
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseDieInformation(data, cores, packages));
    }

    [Fact]
    public void UnavailableParentDoesNotInventAPackageAndEmptySuccessRemainsEmpty()
    {
        var die = Assert.Single(WindowsCpuOsTopologyReader.ParseDieInformation(Die((0, 3))));
        Assert.Null(die.PackageDeviceIdentity); Assert.Equal(new[] { 0, 1 }, die.LogicalProcessorIds);
        Assert.Empty(WindowsCpuOsTopologyReader.ParseDieInformation([], [], []));
    }

    [Fact]
    public void OriginalProjectionPublishesStaticFieldsAndFailureEmptyRecoveryRemainDistinct()
    {
        var snapshot = SnapshotWithDies();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var die = Assert.Single(current.Devices, static device => device.DeviceIdentity == "windows-cpu-die|2:7");
        var members = Field(die, "cpu_all_die_logical_processor_addresses");
        Assert.Equal("processor-addresses", members.Definition.ValueKind); Assert.Equal("all", members.Definition.Vendor);
        Assert.Equal("windows.processor-die", members.Definition.SourceId); Assert.Equal("group-index[]", members.Definition.Unit);
        Assert.Equal(2, members.Value!.Value[0].GetProperty("processorGroup").GetInt32());
        Assert.Equal(7, members.Value.Value[0].GetProperty("groupRelativeIndex").GetInt32());
        Assert.Equal("windows-cpu-package|0:1,0:3,2:7", Field(die, "cpu_all_die_package_device_identity").Value!.Value.GetString());
        Assert.Equal("2", Count(current).Value!.Value.GetString());
        var failed = CpuTopologyInformationProjection.Create(snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { Dies = null } }, current);
        Assert.Equal(current.Devices.Select(static value => value.DeviceIdentity), failed.Devices.Select(static value => value.DeviceIdentity));
        Assert.All(failed.Devices.SelectMany(static value => value.Fields).Where(static field => field.Definition.SourceId == "windows.processor-die"), static field => Assert.Null(field.Value));
        Assert.NotNull(Field(Assert.Single(failed.Devices, static device => device.ObjectType == "cpu_package"), "cpu_all_package_logical_processor_addresses").Value);
        var empty = CpuTopologyInformationProjection.Create(snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { Dies = [] } }, failed);
        Assert.DoesNotContain(empty.Devices, static device => device.ObjectType == "cpu_die"); Assert.Equal("0", Count(empty).Value!.Value.GetString());
        var recovered = CpuTopologyInformationProjection.Create(snapshot, empty);
        Assert.Equal("2", Count(recovered).Value!.Value.GetString());
        var unknownParent = CpuTopologyInformationProjection.Create(snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { Dies = snapshot.OsTopologyInformation.Dies!.Select(static value => value with { PackageDeviceIdentity = null }).ToArray() } }, recovered);
        Assert.All(unknownParent.Devices.Where(static device => device.ObjectType == "cpu_die"), static device => Assert.Null(Field(device, "cpu_all_die_package_device_identity").Value));
    }

    [Fact]
    public async Task DieFieldsUseOriginalSharedItemCacheAndAtomicSubscription()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var stream = fixture.Reader.SubscribeInformationAsync("die-fields", TimeSpan.FromMilliseconds(100), CancellationToken.None).GetAsyncEnumerator();
        var pending = stream.MoveNextAsync().AsTask();
        Assert.Equal((1u, 1u, 1u, 100ul), fixture.ReadDemand());
        Assert.Same(DeviceInformationSnapshot.Empty, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        await fixture.Reader.StartAsync(CancellationToken.None); _ = await fixture.Sampler.WaitForCaptureAsync();
        var snapshot = SnapshotWithDies(); fixture.Sampler.Complete(snapshot);
        Assert.True(await pending.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(stream.Current, fixture.Reader.GetInformationSnapshot()); Assert.Same(snapshot, fixture.Reader.GetSnapshot());
        Assert.Equal("2", Count(stream.Current).Value!.Value.GetString());
        var failed = stream.MoveNextAsync().AsTask(); _ = await fixture.Sampler.WaitForCaptureAsync();
        fixture.Sampler.Complete(new InvalidOperationException("Fixture capture failure"));
        Assert.True(await failed.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(stream.Current.Devices.SelectMany(static device => device.Fields), static field => Assert.Null(field.Value));
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public void InstalledWindowsDieReadUsesNativeRelationshipsWithoutADriver()
    {
        Assert.True(OperatingSystem.IsWindows() && IntPtr.Size == 8);
        var notes = new List<string>(); var packages = WindowsCpuOsTopologyReader.ReadPackageInformation(null, notes);
        Assert.NotNull(packages);
        var dies = WindowsCpuOsTopologyReader.ReadDieInformation(null, packages, notes);
        Assert.NotNull(dies); Assert.NotEmpty(dies);
        var addresses = dies.SelectMany(static die => die.LogicalProcessorIds).ToArray();
        Assert.Equal(addresses.Length, addresses.Distinct().Count());
        Assert.All(dies, die => Assert.Contains(packages, package => package.DeviceIdentity == die.PackageDeviceIdentity));
        Assert.Empty(notes);
    }

    private static CpuTopologySnapshot SnapshotWithDies()
    {
        var snapshot = CpuTopologyInformationFieldTests.Snapshot();
        IReadOnlyList<CpuOsPackage> packages = [new("windows-cpu-package|0:1,0:3,2:7", [1, 3, 135])];
        var dies = WindowsCpuOsTopologyReader.ParseDieInformation([.. Die((0, 10)), .. Die((2, 128))], snapshot.OsTopologyInformation!.PhysicalCores, packages);
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation with { Packages = packages, Dies = dies } };
    }
    private static DeviceInformationField Count(DeviceInformationSnapshot snapshot) => Field(Assert.Single(snapshot.Devices,
        static device => device.ObjectType == "cpu_topology"), "cpu_all_die_count_os_reported");
    private static DeviceInformationField Field(DeviceInformationObject device, string name) => CpuTopologyInformationFieldTests.Field(device, name);
    private static byte[] Die(params (ushort Group, ulong Mask)[] masks)
    {
        var data = new byte[32 + masks.Length * 16]; U32(data, 0, 5); U32(data, 4, (uint)data.Length); U16(data, 30, (ushort)masks.Length);
        for (var index = 0; index < masks.Length; index++)
        { BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32 + index * 16), masks[index].Mask); U16(data, 40 + index * 16, masks[index].Group); }
        return data;
    }
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}
