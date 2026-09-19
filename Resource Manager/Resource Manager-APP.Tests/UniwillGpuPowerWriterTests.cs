using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class UniwillGpuPowerWriterTests
{
    private const string Offset = "gpu.ctgp-offset";
    private static ControlCapability Capability(string id = Offset) => new(id, id, ControlValueKinds.Number, true);
    private static ControlObject Target => new("gpu:test", ControlObjectKinds.Gpu, "GPU",
        new(ControlOperatingSystems.Windows, ControlVendors.Nvidia), [], AdapterIndex: 0,
        GpuAttachment: ControlGpuAttachments.Discrete);

    [Theory]
    [InlineData(45, 50)]
    [InlineData(65, 50)]
    [InlineData(25, 12)]
    public async Task DifferentReadbackIsNotSuccessOrALearnedLimit(double request, byte observed)
    {
        var hardware = new FakeHardware { OffsetReadback = observed };
        var writer = new UniwillGpuPowerWriter(hardware);
        var before = writer.Probe(Target, Capability()).Range;
        var result = await writer.WriteAsync(Target, Capability(), new(Offset, Number: request), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Single(hardware.Writes);
        Assert.Equal(before, writer.Probe(Target, Capability()).Range);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(65.1)]
    [InlineData(66)]
    [InlineData(256)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InvalidRequestNeverChangesEnableBits(double value)
    {
        var hardware = new FakeHardware { Enable = 0 };
        var result = await new UniwillGpuPowerWriter(hardware).WriteAsync(
            Target, Capability(), new(Offset, Number: value), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Empty(hardware.Writes);
    }

    [Fact]
    public async Task ExactReadbackAppliesOnceAndPreservesOtherEnableBits()
    {
        var hardware = new FakeHardware { Enable = 0xA0 };
        var result = await new UniwillGpuPowerWriter(hardware).WriteAsync(
            Target, Capability(), new(Offset, Number: 25), default);
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        Assert.Equal(new (ushort, byte)[] { (0x0743, 0xA5), (0x0744, 25) }, hardware.Writes);
    }

    [Fact]
    public async Task EnableMismatchStopsBeforeOffsetWrite()
    {
        var hardware = new FakeHardware { Enable = 0, RejectEnable = true };
        var result = await new UniwillGpuPowerWriter(hardware).WriteAsync(
            Target, Capability(), new(Offset, Number: 25), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Equal((ushort)0x0743, Assert.Single(hardware.Writes).Address);
    }

    [Fact]
    public async Task MissingReadbackIsFailureWithoutRetry()
    {
        var hardware = new FakeHardware { MissingReadback = true };
        var result = await new UniwillGpuPowerWriter(hardware).WriteAsync(
            Target, Capability(), new(Offset, Number: 25), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Single(hardware.Writes);
    }

    [Theory]
    [InlineData("unknown", "unknown")]
    [InlineData(Offset, "gpu.dynamic-boost-offset")]
    public async Task UnownedOrMismatchedCapabilityDoesNotTouchHardware(string capability, string setting)
    {
        var hardware = new FakeHardware();
        var result = await new UniwillGpuPowerWriter(hardware).WriteAsync(
            Target, Capability(capability), new(setting, Number: 10), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Equal(0, hardware.Reads);
        Assert.Empty(hardware.Writes);
    }

    [Theory]
    [InlineData(115.9, 65)]
    [InlineData(900, 255)]
    public void RangeCannotRoundUpOrOverflowTheRegister(double maximum, double expected)
    {
        var hardware = new FakeHardware { Maximum = maximum };
        Assert.Equal(expected, new UniwillGpuPowerWriter(hardware).Probe(Target, Capability()).Range!.Maximum);
    }

    [Fact]
    public async Task CancelledRequestDoesNotTouchHardware()
    {
        var hardware = new FakeHardware();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UniwillGpuPowerWriter(hardware)
            .WriteAsync(Target, Capability(), new(Offset, Number: 10), new CancellationToken(true)));
        Assert.Empty(hardware.Writes);
        Assert.Equal(0, hardware.Reads);
    }

    [Fact]
    public async Task ReleaseLastSettingReturnsTakeoverBitsToFirmwareWithoutChangingOtherBits()
    {
        var hardware = new FakeHardware { Enable = 0x87 };
        Assert.True(await new UniwillGpuPowerWriter(hardware).RestoreAsync(Target, Capability(), new HashSet<string>(), default));
        Assert.Equal(0x82, hardware.Enable);
        Assert.Single(hardware.Writes);
    }

    [Fact]
    public async Task ReleaseReportsRejectedEnableReadback()
    {
        var hardware = new FakeHardware { RejectEnable = true };
        Assert.False(await new UniwillGpuPowerWriter(hardware).RestoreAsync(Target, Capability(), new HashSet<string>(), default));
    }

    [Fact]
    public void ToggleDoesNotRequireOffsetBudgetAndProbeNeverWrites()
    {
        var hardware = new FakeHardware { MissingBudget = true };
        var writer = new UniwillGpuPowerWriter(hardware);
        Assert.True(writer.Probe(Target, Capability("gpu.dynamic-boost-enabled")).CanWrite);
        Assert.False(writer.Probe(Target, Capability()).CanWrite);
        Assert.Empty(hardware.Writes);
    }

    private sealed class FakeHardware : IUniwillGpuPowerHardware
    {
        public bool IsAvailable => true;
        public byte Enable { get; set; } = 5;
        public double Maximum { get; init; } = 115;
        public bool MissingBudget { get; init; }
        public byte? OffsetReadback { get; init; }
        public bool MissingReadback { get; init; }
        public bool RejectEnable { get; init; }
        public int Reads { get; private set; }
        public List<(ushort Address, byte Value)> Writes { get; } = [];
        public byte? Read(ushort address) { Reads++; return address == 0x0743 ? Enable : (byte)50; }
        public NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex) => MissingBudget ? null : new(0, Maximum, 50, 115);
        public byte? WriteReadingBack(ushort address, byte value)
        {
            Writes.Add((address, value));
            if (address == 0x0743)
            {
                if (!RejectEnable) Enable = value;
                return Enable;
            }
            return MissingReadback ? null : OffsetReadback ?? value;
        }
    }
}
