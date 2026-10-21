using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsPciResourceInformationApi
{
    int DescriptorPointerBytes { get; }
    string? GetIdentity(uint devnode);
    uint FirstConfiguration(uint devnode, out nuint configuration);
    uint NextConfiguration(nuint previous, out nuint configuration);
    uint NextResource(nuint previous, out nuint resource, out uint type);
    uint DataSize(nuint resource, out uint bytes);
    uint Data(nuint resource, byte[] buffer);
    uint ReleaseConfiguration(nuint configuration);
    uint ReleaseResource(nuint resource);
}

internal sealed partial class WindowsPciResourceInformationReader(IWindowsPciResourceInformationApi api)
{
    internal const uint NoMoreConfigurations = 14, NoMoreResources = 15;
    internal const int MaximumConfigurations = 32, MaximumResources = 4096, MaximumDescriptorBytes = 1048576;

    internal static DeviceTopologyPciResourceInformation ReadNative(uint devnode, string deviceId)
        => deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
            ? new WindowsPciResourceInformationReader(new WindowsPciResourceInformationNativeApi()).Read(devnode, deviceId) : DeviceTopologyPciResourceInformation.Unavailable;

    internal static DeviceTopologyPciResourceInformation ReadForDevice(string deviceId, DeviceTopologyNativeDevice? native)
        => deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && native is not null
            && string.Equals(deviceId, native.DeviceId, StringComparison.OrdinalIgnoreCase) ? native.PciResources : DeviceTopologyPciResourceInformation.Unavailable;

    internal DeviceTopologyPciResourceInformation Read(uint devnode, string deviceId)
    {
        var owned = new List<(bool Configuration, nuint Handle)>();
        var result = DeviceTopologyPciResourceInformation.Unavailable;
        var released = true;
        try { result = ReadCore(devnode, deviceId, owned); }
        catch (Exception error) when (IsSourceFailure(error)) { }
        finally
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                var lease = owned[index];
                try { if ((lease.Configuration ? api.ReleaseConfiguration(lease.Handle) : api.ReleaseResource(lease.Handle)) != 0) released = false; }
                catch (Exception error) when (IsSourceFailure(error)) { released = false; }
            }
        }
        return released ? result : DeviceTopologyPciResourceInformation.Unavailable;
    }

    private DeviceTopologyPciResourceInformation ReadCore(uint devnode, string deviceId, List<(bool Configuration, nuint Handle)> owned)
    {
        if (!string.Equals(api.GetIdentity(devnode), deviceId, StringComparison.OrdinalIgnoreCase)) return DeviceTopologyPciResourceInformation.Unavailable;
        List<DeviceTopologyPciResourceRange>? ranges = [];
        List<DeviceTopologyPciInterrupt>? interrupts = [];
        List<DeviceTopologyPciDmaChannel>? dmaChannels = [];
        var configurations = new HashSet<nuint>(); var resources = new HashSet<nuint>();
        var status = api.FirstConfiguration(devnode, out var configuration);
        while (status == 0)
        {
            if (!configurations.Add(configuration)) return DeviceTopologyPciResourceInformation.Unavailable;
            owned.Add((true, configuration));
            if (configurations.Count > MaximumConfigurations) return DeviceTopologyPciResourceInformation.Unavailable;
            var previous = configuration;
            var resourceStatus = api.NextResource(previous, out var resource, out var type);
            while (resourceStatus == 0)
            {
                if (!resources.Add(resource)) return DeviceTopologyPciResourceInformation.Unavailable;
                owned.Add((false, resource));
                if (resources.Count > MaximumResources) return DeviceTopologyPciResourceInformation.Unavailable;
                if (ranges is not null && type is 1 or 2 or 6 or 7)
                {
                    if (ReadDescriptor(resource) is { } bytes && Parse(type, bytes) is { } range) ranges.Add(range);
                    else ranges = null;
                }
                else if (interrupts is not null && type == 4)
                {
                    if (ReadDescriptor(resource) is { } bytes && ParseInterrupt(api.DescriptorPointerBytes, bytes) is { } interrupt) interrupts.Add(interrupt);
                    else interrupts = null;
                }
                else if (dmaChannels is not null && type == 3)
                {
                    if (ReadDescriptor(resource) is { } bytes && ParseDmaChannel(bytes) is { } channel) dmaChannels.Add(channel);
                    else dmaChannels = null;
                }
                previous = resource;
                resourceStatus = api.NextResource(previous, out resource, out type);
            }
            if (resourceStatus != NoMoreResources) return DeviceTopologyPciResourceInformation.Unavailable;
            status = api.NextConfiguration(configuration, out configuration);
        }
        if (status != NoMoreConfigurations || !string.Equals(api.GetIdentity(devnode), deviceId, StringComparison.OrdinalIgnoreCase)) return DeviceTopologyPciResourceInformation.Unavailable;
        return new(ranges?.AsReadOnly(), interrupts?.AsReadOnly(), dmaChannels?.AsReadOnly());
    }

    private byte[]? ReadDescriptor(nuint resource)
    {
        try
        {
            if (api.DataSize(resource, out var size) != 0 || size == 0 || size > MaximumDescriptorBytes) return null;
            var bytes = new byte[(int)size];
            return api.Data(resource, bytes) == 0 ? bytes : null;
        }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    private static bool IsSourceFailure(Exception error) => error is ExternalException or DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
}
