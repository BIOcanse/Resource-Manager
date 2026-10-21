using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> DisplayFields(DeviceTopologyDisplayInformation display)
    {
        var active = display.Active && display.TargetAvailable;
        var resolution = active && display.CurrentWidth is > 0 && display.CurrentHeight is > 0
            ? new DeviceInformationResolution(display.CurrentWidth.Value, display.CurrentHeight.Value) : null;
        var rate = active && display.CurrentRefreshRateNumerator is > 0 && display.CurrentRefreshRateDenominator is > 0
            ? new DeviceInformationRate(display.CurrentRefreshRateNumerator.Value, display.CurrentRefreshRateDenominator.Value,
                (double)display.CurrentRefreshRateNumerator.Value / display.CurrentRefreshRateDenominator.Value) : null;
        return [Value(F.DisplayAllModeCapability, display.ModeCapabilityDeclared),
            Value(F.DisplayAllEdidRawDeclared, display.EdidRawDeclaredBase64),
            Value(F.DisplayAllChromaticityDeclared, display.ChromaticityDeclared),
            Value(F.DisplayAllHdrLuminanceDeclared, display.HdrLuminanceDeclared),
            Value(F.DisplayAllResolutionActual, resolution), Value(F.DisplayAllRefreshRateActual, rate),
            Integer(F.DisplayAllRotationActual, active && display.ClockwiseRotationDegrees is 0 or 90 or 180 or 270
                ? display.ClockwiseRotationDegrees : null),
            Boolean(F.DisplayAllHdrCapability, display.HdrSupportedReported),
            Boolean(F.DisplayAllHdrEnabledActual, active ? display.HdrUserEnabledActual : null),
            Boolean(F.DisplayAllHdrActiveActual, active ? display.HdrActiveActual : null),
            Integer(F.DisplayAllColorModeActual, active ? display.ColorModeActual : null),
            Integer(F.DisplayAllBitDepthActual, active && display.CurrentBitsPerColorChannel is > 0 ? display.CurrentBitsPerColorChannel : null),
            Boolean(F.DisplayAllAdvancedColorSupported, display.AdvancedColorSupported),
            Boolean(F.DisplayAllAdvancedColorEnabledActual, active ? display.AdvancedColorEnabled : null),
            Number(F.DisplayAllSdrWhiteLevelActualNits, active ? display.SdrWhiteLevelNits : null),
            Text(F.DisplayAllColorEncodingActual, active ? display.ColorEncoding : null),
            Number(F.DisplayAllMinimumLuminanceReportedNits, display.MinimumLuminanceNits),
            Number(F.DisplayAllMaximumLuminanceReportedNits, display.MaximumLuminanceNits),
            Number(F.DisplayAllMaximumFullFrameLuminanceReportedNits, display.MaximumFullFrameLuminanceNits)];
    }

    public static DeviceInformationSnapshot Displays(IEnumerable<DeviceTopologyDisplayInformation> displays, DateTimeOffset? observedAt)
        => Frame(displays.Where(display => !string.IsNullOrWhiteSpace(display.DeviceIdentity))
            .Select(display => new DeviceInformationObject(display.DeviceIdentity, "display_path", DisplayFields(display))), observedAt);
}
