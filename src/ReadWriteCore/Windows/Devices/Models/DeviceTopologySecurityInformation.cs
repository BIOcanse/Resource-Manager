namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>WSC category reports and Windows firewall policy are distinct sources and meanings.</summary>
public sealed record DeviceTopologySecurityInformation(
    uint? AntivirusHealthReported, uint? FirewallHealthReported, uint? ActiveFirewallProfiles,
    bool? DomainFirewallEnabledConfigured, bool? PrivateFirewallEnabledConfigured, bool? PublicFirewallEnabledConfigured)
{
    public IReadOnlyList<DeviceTopologySecurityProductInformation>? AntivirusProductsReported { get; init; }
    public IReadOnlyList<DeviceTopologySecurityProductInformation>? FirewallProductsReported { get; init; }
    public static DeviceTopologySecurityInformation Unavailable { get; } = new(null, null, null, null, null, null);
}
