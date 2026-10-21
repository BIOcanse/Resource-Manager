using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

internal static class WindowsInformationConsumer
{
    internal static void Run()
    {
        var observed = DateTimeOffset.UtcNow;
        var page = new DeviceTopologyPageFileInformation("windows-pagefile|recorded")
        { PathReported = "recorded", CapacityPages = ulong.MaxValue, UsedPages = 0, PageSizeBytes = uint.MaxValue };
        var frame = WindowsInformationFieldProjection.PageFiles([page], observed);
        var capacity = Read(frame, page.DeviceIdentity, DeviceInformationFieldRegistry.MemoryAllPagefileCapacity);
        Require(capacity.Value?.Integer == ((UInt128)ulong.MaxValue * uint.MaxValue).ToString(CultureInfo.InvariantCulture),
            "independent pagefile byte conversion retains full width");
        Require(Read(frame, page.DeviceIdentity, DeviceInformationFieldRegistry.MemoryAllPagefileUsed).Value?.Integer == "0",
            "successful zero pagefile occupancy");
        var volume = new DeviceTopologyVolumeSpaceInformation("windows-volume|recorded")
        {
            TotalBytes = UInt128.MaxValue, CallerAvailableBytes = null,
            DiskExtents = [new(3, ulong.MaxValue, ulong.MaxValue)],
            MountPaths = [], FileSystem = new("NTFS", "", uint.MaxValue, 255, 0)
        };
        var volumes = WindowsInformationFieldProjection.Volumes([volume], observed);
        Require(Read(volumes, volume.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllSizeActual).Value?.Integer
            == UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), "independent volume capacity retains UInt128");
        Require(Read(volumes, volume.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllUserAvailableBytesActual).Value is null,
            "unknown caller quota remains empty");
        var extent = Read(volumes, volume.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllDiskExtentsReported).Value!.Structure!.Value[0];
        Require(extent.GetProperty("startingOffsetBytes").GetString() == ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
            "independent volume extent retains exact offset");
        Require(Read(volumes, volume.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllLabelConfigured).Value?.Text == "",
            "successful empty volume label");
        Require(WindowsInformationFieldProjection.Volumes([volume], null).Devices.Single().Fields.All(field => field.Value is null),
            "unobserved canonical frame cannot expose stale values");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<DeviceInformationSnapshot>(JsonSerializer.Serialize(volumes, options), options)!;
        Require(Read(restored, volume.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllSizeActual).Value?.Integer
            == UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), "canonical frame round trip owns exact data");

        using var current = Process.GetCurrentProcess();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var endpoints = new WindowsNetworkEndpointInformationReader().ReadFields();
        var udp = Read(endpoints, "machine", DeviceInformationFieldRegistry.NetworkAllUdpIpv4EndpointReported).Value?.Structure;
        Require(udp is { ValueKind: JsonValueKind.Array }, "actual independent IPv4 UDP table available");
        var ownEndpoint = udp!.Value.EnumerateArray().Single(row => row.GetProperty("owningProcessIdReported").GetUInt32() == current.Id
            && row.GetProperty("local").GetProperty("port").GetInt32() == port);
        Require(ownEndpoint.GetProperty("processIdentity").GetProperty("creationWindowsFileTime100Ns").GetString()
            == current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture),
            "actual independent endpoint has exact process creation identity");

        var interfaces = new WindowsNetworkInterfaceInformationReader().ReadFields();
        Require(interfaces.ObservedAt is not null && interfaces.Devices.Count > 0, "actual independent network interfaces");
        Require(interfaces.Devices.All(device => device.ObjectType == "network_interface" && device.Fields.Count == 5),
            "actual interface addresses and routes use canonical definitions");
        var pagefiles = new WindowsPageFileInformationReader().ReadFields();
        Require(pagefiles.ObservedAt is not null && pagefiles.Devices.All(device => device.ObjectType == "page_file"
            && device.Fields.Count == 8), "actual independent pagefile enumeration, including successful empty result");
        var actualVolumes = new WindowsVolumeSpaceInformationReader().ReadFields();
        Require(actualVolumes.ObservedAt is not null && actualVolumes.Devices.Count > 0
            && actualVolumes.Devices.All(device => device.ObjectType == "volume" && device.Fields.Count == 22),
            "actual independent volume capacity and metadata enumeration");
        Console.WriteLine("ReadWriteCore Windows information consumer passed: real interface/routes, identity-verified UDP, pagefiles and volumes; exact canonical projections.");
    }

    private static FieldReading Read(DeviceInformationSnapshot snapshot, string identity, DeviceInformationDefinition definition)
        => InformationFieldProjection.Read(identity, snapshot.Devices.Single(device => device.DeviceIdentity == identity)
            .Fields.Single(field => field.Definition == definition), snapshot.ObservedAt);
    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }
}
