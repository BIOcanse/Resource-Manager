namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Current installed driver declarations for one present exact device instance.
/// Version and provider are declared properties; date is the INF DriverVer date, not installation or hardware time.</summary>
public sealed record DeviceTopologyDriverInformation(string DeviceInstanceId, string? ProviderDeclared = null, string? DateDeclared = null)
{
    public string? VersionDeclared { get; init; }

    public string DeviceIdentity => "windows-device-driver|" + DeviceInstanceId.Trim().ToUpperInvariant();
}
