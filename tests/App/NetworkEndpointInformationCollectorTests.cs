using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class NetworkEndpointInformationCollectorTests
{
    [Theory]
    [InlineData("windows-sdk-endpoints.json", 8)]
    [InlineData("windows-sdk-endpoints-x86.json", 4)]
    public void IndependentSdkBytesVerifyFourLayoutsPortsScopesStatesAndRawPids(string fixture, int pointerSize)
    {
        using var data = Fixture(fixture); var json = data.RootElement;
        Assert.Equal(pointerSize, json.GetProperty("pointerSize").GetInt32());
        Assert.Equal(5, json.GetProperty("tcpTableClass").GetInt32()); Assert.Equal(1, json.GetProperty("udpTableClass").GetInt32());
        var offsets = new Dictionary<string, int> {
            ["tcp4RowSize"] = 24, ["tcp4TableOffset"] = 4, ["tcp6RowSize"] = 56, ["tcp6TableOffset"] = 4,
            ["udp4RowSize"] = 12, ["udp4TableOffset"] = 4, ["udp6RowSize"] = 28, ["udp6TableOffset"] = 4,
            ["tcp4StateOffset"] = 0, ["tcp4LocalOffset"] = 4, ["tcp4LocalPortOffset"] = 8,
            ["tcp4RemoteOffset"] = 12, ["tcp4RemotePortOffset"] = 16, ["tcp4PidOffset"] = 20,
            ["tcp6LocalOffset"] = 0, ["tcp6LocalScopeOffset"] = 16, ["tcp6LocalPortOffset"] = 20,
            ["tcp6RemoteOffset"] = 24, ["tcp6RemoteScopeOffset"] = 40, ["tcp6RemotePortOffset"] = 44,
            ["tcp6StateOffset"] = 48, ["tcp6PidOffset"] = 52,
            ["udp4LocalOffset"] = 0, ["udp4LocalPortOffset"] = 4, ["udp4PidOffset"] = 8,
            ["udp6LocalOffset"] = 0, ["udp6LocalScopeOffset"] = 16, ["udp6LocalPortOffset"] = 20, ["udp6PidOffset"] = 24 };
        foreach (var (name, value) in offsets) Assert.Equal(value, json.GetProperty(name).GetInt32());
        var tcp4 = WindowsNetworkEndpointTableDecoder.Tcp(Raw(WindowsNetworkEndpointTableKind.TcpIpv4, fixture), false)!;
        Assert.Equal(3, tcp4.Count);
        Assert.Equal(new NetworkTcpEndpoint("ipv4", new("0.0.0.0", null, 55535), null, 2, 101), tcp4[0]);
        Assert.Equal(new NetworkTcpEndpoint("ipv4", new("198.51.100.7", null, 50000), new("203.0.113.9", null, 443), 5, 101), tcp4[1]);
        Assert.Equal(new NetworkTcpEndpoint("ipv4", new("127.0.0.1", null, 1), new("0.0.0.0", null, 0), 0xfffffffe, uint.MaxValue), tcp4[2]);
        var tcp6 = WindowsNetworkEndpointTableDecoder.Tcp(Raw(WindowsNetworkEndpointTableKind.TcpIpv6, fixture), true)!;
        Assert.Equal(new NetworkTcpEndpoint("ipv6", new("::", 0, 5353), null, 2, 101), tcp6[0]);
        Assert.Equal(new NetworkTcpEndpoint("ipv6", new("fe80::1", 0x11223344, 65535), new("2001:db8::9", 0xf1234567, 443), 5, 202), tcp6[1]);
        Assert.Equal(new NetworkTcpEndpoint("ipv6", new("::1", 0, 0), new("::", 0, 0), 11, 0), tcp6[2]);
        var udp4 = WindowsNetworkEndpointTableDecoder.Udp(Raw(WindowsNetworkEndpointTableKind.UdpIpv4, fixture), false)!;
        Assert.Equal(new NetworkUdpEndpoint("ipv4", new("0.0.0.0", null, 5353), 101), udp4[0]);
        Assert.Equal(new NetworkUdpEndpoint("ipv4", new("127.0.0.1", null, 65535), 303), udp4[1]);
        Assert.Equal(new NetworkUdpEndpoint("ipv4", new("198.51.100.7", null, 0), uint.MaxValue), udp4[2]);
        var udp6 = WindowsNetworkEndpointTableDecoder.Udp(Raw(WindowsNetworkEndpointTableKind.UdpIpv6, fixture), true)!;
        Assert.Equal(new NetworkUdpEndpoint("ipv6", new("::", 0, 5353), 101), udp6[0]);
        Assert.Equal(new NetworkUdpEndpoint("ipv6", new("fe80::1", 0x11223344, 65535), 202), udp6[1]);
        Assert.Equal(new NetworkUdpEndpoint("ipv6", new("::1", uint.MaxValue, 0), 303), udp6[2]);
    }

    [Fact]
    public void CurrentTablesAreReadWhileExactProcessHandlesAreHeldThenEveryHandleIsReleased()
    {
        var api = new Api(); var result = new WindowsNetworkEndpointInformationReader(api).Read();
        Assert.Equal(8, api.Reads); Assert.Equal(4, api.Opens);
        Assert.Equal(3, api.Leases.Count); Assert.All(api.Leases, static lease => Assert.Equal(1, lease.Disposals));
        var row = result.TcpIpv4!.Single(static row => row.StateRaw == 5);
        Assert.Equal(new NetworkEndpointProcessIdentity(101, "18446744073709551598"), row.ProcessIdentity);
        Assert.Equal(row.ProcessIdentity, result.UdpIpv4!.Single(static row => row.OwningProcessIdReported == 101).ProcessIdentity);
        Assert.Null(result.TcpIpv4!.Single(static row => row.OwningProcessIdReported == uint.MaxValue).ProcessIdentity);
        Assert.Null(result.TcpIpv6!.Single(static row => row.OwningProcessIdReported == 0).ProcessIdentity);
        Assert.All(api.Leases, static lease => Assert.Equal(2, lease.Reads));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DeniedExitedReusedMismatchedOrFailedProcessKeepsEndpointsButClearsItsIdentity(int mode)
    {
        var api = new Api { CreateLease = pid => {
            if (pid == uint.MaxValue) return null;
            if (pid == 101 && mode == 4) throw new InvalidOperationException("access denied");
            var lease = new Lease(pid == 101 && mode == 5 ? 909U : pid);
            if (pid == 101) lease.ReadCreation = count => mode switch {
                0 => null, 1 => count == 1 ? 100UL : 101UL, 2 => count == 1 ? 100UL : null,
                3 => count == 1 ? 100UL : throw new InvalidOperationException("identity failed"), _ => 100UL };
            return lease;
        } };
        var result = new WindowsNetworkEndpointInformationReader(api).Read();
        Assert.Equal(3, result.TcpIpv4!.Count); Assert.Equal(3, result.UdpIpv6!.Count);
        Assert.All(result.TcpIpv4.Where(static row => row.OwningProcessIdReported == 101), static row => Assert.Null(row.ProcessIdentity));
        Assert.Null(result.UdpIpv4!.Single(static row => row.OwningProcessIdReported == 101).ProcessIdentity);
        Assert.NotNull(result.TcpIpv6!.Single(static row => row.OwningProcessIdReported == 202).ProcessIdentity);
        Assert.All(api.Leases, static lease => Assert.Equal(1, lease.Disposals));
    }

    [Fact]
    public void SecondTableContentsWinAndNewPidDoesNotInheritAnotherProcessOrGetOpenedAfterTheRead()
    {
        var api = new Api();
        api.ReadTable = (kind, count) => {
            var bytes = Raw(kind);
            if (count == 2 && kind == WindowsNetworkEndpointTableKind.TcpIpv4)
            { bytes[4+24+4] = 199; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4+24+20), 404); }
            return bytes;
        };
        var result = new WindowsNetworkEndpointInformationReader(api).Read();
        var row = result.TcpIpv4!.Single(static row => row.StateRaw == 5);
        Assert.Equal("199.51.100.7", row.Local.Address); Assert.Equal(404U, row.OwningProcessIdReported); Assert.Null(row.ProcessIdentity);
        Assert.Equal(4, api.Opens); Assert.DoesNotContain(api.Leases, static lease => lease.ProcessId == 404);
    }

    public static TheoryData<int, int> InvalidTables
    {
        get { var data = new TheoryData<int, int>(); for (var kind = 0; kind < 4; kind++) for (var mode = 0; mode < 5; mode++) data.Add(kind, mode); return data; }
    }
    [Theory]
    [MemberData(nameof(InvalidTables))]
    public void TableErrorsShortPayloadsAndBoundsOnlyClearTheSelectedProtocolAndFamily(int selected, int mode)
    {
        var api = new Api();
        api.ReadTable = (kind, _) => {
            var bytes = Raw(kind); if ((int)kind != selected) return bytes;
            if (mode == 0) return null;
            if (mode == 1) throw new InvalidOperationException("table failed");
            if (mode == 2) return bytes[..^1];
            if (mode == 3) { BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16385); return bytes; }
            return new byte[WindowsNetworkEndpointTableDecoder.MaximumBytes(WindowsNetworkEndpointTableDecoder.RowSize(kind))+1];
        };
        var result = new WindowsNetworkEndpointInformationReader(api).Read();
        object?[] groups = [result.TcpIpv4, result.TcpIpv6, result.UdpIpv4, result.UdpIpv6];
        for (var i = 0; i < groups.Length; i++) if (i == selected) Assert.Null(groups[i]); else Assert.NotNull(groups[i]);
        Assert.All(api.Leases, static lease => Assert.Equal(1, lease.Disposals));
    }

    [Fact]
    public void SuccessfulEmptyTablesSkipProcessQueriesAndNoGrantedIdentitySkipsSecondTablePass()
    {
        var api = new Api { ReadTable = static (_, _) => new byte[4] };
        var result = new WindowsNetworkEndpointInformationReader(api).Read();
        Assert.Empty(result.TcpIpv4!); Assert.Empty(result.TcpIpv6!); Assert.Empty(result.UdpIpv4!); Assert.Empty(result.UdpIpv6!);
        Assert.Equal(4, api.Reads); Assert.Equal(0, api.Opens);
        api = new Api { CreateLease = static _ => null };
        result = new WindowsNetworkEndpointInformationReader(api).Read();
        Assert.Equal(4, api.Reads); Assert.All(result.TcpIpv4!, static row => Assert.Null(row.ProcessIdentity));
    }

    [Fact]
    public void FullFourTableCapacityIsNotTruncatedAndEnumerationOrderDoesNotChangeHash()
    {
        var api = new Api { ReadTable = (kind, _) => {
            var stride = WindowsNetworkEndpointTableDecoder.RowSize(kind);
            var bytes = new byte[4+16384*stride+8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes,16384);
            // All-zero unknown/closed records remain OS records, and do not invent a PID identity.
            return bytes;
        } };
        var result = new WindowsNetworkEndpointInformationReader(api).Read();
        Assert.Equal(16384,result.TcpIpv4!.Count); Assert.Equal(16384,result.TcpIpv6!.Count);
        Assert.Equal(16384,result.UdpIpv4!.Count); Assert.Equal(16384,result.UdpIpv6!.Count); Assert.Equal(0,api.Opens);
        api = new Api(); var first = new WindowsNetworkEndpointInformationReader(api).Read();
        api.ReadTable = (kind, _) => {
            var bytes=Raw(kind); var stride=WindowsNetworkEndpointTableDecoder.RowSize(kind);
            var saved=bytes.AsSpan(4,stride).ToArray(); bytes.AsSpan(4+stride,stride).CopyTo(bytes.AsSpan(4)); saved.CopyTo(bytes,4+stride); return bytes;
        };
        var second = new WindowsNetworkEndpointInformationReader(api).Read();
        var snapshot=DeviceInformationTestData.Snapshot(); var comparer=new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(snapshot with { NetworkEndpointInformation=first }),comparer.ComputeHash(snapshot with { NetworkEndpointInformation=second }));
    }

    [Fact]
    public void BufferedQueryUsesExactFamilyClassZeroReservedAndBoundedGrowingBuffers()
    {
        var bytes=Raw(WindowsNetworkEndpointTableKind.TcpIpv6); var calls=0;
        uint Query(nint pointer,ref uint size,bool order,uint family,uint tableClass,uint reserved) {
            Assert.False(order); Assert.Equal(23U,family);Assert.Equal(5U,tableClass);Assert.Equal(0U,reserved);
            calls++;
            if (pointer==0) { size=(uint)bytes.Length; return 122; }
            if (calls==2) { size+=56;return 122; }
            Assert.True(size>=bytes.Length);Marshal.Copy(bytes,0,pointer,bytes.Length);size=(uint)bytes.Length;return 0;
        }
        Assert.Equal(bytes,WindowsNetworkEndpointTableApi.ReadBufferedTable(Query,23,5,56)); Assert.Equal(3,calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidOrFailingBufferQueryNeverReturnsStaleOrOutOfBoundsBytes(int mode)
    {
        var calls=0;
        uint Query(nint pointer,ref uint size,bool _,uint family,uint tableClass,uint reserved) {
            calls++;Assert.Equal(2U,family);Assert.Equal(1U,tableClass);Assert.Equal(0U,reserved);
            if (pointer==0) { size=mode==1?3U:mode==2?uint.MaxValue:16U;return mode==0?5U:122U; }
            if (mode==3) return 5;
            if (mode==4) { size++;return 0; }
            if (mode==5) return 122;
            throw new InvalidOperationException("failed inside query");
        }
        if (mode==6) Assert.Throws<InvalidOperationException>(()=>WindowsNetworkEndpointTableApi.ReadBufferedTable(Query,2,1,12));
        else Assert.Null(WindowsNetworkEndpointTableApi.ReadBufferedTable(Query,2,1,12));
        Assert.InRange(calls,1,2);
    }

    [Fact]
    public void EndlessTableGrowthStopsAtThreeAllocationAttemptsAndNoDataIsAnEmptyTable()
    {
        var calls=0;
        uint Grow(nint pointer,ref uint size,bool _,uint __,uint ___,uint ____) { calls++;size=pointer==0?16:size+12;return 122; }
        Assert.Null(WindowsNetworkEndpointTableApi.ReadBufferedTable(Grow,2,1,12));Assert.Equal(4,calls);
        uint Empty(nint _,ref uint __,bool ___,uint ____,uint _____,uint ______) => 232;
        Assert.Equal(new byte[4],WindowsNetworkEndpointTableApi.ReadBufferedTable(Empty,2,1,12));
    }

    private static JsonDocument Fixture(string fixture)
    {
        var assembly=Assembly.GetExecutingAssembly();
        var name=Assert.Single(assembly.GetManifestResourceNames(), n=>n.EndsWith("Fixtures.Network."+fixture,StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;return JsonDocument.Parse(stream);
    }
    internal static byte[] Raw(WindowsNetworkEndpointTableKind kind,string fixture="windows-sdk-endpoints.json")
    {
        var key=kind switch { WindowsNetworkEndpointTableKind.TcpIpv4=>"tcp4Hex",WindowsNetworkEndpointTableKind.TcpIpv6=>"tcp6Hex",
            WindowsNetworkEndpointTableKind.UdpIpv4=>"udp4Hex",WindowsNetworkEndpointTableKind.UdpIpv6=>"udp6Hex",_=>throw new ArgumentOutOfRangeException(nameof(kind)) };
        using var data=Fixture(fixture);return Convert.FromHexString(data.RootElement.GetProperty(key).GetString()!);
    }
    internal sealed class Api : IWindowsNetworkEndpointTableApi
    {
        private int reads,opens;
        private readonly Dictionary<WindowsNetworkEndpointTableKind,int> perTable=[];
        internal int Reads=>Volatile.Read(ref reads); internal int Opens=>Volatile.Read(ref opens);
        internal readonly List<Lease> Leases=[];
        internal Func<WindowsNetworkEndpointTableKind,int,byte[]?>? ReadTable;
        internal Func<uint,Lease?>? CreateLease;
        public byte[]? Read(WindowsNetworkEndpointTableKind kind) {
            Interlocked.Increment(ref reads);var count=perTable.GetValueOrDefault(kind)+1;perTable[kind]=count;
            return ReadTable is null?Raw(kind):ReadTable(kind,count);
        }
        public IWindowsNetworkEndpointProcessLease? OpenProcess(uint pid) {
            Interlocked.Increment(ref opens);var lease=CreateLease is null?(pid==uint.MaxValue?null:new Lease(pid)):CreateLease(pid);
            if (lease is not null) Leases.Add(lease);return lease;
        }
    }
    internal sealed class Lease(uint pid) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId=>pid;
        internal int Reads,Disposals;
        internal Func<int,ulong?>? ReadCreation;
        public bool TryReadCreationFileTime(out ulong created) {
            var count=++Reads;var value=ReadCreation is null?(pid==101?ulong.MaxValue-17:100UL+pid):ReadCreation(count);
            created=value??0;return value.HasValue;
        }
        public void Dispose() { Assert.Equal(0,Disposals++); }
    }
}
