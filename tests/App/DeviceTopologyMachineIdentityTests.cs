using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyMachineIdentityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(14)] [InlineData(30)] [InlineData(31)] [InlineData(32)]
    public void PortableChassisIsLaptopEvenWithoutBattery(byte code)
        => Assert.Equal("laptop", Resolve([code], false).FormFactor);

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(13)] [InlineData(15)] [InlineData(16)] [InlineData(35)] [InlineData(36)]
    public void DesktopChassisIsDesktop(byte code)
        => Assert.Equal("desktop", Resolve([code], false).FormFactor);

    [Fact]
    public void BatteryAloneIsLaptopAndUnknownFactsDoNotInvalidateKnownEvidence()
    {
        Assert.Equal("laptop", Resolve(null, true).FormFactor);
        Assert.Equal("laptop", Resolve([10], null).FormFactor);
        Assert.Equal("laptop", Resolve([3], true).FormFactor);
        Assert.Equal("workstation", Resolve(null, null, ["Intel Xeon W-2495X"]).FormFactor);
    }

    [Theory]
    [InlineData("AMD Ryzen Threadripper PRO 9955WX", "amd-threadripper-pro")]
    [InlineData("AMD Ryzen Threadripper 7980X", "amd-threadripper")]
    [InlineData("AMD EPYC 9654", "amd-epyc")]
    [InlineData("Intel(R) Xeon(R) w9-3495X", "intel-xeon")]
    public void WorkstationCpuOverridesDesktopChassis(string name, string rule)
    {
        var identity = Resolve([7], false, [name]);
        Assert.Equal("workstation", identity.FormFactor);
        Assert.Equal(new("cpu", name, rule), Assert.Single(identity.Evidence.WorkstationComponents));
    }

    [Theory]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "nvidia-rtx-pro")]
    [InlineData("NVIDIA RTX A6000", "nvidia-rtx-a")]
    [InlineData("NVIDIA RTX 6000 Ada Generation", "nvidia-rtx-ada")]
    [InlineData("NVIDIA RTX 4000 SFF Ada Generation", "nvidia-rtx-ada")]
    [InlineData("NVIDIA RTX 5000 Ada Generation Laptop GPU", "nvidia-rtx-ada")]
    [InlineData("nvidia quadro RTX 8000", "nvidia-quadro")]
    [InlineData("AMD Radeon PRO W7900", "amd-radeon-pro")]
    [InlineData("AMD Radeon(TM) Pro Graphics", "amd-radeon-pro")]
    public void ProfessionalGpuIsWorkstationAndPortableIsMobileWorkstation(string name, string rule)
    {
        var workstation = Resolve([3], false, gpus: [name]);
        Assert.Equal("workstation", workstation.FormFactor);
        var laptop = Resolve([10], true, ["AMD Ryzen Threadripper PRO 9955WX"], [name]);
        Assert.Equal("mobile_workstation", laptop.FormFactor);
        Assert.Contains(new("gpu", name, rule), laptop.Evidence.WorkstationComponents);
        Assert.Contains(laptop.Evidence.WorkstationComponents, c => c.RuleId == "amd-threadripper-pro");
    }

    [Theory]
    [InlineData("Intel(R) Xeon(R) W-10885M")]
    [InlineData("AMD Ryzen Threadripper PRO 9955WX")]
    public void PortableWithWorkstationCpuIsMobileWorkstation(string cpu)
        => Assert.Equal("mobile_workstation", Resolve([10], true, [cpu]).FormFactor);

    [Theory]
    [InlineData(true, "laptop")]
    [InlineData(false, "mobile_workstation")]
    [InlineData(null, "laptop")]
    public void OnlyConfirmedMachineGpuCountsTowardsFormFactor(bool? detachable, string expected)
    {
        const string model = "NVIDIA RTX PRO 6000";
        var evidence = Resolve([10], true, gpus: [model]).Evidence with {
            GpuAttachments = new([new(model, detachable, detachable.HasValue ? detachable.Value ? 0x400u : 0u : null)], null)
        };
        var identity = DeviceTopologyMachineClassifier.Default.Classify(evidence);
        Assert.Equal(expected, identity.FormFactor);
        var gpu = Assert.Single(identity.Evidence.GpuAttachments.Value!);
        Assert.Equal(detachable == true ? "graphics_dock" : null, gpu.AttachmentKind);
        Assert.Equal(detachable == false, identity.Evidence.WorkstationComponents.Any(c => c.Component == "gpu"));
        Assert.Contains(model, identity.Evidence.GpuModels.Value!);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("NVIDIA RTX 9999")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("NotNVIDIA RTX PRO 6000")]
    public void ConsumerOrUnmatchedGpuDoesNotMakeWorkstation(string name)
    {
        Assert.Equal("desktop", Resolve([3], false, ["AMD Ryzen 9 9950X"], [name]).FormFactor);
        Assert.Empty(Resolve([3], false, gpus: [name]).Evidence.WorkstationComponents);
    }

    [Fact]
    public void MissingUnclassifiedOrContradictoryFactsAreUnknown()
    {
        var missing = Resolve(null, null);
        Assert.Equal("unknown", missing.FormFactor);
        Assert.Null(missing.Evidence.ChassisTypes.Value);
        Assert.Equal("source-unavailable", missing.Evidence.ChassisTypes.Reason);
        Assert.Equal("unknown", Resolve([2, 12], false).FormFactor);
        Assert.Equal("unknown", Resolve([10, 7], true, gpus: ["NVIDIA RTX PRO 6000"]).FormFactor);
        Assert.Equal("unknown", Resolve([31, 23], null).FormFactor);
    }

    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(0u, false)]
    [InlineData(0xa0000000u, false)] // UPS: system + short term.
    public void SystemBatteryRequiresSystemFlagAndExcludesUps(uint flags, bool expected)
    {
        var fact = WindowsDeviceTopologyReader.SystemBatteryEvidence(Battery(flags));
        Assert.Equal(expected, fact.Value);
        Assert.Null(fact.Reason);
    }

    [Fact]
    public void FailedAndPartialBatteryReadsDoNotFabricateAbsence()
    {
        var unavailable = WindowsDeviceTopologyReader.SystemBatteryEvidence(new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []));
        Assert.Null(unavailable.Value);
        Assert.Equal("source-unavailable", unavailable.Reason);
        var partial = WindowsDeviceTopologyReader.SystemBatteryEvidence(Battery(0) with { RejectedCount = 1 });
        Assert.Null(partial.Value);
        Assert.Equal("partial-enumeration", partial.Reason);
        Assert.True(WindowsDeviceTopologyReader.SystemBatteryEvidence(Battery(0x80000000) with { RejectedCount = 1 }).Value);
    }

    [Fact]
    public void IdentityAndEvidenceChangesAreSemanticButFactOrderAndCaptureTimeAreNot()
    {
        var first = DeviceInformationTestData.Snapshot() with {
            MachineIdentity = Resolve([3, 7], false, gpus: ["NVIDIA Quadro P6000", "NVIDIA RTX PRO 6000"])
        };
        var comparer = new DeviceTopologySemanticComparer();
        var reordered = first with { CapturedAt = first.CapturedAt.AddSeconds(1), MachineIdentity = Resolve([7, 3], false,
            gpus: ["NVIDIA RTX PRO 6000", "NVIDIA Quadro P6000"]) };
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(reordered));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { MachineIdentity = Resolve([10], true) }));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with {
            MachineIdentity = Resolve([3, 7], false, gpus: ["NVIDIA RTX PRO 6000"]) }));
    }

    [Fact]
    public void AddedSourceFactsPublishChangesWithoutTreatingOrderingAsTopologyChange()
    {
        var wireless = new DeviceTopologyWirelessConnection("wlan-api", 1, [7, 10], ["802.11n", "802.11ax"], 7, "802.11n");
        var network = new DeviceTopologyNetworkConnection(null, "connected", null, null, null, null, true, true) { Wireless = wireless };
        var usb = WindowsDeviceTopologyReader.CreateUsbConnection(DeviceTopologyUsbClassFactsTests.Port(
            [new(0, 0, 3, 1, 1), new(1, 0, 14, 1, 0)]), false)!;
        var first = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("NET") with { Network = network, Usb = usb });
        var comparer = new DeviceTopologySemanticComparer();
        var reordered = first with { Ports = [first.Ports[0] with {
            Network = network with { Wireless = wireless with { SupportedPhyTypesRaw = [10, 7], SupportedStandards = ["802.11ax", "802.11n"] } },
            Usb = usb with { Interfaces = usb.Interfaces!.Reverse().ToArray() }
        }] };
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(reordered));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { Ports = [first.Ports[0] with {
            Network = network with { Wireless = wireless with { SupportedPhyTypesRaw = [7, 10, 11], SupportedStandards = ["802.11n", "802.11ax", "802.11be"] } }
        }] }));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { Ports = [first.Ports[0] with {
            Usb = usb with { Interfaces = [new(0, 0, 3, 1, 2), new(1, 0, 14, 1, 0)] }
        }] }));
        var peer = new DeviceTopologyExternalMachine("USB4\\A", null, "workstation", null, [new("soc", null)],
            new("windows-usb4net", @"USB4\USB4NET", 1, "windows-peer-properties-unavailable"));
        var withPeers = first with { ExternalMachines = [peer, peer with { LocalLinkDeviceId = "USB4\\B" }] };
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(withPeers));
        Assert.Equal(comparer.ComputeHash(withPeers), comparer.ComputeHash(withPeers with { ExternalMachines = withPeers.ExternalMachines!.Reverse().ToArray() }));
    }

    [Fact]
    public void RealMachineSmokePrintsProductionIdentityAndRawEvidence()
    {
        var identity = WindowsDeviceTopologyReader.ReadMachineIdentity(SmbiosFirmwareInformationReader.Read(),
            new WindowsBatteryReader().ReadIdentities(includeDeclarations: true),
            new WindowsGpuAdapterOrderReader().ReadInventory());
        var json = JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        output.WriteLine(json);
        Assert.Equal("laptop", identity.FormFactor);
        Assert.True(identity.Evidence.ChassisTypes.Value?.Any(c => c is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32) == true
            || identity.Evidence.SystemBatteryPresent.Value == true, json);
    }

    [Fact]
    public void KnownProfessionalGpuNameDoesNotRequirePhysicalIdentityMapping()
    {
        var adapter = new WindowsGpuAdapter(0, "NVIDIA RTX PRO 6000", 0x10de, 0, 0,
            default, false, 0, WindowsGpuAdapterKind.Dedicated) { AdapterTypeFlags = 0 };
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current,
            1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1, [adapter]);
        Assert.Null(WindowsGpuInformationProjection.Create(inventory));
        var identity = WindowsDeviceTopologyReader.ReadMachineIdentity(null,
            new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), inventory);
        Assert.Equal("workstation", identity.FormFactor);
        Assert.Contains(new DeviceTopologyWorkstationComponent("gpu", adapter.Name, "nvidia-rtx-pro"),
            identity.Evidence.WorkstationComponents);
        Assert.Equal("source-unavailable", WindowsDeviceTopologyReader.GpuModelEvidence(inventory with {
            Status = SamplingObservationStatus.Unavailable }).Reason);
        Assert.Equal("missing-model", WindowsDeviceTopologyReader.GpuModelEvidence(inventory with {
            Adapters = [adapter with { Name = string.Empty }] }).Reason);
    }

    internal static DeviceTopologyMachineIdentity Resolve(byte[]? chassis, bool? battery,
        string[]? cpus = null, string[]? gpus = null) => DeviceTopologyMachineClassifier.Default.Classify(new(
            new(chassis, chassis is null ? "source-unavailable" : null),
            new(battery, battery is null ? "source-unavailable" : null),
            new(cpus, cpus is null ? "source-unavailable" : null),
            new(gpus, gpus is null ? "source-unavailable" : null), []) {
                GpuAttachments = new(gpus?.Select(static name => new DeviceTopologyMachineGpuAttachment(name, false, 0)).ToArray(),
                    gpus is null ? "source-unavailable" : null) });

    private static BatteryTopologyRead Battery(uint flags) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
        [new(0, "path", "id", new(flags, "LION", 10, 10, 0))]);
}
