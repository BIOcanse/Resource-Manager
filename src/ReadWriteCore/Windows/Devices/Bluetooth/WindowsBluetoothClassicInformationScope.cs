using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Bluetooth;

[Flags]
public enum WindowsBluetoothClassicInformationFields
{
    None = 0,
    ControllerId = 1,
    DeviceConnected = 2,
    DevicePaired = 4,
    DeviceState = DeviceConnected | DevicePaired,
    All = ControllerId | DeviceState
}

/// <summary>Explicit Classic Bluetooth field scope and hard enumeration bounds. This scope does
/// not cover Bluetooth LE; no inquiry, pairing, connection, or radio mutation is requested.</summary>
public sealed record WindowsBluetoothClassicInformationScope
{
    public WindowsBluetoothClassicInformationFields Fields { get; }
    public int MaximumControllers { get; }
    public int MaximumDevices { get; }

    public WindowsBluetoothClassicInformationScope(WindowsBluetoothClassicInformationFields fields,
        int maximumControllers, int maximumDevices)
    {
        if (fields == WindowsBluetoothClassicInformationFields.None
            || (fields & ~WindowsBluetoothClassicInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (maximumControllers is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumControllers));
        if (maximumDevices is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        Fields = fields;
        MaximumControllers = maximumControllers;
        MaximumDevices = maximumDevices;
    }

    internal WindowsBluetoothClassicInformationFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var fields = WindowsBluetoothClassicInformationFields.None;
        foreach (var item in selection)
        {
            var field = DefinitionField(item.Definition);
            if (item.Field.FieldName != item.Definition.Name || !ReferenceEquals(item.Definition, DefinitionFor(field)))
                throw new ArgumentException("Bluetooth selection must use the exact static field definition.", nameof(selection));
            if ((Fields & field) == 0)
                throw new ArgumentException("The selected Bluetooth field is outside this scope.", nameof(selection));
            fields |= field;
        }
        return fields;
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>();
            foreach (var selectedField in new[]
            {
                WindowsBluetoothClassicInformationFields.ControllerId,
                WindowsBluetoothClassicInformationFields.DeviceConnected,
                WindowsBluetoothClassicInformationFields.DevicePaired
            })
                if ((Fields & selectedField) != 0) definitions.Add(DefinitionFor(selectedField));
            return definitions.ToImmutable();
        }
    }

    internal static WindowsBluetoothClassicInformationFields DefinitionField(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.BluetoothAllControllerIdDeclared))
            return WindowsBluetoothClassicInformationFields.ControllerId;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.BluetoothAllDeviceConnectedActual))
            return WindowsBluetoothClassicInformationFields.DeviceConnected;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.BluetoothAllDevicePairedActual))
            return WindowsBluetoothClassicInformationFields.DevicePaired;
        throw new ArgumentException("Unknown Bluetooth information definition.", nameof(definition));
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsBluetoothClassicInformationFields field) => field switch
    {
        WindowsBluetoothClassicInformationFields.ControllerId => DeviceInformationFieldRegistry.BluetoothAllControllerIdDeclared,
        WindowsBluetoothClassicInformationFields.DeviceConnected => DeviceInformationFieldRegistry.BluetoothAllDeviceConnectedActual,
        WindowsBluetoothClassicInformationFields.DevicePaired => DeviceInformationFieldRegistry.BluetoothAllDevicePairedActual,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
