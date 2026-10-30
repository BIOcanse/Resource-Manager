using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class BatteryDeclarationContractTests
{
    [Fact]
    public void UnicodeDeclarationParsingIsBoundedTerminatedAndLossless()
    {
        var name = "電池制造商 Ω 🚀";
        Assert.Equal(name, WindowsBatteryReader.ParseString(Encoding.Unicode.GetBytes(name + "\0")));
        Assert.Equal(new string('X', 511), WindowsBatteryReader.ParseString(Encoding.Unicode.GetBytes(new string('X', 511) + "\0")));
        Assert.Null(WindowsBatteryReader.ParseString(Encoding.Unicode.GetBytes(new string('X', 512) + "\0")));
        Assert.Null(WindowsBatteryReader.ParseString(Encoding.Unicode.GetBytes("unterminated")));
        Assert.Null(WindowsBatteryReader.ParseString([0, 0xD8, 0, 0]));
        Assert.Null(WindowsBatteryReader.ParseString([65, 0, 0]));
        Assert.Null(WindowsBatteryReader.ParseString([0, 0]));
        Assert.Equal("A", WindowsBatteryReader.ParseString([65, 0, 0, 0, 0, 0xD8])); // Padding after the terminator is not text.
    }

    [Fact]
    public void ChemistryIsRawFourByteAsciiAndDoesNotGuessUnknownDeclarations()
    {
        Assert.Equal("LION", WindowsBatteryReader.ParseChemistry("LION"u8));
        Assert.Equal("X123", WindowsBatteryReader.ParseChemistry("X123"u8));
        Assert.Equal("Li", WindowsBatteryReader.ParseChemistry([76, 105, 0, 0]));
        Assert.Equal("", WindowsBatteryReader.ParseChemistry([76, 0, 105, 0]));
        Assert.Equal("", WindowsBatteryReader.ParseChemistry([255, 65, 65, 65]));
        Assert.Equal("", WindowsBatteryReader.ParseChemistry([0, 0, 0, 0]));
    }

    [Fact]
    public void DeclarationQueriesUseSameTagAndKeepMissingFieldIndependent()
    {
        var device = new FakeBatteryQuery { Serial = null };
        var reader = new WindowsBatteryReader(() => ["battery-path"], _ => device);
        var declarations = reader.ReadIdentities(includeDeclarations: true);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, declarations.Status);
        var identity = Assert.Single(declarations.Identities);
        Assert.Equal("電池厂商", identity.Manufacturer);
        Assert.Null(identity.SerialNumber);
        Assert.Equal("X123", identity.Information.Chemistry);
        Assert.Equal(BatteryCatalogIdentity.CreateIdentity("battery-path", "unique-uid"), identity.ExactIdentityKey);
        Assert.Equal(new uint[] { 0, 7, 6, 8 }, device.Levels);
        Assert.Equal(2, device.TagReads);
        Assert.True(device.Disposed);
        var numeric = new FakeBatteryQuery();
        new WindowsBatteryReader(() => ["battery-path"], _ => numeric).ReadIdentities();
        Assert.Equal(new uint[] { 0, 7 }, numeric.Levels); // The metric identity path does not query declaration-only text.
    }

    [Fact]
    public void ChangedBatteryTagRejectsOnlyItsOwnDeclarations()
    {
        var changed = new FakeBatteryQuery { ChangeTag = true };
        var zone = new WindowsBatteryMonitoringZone(new(() => ["battery-path"], _ => changed));
        Assert.Empty(WindowsDeviceTopologyReader.ReadBatteryDeclarations(zone)!);
        zone = new(new WindowsBatteryReader(() => ["a-changing", "b-healthy"],
            path => new FakeBatteryQuery { ChangeTag = path == "a-changing" }));
        var healthy = Assert.Single(WindowsDeviceTopologyReader.ReadBatteryDeclarations(zone)!);
        Assert.Equal(BatteryCatalogIdentity.CreateIdentity("b-healthy", "unique-uid"), healthy.DeviceIdentity);
        Assert.Equal("電池厂商", healthy.Manufacturer);
        var calls = 0;
        zone = new(new WindowsBatteryReader(() => { calls++; return []; }, _ => null));
        Assert.Empty(WindowsDeviceTopologyReader.ReadBatteryDeclarations(zone)!);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ProjectionUsesExplicitDeclarationsIdentityAndIndependentEmptyValues()
    {
        var device = new FakeBatteryQuery { Serial = null };
        var zone = new WindowsBatteryMonitoringZone(new(() => ["battery-path"], _ => device));
        var snapshot = DeviceInformationTestData.Snapshot() with { Batteries = WindowsDeviceTopologyReader.ReadBatteryDeclarations(zone) };
        var fields = DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow);
        var battery = fields.Devices.Single(d => d.ObjectType == "battery_device");
        Assert.Equal(BatteryCatalogIdentity.CreateIdentity("battery-path", "unique-uid"), battery.DeviceIdentity);
        Assert.Equal(3, battery.Fields.Count);
        Assert.Equal("電池厂商", battery.Fields.Single(f => f.Definition.Name == "battery_all_manufacturer_declared").Value!.Value.GetString());
        Assert.Null(battery.Fields.Single(f => f.Definition.Name == "battery_all_serial_declared").Value);
        Assert.Equal("X123", battery.Fields.Single(f => f.Definition.Name == "battery_all_chemistry_declared").Value!.Value.GetString());
        Assert.All(battery.Fields, field => Assert.Equal("windows.battery", field.Definition.SourceId));
        Assert.All(DeviceTopologyInformationProjection.Create(snapshot, null).Devices.SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        var duplicated = snapshot with { Batteries = [snapshot.Batteries![0], snapshot.Batteries[0]] };
        Assert.DoesNotContain(DeviceTopologyInformationProjection.Create(duplicated, DateTimeOffset.UtcNow).Devices, d => d.ObjectType == "battery_device");
    }

    private sealed class FakeBatteryQuery : IWindowsBatteryQuery
    {
        internal string? Serial { get; init; } = "declared-serial";
        internal bool ChangeTag { get; init; }
        internal int TagReads { get; private set; }
        internal List<uint> Levels { get; } = [];
        internal bool Disposed { get; private set; }
        public int LastError => 0;
        public byte[]? Query(uint code, byte[] input, int maximumOutputBytes)
        {
            Assert.DoesNotContain(code, new uint[] { 0x294048, WindowsBatteryReader.QueryStatus });
            if (code == WindowsBatteryReader.QueryTag)
            {
                TagReads++;
                Assert.Equal(new byte[4], input);
                return BitConverter.GetBytes(ChangeTag && TagReads > 1 ? 8U : 7U);
            }
            Assert.Equal(WindowsBatteryReader.QueryInformation, code);
            Assert.Equal(7U, BinaryPrimitives.ReadUInt32LittleEndian(input));
            Assert.Equal(new byte[4], input[8..]);
            var level = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
            Levels.Add(level);
            if (level == 0)
            {
                Assert.Equal(36, maximumOutputBytes);
                var info = new byte[36]; "X123"u8.CopyTo(info.AsSpan(8)); return info;
            }
            Assert.Equal(1024, maximumOutputBytes);
            return level switch
            {
                7 => Encoding.Unicode.GetBytes("unique-uid\0"),
                6 => Encoding.Unicode.GetBytes("電池厂商\0"),
                8 => Serial is null ? null : Encoding.Unicode.GetBytes(Serial + "\0"),
                _ => throw new InvalidOperationException("Unexpected query.")
            };
        }
        public void Dispose() => Disposed = true;
    }
}
