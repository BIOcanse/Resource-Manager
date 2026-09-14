using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsMonitorEdidReader
{
    public static DeviceTopologyEdidReadResult Read(string normalizedMonitorDeviceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(normalizedMonitorDeviceId))
        {
            return DeviceTopologyEdidReadResult.Unavailable;
        }

        return ReadCore(normalizedMonitorDeviceId);
    }

    private static DeviceTopologyEdidReadResult ReadCore(string normalizedMonitorDeviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{normalizedMonitorDeviceId}\Device Parameters",
                writable: false);
            var capabilities = key?.GetValue("EDID") is byte[] edid
                ? EdidParser.Parse(edid)
                : null;
            return new DeviceTopologyEdidReadResult(true, capabilities);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return DeviceTopologyEdidReadResult.Unavailable;
        }
    }
}

internal sealed record DeviceTopologyEdidReadResult(
    bool Complete,
    DeviceTopologyEdidCapabilities? Capabilities)
{
    internal static DeviceTopologyEdidReadResult Unavailable { get; } = new(false, null);
}
