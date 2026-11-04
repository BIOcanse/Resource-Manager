using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>Passive source. Null means enumeration failed; individual content failures remain null.</summary>
public interface IAcpiFirmwareTableReader
{
    IReadOnlyList<AcpiFirmwareTable>? ReadTables();
}
