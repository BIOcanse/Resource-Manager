namespace ResourceManager.ReadWrite.Information;

/// <summary>One CTA HDR static metadata block's desired content brightness, not measured panel output.</summary>
public sealed record DeviceInformationHdrLuminanceDeclaration(
    uint ExtensionBlock,
    uint DataBlockOffset,
    double? DesiredContentMaxLuminanceNits,
    double? DesiredContentMaxFrameAverageLuminanceNits,
    double? DesiredContentMinLuminanceNits);
