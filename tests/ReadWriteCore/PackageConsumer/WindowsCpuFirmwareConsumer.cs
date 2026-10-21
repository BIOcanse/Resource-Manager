using System.Buffers.Binary;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

internal static class WindowsCpuFirmwareConsumer
{
    public static void Run()
    {
        var assembly = typeof(ResourceManager.ReadWrite.ReadWriteCore).Assembly;
        Require(typeof(CpuIdentityInformationReader).Assembly == assembly
            && typeof(WindowsCpuThreadCpuidReader).Assembly == assembly
            && typeof(WindowsAcpiFirmwareTableReader).Assembly == assembly
            && typeof(SmbiosFirmwareInformationReader).Assembly == assembly, "actual library ownership");
        var raw = new byte[14]; raw[1] = 3; raw[2] = 5;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 6);
        raw[8] = 127; raw[9] = 4; raw[10] = 0xff; raw[11] = 0xff;
        var empty = SmbiosFirmwareInformationReader.Parse(raw);
        Require(empty is not null && empty.Modules.Count == 0 && empty.Declarations.Slots.Count == 0,
            "valid empty SMBIOS declaration table");
        Require(SmbiosFirmwareInformationReader.Parse(raw.AsSpan(0, 13).ToArray()) is null,
            "truncated SMBIOS table is unavailable");

        var module = new FirmwareMemoryModule("fixture-dimm", ulong.MaxValue, "DIMM0", "Fixture", "PART", "SERIAL", "DDR5", 6400, 5600, 72, 64, 1.1);
        var firmware = WindowsInformationFieldProjection.Firmware(new([module], new(null, [])), DateTimeOffset.UtcNow);
        var dimm = firmware.Devices.Single(device => device.ObjectType == "memory_dimm");
        Require(dimm.Fields.Single(field => field.Definition == F.MemoryAllDimmCapacity).Value!.Value.GetString()
            == ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "exact DIMM capacity declaration");
        Require(dimm.Fields.All(field => field.Definition.Name.StartsWith("memory_all_", StringComparison.Ordinal)),
            "canonical module fields");
        Require(WindowsInformationFieldProjection.Firmware(new([module], new(null, [])), null).Devices
            .SelectMany(static device => device.Fields).All(static field => field.Value is null), "failed firmware values clear");

        var acpiRaw = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(acpiRaw, 0x50434146);
        BinaryPrimitives.WriteUInt32LittleEndian(acpiRaw.AsSpan(4), 36); acpiRaw[8] = 6;
        acpiRaw[9] = unchecked((byte)-acpiRaw.Sum(static value => (int)value));
        var table = WindowsAcpiFirmwareTableReader.ParseTable(0x50434146, acpiRaw);
        Require(table is not null && table.Raw.Length == 36 && table.Revision == 6, "actual ACPI parser checksum and header");
        acpiRaw[0] = 0;
        Require(table!.Raw[0] == (byte)'F', "ACPI parser owns captured bytes");

        var identity = CpuIdentityInformationReader.Read([]);
        Require(identity is not null && !string.IsNullOrWhiteSpace(identity.Vendor), "actual current CPU identity");
        var notes = new List<string>();
        var cpuid = WindowsCpuThreadCpuidReader.ReadFields([0], notes);
        Require(cpuid.Devices.Count == 1 && cpuid.Devices[0].DeviceIdentity == "windows-cpu-thread|0:0", "explicit CPUID processor scope");
        var os = WindowsCpuOsFeatureReader.ReadFields();
        var acpi = new WindowsAcpiFirmwareTableReader().ReadFields();
        var smbios = SmbiosFirmwareInformationReader.ReadFields();
        Console.WriteLine($"ReadWriteCore CPU/firmware consumer passed: CPU {identity!.Vendor}, CPUID available {cpuid.ObservedAt.HasValue}, OS available {os.ObservedAt.HasValue}, ACPI tables {acpi.Devices.Count}, SMBIOS objects {smbios.Devices.Count}; no product/subscription dependency.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
