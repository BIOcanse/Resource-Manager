using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Bluetooth;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private const string BluetoothControllerPrefix = "windows-bluetooth-controller|";
    private const string BluetoothDevicePrefix = "windows-bluetooth-device|";
    private static readonly JsonSerializerOptions BluetoothJsonOptions = InformationJsonContext.Default.Options;

    internal static string BluetoothControllerIdentity(string controllerAddress)
        => BluetoothControllerPrefix + controllerAddress;

    internal static string BluetoothDeviceIdentity(string controllerAddress, string deviceAddress)
        => BluetoothDevicePrefix + controllerAddress + "|" + deviceAddress;

    internal static DeviceInformationSnapshot BluetoothCurrent(DeviceInformationSnapshot current,
        WindowsBluetoothClassicInformationScope scope)
    {
        var names = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => names.Contains(field.Definition.Name)
                && IsIdentityInBluetoothScope(device.DeviceIdentity, scope)).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeBluetooth(DeviceInformationSnapshot current,
        WindowsBluetoothClassicInformationScope scope, WindowsBluetoothClassicInformationFields selected,
        WindowsBluetoothClassicInformationReadResult read, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if ((selected & ~scope.Fields) != 0) throw new ArgumentOutOfRangeException(nameof(selected));
        ValidateRead(scope, selected, read, cancellationToken);

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        if ((selected & WindowsBluetoothClassicInformationFields.ControllerId) != 0)
        {
            var directory = read.Controllers;
            DeviceInformationObject[] observed = directory is { DirectorySucceeded: true }
                ? directory.Objects.Select(item => ProjectBluetoothController(item, selected, observedAt)).ToArray()
                : Array.Empty<DeviceInformationObject>();
            MergeDirectory(objects, BluetoothControllerPrefix, "bluetooth_controller",
                [DeviceInformationFieldRegistry.BluetoothAllControllerIdDeclared],
                directory?.DirectorySucceeded == true, observed, cancellationToken);
        }

        if ((selected & WindowsBluetoothClassicInformationFields.DeviceState) != 0)
        {
            var directory = read.Devices;
            var definitions = new List<DeviceInformationDefinition>(2);
            if ((selected & WindowsBluetoothClassicInformationFields.DeviceConnected) != 0)
                definitions.Add(DeviceInformationFieldRegistry.BluetoothAllDeviceConnectedActual);
            if ((selected & WindowsBluetoothClassicInformationFields.DevicePaired) != 0)
                definitions.Add(DeviceInformationFieldRegistry.BluetoothAllDevicePairedActual);
            DeviceInformationObject[] observed = directory is { DirectorySucceeded: true }
                ? directory.Objects.Select(item => ProjectBluetoothDevice(item, selected, observedAt)).ToArray()
                : Array.Empty<DeviceInformationObject>();
            MergeDirectory(objects, BluetoothDevicePrefix, "bluetooth_device", definitions,
                directory?.DirectorySucceeded == true, observed, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(current.ObservedAt,
            objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static DeviceInformationSnapshot PreserveBluetoothFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var bluetoothFields = device.Fields.Where(static field => IsBluetoothDefinition(field.Definition)).ToArray();
            if (bluetoothFields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var projectedDevice))
            {
                var fields = bluetoothFields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in projectedDevice.Fields) fields[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = projectedDevice with { Fields = fields.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = bluetoothFields });
        }
        return new(projected.ObservedAt,
            objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsBluetoothDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.BluetoothAllControllerIdDeclared)
            || ReferenceEquals(definition, F.BluetoothAllDeviceConnectedActual)
            || ReferenceEquals(definition, F.BluetoothAllDevicePairedActual);

    private static DeviceInformationObject ProjectBluetoothController(
        DeviceTopologyBluetoothClassicControllerInformation controller,
        WindowsBluetoothClassicInformationFields selected, DateTimeOffset observedAt)
    {
        var fields = new List<DeviceInformationField>(1);
        if ((selected & WindowsBluetoothClassicInformationFields.ControllerId) != 0)
            fields.Add(BluetoothField(F.BluetoothAllControllerIdDeclared, controller.ControllerAddress, observedAt));
        return new(BluetoothControllerIdentity(controller.ControllerAddress), "bluetooth_controller", fields);
    }

    private static DeviceInformationObject ProjectBluetoothDevice(
        DeviceTopologyBluetoothClassicDeviceInformation device,
        WindowsBluetoothClassicInformationFields selected, DateTimeOffset observedAt)
    {
        var fields = new List<DeviceInformationField>(2);
        if ((selected & WindowsBluetoothClassicInformationFields.DeviceConnected) != 0)
            fields.Add(BluetoothField(F.BluetoothAllDeviceConnectedActual, device.Connected, observedAt));
        if ((selected & WindowsBluetoothClassicInformationFields.DevicePaired) != 0)
            fields.Add(BluetoothField(F.BluetoothAllDevicePairedActual, device.Authenticated, observedAt));
        return new(BluetoothDeviceIdentity(device.ControllerAddress, device.DeviceAddress), "bluetooth_device", fields);
    }

    private static void MergeDirectory(Dictionary<string, DeviceInformationObject> objects,
        string prefix, string objectType, IReadOnlyList<DeviceInformationDefinition> selectedDefinitions,
        bool directorySucceeded, IReadOnlyList<DeviceInformationObject> observed, CancellationToken cancellationToken)
    {
        var selectedNames = selectedDefinitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var existing = objects.Values.Where(device => device.DeviceIdentity.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (!directorySucceeded)
        {
            foreach (var device in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = device.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var definition in selectedDefinitions)
                    fields[definition.Name] = new(definition, null) { ObservedAt = null };
                objects[device.DeviceIdentity] = device with { Fields = fields.Values.ToArray() };
            }
            return;
        }

        foreach (var device in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = device.Fields.Where(field => !selectedNames.Contains(field.Definition.Name)).ToArray();
            if (fields.Length == 0) objects.Remove(device.DeviceIdentity);
            else objects[device.DeviceIdentity] = device with { Fields = fields };
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in observed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.DeviceIdentity.StartsWith(prefix, StringComparison.Ordinal)
                || item.DeviceIdentity.Length == prefix.Length || item.ObjectType != objectType
                || !identities.Add(item.DeviceIdentity))
                throw new InvalidOperationException("A Bluetooth reader returned an object outside its exact directory.");
            var names = item.Fields.Select(static field => field.Definition.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != item.Fields.Count || !names.SetEquals(selectedNames)
                || item.Fields.Any(field => !selectedDefinitions.Any(definition => ReferenceEquals(definition, field.Definition))))
                throw new InvalidOperationException("A Bluetooth reader did not project exactly the selected static fields.");

            var fields = objects.TryGetValue(item.DeviceIdentity, out var sameIdentity)
                ? sameIdentity.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
            foreach (var field in item.Fields) fields[field.Definition.Name] = field;
            objects[item.DeviceIdentity] = item with { Fields = fields.Values.ToArray() };
        }
    }

    private static void ValidateRead(WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selected, WindowsBluetoothClassicInformationReadResult read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(read.ControllerAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        if (read.ControllerAddresses.Count > scope.MaximumControllers)
            throw new InvalidOperationException("Bluetooth source exceeded the explicit controller bound.");
        var controllerAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in read.ControllerAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAddress(address);
            if (!controllerAddresses.Add(address))
                throw new InvalidOperationException("Bluetooth source returned a duplicate controller address.");
        }

        var controllerSelected = (selected & WindowsBluetoothClassicInformationFields.ControllerId) != 0;
        if (!controllerSelected && read.Controllers is not null)
            throw new InvalidOperationException("Bluetooth source returned an unrequested controller directory.");
        if (controllerSelected && read.Controllers is { } controllers)
        {
            ValidateDirectory(controllers, "controller");
            if (controllers.DirectorySucceeded)
            {
                var returned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in controllers.Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateAddress(item.ControllerAddress);
                    if (!returned.Add(item.ControllerAddress))
                        throw new InvalidOperationException("Bluetooth source returned a duplicate controller object.");
                }
                if (!returned.SetEquals(controllerAddresses))
                    throw new InvalidOperationException("Bluetooth controller rows did not match the enumerated controller identities.");
            }
        }

        var devicesSelected = (selected & WindowsBluetoothClassicInformationFields.DeviceState) != 0;
        if (!devicesSelected && read.Devices is not null)
            throw new InvalidOperationException("Bluetooth source returned an unrequested device directory.");
        if (devicesSelected && read.Devices is { DirectorySucceeded: true } devices)
        {
            if (devices.Objects.Count > scope.MaximumDevices)
                throw new InvalidOperationException("Bluetooth source exceeded the explicit device bound.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in devices.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateAddress(item.ControllerAddress);
                ValidateAddress(item.DeviceAddress);
                if (!controllerAddresses.Contains(item.ControllerAddress))
                    throw new InvalidOperationException("Bluetooth device row referenced a controller outside this read.");
                if (!identities.Add(item.ControllerAddress + "|" + item.DeviceAddress))
                    throw new InvalidOperationException("Bluetooth source returned the same device twice for one controller.");
            }
        }
        else if (devicesSelected && read.Devices is { } failedDevices)
        {
            ValidateDirectory(failedDevices, "device");
        }
    }

    private static void ValidateDirectory<T>(WindowsBluetoothClassicDirectoryRead<T> directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory.Objects);
        if (!directory.DirectorySucceeded && directory.Objects.Count != 0)
            throw new InvalidOperationException($"A failed Bluetooth {name} directory returned partial rows.");
    }

    private static void ValidateAddress(string address)
    {
        if (address is null || address.Length != 17)
            throw new InvalidOperationException("Bluetooth addresses must use uppercase XX:XX:XX:XX:XX:XX form.");
        for (var index = 0; index < address.Length; index++)
        {
            if (index % 3 == 2)
            {
                if (address[index] != ':') throw new InvalidOperationException("Bluetooth address separator is invalid.");
            }
            else
            {
                var character = address[index];
                if (!(character is >= '0' and <= '9' || character is >= 'A' and <= 'F'))
                    throw new InvalidOperationException("Bluetooth address must use uppercase hexadecimal digits.");
            }
        }
    }

    private static bool IsIdentityInBluetoothScope(string identity, WindowsBluetoothClassicInformationScope scope)
        => (scope.Fields & WindowsBluetoothClassicInformationFields.ControllerId) != 0
                && identity.StartsWith(BluetoothControllerPrefix, StringComparison.Ordinal)
            || (scope.Fields & WindowsBluetoothClassicInformationFields.DeviceState) != 0
                && identity.StartsWith(BluetoothDevicePrefix, StringComparison.Ordinal);

    private static DeviceInformationField BluetoothField<T>(DeviceInformationDefinition definition, T value, DateTimeOffset observedAt)
        => new(definition, JsonSerializer.SerializeToElement(value, BluetoothJsonOptions)) { ObservedAt = observedAt };
}
