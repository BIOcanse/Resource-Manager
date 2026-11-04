namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyPciResourceInformation(
    IReadOnlyList<DeviceTopologyPciResourceRange>? Ranges,
    IReadOnlyList<DeviceTopologyPciInterrupt>? Interrupts,
    IReadOnlyList<DeviceTopologyPciDmaChannel>? DmaChannels)
{
    public static readonly DeviceTopologyPciResourceInformation Unavailable = new(null, null, null);
}

public sealed record DeviceTopologyPciInterrupt(
    uint NumberReported,
    ushort ProcessorGroupReported,
    string ProcessorAffinityMaskReported,
    ushort FlagsReported);

public sealed record DeviceTopologyPciDmaChannel(uint ChannelReported, uint FlagsReported);
