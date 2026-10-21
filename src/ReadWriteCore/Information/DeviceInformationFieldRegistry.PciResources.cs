namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition PciAllResourceRange =
        new("pci_all_resource_range", "pci", "all", "resource-ranges", "range[]", "pci.windows_allocated_memory_io_bus_ranges", "windows.pci-allocated-resources");
    public static readonly DeviceInformationDefinition PciAllInterruptsAllocatedReported =
        new("pci_all_interrupts_allocated_reported", "pci", "all", "allocated-interrupts", "record[]", "pci.windows_allocated_interrupts", "windows.pci-allocated-resources");
    public static readonly DeviceInformationDefinition PciAllDmaChannelsAllocatedReported =
        new("pci_all_dma_channels_allocated_reported", "pci", "all", "allocated-dma-channels", "record[]", "pci.windows_allocated_dma_channels", "windows.pci-allocated-resources");
}
