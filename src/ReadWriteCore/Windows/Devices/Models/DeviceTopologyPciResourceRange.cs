namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Windows allocated address/bus-number range; exact inclusive endpoints, not a BAR index.</summary>
public sealed record DeviceTopologyPciResourceRange(uint ResourceTypeReported, string StartReported,
    string EndReported, string LengthDerived, uint FlagsReported);
