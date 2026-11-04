namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition SecurityAllAntivirusStateReported =
        new("security_all_antivirus_state_reported", "security", "all", "unsigned-integer", "enum32", "security.antivirus_category_health_reported", "windows.security-center", ReadVolatility.EventDriven("Windows Security Center antivirus category health changed"));
    public static readonly DeviceInformationDefinition SecurityAllFirewallStateReported =
        new("security_all_firewall_state_reported", "security", "all", "unsigned-integer", "enum32", "security.firewall_category_health_reported", "windows.security-center", ReadVolatility.EventDriven("Windows Security Center firewall category health changed"));
    public static readonly DeviceInformationDefinition SecurityAllWindowsFirewallProfilesActiveMask =
        new("security_all_windows_firewall_profiles_active_mask", "security", "all", "unsigned-integer", "bitmask32", "security.windows_firewall_profiles_active_mask", "windows.firewall-policy", ReadVolatility.EventDriven("Windows active firewall profile set changed"));
    public static readonly DeviceInformationDefinition SecurityAllWindowsFirewallProfileType =
        new("security_all_windows_firewall_profile_type", "security", "all", "unsigned-integer", "enum32", "security.windows_firewall_profile_identity_code", "windows.firewall-policy", ReadVolatility.EventDriven("Windows firewall profile registration changed"));
    public static readonly DeviceInformationDefinition SecurityAllWindowsFirewallEnabledConfigured =
        new("security_all_windows_firewall_enabled_configured", "security", "all", "boolean", "boolean", "security.windows_firewall_enabled_locally_configured", "windows.firewall-policy", ReadVolatility.EventDriven("Windows firewall profile enabled setting changed"));
}
