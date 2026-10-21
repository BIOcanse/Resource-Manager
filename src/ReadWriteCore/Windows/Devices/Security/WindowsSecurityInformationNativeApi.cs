using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsSecurityInformationNativeApi : IWindowsSecurityInformationApi
{
    public int GetHealth(uint provider, out uint health)
    {
        health = 0;
        return OperatingSystem.IsWindows() ? WscGetSecurityProviderHealth(provider, out health) : unchecked((int)0x80004001);
    }
    public IWindowsFirewallPolicyRead? OpenFirewall() => OperatingSystem.IsWindows() ? WindowsFirewallPolicyReadSession.Open() : null;
    public IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts(uint provider)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ReadProducts(provider, WindowsSecurityProductListReadSession.Open);
    }
    internal static IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts(uint provider,
        Func<uint, WindowsSecurityProductListReadSession?> open)
    {
        using var session = open(provider);
        return session?.ReadProducts();
    }

    [DllImport("wscapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WscGetSecurityProviderHealth(uint providers, out uint health);
}
