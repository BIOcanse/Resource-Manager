using System.Buffers.Binary;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuPackageInformationTests
{
    [Fact]
    public void OnePackageSpansGroupsAndIdentityDoesNotDependOnMaskOrder()
    {
        var first = Assert.Single(WindowsCpuOsTopologyReader.ParsePackageInformation(Package((2, 128), (0, 10))));
        var second = Assert.Single(WindowsCpuOsTopologyReader.ParsePackageInformation(Package((0, 10), (2, 128))));
        Assert.Equal("windows-cpu-package|0:1,0:3,2:7", first.DeviceIdentity);
        Assert.Equal(first.DeviceIdentity, second.DeviceIdentity);
        Assert.Equal(new[] { 1, 3, 135 }, first.LogicalProcessorIds);
    }

    [Fact]
    public void PackagesRemainSeparateEvenWhenSharingAnOsGroup()
    {
        var first = Package((0, 10)); var second = Package((0, 128));
        var packages = WindowsCpuOsTopologyReader.ParsePackageInformation([.. second, .. first]);
        Assert.Equal(new[] { "windows-cpu-package|0:1,0:3", "windows-cpu-package|0:7" },
            packages.Select(static package => package.DeviceIdentity));
        Assert.Equal(packages.Select(static package => package.DeviceIdentity),
            WindowsCpuOsTopologyReader.ParsePackageInformation([.. first, .. second])
                .Select(static package => package.DeviceIdentity));
    }

    [Fact]
    public void UnsignedGroupAndHighestMaskBitArePreserved()
    {
        var package = Assert.Single(WindowsCpuOsTopologyReader.ParsePackageInformation(Package((ushort.MaxValue, 1UL << 63))));
        Assert.Equal("windows-cpu-package|65535:63", package.DeviceIdentity);
        Assert.Equal(ushort.MaxValue * 64 + 63, Assert.Single(package.LogicalProcessorIds));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("length")]
    [InlineData("relationship")]
    [InlineData("groups")]
    [InlineData("empty-mask")]
    [InlineData("tail")]
    public void BadRecordDoesNotReturnAPartialPackage(string defect)
    {
        var bytes = Package((0, 1));
        switch (defect)
        {
            case "header": bytes = [1, 2, 3, 4]; break;
            case "length": U32(bytes, 4, 49); break;
            case "relationship": U32(bytes, 0, 5); break;
            case "groups": U16(bytes, 30, 0); break;
            case "empty-mask": U64(bytes, 32, 0); break;
            case "tail": bytes = [.. bytes, 1, 2, 3]; break;
        }
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePackageInformation(bytes));
    }

    [Fact]
    public void RepeatedGroupsAndOverlappingPackagesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePackageInformation(Package((2, 1), (2, 2))));
        var first = Package((0, 3)); var second = Package((0, 2));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePackageInformation([.. first, .. second]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("split-core")]
    [InlineData("duplicate-core")]
    public void SourcesAgreeOnAddressesAndAWholeCoreBelongsToOnePackage(string defect)
    {
        IReadOnlyList<CpuOsPhysicalCore> cores = [new("core:0", 0, true, [1, 3]), new("core:1", 0, false, [135])];
        var bytes = Package((0, 10), (2, 128));
        switch (defect)
        {
            case "missing": bytes = Package((0, 10)); break;
            case "extra": bytes = Package((0, 10), (2, 384)); break;
            case "split-core": bytes = [.. Package((0, 2)), .. Package((0, 8), (2, 128))]; break;
            case "duplicate-core": cores = [.. cores, new("core:2", 0, false, [135])]; break;
        }
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePackageInformation(bytes, cores));
    }

    [Fact]
    public void UnavailableCoresAreIndependentAndEmptySuccessIsEmpty()
    {
        Assert.Single(WindowsCpuOsTopologyReader.ParsePackageInformation(Package((0, 1)), null));
        Assert.Empty(WindowsCpuOsTopologyReader.ParsePackageInformation([], []));
    }

    [Fact]
    public void ProjectionUsesRealPackageIdentityAndGroupAddressesWithStaticMetadata()
    {
        var snapshot = SnapshotWithPackages();
        var fields = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var aggregate = Assert.Single(fields.Devices, device => device.ObjectType == "cpu_topology");
        var count = Field(aggregate, "cpu_all_package_count_reported");
        Assert.Equal("2", count.Value!.Value.GetString()); Assert.Equal("unsigned-integer", count.Definition.ValueKind);
        Assert.Equal("count", count.Definition.Unit); Assert.Equal("windows.processor-package", count.Definition.SourceId);
        Assert.Equal("all", count.Definition.Vendor);
        Assert.Equal("2", Field(aggregate, "cpu_all_physical_core_count_reported").Value!.Value.GetString());
        Assert.Equal(aggregate.Fields.Count, aggregate.Fields.Select(static field => field.Definition.Name).Distinct().Count());
        var package = Assert.Single(fields.Devices, device => device.DeviceIdentity == "windows-cpu-package|2:7");
        var members = Field(package, "cpu_all_package_logical_processor_addresses");
        Assert.Equal("processor-addresses", members.Definition.ValueKind); Assert.Equal("group-index[]", members.Definition.Unit);
        Assert.Equal(2, members.Value!.Value[0].GetProperty("processorGroup").GetInt32());
        Assert.Equal(7, members.Value.Value[0].GetProperty("groupRelativeIndex").GetInt32());
        Assert.Equal(snapshot.CapturedAt, fields.ObservedAt);
    }

    [Fact]
    public void FailedPackagesClearValuesAndCountKeepOtherSourcesThenRecover()
    {
        var snapshot = SnapshotWithPackages();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = CpuTopologyInformationProjection.Create(snapshot with
            { OsTopologyInformation = snapshot.OsTopologyInformation! with { Packages = null } }, current);
        Assert.Equal(current.Devices.Select(static device => device.DeviceIdentity), failed.Devices.Select(static device => device.DeviceIdentity));
        foreach (var field in failed.Devices.SelectMany(static device => device.Fields))
            if (field.Definition.SourceId == "windows.processor-package") Assert.Null(field.Value);
            else Assert.NotNull(field.Value);
        Assert.NotNull(Field(Assert.Single(current.Devices, device => device.ObjectType == "cpu_topology"), "cpu_all_package_count_reported").Value);
        var recovered = CpuTopologyInformationProjection.Create(snapshot, failed);
        Assert.All(recovered.Devices.SelectMany(static device => device.Fields), field => Assert.NotNull(field.Value));
    }

    [Fact]
    public void FailedCoreReadKeepsSuccessfulPackagesAndCount()
    {
        var snapshot = SnapshotWithPackages();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = CpuTopologyInformationProjection.Create(snapshot with
            { OsTopologyInformation = snapshot.OsTopologyInformation! with { PhysicalCores = null } }, current);
        var aggregate = Assert.Single(failed.Devices, device => device.ObjectType == "cpu_topology");
        Assert.Equal("2", Field(aggregate, "cpu_all_package_count_reported").Value!.Value.GetString());
        Assert.Null(Field(aggregate, "cpu_all_physical_core_count_reported").Value);
        Assert.All(failed.Devices.Where(static device => device.ObjectType == "cpu_package").SelectMany(static device => device.Fields),
            field => Assert.NotNull(field.Value));
    }

    [Fact]
    public void EmptyPackagesRemoveOldObjectsAndReportZeroWithoutLegacyFallback()
    {
        var snapshot = SnapshotWithPackages();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var empty = CpuTopologyInformationProjection.Create(snapshot with
            { OsTopologyInformation = snapshot.OsTopologyInformation! with { Packages = [] } }, current);
        Assert.DoesNotContain(empty.Devices, device => device.ObjectType == "cpu_package");
        Assert.Equal("0", Field(Assert.Single(empty.Devices, device => device.ObjectType == "cpu_topology"), "cpu_all_package_count_reported").Value!.Value.GetString());
        var unavailable = CpuTopologyInformationProjection.Create(CpuTopologyInformationFieldTests.Snapshot(), DeviceInformationSnapshot.Empty);
        Assert.DoesNotContain(unavailable.Devices, device => device.ObjectType == "cpu_package");
        Assert.DoesNotContain(unavailable.Devices.SelectMany(static device => device.Fields), field => field.Definition.Name == "cpu_all_package_count_reported");
    }

    [Fact]
    public void PackageOnlySuccessPublishesWithoutCoreAndCacheDeclarations()
    {
        var snapshot = SnapshotWithPackages();
        var fields = CpuTopologyInformationProjection.Create(snapshot with
            { OsTopologyInformation = snapshot.OsTopologyInformation! with { PhysicalCores = null, Caches = null } }, DeviceInformationSnapshot.Empty);
        Assert.Equal(3, fields.Devices.Count);
        Assert.Equal("cpu_all_package_count_reported", Assert.Single(Assert.Single(fields.Devices, device => device.ObjectType == "cpu_topology").Fields).Definition.Name);
    }

    [Fact]
    public async Task PackagesUseOriginalAtomicCacheAndDemandAndClearOnWholeFailure()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var stream = fixture.Reader.SubscribeInformationAsync("package-fields", TimeSpan.FromMilliseconds(100), CancellationToken.None).GetAsyncEnumerator();
        var pending = stream.MoveNextAsync().AsTask();
        Assert.Equal((1u, 1u, 1u, 100ul), fixture.ReadDemand());
        Assert.Same(DeviceInformationSnapshot.Empty, fixture.Reader.GetInformationSnapshot());
        await fixture.Reader.StartAsync(CancellationToken.None); _ = await fixture.Sampler.WaitForCaptureAsync();
        var snapshot = SnapshotWithPackages(); fixture.Sampler.Complete(snapshot);
        Assert.True(await pending.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(stream.Current, fixture.Reader.GetInformationSnapshot()); Assert.Same(snapshot, fixture.Reader.GetSnapshot());
        Assert.Equal(2, stream.Current.Devices.Count(static device => device.ObjectType == "cpu_package"));
        var failed = stream.MoveNextAsync().AsTask();
        _ = await fixture.Sampler.WaitForCaptureAsync(); fixture.Sampler.Complete(new InvalidOperationException("capture failed"));
        Assert.True(await failed.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(stream.Current.Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public void InstalledWindowsReadsNonoverlappingPackagesWithoutLoadingADriver()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return;
        var notes = new List<string>(); var packages = WindowsCpuOsTopologyReader.ReadPackageInformation(null, notes);
        Assert.NotNull(packages); Assert.NotEmpty(packages);
        var addresses = packages.SelectMany(static package => package.LogicalProcessorIds).ToArray();
        Assert.NotEmpty(addresses); Assert.Equal(addresses.Length, addresses.Distinct().Count());
        Assert.All(packages, package => Assert.StartsWith("windows-cpu-package|", package.DeviceIdentity)); Assert.Empty(notes);
    }

    internal static CpuTopologySnapshot SnapshotWithPackages(CpuTopologySnapshot? snapshot = null)
    {
        snapshot ??= CpuTopologyInformationFieldTests.Snapshot();
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { Packages = WindowsCpuOsTopologyReader.ParsePackageInformation([.. Package((0, 10)), .. Package((2, 128))], snapshot.OsTopologyInformation.PhysicalCores) } };
    }
    private static DeviceInformationField Field(DeviceInformationObject device, string name) => CpuTopologyInformationFieldTests.Field(device, name);
    private static byte[] Package(params (ushort Group, ulong Mask)[] masks)
    {
        var data = new byte[32 + masks.Length * 16]; U32(data, 0, 3); U32(data, 4, (uint)data.Length); U16(data, 30, (ushort)masks.Length);
        for (var index = 0; index < masks.Length; index++)
        { U64(data, 32 + index * 16, masks[index].Mask); U16(data, 40 + index * 16, masks[index].Group); }
        return data;
    }
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
}
