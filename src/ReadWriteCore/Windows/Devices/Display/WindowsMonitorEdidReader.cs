using ResourceManager.ReadWrite.Information;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static class WindowsMonitorEdidReader
{
    public static DeviceTopologyEdidReadResult Read(string normalizedMonitorDeviceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(normalizedMonitorDeviceId))
        {
            return DeviceTopologyEdidReadResult.Unavailable;
        }

        return ReadCore(normalizedMonitorDeviceId);
    }

    [SupportedOSPlatform("windows")]
    private static DeviceTopologyEdidReadResult ReadCore(string normalizedMonitorDeviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{normalizedMonitorDeviceId}\Device Parameters",
                writable: false);
            return CreateReadResult(key?.GetValue("EDID"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return DeviceTopologyEdidReadResult.Unavailable;
        }
    }

    internal static DeviceTopologyEdidReadResult CreateReadResult(object? value)
        => value is byte[] edid
            ? new(true, EdidParser.Parse(edid), Convert.ToBase64String(edid))
            : new(true, null);
}

public sealed record DeviceTopologyEdidReadResult(
    bool Complete,
    DeviceTopologyEdidCapabilities? Capabilities,
    string? RawEdidDeclaredBase64 = null)
{
    internal static DeviceTopologyEdidReadResult Unavailable { get; } = new(false, null);
}
