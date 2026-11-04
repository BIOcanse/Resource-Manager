namespace ResourceManager.ReadWrite.Information;

/// <summary>CIE 1931 xy fractions declared in the EDID base block, not measured color.</summary>
public sealed record DeviceInformationChromaticity(
    DeviceInformationChromaticityPoint? Red,
    DeviceInformationChromaticityPoint? Green,
    DeviceInformationChromaticityPoint? Blue,
    DeviceInformationChromaticityPoint DefaultWhite);

public sealed record DeviceInformationChromaticityPoint(double X, double Y);
