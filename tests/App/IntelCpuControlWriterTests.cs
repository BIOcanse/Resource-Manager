using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace Resource_Manager_APP.Tests;

public sealed class IntelCpuControlWriterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rm-intel-test-" + Guid.NewGuid());
    private string Baseline => Path.Combine(folder, "baseline.json");
    private static ControlObject Cpu(string vendor = ControlVendors.Intel) => new("cpu:test", ControlObjectKinds.Cpu,
        "Test CPU", new(ControlOperatingSystems.Windows, vendor), []);
    private static ControlCapability Cap(string id) => new(id, ControlValueKinds.Number, false,
        Range: id.Contains("voltage") ? new(-250, 100, 1, "mV") : new(1, 300, 1, id.EndsWith("window") ? "s" : "W"));
    [Fact]
    public async Task CancellationDuringProbePreventsBaselineCaptureAndHardwareWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var io = new Hardware { DuringRead = cancellation.Cancel };
        using var writer = new IntelCpuControlWriter(() => io, Baseline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Cpu(),
            Cap("cpu.power-limit"), new("cpu.power-limit", Number: 45), cancellation.Token));

        Assert.Equal(0, io.SettingWrites);
        Assert.False(File.Exists(Baseline));
    }
    [Fact]
    public void ProbeDoesNotWriteSettingsOrCreateBaselineAndWrongVendorDoesNotOpen()
    {
        var io = new Hardware();
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        Assert.False(writer.Probe(Cpu(ControlVendors.Amd), Cap("cpu.power-limit")).CanWrite);
        Assert.Equal(0, io.Reads);
        Assert.True(writer.Probe(Cpu(), Cap("cpu.power-limit")).CanWrite);
        Assert.True(writer.Probe(Cpu(), Cap("cpu.core-voltage-offset")).CanWrite);
        Assert.Equal(0, io.SettingWrites);
        Assert.False(File.Exists(Baseline));
    }
    [Fact]
    public async Task PowerWriteAndRestartRestorePreserveSiblingAndReservedBits()
    {
        var io = new Hardware();
        var original = io.Limits;
        using (var writer = new IntelCpuControlWriter(() => io, Baseline))
        {
            Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Cpu(), Cap("cpu.power-limit"), new("cpu.power-limit", Number: 45), default)).Status);
            Assert.Equal(45, IntelRaplEncoding.ReadPower(io.Limits, io.Units, false));
            Assert.Equal(original & ~0xffffUL, io.Limits & ~0xffffUL);
        }
        io.Limits = IntelRaplEncoding.SetPower(io.Limits, io.Units, true, 90);
        var sibling = io.Limits & ~0xffffUL;
        using var restarted = new IntelCpuControlWriter(() => io, Baseline);
        Assert.True(await restarted.RestoreAsync(Cpu(), Cap("cpu.power-limit"), new HashSet<string> { "cpu.short-power-limit" }, default));
        Assert.Equal(original & 0xffffUL, io.Limits & 0xffffUL);
        Assert.Equal(sibling, io.Limits & ~0xffffUL);
    }
    [Fact]
    public async Task LockAndFailedReadbackCannotReportApplied()
    {
        var io = new Hardware { Limits = 1UL << 63 };
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        Assert.False(writer.Probe(Cpu(), Cap("cpu.power-limit")).CanWrite);
        Assert.Equal(0, io.SettingWrites);
        io.Limits = 0;
        io.IgnoreWrites = true;
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Cpu(), Cap("cpu.power-limit"), new("cpu.power-limit", Number: 45), default)).Status);
    }
    [Fact]
    public void TimeWindowQuantizesAndPreservesPowerAndOtherWindow()
    {
        const ulong units = 10UL << 16;
        const ulong original = 0x5134567852345678;
        var value = IntelRaplEncoding.SetWindow(original, units, false, 28);
        Assert.Equal(28, IntelRaplEncoding.ReadWindow(value, units, false));
        Assert.Equal(original & ~(0x7fUL << 17), value & ~(0x7fUL << 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntelRaplEncoding.SetPower(0, units, false, double.NaN));
    }
    [Theory]
    [InlineData(-125)] [InlineData(50)] [InlineData(0)]
    public async Task VoltageUsesCorrectPlaneAndRestoresOriginal(double millivolts)
    {
        var io = new Hardware();
        io.Voltage[2] = IntelOcMailbox.Encode(-20) | 0x12U;
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        var cap = Cap("cpu.cache-voltage-offset");
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Cpu(), cap, new(cap.Id, Number: millivolts), default)).Status);
        Assert.Equal(IntelOcMailbox.Encode(millivolts) | 0x12U, io.Voltage[2]);
        Assert.Equal(0U, io.Voltage[0]);
        Assert.True(await writer.RestoreAsync(Cpu(), cap, new HashSet<string>(), default));
        Assert.Equal(IntelOcMailbox.Encode(-20) | 0x12U, io.Voltage[2]);
    }
    [Fact]
    public async Task VoltageRestorePreservesCurrentNonOffsetDomainBits()
    {
        var io = new Hardware();
        io.Voltage[0] = IntelOcMailbox.Encode(-20) | 0x12U;
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        var cap = Cap("cpu.core-voltage-offset");
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Cpu(), cap, new(cap.Id, Number: -100), default)).Status);
        io.Voltage[0] = (io.Voltage[0] & ~0x1fffffU) | 0x345U;
        Assert.True(await writer.RestoreAsync(Cpu(), cap, new HashSet<string>(), default));
        Assert.Equal(IntelOcMailbox.Encode(-20) | 0x345U, io.Voltage[0]);
    }
    [Fact]
    public void BusyMailboxAndUnavailableModuleRefuseWithoutSettingWrite()
    {
        var io = new Hardware { Mailbox = 1UL << 63 };
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        Assert.False(writer.Probe(Cpu(), Cap("cpu.core-voltage-offset")).CanWrite);
        Assert.Equal(0, io.SettingWrites);
        using var missing = new IntelCpuControlWriter(() => throw new FileNotFoundException("module missing"), Baseline);
        var unavailable = missing.Probe(Cpu(), Cap("cpu.power-limit"));
        Assert.Equal("control-helper-missing", unavailable.Reason?.Code);
        Assert.Equal(ControlUnavailableKinds.Component, unavailable.UnavailableKind);
    }
    [Theory]
    [InlineData("cpu.power-limit-mmio")]
    [InlineData("cpu.temperature-offset")]
    public async Task ModulePermissionGapIsReportedBeforeOpeningHardware(string id)
    {
        var opens = 0;
        using var writer = new IntelCpuControlWriter(() => { opens++; return new Hardware(); }, Baseline);
        var availability = writer.Probe(Cpu(), Cap(id));
        Assert.False(availability.CanWrite);
        Assert.Equal(ControlUnavailableKinds.NotImplemented, availability.UnavailableKind);
        Assert.Null(await writer.ReadAsync(Cpu(), Cap(id), default));
        Assert.Equal(0, opens);
    }
    private sealed class Hardware : IIntelCpuHardware
    {
        public ulong Units = 3UL | (10UL << 16);
        public ulong Limits = 0x1234abcd923480a0;
        public ulong Mailbox;
        public uint[] Voltage = new uint[4];
        public int Reads, SettingWrites;
        public bool IgnoreWrites;
        public Action? DuringRead;
        public ulong ReadMsr(uint register) { Reads++; DuringRead?.Invoke(); return register switch { 0x606 => Units, 0x610 => Limits, 0x150 => Mailbox, _ => throw new IOException() }; }
        public void WriteMsr(uint register, ulong value)
        {
            if (register == 0x610) { SettingWrites++; if (!IgnoreWrites) Limits = value; return; }
            if (register != 0x150) throw new IOException();
            var plane = (int)((value >> 40) & 0xff);
            if (((value >> 32) & 0xff) == 0x11) { SettingWrites++; if (!IgnoreWrites) Voltage[plane] = (uint)value; }
            Mailbox = Voltage[plane];
        }
        public bool CanWriteMsr(uint register) => register is 0x150 or 0x610;
        public void Dispose() { }
    }
    [Fact]
    public async Task ReportedNameChangeDoesNotLoseAnExistingRestoreBaseline()
    {
        var io = new Hardware();
        var original = io.Limits;
        var capability = Cap("cpu.power-limit");
        using (var writer = new IntelCpuControlWriter(() => io, Baseline))
            Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Cpu(), capability, new(capability.Id, Number: 45), default)).Status);
        using var restarted = new IntelCpuControlWriter(() => io, Baseline);
        Assert.True(await restarted.RestoreAsync(Cpu() with { ReportedName = "Renamed CPU" }, capability, new HashSet<string>(), default));
        Assert.Equal(original, io.Limits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDisplayKeysMigrateOnlyWhenTheRestoreValueIsUnambiguous(bool conflict)
    {
        var io = new Hardware();
        var original = io.Limits;
        var capability = Cap("cpu.power-limit");
        var mask = IntelRaplEncoding.FieldMask(false, false);
        Directory.CreateDirectory(folder);
        var stored = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, ulong>
        {
            [$"{Cpu().Id}\nFirst name\n{capability.Id}"] = original & mask,
            [$"{Cpu().Id}\nSecond name\n{capability.Id}"] = conflict ? 0 : original & mask
        });
        File.WriteAllText(Baseline, stored);
        io.Limits = IntelRaplEncoding.SetPower(io.Limits, io.Units, false, 45);
        var before = io.Limits;
        using var writer = new IntelCpuControlWriter(() => io, Baseline);
        Assert.Equal(!conflict, await writer.RestoreAsync(Cpu() with { ReportedName = "Third name" }, capability, new HashSet<string>(), default));
        Assert.Equal(conflict ? before : original, io.Limits);
        if (conflict) { Assert.Equal(stored, File.ReadAllText(Baseline)); Assert.Equal(0, io.SettingWrites); }
        else Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(Baseline))!);
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
