namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition SecurityAllAntivirusProductsReported =
        new("security_all_antivirus_products_reported", "security", "all", "security-products", "record[]", "security.antivirus_product_registrations_reported", "windows.security-center-products", ReadVolatility.EventDriven("Windows Security Center antivirus product registration changed"));
    public static readonly DeviceInformationDefinition SecurityAllFirewallProductsReported =
        new("security_all_firewall_products_reported", "security", "all", "security-products", "record[]", "security.firewall_product_registrations_reported", "windows.security-center-products", ReadVolatility.EventDriven("Windows Security Center firewall product registration changed"));
}
