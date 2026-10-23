using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace Resource_Manager_APP.Tests;

public sealed class AmdCcdTemperatureTests
{
    [Theory]
    [InlineData(0x17, 0x01, 0x59954, 4)]
    [InlineData(0x17, 0x71, 0x59954, 8)]
    [InlineData(0x17, 0xAF, 0x59B00, 8)]
    [InlineData(0x19, 0x61, 0x59B08, 8)]
    [InlineData(0x19, 0x1F, 0x59B00, 12)]
    [InlineData(0x1A, 0x1F, 0x599F0, 16)]
    [InlineData(0x1A, 0x40, 0x59B08, 8)]
    public void LayoutUsesAnExplicitFamilyModelAndBoundedSlots(int family, int model, int address, int count)
    {
        var format = AmdCcdTemperatureFormat.Resolve((uint)family, (uint)model)!;
        Assert.Equal((uint)address, format.RegisterAddress(0));
        Assert.Equal((uint)(address + 4 * (count - 1)), format.RegisterAddress(count - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => format.RegisterAddress(count));
        Assert.Throws<ArgumentOutOfRangeException>(() => format.RegisterAddress(-1));
    }

    [Theory]
    [InlineData(0x800, -49)]
    [InlineData(0x988, 0)]
    [InlineData(0xB20, 51)]
    [InlineData(0xFFFF, 206.875)]
    public void DecodePreservesZeroNegativeAndOnlyTheDeclaredDataBits(uint raw, double expected)
        => Assert.Equal(expected, AmdCcdTemperatureFormat.Decode(raw));

    [Theory]
    [InlineData(0U)]
    [InlineData(0x7FFU)]
    [InlineData(uint.MaxValue)]
    public void InvalidOrBusSentinelHasNoValue(uint raw) => Assert.Null(AmdCcdTemperatureFormat.Decode(raw));

    [Fact]
    public void SparseDiscoveryOwnsOneSessionAndRejectsUndiscoveredOrStaleTargets()
    {
        var session = new RecordedSession(); var opens = 0;
        using var resource = new AmdCcdMetricResource(target => new(target.Family, target.Model, 1,
            () => { opens++; return session; }));
        Assert.Equal(0, opens);
        var found = resource.Discover(Topology(), 16);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, found.Status);
        Assert.Equal(new[] { 0, 3 }, found.Identities.Select(identity => identity.SensorIndex));
        Assert.Equal(8, session.Reads);
        var slot = found.Identities[1];
        Assert.Equal("cpu_die", slot.Device.Kind); Assert.Equal(3, slot.Device.Ordinal);
        Assert.Equal("package-opaque", slot.Device.Address!.PackageId);
        Assert.Equal("amd.smn:00059B14", slot.Device.Address.SensorIdentifier);
        Assert.Empty(slot.Device.Address.ProcessorAddresses!);
        Assert.Equal(51, resource.Read(slot));
        var reads = session.Reads;
        Assert.Throws<InvalidOperationException>(() => resource.Read(slot with { SensorIndex = 1, SmnAddress = 0x59B0C }));
        Assert.Equal(reads, session.Reads);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, resource.Discover(null, 16).Status);
        Assert.Equal(1, session.Disposals);
        Assert.Throws<InvalidOperationException>(() => resource.Read(slot));
        Assert.Equal(reads, session.Reads);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void DisabledUnknownIncompleteAndMultiplePackagesDoNotOpenHardware()
    {
        var opens = 0;
        using var resource = new AmdCcdMetricResource(_ => { opens++; throw new Exception("Must not create a reader"); });
        Assert.Empty(resource.Discover(Topology(), 0).Identities);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            resource.Discover(Topology(model: 0x80), 16).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            resource.Discover(Topology() with { Packages = [new("p0", [0]), new("p1", [1])] }, 16).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            resource.Discover(Topology() with { ThreadCpuid = [Topology().ThreadCpuid![0]] }, 16).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete,
            resource.Discover(Topology(vendor: "GenuineIntel"), 16).Status);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void ReadFailureDisposesSessionAndDoesNotReturnThePreviousTemperature()
    {
        var first = new RecordedSession(); var second = new RecordedSession(); var opens = 0;
        using var reader = new AmdCcdTemperatureReader(0x19, 0x61, 1,
            () => ++opens == 1 ? first : second);
        Assert.Equal(51, reader.Read(0).TemperatureCelsius);
        first.Fail = true;
        Assert.Throws<IOException>(() => reader.Read(0));
        Assert.Equal(1, first.Disposals);
        second.Values[0x59B08] = 0;
        Assert.Null(reader.Read(0).TemperatureCelsius);
        reader.Dispose(); reader.Dispose();
        Assert.Equal(1, second.Disposals);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(0));
    }

    [Fact]
    public void LargeStructuredPackageKeepsTheNativeIdentityBoundedAndStable()
    {
        var target = new AmdCcdCpuTarget("package-opaque", 0x19, 0x61, 0xA60F12,
            Enumerable.Range(0, 512).Select(id => new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray());
        var identity = new AmdCcdCatalogIdentity(target, 0, 0x59B08);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(identity.ExactIdentityKey) < 128);
        Assert.Equal(identity.ExactIdentityKey, (identity with { Target = target with { } }).ExactIdentityKey);
        Assert.NotEqual(identity.ExactIdentityKey, (identity with { Target = target with { PackageId = "another" } }).ExactIdentityKey);
    }

    private static CpuOsTopologyInformation Topology(int model = 0x61, string vendor = "AuthenticAMD")
    {
        var data = new CpuThreadCpuidData(new(vendor, 0x19, model, 1, "recorded AMD", [], []),
            0xA60F12, null, null, null, null, null, []);
        return new([], []) { Packages = [new("package-opaque", [0, 1])], ThreadCpuid = [new(0, data), new(1, data)] };
    }

    private sealed class RecordedSession : IAmdSmnSession
    {
        internal Dictionary<uint, uint> Values { get; } = new() { [0x59B08] = 0xB20, [0x59B14] = 0xB20 };
        internal int Reads, Disposals;
        internal bool Fail;
        public uint ReadRegister(uint address) { Reads++; if (Fail) throw new IOException("recorded failure"); return Values.GetValueOrDefault(address); }
        public void Dispose() => Disposals++;
    }
}
