using System.Buffers.Binary;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace Resource_Manager_APP.Tests;

public sealed class CpuFirmwareReaderLibraryIntegrationTests
{
    [Fact]
    public void AcpiConstructionDoesNotReadAndPartialContentFailureKeepsKnownInventory()
    {
        var api = new FirmwareApi();
        var diagnostics = new List<string>();
        var reader = new WindowsAcpiFirmwareTableReader(api, (message, _) => diagnostics.Add(message));
        Assert.Equal(0, api.EnumerationCalls);
        Assert.Equal(0, api.TableCalls);

        var fields = reader.ReadFields();
        Assert.NotNull(fields.ObservedAt);
        var table = Assert.Single(fields.Devices);
        Assert.Equal("FACP", table.Fields.Single(field => field.Definition == F.FirmwareAllAcpiTableSignature).Value!.Value.GetString());
        Assert.Equal("1", table.Fields.Single(field => field.Definition == F.FirmwareAllAcpiSignatureInstanceCountReported).Value!.Value.GetString());
        Assert.Null(table.Fields.Single(field => field.Definition == F.FirmwareAllAcpiTableRaw).Value);
        Assert.Equal(2, api.EnumerationCalls);
        Assert.Equal(1, api.TableCalls);
        Assert.NotEmpty(diagnostics);

        api.FailEnumeration = true;
        var unavailable = reader.ReadFields();
        Assert.Null(unavailable.ObservedAt);
        Assert.Empty(unavailable.Devices);
    }

    [Fact]
    public void CpuDirectProjectionPreservesVendorDomainsExactMasksAndSuccessfulEmptyFeatures()
    {
        var now = DateTimeOffset.UtcNow;
        var intel = new ResourceManager.ReadWrite.Windows.Cpu.Models.CpuThreadCpuidData(
            new("GenuineIntel", 6, 158, 10, "Fixture", ["AVX2"], []), 0x906EA, 1, 1, null, null, null, [])
            { IntelSupportedXssStateMask = ulong.MaxValue, SupportedXcr0StateMask = ulong.MaxValue, IntelVmxSupported = false };
        var amd = new ResourceManager.ReadWrite.Windows.Cpu.Models.CpuThreadCpuidData(
            new("AuthenticAMD", 25, 33, 2, "Fixture", [], []), 0xA20F12, 2, null, null, null, 2, [])
            { AmdSupportedXssStateMask = uint.MaxValue, AmdSvmAsidCountDeclared = uint.MaxValue, AmdSvmSupported = true };
        var frame = CpuInformationFieldProjection.Cpuid([new(0, intel), new(64, amd)], now);
        var intelDevice = frame.Devices.Single(device => device.DeviceIdentity == "windows-cpu-thread|0:0");
        var amdDevice = frame.Devices.Single(device => device.DeviceIdentity == "windows-cpu-thread|1:0");
        Assert.Equal(ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            intelDevice.Fields.Single(field => field.Definition == F.CpuIntelSupportedXssStateMask).Value!.Value.GetString());
        Assert.False(intelDevice.Fields.Single(field => field.Definition == F.CpuIntelVmxSupported).Value!.Value.GetBoolean());
        Assert.DoesNotContain(intelDevice.Fields, field => field.Definition == F.CpuAmdSvmSupported);
        Assert.DoesNotContain(amdDevice.Fields, field => field.Definition == F.CpuIntelVmxSupported);
        Assert.Equal(uint.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            amdDevice.Fields.Single(field => field.Definition == F.CpuAmdSvmAsidCountDeclared).Value!.Value.GetString());
        Assert.All(CpuInformationFieldProjection.Cpuid([new(0, intel)], null).Devices.Single().Fields,
            field => Assert.Null(field.Value));

        var os = CpuInformationFieldProjection.OsFeatures([], 0, now).Devices.Single();
        Assert.Equal(0, os.Fields.Single(field => field.Definition == F.CpuAllOsUsableFeature).Value!.Value.GetArrayLength());
        Assert.Equal("0", os.Fields.Single(field => field.Definition == F.CpuAllOsEnabledXStateMask).Value!.Value.GetString());
    }

    private sealed class FirmwareApi : IWindowsAcpiFirmwareApi
    {
        public int EnumerationCalls { get; private set; }
        public int TableCalls { get; private set; }
        public bool FailEnumeration { get; set; }
        public WindowsFirmwareCallResult Enumerate(uint provider, byte[]? buffer)
        {
            EnumerationCalls++;
            if (FailEnumeration) return new(0, 5);
            if (buffer is not null) BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x50434146);
            return new(4, 0);
        }
        public WindowsFirmwareCallResult GetTable(uint provider, uint id, byte[]? buffer)
        { TableCalls++; return new(0, 5); }
    }
}
