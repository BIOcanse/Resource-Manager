using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaClockIntervalTests
{
    [Theory]
    [InlineData("core", false)]
    [InlineData("core", true)]
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    public async Task OneApplyMovesCompleteIntervalUpAndDownRegardlessOfRequestOrder(string domain, bool maximumFirst)
    {
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        using var writer = new NvidiaGpuControlWriter(resource);
        var gpu = Gpu(domain);
        var layer = new ControlWriteLayer(new Catalog(gpu), [writer], new Access());
        await Apply(1200, 1800);
        Assert.Equal((1200u, 1800u), nvml.Pairs.Last());
        nvml.Pairs.Clear();

        await Apply(1950, 2250);
        Assert.Equal(new[] { (1200u, 2250u), (1950u, 2250u) }, nvml.Pairs);
        nvml.Pairs.Clear();
        await Apply(900, 1500);
        Assert.Equal(new[] { (900u, 2250u), (900u, 1500u) }, nvml.Pairs);

        async Task Apply(double minimum, double maximum)
        {
            var settings = new[] { Setting(domain, "minimum", minimum), Setting(domain, "maximum", maximum) };
            if (maximumFirst) Array.Reverse(settings);
            var report = await layer.WriteAsync(new([new(gpu.Id, settings)]), default);
            Assert.Equal(2, report.Outcomes.Count);
            Assert.All(report.Outcomes, outcome => Assert.Equal(ControlApplyStatuses.Applied, outcome.Status));
        }
    }

    [Fact]
    public async Task InvalidFinalPairRefusesBothBoundsBeforeEitherSetter()
    {
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        using var writer = new NvidiaGpuControlWriter(resource);
        var gpu = Gpu("core");
        var layer = new ControlWriteLayer(new Catalog(gpu), [writer], new Access());
        var result = await layer.WriteAsync(new([new(gpu.Id,
            [Setting("core", "maximum", 1500), Setting("core", "minimum", 1950)])]), default);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.Equal(ControlApplyStatuses.Failed, outcome.Status));
        Assert.Empty(nvml.Pairs);
        Assert.Equal(0, nvml.Probes);
    }

    [Fact]
    public async Task ZeroMaximumMeansNoUpperRestrictionWhenMovingAbovePreviousMaximum()
    {
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        using var writer = new NvidiaGpuControlWriter(resource);
        var gpu = Gpu("core");
        var layer = new ControlWriteLayer(new Catalog(gpu), [writer], new Access());
        await layer.WriteAsync(new([new(gpu.Id, [Setting("core", "maximum", 1500)])]), default);
        var result = await layer.WriteAsync(new([new(gpu.Id,
            [Setting("core", "minimum", 1950), Setting("core", "maximum", 0)])]), default);
        Assert.All(result.Outcomes, outcome => Assert.Equal(ControlApplyStatuses.Applied, outcome.Status));
        Assert.Equal((1950u, 3000u), nvml.Pairs.Last());
    }

    [Fact]
    public void PlanningDoesNotTouchHardwareOrAlterOtherVendorsAndRequiresConfirmedUnits()
    {
        var nvml = new RecordedNvml();
        using var resource = new NvidiaGpuControlResource(nvml, new RecordedNvapi());
        using var writer = new NvidiaGpuControlWriter(resource);
        var gpu = Gpu("core");
        ControlSetting[] settings = [Setting("core", "maximum", 1500), Setting("core", "minimum", 1950)];
        var other = gpu with { Platform = gpu.Platform with { Vendor = ControlVendors.Amd } };
        Assert.Empty(writer.ValidateSettings(other, settings));
        Assert.Same(settings, writer.OrderSettings(other, settings));
        Assert.Empty(writer.ValidateSettings(gpu, [settings[0], settings[1] with { Unit = ControlUnits.Watt }]));
        Assert.Equal(2, writer.ValidateSettings(gpu, [settings[0], settings[1] with { Unit = null }]).Count);
        writer.OrderSettings(gpu, settings);
        Assert.Equal(0, nvml.Probes);
        Assert.Empty(nvml.Pairs);
    }

    private static ControlSetting Setting(string domain, string endpoint, double value)
        => new($"gpu.{domain}-clock-{endpoint}", Number: value, Unit: ControlUnits.Megahertz);
    private static ControlObject Gpu(string domain) => new("gpu:recorded", ControlObjectKinds.Gpu, "Recorded GPU",
        new(ControlOperatingSystems.Windows, ControlVendors.Nvidia),
        [new($"gpu.{domain}-clock-minimum", "Minimum", ControlValueKinds.Number, true, Range: new(0, 3000, 15, "MHz")),
         new($"gpu.{domain}-clock-maximum", "Maximum", ControlValueKinds.Number, true, Range: new(0, 3000, 15, "MHz"))],
        AdapterIndex: 0, GpuAttachment: ControlGpuAttachments.Discrete);
    private sealed class Catalog(ControlObject gpu) : IControlObjectCatalog
    { public ControlObjectCatalog ReadObjects() => new([gpu], DateTimeOffset.UtcNow); }
    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class RecordedNvapi : NvidiaNvapiControlBridge
    { public override IntPtr? FindHandleByAdapterIndex(int index) => null; }
    private sealed class RecordedNvml : NvidiaNvmlControlBridge
    {
        public readonly List<(uint Minimum, uint Maximum)> Pairs = [];
        public int Probes;
        public override IntPtr? FindHandleByAdapterIndex(int index) { Probes++; return new(1); }
        public override double? ReadMaximumClockMhz(IntPtr device, uint type) => 3000;
        public override int WriteLockedClocks(IntPtr device, uint type, uint minimum, uint maximum)
        { Pairs.Add((minimum, maximum)); return 0; }
    }
}
