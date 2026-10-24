using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class NetworkRouteInformationCollectorTests
{
    [Theory]
    [InlineData("windows-sdk-routes.json", 8)]
    [InlineData("windows-sdk-routes-x86.json", 4)]
    public void IndependentOfficialSdkBytesVerifyBothAbisAndEveryRouteMember(string fixture, int pointerSize)
    {
        using var data = Fixture(fixture);
        var json = data.RootElement;
        Assert.Equal(pointerSize, json.GetProperty("pointerSize").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.RouteRowSize, json.GetProperty("rowSize").GetInt32());
        Assert.Equal(WindowsNetworkInterfaceTableDecoder.TableOffset, json.GetProperty("tableOffset").GetInt32());
        var offsets = new Dictionary<string, int> { ["prefixSize"] = 32, ["luidOffset"] = 0, ["indexOffset"] = 8,
            ["destinationOffset"] = 12, ["prefixLengthOffset"] = 28, ["nextHopOffset"] = 44, ["sitePrefixOffset"] = 72,
            ["validLifetimeOffset"] = 76, ["preferredLifetimeOffset"] = 80, ["metricOffset"] = 84,
            ["protocolOffset"] = 88, ["loopbackOffset"] = 92, ["autoconfigureOffset"] = 93, ["publishOffset"] = 94,
            ["immortalOffset"] = 95, ["ageOffset"] = 96, ["originOffset"] = 100,
            ["socketV4Offset"] = 4, ["socketV6Offset"] = 8, ["socketScopeOffset"] = 24 };
        foreach (var (name, expected) in offsets) Assert.Equal(expected, json.GetProperty(name).GetInt32());
        var api = new NetworkAddressInformationCollectorTests.Api { Routes = Raw(fixture) };
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        var a = Assert.Single(rows, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA);
        Assert.Equal(3, a.Routes!.Count);
        Assert.Equal(new NetworkRoute("ipv4", "0.0.0.0", 0, null, "198.51.100.1", null, 255, uint.MaxValue, 17,
            uint.MaxValue, 0xf0000041, true, false, true, false, uint.MaxValue, 0xf0000043), a.Routes[0]);
        Assert.Equal(new NetworkRoute("ipv4", "198.51.100.0", 24, null, "0.0.0.0", null, 24, 0, 0,
            0, 0, false, false, false, false, 0, 0), a.Routes[1]);
        Assert.Equal(new NetworkRoute("ipv6", "fe80::", 64, 55, "fe80::1", 77, 64, 0, uint.MaxValue,
            31, 3, false, true, false, true, 9, 2), a.Routes[2]);
        var b = Assert.Single(rows, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityB);
        Assert.Equal(new NetworkRoute("ipv6", "::", 0, 0, "::", 0, 0, uint.MaxValue, uint.MaxValue,
            0, 0, false, false, false, false, 0, 0), Assert.Single(b.Routes!));
        Assert.Equal(2, a.UnicastAddresses!.Count);
        Assert.Equal(1, api.InterfaceReads); Assert.Equal(1, api.UnicastReads); Assert.Equal(1, api.RouteReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RouteTableFailureKeepsCurrentIdentityAndUnicastValues(int mode)
    {
        var api = new NetworkAddressInformationCollectorTests.Api();
        if (mode == 0) api.Routes = null;
        if (mode == 1) api.RouteThrows = true;
        if (mode == 2) api.Routes = api.Routes![..^1];
        if (mode == 3) BinaryPrimitives.WriteUInt32LittleEndian(api.Routes!, 16385);
        if (mode == 4) api.Routes = new byte[7];
        if (mode == 5) api.Routes = new byte[8 + 16384 * 104 + 1];
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        Assert.All(rows, static row => { Assert.NotNull(row.InterfaceGuid); Assert.NotNull(row.InterfaceLuid);
            Assert.NotNull(row.InterfaceIndex); Assert.NotNull(row.UnicastAddresses); Assert.Null(row.Routes); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MalformedKnownRouteClearsOnlyItsInterfaceRouteGroup(int mode)
    {
        var api = new NetworkAddressInformationCollectorTests.Api(); var bytes = api.Routes!.AsSpan(8, 104);
        if (mode == 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], 23);
        if (mode == 1) bytes[40] = 33;
        if (mode == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], 999);
        if (mode == 3) BinaryPrimitives.WriteUInt16LittleEndian(bytes[44..], 23);
        if (mode == 4) api.Routes![8+104+40] = 129;
        if (mode == 5) BinaryPrimitives.WriteUInt16LittleEndian(bytes[44..], 999);
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read()!;
        var a = Assert.Single(rows, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA);
        var b = Assert.Single(rows, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityB);
        Assert.Null(a.Routes); Assert.Equal(2, a.UnicastAddresses!.Count); Assert.Single(b.Routes!);
    }

    [Fact]
    public void UnknownLuidCannotBeReassignedByIndexAndUnicastFailureCannotClearRoutes()
    {
        var api = new NetworkAddressInformationCollectorTests.Api { Unicast = null };
        BinaryPrimitives.WriteUInt64LittleEndian(api.Routes!.AsSpan(8), 0x1111111111111111UL);
        var a = Assert.Single(new WindowsNetworkInterfaceInformationReader(api).Read()!, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA);
        Assert.Null(a.UnicastAddresses); Assert.Equal(2, a.Routes!.Count);
        Assert.DoesNotContain(a.Routes, static r => r.DestinationPrefixLength == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrSuccessfulEmptyDirectorySkipsBothOptionalTables(bool success)
    {
        var api = new NetworkAddressInformationCollectorTests.Api { Interfaces = success ? new byte[8] : null };
        var rows = new WindowsNetworkInterfaceInformationReader(api).Read();
        if (success) Assert.Empty(rows!); else Assert.Null(rows);
        Assert.Equal(0, api.UnicastReads); Assert.Equal(0, api.RouteReads);
    }

    [Fact]
    public void SuccessfulEmptyRoutesAreArraysAndMaximumTableIsNotTruncated()
    {
        var api = new NetworkAddressInformationCollectorTests.Api { Routes = new byte[8] };
        Assert.All(new WindowsNetworkInterfaceInformationReader(api).Read()!, static row => Assert.Empty(row.Routes!));
        var sample = Raw().AsSpan(8, 104).ToArray();
        api.Routes = new byte[8+16384*104];
        BinaryPrimitives.WriteUInt32LittleEndian(api.Routes, 16384);
        for (var i = 0; i < 16384; i++) sample.CopyTo(api.Routes, 8+i*104);
        var a = Assert.Single(new WindowsNetworkInterfaceInformationReader(api).Read()!, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA);
        Assert.Equal(16384, a.Routes!.Count);
    }

    [Fact]
    public void HostPrefixesKeepFullWidthAndNonzeroBooleanBytesRemainTrue()
    {
        var api = new NetworkAddressInformationCollectorTests.Api();
        api.Routes![8+40] = 32; api.Routes[8+104+40] = 128;
        api.Routes[8+92] = 0xff; api.Routes[8+94] = 2;
        var a = Assert.Single(new WindowsNetworkInterfaceInformationReader(api).Read()!, static r => r.DeviceIdentity == NetworkAddressInformationCollectorTests.IdentityA);
        Assert.Equal((byte)32, a.Routes![0].DestinationPrefixLength);
        Assert.Equal((byte)128, a.Routes[2].DestinationPrefixLength);
        Assert.True(a.Routes[0].Loopback); Assert.True(a.Routes[0].Publish);
    }

    [Fact]
    public void RouteEnumerationAndLiveAgeChangesDoNotChangeStructuralHash()
    {
        var api = new NetworkAddressInformationCollectorTests.Api();
        var first = DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation = new WindowsNetworkInterfaceInformationReader(api).Read() };
        var saved = api.Routes!.AsSpan(8, 104).ToArray();
        api.Routes.AsSpan(8+104,104).CopyTo(api.Routes.AsSpan(8)); saved.CopyTo(api.Routes!,8+104);
        var comparer = new DeviceTopologySemanticComparer();
        var reordered = first with { NetworkInterfaceInformation = new WindowsNetworkInterfaceInformationReader(api).Read() };
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(reordered));
        // Route readings remain current information; time passing does not replace topology.
        BinaryPrimitives.WriteUInt32LittleEndian(api.Routes.AsSpan(8+96), 10);
        var changed = first with { NetworkInterfaceInformation = new WindowsNetworkInterfaceInformationReader(api).Read() };
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(changed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OwnedRouteTableMemoryIsFreedOnceOnSuccessErrorLimitOrException(int mode)
    {
        var bytes = Raw();
        if (mode == 2) BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16385);
        var pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length);
        var frees = 0;
        uint Read(out nint table) { table = pointer; if (mode == 3) throw new InvalidOperationException("failed after allocation"); return mode == 1 ? 5U : 0U; }
        void Free(nint table) { Assert.Equal(pointer, table); Assert.Equal(0, frees++); Marshal.FreeHGlobal(table); }
        try
        {
            if (mode == 3) Assert.Throws<InvalidOperationException>(() => WindowsNetworkInterfaceTableApi.ReadOwnedTable(Read, Free, 104, 16384));
            else { var result = WindowsNetworkInterfaceTableApi.ReadOwnedTable(Read, Free, 104, 16384);
                if (mode == 0) Assert.Equal(bytes, result); else Assert.Null(result); }
            Assert.Equal(1, frees);
        }
        finally { if (frees == 0) Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void ActualWindowsReadOnlyRouteApiReturnsCurrentBoundRecords()
    {
        Assert.True(OperatingSystem.IsWindows());
        var rows = new WindowsNetworkInterfaceInformationReader(new WindowsNetworkInterfaceTableApi()).Read();
        Assert.NotNull(rows); Assert.NotEmpty(rows);
        Assert.All(rows, static row => Assert.NotNull(row.Routes));
        Assert.True(rows.Sum(static row => row.Routes!.Count) > 0);
        Assert.All(rows.SelectMany(static row => row.Routes!), static route => {
            Assert.True(route.AddressFamily is "ipv4" or "ipv6");
            Assert.True(route.DestinationPrefixLength <= (route.AddressFamily == "ipv4" ? 32 : 128)); });
    }

    private static JsonDocument Fixture(string fixture)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith("Fixtures.Network."+fixture, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }

    internal static byte[] Raw(string fixture = "windows-sdk-routes.json")
    { using var data = Fixture(fixture); return Convert.FromHexString(data.RootElement.GetProperty("routeTableHex").GetString()!); }
}
