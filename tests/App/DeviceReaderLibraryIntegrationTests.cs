using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Hid;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceReaderLibraryIntegrationTests
{
    [Fact]
    public void HidAndSecurityPublicFieldsPreserveEmptyFailureZeroAndConfiguredFalse()
    {
        var hidApi = new HidApi();
        var hid = new WindowsHidInformationReader(hidApi);
        Assert.Equal(0, hidApi.Calls);
        var empty = hid.ReadFields();
        Assert.NotNull(empty.ObservedAt); Assert.Empty(empty.Devices);
        hidApi.Collections = null;
        var failed = hid.ReadFields();
        Assert.Null(failed.ObservedAt); Assert.Empty(failed.Devices);
        var at = DateTimeOffset.UtcNow;
        var declared = WindowsInformationFieldProjection.Hid([new("collection", "pnp", new(0,0), new(0,0,0), [], null)], at);
        var fields = Assert.Single(declared.Devices).Fields;
        Assert.Equal(0, fields.Single(field => field.Definition.Name == "hid_all_usage_declared")
            .Value!.Value.GetProperty("usagePage").GetInt32());
        Assert.Equal(0, fields.Single(field => field.Definition.Name == "hid_all_buttons_capability")
            .Value!.Value.GetArrayLength());
        Assert.Null(fields.Single(field => field.Definition.Name == "hid_all_value_capability").Value);

        var securityApi = new SecurityApi();
        var reader = new WindowsSecurityInformationReader(securityApi);
        Assert.Equal(0, securityApi.Calls);
        var security = reader.ReadFields();
        Assert.Equal(4, security.Devices.Count);
        var machine = security.Devices.Single(device => device.DeviceIdentity == "machine");
        Assert.Equal("0", machine.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.SecurityAllAntivirusStateReported)
            .Value!.Value.GetString());
        var domain = security.Devices.Single(device => device.DeviceIdentity == "windows-firewall-profile|domain");
        Assert.False(domain.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.SecurityAllWindowsFirewallEnabledConfigured)
            .Value!.Value.GetBoolean());
        Assert.Equal(1, securityApi.Disposals);
        Assert.All(WindowsInformationFieldProjection.Security(DeviceTopologySecurityInformation.Unavailable, null)
            .Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
    }

    [Fact]
    public void BatteryNamedReadsKeepUnitsOrderExactMaskAndDirectoryRefreshWithoutImplicitIo()
    {
        var opens = 0; var disposed = 0; var uid = "uid";
        var reader = new WindowsBatteryReader(() => [], _ => { opens++; return new BatteryQuery(uid, () => disposed++); });
        var identity = new BatteryCatalogIdentity(0, "path", "uid", new(0, "LION", 1000, 500, 1));
        Assert.Equal(0, opens);
        var unsupported = reader.ReadFields(identity, ["cpu_all_fake", "battery_all_charge_rate_actual_relative_units_per_hour"]);
        Assert.All(unsupported.Readings, reading => Assert.Equal(FieldObservationStatus.Unsupported, reading.Status));
        Assert.Equal(0, opens);
        string[] names = ["battery_all_charge_state_actual", "battery_all_remaining_capacity_actual_mwh",
            "battery_all_charge_rate_actual_mw", "battery_all_remaining_capacity_actual_mwh"];
        var captured = reader.ReadFields(identity, names);
        Assert.Equal(names, captured.Readings.Select(static reading => reading.Field.FieldName));
        Assert.All(captured.Readings, reading => Assert.Equal(FieldObservationStatus.Current, reading.Status));
        Assert.Equal(MetricInteger.FromUnsigned128(uint.MaxValue), captured.Readings[0].Value!.Integer);
        Assert.Null(captured.Readings[0].Value!.Number);
        Assert.Equal("unsigned64", MetricFieldRegistry.FindByName(names[0])!.ValueKind);
        Assert.Equal(0d, captured.Readings[1].Value!.Number); Assert.Equal(-123d, captured.Readings[2].Value!.Number);
        Assert.Equal(captured.Readings[1], captured.Readings[3]); Assert.Equal(1, opens); Assert.Equal(1, disposed);
        Assert.False(captured.RequiresTopologyRefresh);
        uid = "replacement";
        var stale = reader.ReadFields(identity, names);
        Assert.True(stale.RequiresTopologyRefresh);
        Assert.All(stale.Readings, reading => { Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); });
        Assert.Equal(2, disposed);
        Assert.Equal("battery_device", Assert.Single(WindowsInformationFieldProjection.BatteryDeclarations(
            [new(identity.ExactIdentityKey, null, "serial", "LION")], DateTimeOffset.UtcNow).Devices).ObjectType);
    }

    private sealed class HidApi : IWindowsHidCollectionApi
    {
        internal int Calls; internal IReadOnlyList<WindowsHidCollection>? Collections = [];
        public IReadOnlyList<WindowsHidCollection>? Enumerate() { Calls++; return Collections; }
        public uint? ReadVersion() => throw new InvalidOperationException("Empty enumeration must not query version.");
        public IWindowsHidCollectionSession? Open(WindowsHidCollection collection) => throw new InvalidOperationException();
    }
    private sealed class SecurityApi : IWindowsSecurityInformationApi, IWindowsFirewallPolicyRead
    {
        internal int Calls; internal int Disposals;
        public int GetHealth(uint provider, out uint health) { Calls++; health = 0; return 0; }
        public IWindowsFirewallPolicyRead OpenFirewall() { Calls++; return this; }
        public IReadOnlyList<DeviceTopologySecurityProductInformation> ReadProducts(uint provider) => [];
        public int GetCurrentProfiles(out uint profiles) { profiles = 0; return 0; }
        public int GetEnabled(uint profile, out short enabled) { enabled = 0; return 0; }
        public void Dispose() => Disposals++;
    }
    private sealed class BatteryQuery(string uid, Action dispose) : IWindowsBatteryQuery
    {
        public int LastError => 0;
        public byte[]? Query(uint code, byte[] input, int maximumOutputBytes)
        {
            if (code == WindowsBatteryReader.QueryTag) return BitConverter.GetBytes(7U);
            if (code == WindowsBatteryReader.QueryInformation)
            {
                var level = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
                if (level == 7) return Encoding.Unicode.GetBytes(uid + "\0");
                if (level != 0) throw new InvalidOperationException("Unexpected battery information query.");
                var info = new byte[36]; "LION"u8.CopyTo(info.AsSpan(8));
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12),1000);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16),500); return info;
            }
            Assert.Equal(WindowsBatteryReader.QueryStatus,code);
            var status = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(status,uint.MaxValue);
            BinaryPrimitives.WriteInt32LittleEndian(status.AsSpan(12),-123); return status;
        }
        public void Dispose() => dispose();
    }
}
