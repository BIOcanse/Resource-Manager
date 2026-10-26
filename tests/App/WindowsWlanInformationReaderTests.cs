using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsWlanInformationReaderTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public WindowsWlanInformationReaderTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;
    private static readonly Guid Identity = Guid.Parse("12345678-abcd-1234-8001-020304050607");

    [Fact]
    public void ProductionCaptureReportsActualWlanFactsWithoutAssumingLocalAdapters()
    {
        var capture = new WindowsWlanInformationReader().Read();
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(capture));
        if (capture.Interfaces is null) Assert.False(string.IsNullOrWhiteSpace(capture.Error));
        foreach (var pair in capture.Interfaces ?? new Dictionary<Guid, DeviceTopologyWirelessConnection>())
        {
            Assert.NotEqual(Guid.Empty, pair.Key);
            Assert.Equal("wlan-api", pair.Value.Source);
        }
    }

    [Fact]
    public void CapabilityAndNegotiationComeFromIndependentNativeFactsAndSessionIsDisposed()
    {
        var api = new Session { Capability = Capability(11, 10, 7, 10), Connection = Connection(1, 7) };
        var result = new WindowsWlanInformationReader(() => api).Read();
        var value = result.Interfaces![Identity];
        Assert.Equal("wlan-api", value.Source);
        Assert.Equal(new uint[] { 7, 10, 11 }, value.SupportedPhyTypesRaw);
        Assert.Equal(new[] { "802.11ax", "802.11be", "802.11n" }, value.SupportedStandards);
        Assert.Equal(7U, value.NegotiatedPhyTypeRaw);
        Assert.Equal("802.11n", value.NegotiatedStandard);
        Assert.Null(result.Error);
        Assert.Equal(1, api.DirectoryReads); Assert.Equal(1, api.CapabilityReads); Assert.Equal(1, api.ConnectionReads);
        Assert.True(api.Disposed);
    }

    [Fact]
    public void DisconnectedCapabilityDoesNotQueryOrInventNegotiation()
    {
        var api = new Session { Directory = Directory(4), Capability = Capability(11) };
        var value = new WindowsWlanInformationReader(() => api).Read().Interfaces![Identity];
        Assert.Equal("802.11be", Assert.Single(value.SupportedStandards!));
        Assert.Null(value.NegotiatedPhyTypeRaw); Assert.Null(value.NegotiatedStandard);
        Assert.Equal(0, api.ConnectionReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceFailureClearsOnlyFailedFact(bool capabilityFails)
    {
        var api = new Session { CapabilityFails = capabilityFails, ConnectionFails = !capabilityFails };
        var result = new WindowsWlanInformationReader(() => api).Read();
        var value = result.Interfaces![Identity];
        Assert.NotNull(result.Error);
        if (capabilityFails) { Assert.Null(value.SupportedStandards); Assert.Null(value.SupportedPhyTypesRaw); Assert.Equal("802.11ax", value.NegotiatedStandard); }
        else { Assert.Equal("802.11be", Assert.Single(value.SupportedStandards!)); Assert.Null(value.NegotiatedPhyTypeRaw); }
        Assert.True(api.Disposed);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(12U)]
    [InlineData(0x80000000U)]
    [InlineData(uint.MaxValue)]
    public void UnknownAndVendorPhysRemainRawAndNeverBecomeMarketingStandards(uint phy)
    {
        var api = new Session { Capability = Capability(phy), Connection = Connection(1, phy) };
        var value = new WindowsWlanInformationReader(() => api).Read().Interfaces![Identity];
        Assert.Equal(phy, Assert.Single(value.SupportedPhyTypesRaw!));
        Assert.Null(value.SupportedStandards); Assert.Null(value.NegotiatedStandard);
        Assert.Equal(phy == 0 ? null : (uint?)phy, value.NegotiatedPhyTypeRaw);
    }

    [Theory]
    [InlineData(4U, "802.11a")]
    [InlineData(5U, "802.11b")]
    [InlineData(6U, "802.11g")]
    [InlineData(7U, "802.11n")]
    [InlineData(8U, "802.11ac")]
    [InlineData(9U, "802.11ad")]
    [InlineData(10U, "802.11ax")]
    [InlineData(11U, "802.11be")]
    public void OfficialDot11PhyIdsMapToStableIeeeStandardIds(uint phy, string expected)
        => Assert.Equal(expected, WindowsWlanInformationReader.Standard(phy));

    [Fact]
    public void EmptySuccessfulDirectoryAndCapabilityDifferFromUnavailable()
    {
        var api = new Session { Directory = new byte[8] };
        var result = new WindowsWlanInformationReader(() => api).Read();
        Assert.Empty(result.Interfaces!); Assert.Null(result.Error); Assert.Equal(0, api.CapabilityReads);
        api = new Session { Capability = Capability() };
        var value = new WindowsWlanInformationReader(() => api).Read().Interfaces![Identity];
        Assert.Empty(value.SupportedStandards!); Assert.Empty(value.SupportedPhyTypesRaw!);
        Assert.Null(new WindowsWlanInformationReader(() => throw new IOException("offline")).Read().Interfaces);
    }

    [Fact]
    public void InvalidDirectoryIsRejectedBeforeReadingAdapters()
    {
        var api = new Session { Directory = Directory(1)[..^1] };
        Assert.Null(new WindowsWlanInformationReader(() => api).Read().Interfaces);
        Assert.Equal(0, api.CapabilityReads); Assert.True(api.Disposed);
        var duplicate = new byte[8 + 2 * 532]; Put(duplicate, 0, 2);
        Directory(1).AsSpan(8).CopyTo(duplicate.AsSpan(8));
        Directory(1).AsSpan(8).CopyTo(duplicate.AsSpan(540));
        Assert.Throws<InvalidDataException>(() => WindowsWlanInformationReader.DecodeInterfaces(duplicate));
        Put(duplicate, 0, 1025);
        Assert.Throws<InvalidDataException>(() => WindowsWlanInformationReader.DecodeInterfaces(duplicate));
    }

    [Fact]
    public void MalformedAndChangedConnectionBuffersCannotManufactureFacts()
    {
        var data = Capability(11); Put(data, 16, 65);
        Assert.Throws<InvalidDataException>(() => WindowsWlanInformationReader.DecodeCapability(data));
        Assert.Throws<InvalidDataException>(() => WindowsWlanInformationReader.DecodeCapability(new byte[275]));
        Assert.Throws<InvalidDataException>(() => WindowsWlanInformationReader.DecodeCurrentConnection(new byte[603]));
        Assert.Null(WindowsWlanInformationReader.DecodeCurrentConnection(Connection(4, 11)));
    }

    [Theory]
    [InlineData(0U, false)]
    [InlineData(5U, false)]
    [InlineData(0U, true)]
    public void NativeBuffersAreFreedOnSuccessErrorAndRejectedLength(uint code, bool invalidSize)
    {
        var pointer = Marshal.AllocHGlobal(8); var freed = 0;
        try
        {
            byte[] Read() => WindowsWlanSession.ReadOwnedBuffer((out nint value) => { value = pointer; return code; },
                value => { Assert.Equal(pointer, value); freed++; }, _ => invalidSize ? throw new InvalidDataException() : 8);
            if (code != 0 || invalidSize) Assert.ThrowsAny<Exception>(() => Read()); else Assert.Equal(8, Read().Length);
            Assert.Equal(1, freed);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void AdapterNamesNeverJoinWirelessFactsAndNewFailuresClearOldValues()
    {
        var adapter = new DeviceTopologyNetworkAdapter("PCI\\DEVICE", "Wi-Fi 7 802.11be", 1, null, null, null, null, true, true)
            { InterfaceGuid = Identity.ToString("D") };
        var adapters = new DeviceTopologyNetworkAdapterSnapshot(new Dictionary<string, DeviceTopologyNetworkAdapter> { [adapter.DeviceId] = adapter }, null);
        var source = new WindowsWlanInformationReader(() => new Session()).Read();
        var joined = WindowsDeviceTopologyReader.ApplyWirelessInformation(adapters, source);
        Assert.Equal("802.11ax", WindowsDeviceTopologyReader.CreateNetworkConnection(joined.Adapters[adapter.DeviceId])!.Wireless!.NegotiatedStandard);
        Assert.Null(WindowsDeviceTopologyReader.ApplyWirelessInformation(joined, new(null, "offline")).Adapters[adapter.DeviceId].Wireless);
        adapters = adapters with { Adapters = new Dictionary<string, DeviceTopologyNetworkAdapter> { [adapter.DeviceId] = adapter with { InterfaceGuid = Guid.NewGuid().ToString() } } };
        Assert.Null(WindowsDeviceTopologyReader.ApplyWirelessInformation(adapters, source).Adapters[adapter.DeviceId].Wireless);
    }

    private static byte[] Directory(uint state)
    {
        var data = new byte[540]; Put(data, 0, 1); Identity.TryWriteBytes(data.AsSpan(8, 16)); Put(data, 536, state); return data;
    }
    private static byte[] Capability(params uint[] phys)
    {
        var data = new byte[276]; Put(data, 16, (uint)phys.Length);
        for (var index = 0; index < phys.Length; index++) Put(data, 20 + 4 * index, phys[index]);
        return data;
    }
    private static byte[] Connection(uint state, uint phy)
    {
        var data = new byte[604]; Put(data, 0, state); Put(data, 568, phy); return data;
    }
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    private sealed class Session : IWindowsWlanSession
    {
        public byte[] Directory = WindowsWlanInformationReaderTests.Directory(1);
        public byte[] Capability = WindowsWlanInformationReaderTests.Capability(11);
        public byte[] Connection = WindowsWlanInformationReaderTests.Connection(1, 10);
        public bool CapabilityFails, ConnectionFails, Disposed;
        public int DirectoryReads, CapabilityReads, ConnectionReads;
        public byte[] ReadInterfaces() { DirectoryReads++; return Directory; }
        public byte[] ReadCapability(Guid guid) { Assert.Equal(Identity, guid); CapabilityReads++; return CapabilityFails ? throw new IOException("capability failed") : Capability; }
        public byte[] ReadCurrentConnection(Guid guid) { Assert.Equal(Identity, guid); ConnectionReads++; return ConnectionFails ? throw new IOException("connection failed") : Connection; }
        public void Dispose() => Disposed = true;
    }
}
