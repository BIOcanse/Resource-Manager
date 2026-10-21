namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>One current WSC registration record; name and position are not stable identities.</summary>
public sealed record DeviceTopologySecurityProductInformation(string? NameReported, uint? StateReported,
    uint? SignatureStatusReported, string? RemediationPathReported, string? StateTimestampReported)
{
    public string? ProductGuidReported { get; init; }
    public bool? ProductIsDefaultReported { get; init; }
    public uint? AntivirusScanSubstatusReported { get; init; }
    public uint? AntivirusSettingsSubstatusReported { get; init; }
    public uint? AntivirusProtectionUpdateSubstatusReported { get; init; }
    public uint? FirewallDomainProfileSubstatusReported { get; init; }
    public uint? FirewallPrivateProfileSubstatusReported { get; init; }
    public uint? FirewallPublicProfileSubstatusReported { get; init; }
    public uint? AntivirusDaysUntilExpiredReported { get; init; }
}
