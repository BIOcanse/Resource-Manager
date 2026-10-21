using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class UniwillGpuPowerWriterTests
{
    private const string Offset = "gpu.ctgp-offset";
    private static ControlCapability Capability(string id = Offset) => new(id, id, ControlValueKinds.Number, true);
    private static ControlObject Target => new("gpu:test", ControlObjectKinds.Gpu, "GPU",
        new(ControlOperatingSystems.Windows, ControlVendors.Nvidia), [], AdapterIndex: 0,
        GpuAttachment: ControlGpuAttachments.Discrete) { AdapterKey = 123 };

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

    [Theory]
    [InlineData(Offset)]
    [InlineData("gpu.dynamic-boost-offset")]
    [InlineData("gpu.dynamic-boost-enabled")]
    public async Task ExternalOrUnconfirmedTargetNeverTouchesGlobalEcIncludingRestore(string id)
    {
        var hardware = new FakeHardware { TargetConfirmed = false };
        var writer = new UniwillGpuPowerWriter(hardware);
        Assert.Equal(UniwillGpuPowerWriter.TargetUnconfirmed, writer.Probe(Target, Capability(id)).Reason);
        Assert.Null(await writer.ReadAsync(Target, Capability(id), default));
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Target, Capability(id), new(id, Number: 10), default)).Status);
        Assert.False(await writer.RestoreAsync(Target, Capability(id), new HashSet<string>(), default));
        Assert.Equal(0, hardware.Reads);
        Assert.Empty(hardware.Writes);
    }

    [Fact]
    public async Task MissingLuidOrWrongVendorDoesNotReachTheEc()
    {
        var hardware = new FakeHardware();
        var writer = new UniwillGpuPowerWriter(hardware);
        foreach (var target in new[] { Target with { AdapterKey = null }, Target with { AdapterKey = 0 },
                     Target with { AdapterIndex = 1 }, Target with { Platform = new(ControlOperatingSystems.Windows, ControlVendors.Amd) } })
            Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(target, Capability(), new(Offset, Number: 10), default)).Status);
        Assert.Equal(0, hardware.Reads);
        Assert.Empty(hardware.Writes);
    }

    [Theory]
    [InlineData(Offset, 40, 10, 55)]
    [InlineData("gpu.dynamic-boost-offset", 40, 10, 25)]
    [InlineData(Offset, 0, 100, 0)]
    public async Task BothOffsetsShareTheSameHeadroom(string id, byte ctgp, byte dynamicBoost, double expectedMaximum)
    {
        var hardware = new FakeHardware { Ctgp = ctgp, DynamicBoost = dynamicBoost, Enable = 0 };
        var writer = new UniwillGpuPowerWriter(hardware);
        Assert.Equal(expectedMaximum, writer.Probe(Target, Capability(id)).Range!.Maximum);
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Target, Capability(id), new(id, Number: expectedMaximum + 1), default)).Status);
        Assert.Empty(hardware.Writes);
    }

    [Fact]
    public void UniqueInternalHybridAdapterIsIndependentOfEnumerationOrderAndExternalGpu()
    {
        var internalGpu = new UniwillGpuPowerHardware.BudgetAdapter(0, 123, 0x11);
        var externalGpu = new UniwillGpuPowerHardware.BudgetAdapter(1, 456, 0x411);
        Assert.True(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 123, [internalGpu, externalGpu]));
        Assert.True(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 123, [externalGpu, internalGpu]));
        Assert.False(UniwillGpuPowerHardware.IsUniqueInternalTarget(1, 456, [externalGpu, internalGpu]));
        Assert.False(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 456, [internalGpu, externalGpu]));
        Assert.False(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 123, [internalGpu, internalGpu with { Index = 2, Key = 789 }]));
        Assert.False(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 123, [internalGpu, externalGpu with { Type = null }]));
        foreach (uint type in new uint[] { 0, 1, 0x15, 0x411 })
            Assert.False(UniwillGpuPowerHardware.IsUniqueInternalTarget(0, 123, [internalGpu with { Type = type }]));
    }

    [Theory]
    [InlineData(Offset)]
    [InlineData("gpu.dynamic-boost-offset")]
    [InlineData("gpu.dynamic-boost-enabled")]
    public async Task UnknownFirmwareNeverAccessesBudgetRegistersEvenWhenChannelAndGpuMatch(string id)
    {
        var hardware = new FakeHardware { IsBudgetFirmwareConfirmed = false };
        var writer = new UniwillGpuPowerWriter(hardware);
        Assert.Equal(UniwillGpuPowerWriter.FirmwareUnconfirmed, writer.Probe(Target, Capability(id)).Reason);
        Assert.Null(await writer.ReadAsync(Target, Capability(id), default));
        var result = await writer.WriteAsync(Target, Capability(id), new(id, Number: 10), default);
        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Equal(UniwillGpuPowerWriter.FirmwareUnconfirmed, result.Message);
        Assert.False(await writer.RestoreAsync(Target, Capability(id), new HashSet<string>(), default));
        Assert.Equal(0, hardware.Reads);
        Assert.Empty(hardware.Writes);
    }

    [Theory]
    [InlineData(Offset, 18)]
    [InlineData("gpu.dynamic-boost-offset", 7)]
    public async Task PartialReleaseRestoresNonzeroOriginalAcrossRestartAndPreservesSibling(string id, byte expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm-uniwill-baseline-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "baseline.json");
        try
        {
            var hardware = new FakeHardware { Enable = 0x82, Ctgp = 18, DynamicBoost = 7 };
            var writer = new UniwillGpuPowerWriter(hardware, path);
            Assert.False(File.Exists(path));
            Assert.True(writer.Probe(Target, Capability()).CanWrite);
            Assert.False(File.Exists(path));
            Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, Capability(), new(Offset, Number: 30), default)).Status);
            Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, Capability("gpu.dynamic-boost-offset"), new("gpu.dynamic-boost-offset", Number: 15), default)).Status);
            var stored = File.ReadAllText(path);
            hardware.Writes.Clear();
            var retained = id == Offset ? "gpu.dynamic-boost-offset" : Offset;
            var restarted = new UniwillGpuPowerWriter(hardware, path);
            Assert.True(await restarted.RestoreAsync(Target, Capability(id), new HashSet<string> { retained }, default));
            Assert.Equal((id == Offset ? (ushort)0x0744 : (ushort)0x0746, expected), Assert.Single(hardware.Writes));
            Assert.Equal(id == Offset ? 15 : 30, id == Offset ? hardware.DynamicBoost : hardware.Ctgp);
            Assert.Equal(0x87, hardware.Enable);
            Assert.Equal(stored, File.ReadAllText(path));
            Assert.True(await restarted.RestoreAsync(Target, Capability(retained), new HashSet<string>(), default));
            Assert.Equal(0x82, hardware.Enable);
            Assert.Equal("{}", File.ReadAllText(path));
            // The next ownership period captures the new firmware values, not this period's record.
            hardware.Ctgp = 22;
            Assert.Equal(ControlApplyStatuses.Applied, (await restarted.WriteAsync(Target, Capability(), new(Offset, Number: 25), default)).Status);
            Assert.True(await restarted.RestoreAsync(Target, Capability(), new HashSet<string> { "gpu.dynamic-boost-enabled" }, default));
            Assert.Equal(22, hardware.Ctgp);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(0x80, true, 0x85)]
    [InlineData(0x82, false, 0x87)]
    public async Task PartialToggleReleaseRestoresOriginalBitInsteadOfForcingOff(byte initial, bool requested, byte expected)
    {
        var hardware = new FakeHardware { Enable = initial, Ctgp = 12, DynamicBoost = 8 };
        var writer = new UniwillGpuPowerWriter(hardware);
        const string toggle = "gpu.dynamic-boost-enabled";
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, Capability(toggle), new(toggle, Toggle: requested), default)).Status);
        Assert.True(await writer.RestoreAsync(Target, Capability(toggle), new HashSet<string> { Offset }, default));
        Assert.Equal(expected, hardware.Enable);
        Assert.Equal(12, hardware.Ctgp);
        Assert.Equal(8, hardware.DynamicBoost);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"gpu:test\":null}")]
    [InlineData("{\"gpu:test\":{\"Ctgp\":12}}")]
    [InlineData("{\"gpu:test\":{\"Ctgp\":300,\"DynamicBoost\":7,\"DynamicBoostEnabled\":true}}")]
    public async Task CorruptBaselineNeverWritesOrInventsPartialRelease(string contents)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm-uniwill-baseline-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "baseline.json");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(path, contents);
            var hardware = new FakeHardware { Ctgp = 18, DynamicBoost = 7 };
            var writer = new UniwillGpuPowerWriter(hardware, path);
            var outcome = await writer.WriteAsync(Target, Capability(), new(Offset, Number: 25), default);
            Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
            Assert.Equal(UniwillGpuPowerWriter.BaselineUnavailable, outcome.Message);
            Assert.False(await writer.RestoreAsync(Target, Capability(), new HashSet<string> { "gpu.dynamic-boost-offset" }, default));
            Assert.Empty(hardware.Writes);
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task UnwritableBaselineStopsBeforeAnyRegisterWrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm-uniwill-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var blockedParent = Path.Combine(folder, "occupied");
        try
        {
            File.WriteAllText(blockedParent, "owned test obstacle");
            var hardware = new FakeHardware { Enable = 0x82, Ctgp = 18, DynamicBoost = 7 };
            var result = await new UniwillGpuPowerWriter(hardware, Path.Combine(blockedParent, "baseline.json"))
                .WriteAsync(Target, Capability(), new(Offset, Number: 25), default);
            Assert.Equal(ControlApplyStatuses.Failed, result.Status);
            Assert.Equal(UniwillGpuPowerWriter.BaselineUnavailable, result.Message);
            Assert.Empty(hardware.Writes);
            Assert.Equal(0x82, hardware.Enable);
            Assert.Equal(18, hardware.Ctgp);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task MissingBaselineOrMissingOriginalDoesNotPretendZeroIsDefault()
    {
        var hardware = new FakeHardware { Ctgp = 18, MissingOriginal = true };
        var writer = new UniwillGpuPowerWriter(hardware);
        const string toggle = "gpu.dynamic-boost-enabled";
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Target, Capability(toggle), new(toggle, Toggle: true), default)).Status);
        Assert.False(await writer.RestoreAsync(Target, Capability(), new HashSet<string> { toggle }, default));
        Assert.Empty(hardware.Writes);
    }

    [Fact]
    public async Task RejectedPartialRestoreRetainsOriginalForRetry()
    {
        var hardware = new FakeHardware { Ctgp = 18, DynamicBoost = 7 };
        var writer = new UniwillGpuPowerWriter(hardware);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, Capability(), new(Offset, Number: 25), default)).Status);
        hardware.MissingReadback = true;
        Assert.False(await writer.RestoreAsync(Target, Capability(), new HashSet<string> { "gpu.dynamic-boost-offset" }, default));
        hardware.MissingReadback = false;
        Assert.True(await writer.RestoreAsync(Target, Capability(), new HashSet<string> { "gpu.dynamic-boost-offset" }, default));
        Assert.Equal(18, hardware.Ctgp);
    }

    private sealed class FakeHardware : IUniwillGpuPowerHardware
    {
        public bool IsAvailable => true;
        public bool IsBudgetFirmwareConfirmed { get; init; } = true;
        public bool TargetConfirmed { get; init; } = true;
        public bool IsBudgetTarget(int adapterIndex, ulong adapterKey) => TargetConfirmed && adapterIndex == 0 && adapterKey == 123;
        public byte Enable { get; set; } = 5;
        public byte Ctgp { get; set; }
        public byte DynamicBoost { get; set; }
        public double Maximum { get; init; } = 115;
        public bool MissingBudget { get; init; }
        public byte? OffsetReadback { get; init; }
        public bool MissingReadback { get; set; }
        public bool MissingOriginal { get; init; }
        public bool RejectEnable { get; init; }
        public int Reads { get; private set; }
        public List<(ushort Address, byte Value)> Writes { get; } = [];
        public byte? Read(ushort address) { Reads++; return address switch { 0x0743 => Enable, 0x0744 => MissingOriginal ? null : Ctgp, 0x0746 => DynamicBoost, _ => null }; }
        public NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex) => MissingBudget ? null : new(0, Maximum, 50, 115);
        public byte? WriteReadingBack(ushort address, byte value)
        {
            Writes.Add((address, value));
            if (address == 0x0743)
            {
                if (!RejectEnable) Enable = value;
                return Enable;
            }
            if (MissingReadback) return null;
            var readback = OffsetReadback ?? value;
            if (address == 0x0744) Ctgp = readback;
            if (address == 0x0746) DynamicBoost = readback;
            return readback;
        }
    }
}
