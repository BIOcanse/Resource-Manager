using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class NetworkAddressInformationCollectorTests
{
    internal const string IdentityA = "windows-network-interface|12345678-ABCD-1234-8001-020304050607";
    internal const string IdentityB = "windows-network-interface|FEDCBA98-DCBA-4321-8101-020304050608";
    internal static IReadOnlyList<DeviceTopologyNetworkInterfaceInformation> Declarations() => new WindowsNetworkInterfaceInformationReader(new Api()).Read()!;

    [Theory]
    [InlineData("windows-sdk-tables.json", 8)]
    [InlineData("windows-sdk-tables-x86.json", 4)]
    public void IndependentOfficialSdkTablesVerifyBothAbisAndActualNetworkOrderDecoder(string fixture, int pointerSize)
    {
        using var data = Fixture(fixture);
        var json = data.RootElement;
        Assert.Equal(pointerSize, json.GetProperty("pointerSize").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.InterfaceRowSize, json.GetProperty("interfaceRowSize").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.UnicastRowSize, json.GetProperty("addressRowSize").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.TableOffset, json.GetProperty("interfaceTableOffset").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.TableOffset, json.GetProperty("addressTableOffset").GetInt32());
        Assert.Equal(12, json.GetProperty("interfaceGuidOffset").GetInt32());
        Assert.Equal(32, json.GetProperty("luidOffset").GetInt32());
        Assert.Equal(72, json.GetProperty("creationOffset").GetInt32());
        Assert.Equal(68, json.GetProperty("scopeOffset").GetInt32());
        Assert.Equal(4, json.GetProperty("socketV4Offset").GetInt32());
        Assert.Equal(8, json.GetProperty("socketV6Offset").GetInt32());
        Assert.Equal(24, json.GetProperty("socketScopeOffset").GetInt32());
        var api = new Api { Interfaces = Raw("interfaceTableHex", fixture), Unicast = Raw("addressTableHex", fixture) };
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        Assert.Equal(2, rows.Count);
        var row = Assert.Single(rows, static row => row.DeviceIdentity == IdentityA);
        Assert.Equal(0xFEDCBA9876543210UL, row.InterfaceLuid);
        Assert.Equal(0xF0000001U, row.InterfaceIndex);
        var v4 = Assert.Single(row.UnicastAddresses!, static a => a.AddressFamily == "ipv4");
        Assert.Equal("198.51.100.7", v4.Address); Assert.Equal((byte)24, v4.OnLinkPrefixLength);
        Assert.Null(v4.SocketScopeId); Assert.Equal(0U, v4.ScopeIdRaw);
        Assert.Equal(4U, v4.PrefixOriginRaw); Assert.Equal(5U, v4.SuffixOriginRaw); Assert.Equal(4U, v4.DadStateRaw);
        Assert.Equal(uint.MaxValue, v4.ValidLifetimeSeconds); Assert.Equal(17U, v4.PreferredLifetimeSeconds);
        Assert.True(v4.SkipAsSource); Assert.Equal("9223372036854775794", v4.CreationWindowsFileTime100NsRaw);
        var v6 = Assert.Single(row.UnicastAddresses!, static a => a.AddressFamily == "ipv6");
        Assert.Equal("fe80::1234", v6.Address); Assert.Equal((byte)64, v6.OnLinkPrefixLength);
        Assert.Equal(55U, v6.SocketScopeId); Assert.Equal(uint.MaxValue, v6.ScopeIdRaw);
        Assert.Equal(41U, v6.PrefixOriginRaw); Assert.Equal(43U, v6.SuffixOriginRaw); Assert.Equal(49U, v6.DadStateRaw);
        Assert.Equal(0U, v6.ValidLifetimeSeconds); Assert.Equal(0U, v6.PreferredLifetimeSeconds);
        Assert.False(v6.SkipAsSource); Assert.Equal("-1", v6.CreationWindowsFileTime100NsRaw);
        Assert.Empty(Assert.Single(rows, static row => row.DeviceIdentity == IdentityB).UnicastAddresses!);
        Assert.Equal(1, api.InterfaceReads); Assert.Equal(1, api.UnicastReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidDirectoryDoesNotQueryUnicastOrGuessIdentity(int mode)
    {
        var api = new Api();
        if (mode == 0) api.Interfaces = null;
        if (mode == 1) api.InterfaceThrows = true;
        if (mode == 2) api.Interfaces = api.Interfaces![..^1];
        if (mode == 3) BinaryPrimitives.WriteUInt32LittleEndian(api.Interfaces!, 1025);
        if (mode == 4) Array.Clear(api.Interfaces!, 8, 8);
        if (mode == 5) Array.Copy(api.Interfaces!, 8, api.Interfaces!, 8+1352, 28);
        Assert.Null(new WindowsNetworkInterfaceInformationReader(api).Read());
        Assert.Equal(1, api.InterfaceReads); Assert.Equal(0, api.UnicastReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AddressTableFailureKeepsCurrentInterfaceIdentitiesWithNullAddressGroups(int mode)
    {
        var api = new Api();
        if (mode == 0) api.Unicast = null;
        if (mode == 1) api.UnicastThrows = true;
        if (mode == 2) api.Unicast = api.Unicast![..^1];
        if (mode == 3) BinaryPrimitives.WriteUInt32LittleEndian(api.Unicast!, 8193);
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, static row => { Assert.NotNull(row.InterfaceGuid); Assert.NotNull(row.InterfaceLuid);
            Assert.NotNull(row.InterfaceIndex); Assert.Null(row.UnicastAddresses); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidOrMismatchedKnownAddressClearsOnlyItsWholeInterfaceGroup(int mode)
    {
        var api = new Api(); var bytes = api.Unicast!.AsSpan(8, 80);
        if (mode == 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], 17);
        if (mode == 1) bytes[60] = 33;
        if (mode == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes, 999);
        if (mode == 3) api.Unicast![8+80+60] = 129;
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        Assert.Null(Assert.Single(rows, static row => row.DeviceIdentity == IdentityA).UnicastAddresses);
        Assert.Empty(Assert.Single(rows, static row => row.DeviceIdentity == IdentityB).UnicastAddresses!);
    }

    [Fact]
    public void UnknownLuidCannotBeReassignedUsingIndexOrEnumerationOrder()
    {
        var api = new Api();
        BinaryPrimitives.WriteUInt64LittleEndian(api.Unicast!.AsSpan(8+32), 0x1111111111111111UL);
        var row = Assert.Single(new WindowsNetworkInterfaceInformationReader(api).Read()!, static row => row.DeviceIdentity == IdentityA);
        Assert.Equal("ipv6", Assert.Single(row.UnicastAddresses!).AddressFamily);
    }

    [Fact]
    public void SuccessfulEmptyDirectorySkipsAddressQueryAndSuccessfulEmptyAddressesRemainArrays()
    {
        var api = new Api { Interfaces = new byte[8] };
        Assert.Empty(new WindowsNetworkInterfaceInformationReader(api).Read()!);
        Assert.Equal(0, api.UnicastReads);
        api.Interfaces = Raw("interfaceTableHex"); api.Unicast = new byte[8];
        Assert.All(new WindowsNetworkInterfaceInformationReader(api).Read()!, static row => Assert.Empty(row.UnicastAddresses!));
    }

    [Fact]
    public void InterfaceAndAddressEnumerationOrderDoesNotChangeSemanticHash()
    {
        var first = DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation = Declarations() };
        var api = new Api(); SwapRows(api.Interfaces!, 1352); SwapRows(api.Unicast!, 80);
        var second = first with { NetworkInterfaceInformation = new WindowsNetworkInterfaceInformationReader(api).Read() };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(second));
    }

    [Fact]
    public void CanonicalFieldsKeepFullLuidIndexAndSignedTimestampWhilePersistedProjectionStartsEmpty()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation = Declarations() };
        var fields = DeviceTopologyInformationProjection.Create(snapshot, snapshot.CapturedAt);
        var row = Assert.Single(fields.Devices, static row => row.DeviceIdentity == IdentityA);
        Assert.Equal("FEDCBA9876543210", Assert.Single(row.Fields, static f => f.Definition.Name == "network_all_interface_luid_reported").Value!.Value.GetString());
        Assert.Equal("4026531841", Assert.Single(row.Fields, static f => f.Definition.Name == "network_all_interface_index_reported").Value!.Value.GetString());
        var addresses = Assert.Single(row.Fields, static f => f.Definition.Name == "network_all_unicast_address_reported");
        Assert.Equal("windows.ip-helper", addresses.Definition.SourceId);
        Assert.Equal("array", addresses.Definition.ValueKind);
        Assert.Equal("9223372036854775794", addresses.Value!.Value[0].GetProperty("creationWindowsFileTime100NsRaw").GetString());
        Assert.Equal(55U, addresses.Value!.Value[1].GetProperty("socketScopeId").GetUInt32());
        Assert.All(DeviceTopologyInformationProjection.Create(snapshot, null).Devices.Single(d => d.DeviceIdentity == IdentityA).Fields,
            static field => Assert.Null(field.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OwnedMibMemoryIsReleasedOnceOnSuccessErrorOverLimitOrException(int mode)
    {
        var fixture = Raw("interfaceTableHex");
        if (mode == 2) BinaryPrimitives.WriteUInt32LittleEndian(fixture, 1025);
        var pointer = Marshal.AllocHGlobal(fixture.Length);
        Marshal.Copy(fixture, 0, pointer, fixture.Length);
        var frees = 0;
        uint Read(out nint result) { result = mode == 4 ? 0 : pointer;
            if (mode == 3) throw new InvalidOperationException("query failed after allocation"); return mode == 1 ? 5U : 0U; }
        void Free(nint table) { Assert.Equal(pointer, table); Assert.Equal(0, frees++); Marshal.FreeHGlobal(table); }
        try
        {
            if (mode == 3) Assert.Throws<InvalidOperationException>(() => WindowsNetworkInterfaceTableApi.ReadOwnedTable(Read, Free, 1352, 1024));
            else
            {
                var result = WindowsNetworkInterfaceTableApi.ReadOwnedTable(Read, Free, 1352, 1024);
                if (mode == 0) Assert.Equal(fixture, result); else Assert.Null(result);
            }
            Assert.Equal(mode == 4 ? 0 : 1, frees);
        }
        finally { if (frees == 0) Marshal.FreeHGlobal(pointer); }
    }

    private static void SwapRows(byte[] bytes, int stride)
    { var first = bytes.AsSpan(8, stride).ToArray(); bytes.AsSpan(8+stride, stride).CopyTo(bytes.AsSpan(8)); first.CopyTo(bytes, 8+stride); }
    private static JsonDocument Fixture(string fixture)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith("Fixtures.Network."+fixture, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }
    private static byte[] Raw(string name, string fixture = "windows-sdk-tables.json")
    { using var data = Fixture(fixture); return Convert.FromHexString(data.RootElement.GetProperty(name).GetString()!); }
    internal sealed class Api : IWindowsNetworkInterfaceTableApi
    {
        internal byte[]? Interfaces = Raw("interfaceTableHex"), Unicast = Raw("addressTableHex"),
            Routes = NetworkRouteInformationCollectorTests.Raw();
        internal bool InterfaceThrows, UnicastThrows, RouteThrows;
        private int interfaceReads, unicastReads, routeReads;
        internal int InterfaceReads => Volatile.Read(ref interfaceReads);
        internal int UnicastReads => Volatile.Read(ref unicastReads);
        internal int RouteReads => Volatile.Read(ref routeReads);
        public byte[]? ReadInterfaces() { Interlocked.Increment(ref interfaceReads);
            if (InterfaceThrows) throw new InvalidOperationException("interface failed"); return Interfaces; }
        public byte[]? ReadUnicast() { Interlocked.Increment(ref unicastReads);
            if (UnicastThrows) throw new InvalidOperationException("unicast failed"); return Unicast; }
        public byte[]? ReadRoutes() { Interlocked.Increment(ref routeReads);
            if (RouteThrows) throw new InvalidOperationException("routes failed"); return Routes; }
    }
}
