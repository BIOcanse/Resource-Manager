using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using Device = ResourceManager.ReadWrite.Vendors.Intel.IntelGraphicsControlBridge.DeviceIdentity;
using Properties = ResourceManager.ReadWrite.Vendors.Intel.IntelGraphicsControlBridge.CtlDeviceAdapterProperties;

namespace Resource_Manager_APP.Tests;

public sealed class IntelIntegratedGpuIdentityTests
{
    private static readonly ControlCapability Capability = new("gpu.core-clock-offset", "offset", ControlValueKinds.Number, true,
        Range: new(-200, 200, 1, ControlUnits.Megahertz));
    private static ControlObject Target(ulong? key = 0x800000001) => new("gpu:test", ControlObjectKinds.Gpu, "Intel iGPU",
        new(ControlOperatingSystems.Windows, ControlVendors.Intel), [], AdapterIndex: 0,
        GpuAttachment: ControlGpuAttachments.Integrated) { AdapterKey = key };
    private static Device Igpu => new(new IntPtr(22), 0x800000001, 0x8086, 1, 1);
    private static Device Dgpu => new(new IntPtr(11), 0x800000002, 0x8086, 1, 0);

    [Fact]
    public void LayoutMatchesCompiledIntelHeaderOnWindowsX64()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(320, Marshal.SizeOf<Properties>());
        foreach (var (field, expected) in new[] { (nameof(Properties.DeviceId), 8), (nameof(Properties.DeviceIdSize), 16),
                     (nameof(Properties.DeviceType), 20), (nameof(Properties.PciVendorId), 64),
                     (nameof(Properties.GraphicsAdapterProperties), 188), (nameof(Properties.Reserved), 208) })
            Assert.Equal(expected, Marshal.OffsetOf<Properties>(field).ToInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeReadWaiverAndWriteUseTheSelectedLuidRegardlessOfEnumerationOrder(bool reverse)
    {
        var hardware = new Hardware(reverse ? [Igpu, Dgpu] : [Dgpu, Igpu]);
        var writer = Writer(hardware);
        Assert.True(writer.Probe(Target(), Capability).CanWrite);
        Assert.Empty(hardware.Calls);
        Assert.Equal(15, (await writer.ReadAsync(Target(), Capability, default))!.Number);
        var result = await writer.WriteAsync(Target(), Capability, new(Capability.Id, Number: 45), default);
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        Assert.Equal(new[] { "read:22", "waiver:22", "write:22", "read:22" }, hardware.Calls);
        Assert.Equal(45, hardware.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    [InlineData(0x800000002UL)]
    [InlineData(0x800000003UL)]
    public async Task MissingOrDiscreteIdentityDoesNotReadOrWriteAnotherAdapter(ulong? key)
    {
        var hardware = new Hardware([Dgpu, Igpu]);
        var writer = Writer(hardware);
        Assert.False(writer.Probe(Target(key), Capability).CanWrite);
        Assert.Equal(IntegratedGpuControlWriter.IntelTargetMissing, (await writer.ReadAsync(Target(key), Capability, default))!.UnreadableReason);
        Assert.Equal(ControlApplyStatuses.Failed, (await writer.WriteAsync(Target(key), Capability, new(Capability.Id, Number: 45), default)).Status);
        Assert.Empty(hardware.Calls);
    }

    [Fact]
    public void ConflictingOrInvalidDriverIdentitiesAreNotSelectable()
    {
        Assert.Null(IntelGraphicsControlBridge.SelectIntegratedDevice(Igpu.AdapterKey, [Igpu, Igpu with { Handle = new IntPtr(33) }]));
        foreach (var invalid in new[] { Igpu with { Handle = IntPtr.Zero }, Igpu with { VendorId = 0x1002 },
                     Igpu with { DeviceType = 2 }, Igpu with { Properties = 2 }, Igpu with { AdapterKey = 0 } })
            Assert.Null(IntelGraphicsControlBridge.SelectIntegratedDevice(Igpu.AdapterKey, [null, Dgpu, invalid]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(150d)]
    public async Task WriteCannotClaimAppliedWithoutFiniteMatchingReadback(double? readback)
    {
        var hardware = new Hardware([Igpu]) { OverrideReadback = true, Readback = readback };
        Assert.Equal(ControlApplyStatuses.Failed, (await Writer(hardware).WriteAsync(Target(), Capability, new(Capability.Id, Number: 45), default)).Status);
    }

    [Fact]
    public async Task DiscreteObjectAndMissingConsentDoNotSubmitWaiverOrWrite()
    {
        var hardware = new Hardware([Igpu]);
        var discrete = Target() with { GpuAttachment = ControlGpuAttachments.Discrete };
        Assert.Equal(ControlApplyStatuses.Failed, (await Writer(hardware).WriteAsync(discrete, Capability, new(Capability.Id, Number: 45), default)).Status);
        Assert.Null(await Writer(hardware).ReadAsync(discrete, Capability, default));
        Assert.Equal(ControlApplyStatuses.Failed, (await Writer(hardware, accepted: false).WriteAsync(Target(), Capability, new(Capability.Id, Number: 45), default)).Status);
        Assert.Empty(hardware.Calls);
    }

    [Fact]
    public void CatalogExecutionIdentityRequiresCompleteCurrentInventoryAndDoesNotEnterTheWireContract()
    {
        var row = new SchedulingGpuAdapterObservation(0, Igpu.AdapterKey, SchedulingGpuCapabilityMask.None, SchedulingGpuMetricMask.None,
            SamplingObservationStatus.Unavailable, SamplingObservationStatus.Unsupported, 0, 0, 0, 2, 100);
        var inventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Current, 2, 100, 1, 0, 0, 1, [row]);
        Assert.Equal(Igpu.AdapterKey, WindowsControlObjectCatalog.ResolveAdapterKey(inventory, 0));
        Assert.Null(WindowsControlObjectCatalog.ResolveAdapterKey(inventory, 1));
        Assert.Null(WindowsControlObjectCatalog.ResolveAdapterKey(inventory with { Status = SamplingObservationStatus.RetainedLastGood }, 0));
        Assert.Null(WindowsControlObjectCatalog.ResolveAdapterKey(inventory with { ObservedCount = 2, Adapters = [row, row] }, 0));
        Assert.DoesNotContain("AdapterKey", JsonSerializer.Serialize(Target()));
    }

    private static IntegratedGpuControlWriter Writer(Hardware hardware, bool accepted = true)
        => new(null!, new Consent(accepted), new EnvironmentInfo(), hardware);

    private sealed class Hardware(Device?[] devices) : IIntelGraphicsControlHardware
    {
        public bool IsAvailable => true;
        public List<string> Calls { get; } = [];
        public double Offset { get; private set; } = 15;
        public bool OverrideReadback { get; init; }
        public double? Readback { get; init; }
        public IntPtr? FindIntegratedDevice(ulong? key) => IntelGraphicsControlBridge.SelectIntegratedDevice(key ?? 0, devices);
        public bool TryAcceptOverclockWaiver(IntPtr device) { Calls.Add($"waiver:{device}"); return true; }
        public double? ReadFrequencyOffsetMhz(IntPtr device) { Calls.Add($"read:{device}"); return OverrideReadback ? Readback : Offset; }
        public int WriteFrequencyOffsetMhz(IntPtr device, double offset) { Calls.Add($"write:{device}"); Offset = offset; return 0; }
    }

    private sealed class Consent(bool accepted) : IControlOverclockConsent
    {
        public Task<bool> IsAcceptedAsync(CancellationToken cancellationToken) => Task.FromResult(accepted);
        public Task SetAcceptedAsync(bool value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EnvironmentInfo : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
