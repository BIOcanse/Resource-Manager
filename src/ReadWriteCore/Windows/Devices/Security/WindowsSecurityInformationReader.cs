using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsSecurityInformationApi
{
    int GetHealth(uint provider, out uint health);
    IWindowsFirewallPolicyRead? OpenFirewall();
    IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts(uint provider);
}

internal interface IWindowsFirewallPolicyRead : IDisposable
{
    int GetCurrentProfiles(out uint profiles);
    int GetEnabled(uint profile, out short enabled);
}

public sealed class WindowsSecurityInformationReader
{
    private readonly IWindowsSecurityInformationApi api;
    public WindowsSecurityInformationReader() : this(new WindowsSecurityInformationNativeApi()) { }
    internal WindowsSecurityInformationReader(IWindowsSecurityInformationApi api) => this.api = api;

    public DeviceInformationSnapshot ReadFields()
        => ResourceManager.ReadWrite.Information.WindowsInformationFieldProjection.Security(Read(), DateTimeOffset.UtcNow);

    public DeviceTopologySecurityInformation Read()
    {
        var antivirus = Health(4); var firewall = Health(1);
        uint? active = null; bool? domain = null, privateProfile = null, publicProfile = null;
        try
        {
            using var policy = api.OpenFirewall();
            if (policy is not null)
            {
                active = Profiles(policy); domain = Enabled(policy, 1); privateProfile = Enabled(policy, 2); publicProfile = Enabled(policy, 4);
            }
        }
        catch (Exception error) when (IsSourceFailure(error)) { active = null; domain = privateProfile = publicProfile = null; }
        return new(antivirus, firewall, active, domain, privateProfile, publicProfile)
        { AntivirusProductsReported = Products(4), FirewallProductsReported = Products(1) };
    }

    private IReadOnlyList<DeviceTopologySecurityProductInformation>? Products(uint provider)
    {
        try { return api.ReadProducts(provider); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    private uint? Health(uint provider)
    {
        try { return api.GetHealth(provider, out var health) == 0 && health <= 3 ? health : null; }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }
    private static uint? Profiles(IWindowsFirewallPolicyRead policy)
    {
        try { return policy.GetCurrentProfiles(out var profiles) == 0 && (profiles & ~7U) == 0 ? profiles : null; }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }
    private static bool? Enabled(IWindowsFirewallPolicyRead policy, uint profile)
    {
        try { return policy.GetEnabled(profile, out var enabled) == 0 ? enabled switch { 0 => false, -1 => true, _ => null } : null; }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }
    internal static bool IsSourceFailure(Exception error) => error is ExternalException or DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
}
