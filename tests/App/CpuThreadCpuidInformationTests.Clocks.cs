using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Theory]
    [InlineData("GenuineIntel", 0x14U, false, false)]
    [InlineData("GenuineIntel", 0x15U, true, false)]
    [InlineData("GenuineIntel", 0x16U, true, true)]
    [InlineData("GenuineIntel", 0x80000000U, false, false)]
    [InlineData("GenuineIntel", 0xFFFFFFFFU, false, false)]
    [InlineData("AuthenticAMD", 0x1FU, false, false)]
    [InlineData("CentaurHauls", 0x1FU, false, false)]
    public void ClockDeclarationReadsUseExplicitIntelAndBasicLeafGates(string vendor, uint maximum, bool crystal, bool specification)
    {
        var registers = ClockRegisters(vendor, maximum);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Equal(crystal, registers.Calls.ContainsKey((0x15, 0)));
        Assert.Equal(specification, registers.Calls.ContainsKey((0x16, 0)));
        Assert.Equal(crystal ? 24000000U : (uint?)null, data.IntelNominalCoreCrystalFrequencyDeclaredHz);
        Assert.Equal(specification ? (ushort)2400 : (ushort?)null, data.IntelProcessorBaseFrequencyDeclaredMhz);
        Assert.Equal(specification ? (ushort)5200 : (ushort?)null, data.IntelProcessorMaximumFrequencyDeclaredMhz);
        Assert.Equal(specification ? (ushort)100 : (ushort?)null, data.IntelBusReferenceFrequencyDeclaredMhz);
        Assert.All(registers.Calls.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(0, 5200, 100, null, 5200, 100)]
    [InlineData(2400, 0, 100, 2400, null, 100)]
    [InlineData(2400, 5200, 0, 2400, 5200, null)]
    [InlineData(-1, -1, -1, 65535, 65535, 65535)]
    [InlineData(-65536, -65536, -65536, null, null, null)]
    public void FrequencySpecificationsUseLow16BitsAndEachUnsupportedZeroIsIndependent(int eax, int ebx, int ecx,
        int? baseMhz, int? maximumMhz, int? busMhz)
    {
        var registers = ClockRegisters(); registers.Values[(0x16, 0)] = (eax, ebx, ecx, -1);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Equal(baseMhz, data.IntelProcessorBaseFrequencyDeclaredMhz is { } b ? (int)b : null);
        Assert.Equal(maximumMhz, data.IntelProcessorMaximumFrequencyDeclaredMhz is { } m ? (int)m : null);
        Assert.Equal(busMhz, data.IntelBusReferenceFrequencyDeclaredMhz is { } c ? (int)c : null);
        Assert.Equal(24000000U, data.IntelNominalCoreCrystalFrequencyDeclaredHz);
        var raw = Assert.Single(data.IdentityRegisters, row => row.Leaf == 0x16);
        Assert.Equal(unchecked((uint)eax), raw.Eax); Assert.Equal(uint.MaxValue, raw.Edx);
    }

    [Theory]
    [InlineData(0U, 100U, 24000000U, false, true)]
    [InlineData(3U, 0U, 24000000U, false, true)]
    [InlineData(3U, 100U, 0U, true, false)]
    [InlineData(4294967295U, 4294967295U, 4294967295U, true, true)]
    public void CrystalAndRatioDeclarationsRetainAll32BitsAndNeverFillMissingClock(uint denominator, uint numerator, uint hz,
        bool ratio, bool crystal)
    {
        var registers = ClockRegisters(); registers.Values[(0x15, 0)] = (unchecked((int)denominator), unchecked((int)numerator), unchecked((int)hz), -1);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Equal(crystal ? hz : (uint?)null, data.IntelNominalCoreCrystalFrequencyDeclaredHz);
        Assert.Equal(ratio ? new CpuTscCrystalClockRatio(numerator, denominator) : null, data.IntelTscToCoreCrystalRatioDeclared);
        Assert.Equal((ushort)2400, data.IntelProcessorBaseFrequencyDeclaredMhz);
        var raw = Assert.Single(data.IdentityRegisters, row => row.Leaf == 0x15);
        Assert.Equal(denominator, raw.Eax); Assert.Equal(numerator, raw.Ebx); Assert.Equal(hz, raw.Ecx);
    }

    [Fact]
    public void ClockProjectionPreservesPerLogicalIdentityTypesUnitsAndRationalIntegers()
    {
        var first = ClockData(); var second = ClockData() with { IntelProcessorBaseFrequencyDeclaredMhz = 1234,
            IntelNominalCoreCrystalFrequencyDeclaredHz = uint.MaxValue,
            IntelTscToCoreCrystalRatioDeclared = new(uint.MaxValue, 3) };
        var fields = CpuTopologyInformationProjection.Create(ClockSnapshot(first, second), DeviceInformationSnapshot.Empty);
        var a = Device(fields, "windows-cpu-thread|0:1"); var b = Device(fields, "windows-cpu-thread|2:7");
        Assert.Equal(5, ClockFields(a).Count); Assert.Equal(5, ClockFields(b).Count);
        Assert.Equal("2400", Field(a, "cpu_intel_processor_base_frequency_declared_mhz").Value!.Value.GetString());
        Assert.Equal("1234", Field(b, "cpu_intel_processor_base_frequency_declared_mhz").Value!.Value.GetString());
        Assert.Equal("4294967295", Field(b, "cpu_intel_nominal_core_crystal_frequency_declared_hz").Value!.Value.GetString());
        var ratio = Field(b, "cpu_intel_tsc_to_core_crystal_ratio_declared");
        Assert.Equal("rational", ratio.Definition.ValueKind); Assert.Equal("ratio", ratio.Definition.Unit);
        Assert.Equal(uint.MaxValue, ratio.Value!.Value.GetProperty("numerator").GetUInt32());
        Assert.Equal(3U, ratio.Value.Value.GetProperty("denominator").GetUInt32());
        Assert.Equal((double)uint.MaxValue / 3, ratio.Value.Value.GetProperty("numericValue").GetDouble());
        Assert.Equal("intel.cpuid.15", ratio.Definition.SourceId);
        Assert.Equal("MHz", Field(a, "cpu_intel_processor_base_frequency_declared_mhz").Definition.Unit);
        Assert.Equal("Hz", Field(b, "cpu_intel_nominal_core_crystal_frequency_declared_hz").Definition.Unit);
        var json = JsonSerializer.Serialize(fields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"numerator\":4294967295", json); Assert.Contains("\"value\":\"4294967295\"", json);
    }

    [Fact]
    public void ClockFailureRetainsDefinitionsClearsValuesAndSuccessfulVendorChangeRemovesThem()
    {
        var data = ClockData(); var snapshot = ClockSnapshot(data, data);
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { ThreadCpuid = [new(1, null), new(135, data)] } };
        var result = CpuTopologyInformationProjection.Create(failed, current);
        Assert.All(ClockFields(Device(result, "windows-cpu-thread|0:1")), field => Assert.Null(field.Value));
        Assert.All(ClockFields(Device(result, "windows-cpu-thread|2:7")), field => Assert.NotNull(field.Value));
        var recovered = CpuTopologyInformationProjection.Create(snapshot, result);
        Assert.All(ClockFields(Device(recovered, "windows-cpu-thread|0:1")), field => Assert.NotNull(field.Value));
        var amd = WindowsCpuThreadCpuidReader.ReadRegisters(Registers("AuthenticAMD").Read);
        var changed = CpuTopologyInformationProjection.Create(ClockSnapshot(amd, data), recovered);
        Assert.Empty(ClockFields(Device(changed, "windows-cpu-thread|0:1")));
        var invalid = CpuTopologyInformationProjection.Create(ClockSnapshot(data with { IntelTscToCoreCrystalRatioDeclared = new(1, 0) }, data), current);
        Assert.Null(Field(Device(invalid, "windows-cpu-thread|0:1"), "cpu_intel_tsc_to_core_crystal_ratio_declared").Value);
    }

    [Fact]
    public async Task ClockDeclarationsUseOriginalSubscriptionSlotCacheAndFailureRecovery()
    {
        await using var fixture = new CpuTopologyProviderTestFixture(); using var cancellation = new CancellationTokenSource();
        Assert.Empty(fixture.Reader.GetInformationSnapshot().Devices); Assert.Equal(0, fixture.Sampler.CaptureCount);
        await using var stream = fixture.Reader.SubscribeInformationAsync("clock-declarations", TimeSpan.FromMilliseconds(100), cancellation.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        // Explicit captures keep the cache assertions independent of the subscription cadence.
        var capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync()); var data = ClockData(); var snapshot = ClockSnapshot(data, data);
        fixture.Sampler.Complete(snapshot); await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        var first = stream.Current; Assert.All(ClockFields(Device(first, "windows-cpu-thread|0:1")), field => Assert.NotNull(field.Value));
        Assert.Equal(5, ClockFields(Device(first, "windows-cpu-thread|0:1")).Count);
        for (var index = 0; index < 10; index++) Assert.Same(first, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(1, fixture.Sampler.CaptureCount); Assert.Equal(1U, fixture.ReadDemand().SourceCount); Assert.Equal(1U, fixture.ReadDemand().ItemCount);
        next = stream.MoveNextAsync().AsTask();
        capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { ThreadCpuid = [new(1, null), new(135, data)] } });
        await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(ClockFields(Device(stream.Current, "windows-cpu-thread|0:1")), field => Assert.Null(field.Value));
        next = stream.MoveNextAsync().AsTask();
        capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(snapshot); await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(ClockFields(Device(stream.Current, "windows-cpu-thread|0:1")), field => Assert.NotNull(field.Value));
    }

    private static RegisterReader ClockRegisters(string vendor = "GenuineIntel", uint maximum = 0x1F)
    {
        var registers = Registers(vendor, maximum); registers.Values[(0x15, 0)] = (3, 100, 24000000, 0);
        registers.Values[(0x16, 0)] = (2400, 5200, 100, 0); return registers;
    }
    private static CpuThreadCpuidData ClockData() => WindowsCpuThreadCpuidReader.ReadRegisters(ClockRegisters().Read);
    private static CpuTopologySnapshot ClockSnapshot(CpuThreadCpuidData first, CpuThreadCpuidData second)
    {
        var snapshot = Snapshot(); return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, first), new(135, second)] } };
    }
    private static IReadOnlyList<DeviceInformationField> ClockFields(DeviceInformationObject device)
        => device.Fields.Where(static field => field.Definition.SourceId is "intel.cpuid.15" or "intel.cpuid.16").ToArray();
}
