using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Canonical display facts. Identity is supplied by the caller's device directory.</summary>
public sealed record DeviceTopologyDisplayInformation(string DeviceIdentity, bool Active, bool TargetAvailable,
    string? EdidRawDeclaredBase64 = null, DeviceInformationChromaticity? ChromaticityDeclared = null,
    IReadOnlyList<DeviceInformationHdrLuminanceDeclaration>? HdrLuminanceDeclared = null,
    uint? CurrentWidth = null, uint? CurrentHeight = null,
    uint? CurrentRefreshRateNumerator = null, uint? CurrentRefreshRateDenominator = null,
    uint? ClockwiseRotationDegrees = null, bool? HdrSupportedReported = null,
    bool? HdrUserEnabledActual = null, bool? HdrActiveActual = null, uint? ColorModeActual = null,
    uint? CurrentBitsPerColorChannel = null, bool? AdvancedColorSupported = null,
    bool? AdvancedColorEnabled = null, double? SdrWhiteLevelNits = null, string? ColorEncoding = null,
    double? MinimumLuminanceNits = null, double? MaximumLuminanceNits = null,
    double? MaximumFullFrameLuminanceNits = null)
{
    public EdidModeCapability? ModeCapabilityDeclared { get; init; }
}
