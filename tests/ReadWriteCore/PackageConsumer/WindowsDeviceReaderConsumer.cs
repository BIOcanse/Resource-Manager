using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Devices.Hid;
using ResourceManager.ReadWrite.Windows.Devices.Models;

static class WindowsDeviceReaderConsumer
{
    public static void Run()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(WindowsHidInformationReader), typeof(WindowsSecurityInformationReader),
            typeof(WindowsBatteryReader), typeof(BatteryCatalogIdentity), typeof(NativeMetricSnapshotCatalogTopologyStatus) }
            .All(type => type.Assembly == owner), "device readers and catalog status belong to the independent library");
        var at = DateTimeOffset.UtcNow;
        var declared = WindowsInformationFieldProjection.Hid(
            [new("recorded-hid", "pnp", new(0, 0), new(0, 0, 0), [], null)], at).Devices.Single();
        Require(declared.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.HidAllUsageDeclared)
            .Value!.Value.GetProperty("usagePage").GetInt32() == 0, "HID declared zero");
        Require(declared.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.HidAllButtonsCapability)
            .Value!.Value.GetArrayLength() == 0, "empty HID capability list is available");
        Require(declared.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.HidAllValueCapability)
            .Value is null, "unavailable HID value capabilities stay null");
        var recordedSecurity = WindowsInformationFieldProjection.Security(new(0, null, 0, false, null, true), at);
        Require(recordedSecurity.Devices.Single(device => device.DeviceIdentity == "machine").Fields
            .Single(field => field.Definition == DeviceInformationFieldRegistry.SecurityAllAntivirusStateReported)
            .Value!.Value.GetString() == "0", "WSC zero is a reported state");
        Require(!recordedSecurity.Devices.Single(device => device.DeviceIdentity == "windows-firewall-profile|domain")
            .Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.SecurityAllWindowsFirewallEnabledConfigured)
            .Value!.Value.GetBoolean(), "configured false is not unavailable");
        Require(WindowsInformationFieldProjection.Security(DeviceTopologySecurityInformation.Unavailable, null)
            .Devices.SelectMany(device => device.Fields).All(field => field.Value is null), "failed security frame clears values");

        var battery = new WindowsBatteryReader();
        var identity = new BatteryCatalogIdentity(0, "recorded-path", "uid", new(0, "LION", 1000, 500, 1));
        var relative = identity with { Information = identity.Information with { Capabilities = 0x40000000 } };
        Require(WindowsBatteryReader.AppliesTo(MetricFieldCode.BatteryRemainingCapacityAbsolute, false)
            && !WindowsBatteryReader.AppliesTo(MetricFieldCode.BatteryRemainingCapacityAbsolute, relative.Information.IsRelative),
            "absolute and relative battery units are distinct");
        var unsupported = battery.ReadFields(identity, ["cpu_all_fake", "battery_all_remaining_capacity_actual_relative_units"]);
        Require(unsupported.Readings.All(reading => reading.Status == FieldObservationStatus.Unsupported
            && reading.Value is null && reading.ObservedAt is null) && !unsupported.RequiresTopologyRefresh,
            "unsupported fields do not probe a recorded device");
        Require(MetricFieldRegistry.FindByName("battery_all_charge_state_actual")!.ValueKind == "unsigned64",
            "battery state uses the exact unsigned field contract");
        Require(WindowsInformationFieldProjection.BatteryDeclarations(
            [new(identity.ExactIdentityKey, null, "serial", "LION")], at).Devices.Single().ObjectType == "battery_device",
            "battery declaration uses the original device type");

        var hid = new WindowsHidInformationReader().ReadFields();
        Require(hid.Devices.SelectMany(device => device.Fields).All(field => field.Definition.Name.StartsWith("hid_", StringComparison.Ordinal)),
            "actual HID frame uses registered fields");
        var security = new WindowsSecurityInformationReader().ReadFields();
        Require(security.Devices.Count == 4, "actual security frame separates machine reports and three policy profiles");
        var securityValues = security.Devices.SelectMany(device => device.Fields).Count(field => field.Value is not null);
        var catalog = battery.ReadIdentities();
        var declarations = battery.ReadDeclarationFields();
        var batteryCurrentValues = 0;
        foreach (var device in catalog.Identities)
        {
            string[] names = ["battery_all_charge_state_actual", device.Information.IsRelative
                ? "battery_all_remaining_capacity_actual_relative_units" : "battery_all_remaining_capacity_actual_mwh"];
            var read = battery.ReadFields(device, names);
            batteryCurrentValues += read.Readings.Count(reading => reading.Status == FieldObservationStatus.Current);
            Require(read.DeviceIdentity == device.ExactIdentityKey && read.Readings.Select(reading => reading.Field.FieldName).SequenceEqual(names),
                "actual battery reads keep exact identity and requested order");
        }
        Console.WriteLine($"ReadWriteCore device reader consumer passed: HID collections {hid.Devices.Count}, HID capture available {hid.ObservedAt is not null}, security objects {security.Devices.Count}, security values {securityValues}, battery catalog {catalog.Status}, batteries {catalog.Identities.Count}, battery declaration capture available {declarations.ObservedAt is not null}, battery current values {batteryCurrentValues}; recorded zero/false/empty/null and unit contracts passed, absent hardware is not certified.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
