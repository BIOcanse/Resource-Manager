using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class WindowsDisplayPathTopologyReader
{
    /// <summary>Explicitly captures the original paths; the caller supplies directory identity.</summary>
    public static DeviceInformationSnapshot ReadFields(Func<DeviceTopologyDisplayPath, string> deviceIdentity)
    {
        var capture = ReadSnapshot();
        return WindowsInformationFieldProjection.Displays(capture.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path.MonitorDevicePath))
            .Select(path => ToInformation(path, deviceIdentity(path))), capture.Error is null ? DateTimeOffset.UtcNow : null);
    }

    public static DeviceTopologyDisplayInformation ToInformation(DeviceTopologyDisplayPath path, string deviceIdentity)
    {
        var color = path.AdvancedColor;
        return new(deviceIdentity, path.Active, path.TargetAvailable, path.EdidObservationComplete ? path.EdidRawDeclaredBase64 : null,
            path.EdidObservationComplete ? path.Edid?.ChromaticityDeclared : null,
            path.EdidObservationComplete ? path.Edid?.HdrLuminanceDeclared : null, path.Width, path.Height, path.RefreshRateNumerator,
            path.RefreshRateDenominator, path.ClockwiseRotationDegrees,
            color?.ActiveColorModeReported is not null ? color.HighDynamicRangeSupported : null,
            color?.ActiveColorModeReported is not null ? color.HighDynamicRangeUserEnabled : null,
            color?.ActiveColorModeReported is 0 or 1 or 2 ? color.HighDynamicRangeActive : null,
            color?.ActiveColorModeReported, color?.BitsPerColorChannel, color?.AdvancedColorSupported,
            color?.AdvancedColorEnabled, path.SdrWhiteLevelNits, color?.ColorEncoding,
            path.Dxgi?.MinimumLuminanceNits, path.Dxgi?.MaximumLuminanceNits, path.Dxgi?.MaximumFullFrameLuminanceNits)
        {
            ModeCapabilityDeclared = path.EdidObservationComplete ? path.Edid?.ModeCapability : null
        };
    }
}
