using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class TopologyWmiPciLibraryIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedFirmwareFactsAreOwnerReadOnceWhileDeviceFactsRemainDemandDriven(bool unavailable)
    {
        var calls = new List<string>();
        var reader = new WindowsDeviceTopologyWmiFactsReader(TimeSpan.FromSeconds(1), (_, query, _) =>
        {
            calls.Add(query);
            return new([], unavailable ? "unavailable" : null);
        });
        foreach (var fact in new[] { WindowsDeviceTopologyWmiFact.Bios, WindowsDeviceTopologyWmiFact.ComputerSystem,
            WindowsDeviceTopologyWmiFact.BaseBoard })
        {
            var first = reader.Read(fact);
            for (var index = 0; index < 100; index++) Assert.Same(first, reader.Read(fact));
        }
        Assert.Equal(3, calls.Count);
        reader.Read(WindowsDeviceTopologyWmiFact.PnpEntities);
        reader.Read(WindowsDeviceTopologyWmiFact.PnpEntities);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public void SelectedWmiSourceIsExplicitAndConstructionDoesNotQuery()
    {
        var calls = new List<(string Scope, string Query, TimeSpan Timeout)>();
        var failure = new DeviceTopologyWmiQueryResult([], "source-unavailable");
        var reader = new WindowsDeviceTopologyWmiFactsReader(TimeSpan.FromMilliseconds(123), (scope, query, timeout) => {
            calls.Add((scope, query, timeout)); return failure;
        });
        Assert.Empty(calls);
        Assert.Same(failure, reader.Read(WindowsDeviceTopologyWmiFact.UsbAssociations));
        var call = Assert.Single(calls);
        Assert.Equal(@"root\CIMV2", call.Scope);
        Assert.Equal("SELECT Antecedent, Dependent FROM Win32_USBControllerDevice", call.Query);
        Assert.Equal(TimeSpan.FromMilliseconds(123), call.Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read((WindowsDeviceTopologyWmiFact)7));
        Assert.Single(calls);
    }

    [Fact]
    public void NetworkWmiRetainsOriginalConflictAndUnknownSpeedSemantics()
    {
        var capture = WindowsNetworkAdapterTopologyReader.ReadSnapshot(TimeSpan.FromMilliseconds(123), (scope, query, timeout) => {
            Assert.Equal(@"root\StandardCimv2", scope);
            Assert.EndsWith("FROM MSFT_NetAdapter", query);
            Assert.Equal(TimeSpan.FromMilliseconds(123), timeout);
            return new([
                new() { ["PnPDeviceID"] = @"PCI\DUPLICATE", ["TransmitLinkSpeed"] = 1UL },
                new() { ["PnPDeviceID"] = @"pci\duplicate", ["TransmitLinkSpeed"] = 2UL },
                new() { ["PnPDeviceID"] = @" PCI\EXACT ", ["TransmitLinkSpeed"] = ulong.MaxValue, ["ReceiveLinkSpeed"] = 0UL }
            ], "partial-query");
        });
        Assert.Equal("partial-query", capture.Error);
        var adapter = Assert.Single(capture.Adapters).Value;
        Assert.Equal(@"PCI\EXACT", adapter.DeviceId);
        Assert.Null(adapter.TransmitLinkSpeedBitsPerSecond);
        Assert.Equal(0UL, adapter.ReceiveLinkSpeedBitsPerSecond);
    }

    [Fact]
    public void ProductUsesTheSameCanonicalPciProjectionWithoutMetadataFallback()
    {
        var port = DeviceInformationTestData.Port(@"PCI\DECLARED") with {
            PciIdentification = PciDeviceIdentificationReader.Read([@"PCI\VEN_1414&DEV_0000&REV_00"], [@"PCI\CC_030000"]),
            HardwareIds = [@"PCI\VEN_DEAD&DEV_BEEF"], PciResourceRanges = [], PciInterrupts = null
        };
        var independent = WindowsInformationFieldProjection.Pci(port);
        var product = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port), DateTimeOffset.UtcNow);
        foreach (var field in independent)
        {
            var actual = DeviceInformationTestData.Field(product, @"windows-pnp|PCI\DECLARED", field.Definition.Name);
            Assert.Equal(field.Definition, actual.Definition);
            Assert.Equal(field.Value?.GetRawText(), actual.Value?.GetRawText());
        }
        Assert.Equal("0000", independent.Single(field => field.Definition == DeviceInformationFieldRegistry.PciAllDeviceId).Value!.Value.GetString());
        Assert.Equal(0, independent.Single(field => field.Definition == DeviceInformationFieldRegistry.PciAllResourceRange).Value!.Value.GetArrayLength());
        Assert.Null(independent.Single(field => field.Definition == DeviceInformationFieldRegistry.PciAllInterruptsAllocatedReported).Value);
    }
}
