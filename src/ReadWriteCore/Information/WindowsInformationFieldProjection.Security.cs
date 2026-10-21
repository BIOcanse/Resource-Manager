using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> SecurityFields(DeviceTopologySecurityInformation information)
        => [Integer(F.SecurityAllAntivirusStateReported, information.AntivirusHealthReported),
            Integer(F.SecurityAllFirewallStateReported, information.FirewallHealthReported),
            Value(F.SecurityAllAntivirusProductsReported, information.AntivirusProductsReported),
            Value(F.SecurityAllFirewallProductsReported, information.FirewallProductsReported),
            Integer(F.SecurityAllWindowsFirewallProfilesActiveMask, information.ActiveFirewallProfiles)];

    public static DeviceInformationSnapshot Security(DeviceTopologySecurityInformation information, DateTimeOffset? observedAt)
    {
        var devices = new List<DeviceInformationObject> { new("machine", "machine", SecurityFields(information)) };
        AppendSecurityProfiles(devices, information, observedAt);
        return Frame(devices, observedAt);
    }

    internal static void AppendSecurityProfiles(List<DeviceInformationObject> devices, DeviceTopologySecurityInformation information, DateTimeOffset? observedAt)
    {
        Add("windows-firewall-profile|domain", 1, information.DomainFirewallEnabledConfigured);
        Add("windows-firewall-profile|private", 2, information.PrivateFirewallEnabledConfigured);
        Add("windows-firewall-profile|public", 4, information.PublicFirewallEnabledConfigured);
        void Add(string identity, uint profile, bool? enabled)
        {
            DeviceInformationField[] fields = [Integer(F.SecurityAllWindowsFirewallProfileType, profile), Boolean(F.SecurityAllWindowsFirewallEnabledConfigured, enabled)];
            if (observedAt is null) fields = fields.Select(static field => field with { Value = null }).ToArray();
            devices.Add(new(identity, "windows_firewall_profile", Array.AsReadOnly(fields)));
        }
    }
}
