using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Bluetooth;

/// <summary>Explicit, typed source contract for recorded or native Classic Bluetooth data.
/// Implementations do not own current state. Only requested directory properties are returned;
/// a missing requested directory is treated as failure, never as a successful empty result.</summary>
public interface IWindowsBluetoothClassicInformationReader
{
    WindowsBluetoothClassicInformationReadResult Read(
        WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selectedFields,
        CancellationToken cancellationToken);
}
