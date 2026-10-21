using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

/// <summary>Public source boundary for explicitly captured WPD values. Implementations
/// receive selected exact objects and return recorded or native typed rows; cached reads
/// never invoke this contract.</summary>
public interface IWindowsWpdInformationReader
{
    WindowsWpdInformationReadResult Read(WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken);
}
