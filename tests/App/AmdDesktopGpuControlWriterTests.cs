using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Control.Writers.Amd;

namespace Resource_Manager_APP.Tests;

public sealed class AmdDesktopGpuControlWriterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rm-adlx-test-" + Guid.NewGuid());
    private string Baseline => Path.Combine(folder, "baseline.json");
    private static ControlObject Gpu(string chassis = ControlChassisKinds.Fixed, string attachment = ControlGpuAttachments.Discrete, string vendor = ControlVendors.Amd)
        => new("gpu:test", ControlObjectKinds.Gpu, "AMD Test", new(ControlOperatingSystems.Windows, vendor), [], Terms: [chassis], GpuAttachment: attachment, AdapterIndex: 1);
    private static ControlCapability Cap(string id = "gpu.power-limit-offset") => new(id, id, ControlValueKinds.Number, false);
    [Fact]
    public async Task UnsupportedRoutesNeverTouchHardware()
    {
        var io = new Hardware();
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        foreach (var gpu in new[] { Gpu(ControlChassisKinds.Portable), Gpu(ControlChassisKinds.Unknown), Gpu(attachment: ControlGpuAttachments.Integrated), Gpu(vendor: ControlVendors.Intel) })
        {
            Assert.False(writer.Probe(gpu, Cap()).CanWrite);
            Assert.Equal(ControlApplyStatuses.Unsupported, (await writer.WriteAsync(gpu, Cap(), new(Cap().Id, Number: 5), default)).Status);
        }
        Assert.Equal(0, io.Reads);
        Assert.Equal(0, io.Writes);
    }
    [Fact]
    public void ProbeUsesDriverRangeAndPercentWithoutChangingHardware()
    {
        var io = new Hardware();
        var result = new AmdDesktopGpuControlWriter(io, Baseline).Probe(Gpu(), Cap());
        Assert.True(result.CanWrite);
        Assert.Equal(-10, result.Range!.Minimum);
        Assert.Equal("%", result.Range.Unit);
        Assert.Equal(0, io.Writes);
        Assert.False(File.Exists(Baseline));
    }
    [Theory]
    [InlineData(16)] [InlineData(3)] [InlineData(double.NaN)] [InlineData(2.5)]
    public async Task InvalidRangeOrStepNeverWrites(double value)
    {
        var io = new Hardware();
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Gpu(), Cap(), new(Cap().Id, Number: value), default)).Status);
        Assert.Equal(0, io.Writes);
        Assert.False(File.Exists(Baseline));
    }
    [Fact]
    public async Task ReadbackMismatchIsFailureAndRestartRestorePreservesOtherField()
    {
        var io = new Hardware { IgnoreWrites = true };
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Gpu(), Cap(), new(Cap().Id, Number: 6), default)).Status);
        io.IgnoreWrites = false;
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Gpu(), Cap(), new(Cap().Id, Number: 6), default)).Status);
        var restarted = new AmdDesktopGpuControlWriter(io, Baseline);
        Assert.True(await restarted.RestoreAsync(Gpu(), Cap(), new HashSet<string> { "gpu.core-clock-maximum" }, default));
        Assert.Equal(0, io.Values[0]);
        Assert.Equal(2400, io.Values[2]);
    }
    [Fact]
    public async Task MissingDriverAndChangedGpuIdentityCannotReportApplied()
    {
        var io = new Hardware { Missing = true };
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        Assert.False(writer.Probe(Gpu(), Cap()).CanWrite);
        Assert.Equal(0, io.Writes);
        io.Missing = false;
        io.ChangeIdentity = true;
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Gpu(), Cap(), new(Cap().Id, Number: 6), default)).Status);
    }
    [Fact]
    public void FanMinimumAndTargetAreRpmRatherThanPercent()
    {
        var writer = new AmdDesktopGpuControlWriter(new Hardware(), Baseline);
        var fan = Gpu() with { Kind = ControlObjectKinds.Fan, Id = "fan:gpu1" };
        Assert.Equal("RPM", writer.Probe(fan, Cap("fan.minimum-rpm")).Range!.Unit);
        Assert.Equal("RPM", writer.Probe(fan, Cap("fan.target-rpm")).Range!.Unit);
    }
    [Fact]
    public async Task FanZeroRpmIsTypedToggleWithReadbackAndPerCapabilityRestore()
    {
        var io = new Hardware();
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        var fan = Gpu() with { Kind = ControlObjectKinds.Fan, Id = "fan:gpu1" };
        var cap = new ControlCapability("fan.zero-rpm", "Zero RPM", ControlValueKinds.Toggle, false);
        Assert.True(writer.Probe(fan, cap).CanWrite);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(fan, cap, new(cap.Id, Toggle: false), default)).Status);
        var actual = await writer.ReadAsync(fan, cap, default);
        Assert.False(actual!.Toggle);
        Assert.Null(actual.Number);
        Assert.True(await writer.RestoreAsync(fan, cap, new HashSet<string> { "fan.target-rpm" }, default));
        Assert.Equal(1, io.Values[7]);
        Assert.Equal(1000, io.Values[6]);
    }
    [Fact]
    public async Task ReleasingBothClockLimitsRestoresTheBlockingSiblingFirst()
    {
        var io = new Hardware();
        io.Values[1] = 1600;
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        var minimum = Cap("gpu.core-clock-minimum");
        var maximum = Cap("gpu.core-clock-maximum");
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Gpu(), minimum, new(minimum.Id, Number: 500), default)).Status);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Gpu(), maximum, new(maximum.Id, Number: 1000), default)).Status);
        Assert.False(await writer.RestoreAsync(Gpu(), minimum, new HashSet<string> { maximum.Id }, default));
        Assert.Equal(1000, io.Values[2]);
        Assert.True(await writer.RestoreAsync(Gpu(), minimum, new HashSet<string>(), default));
        Assert.True(await writer.RestoreAsync(Gpu(), maximum, new HashSet<string>(), default));
        Assert.Equal(1600, io.Values[1]);
        Assert.Equal(2400, io.Values[2]);
    }
    [Fact]
    public async Task Navi4OffsetsHaveSeparateIdsAndRestoreWithoutAbsoluteSiblingComparison()
    {
        var io = new Hardware { Mode = AdlxGfxMode.Offset };
        var writer = new AmdDesktopGpuControlWriter(io, Baseline);
        Assert.False(writer.Probe(Gpu(), Cap("gpu.core-clock-maximum")).CanWrite);
        Assert.False(writer.Probe(Gpu(), Cap("gpu.core-voltage")).CanWrite);
        var offset = Cap("gpu.core-clock-offset");
        Assert.True(writer.Probe(Gpu(), offset).CanWrite);
        Assert.Equal("MHz", writer.Probe(Gpu(), offset).Range!.Unit);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Gpu(), offset, new(offset.Id, Number: 150), default)).Status);
        var minimum = Cap("gpu.core-clock-minimum");
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Gpu(), minimum, new(minimum.Id, Number: 600), default)).Status);
        Assert.True(await writer.RestoreAsync(Gpu(), minimum, new HashSet<string> { offset.Id }, default));
        Assert.True(await writer.RestoreAsync(Gpu(), offset, new HashSet<string>(), default));
        Assert.Equal(500, io.Values[1]);
        Assert.Equal(100, io.Values[8]);
        Assert.False(new AmdDesktopGpuControlWriter(new Hardware(), Baseline).Probe(Gpu(), offset).CanWrite);
    }
    private sealed class Hardware : IAdlxTuningHardware
    {
        public int[] Values = [0, 500, 2400, 1000, 1800, 20, 1000, 1, 100, -50];
        public AdlxGfxMode Mode = AdlxGfxMode.Absolute;
        public int Reads, Writes;
        public bool IgnoreWrites, Missing, ChangeIdentity;
        public AdlxTuningValue Read(ControlObject target, int field)
        {
            Reads++;
            if (Missing) throw new DllNotFoundException("missing driver");
            if ((field is 2 or 3 && Mode != AdlxGfxMode.Absolute) || (field >= 8 && Mode != AdlxGfxMode.Offset))
                throw new IOException("wrong generation semantics");
            return new(ChangeIdentity && Writes > 0 ? "PCI\\OTHER" : "PCI\\AMD", Values[field], field == 7 ? 0 : field is 1 or 2 ? 100 : -10,
                field == 7 ? 1 : field is 1 or 2 ? 3000 : field >= 8 ? 200 : 14, field == 7 || field is 1 or 2 || field >= 8 ? 1 : 2, Mode);
        }
        public void Write(string pnp, int field, int value)
        {
            Assert.Equal("PCI\\AMD", pnp);
            if (Mode == AdlxGfxMode.Absolute && (field == 1 && value > Values[2] || field == 2 && value < Values[1])) throw new IOException("inverted clocks");
            Writes++;
            if (!IgnoreWrites) Values[field] = value;
        }
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
