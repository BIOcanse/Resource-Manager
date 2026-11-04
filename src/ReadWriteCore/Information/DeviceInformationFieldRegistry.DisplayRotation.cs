namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition DisplayAllRotationActual =
        new("display_all_rotation_actual", "display", "all", "unsigned-integer", "degrees",
            "display.rotation_actual_clockwise_degrees", "windows.display-config", ReadVolatility.EventDriven("Windows display configuration changed"));
}
