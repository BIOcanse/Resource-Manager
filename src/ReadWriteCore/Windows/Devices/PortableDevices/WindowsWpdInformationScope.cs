using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

[Flags]
public enum WindowsWpdInformationFields
{
    None = 0,
    BatteryPercent = 1,
    DeviceId = 2,
    FirmwareVersion = 4,
    StorageCapacity = 8,
    Device = BatteryPercent | DeviceId | FirmwareVersion,
    All = Device | StorageCapacity
}

public enum WindowsWpdInformationReadMode
{
    AllConnectedDevices,
    SelectedObjects
}

public enum WindowsWpdInformationObjectKind
{
    Device,
    Storage
}

/// <summary>One exact device-root or storage-object selection. The connection ID is
/// the PnP ID returned by IPortableDeviceManager, not a PnP classification guess.</summary>
public sealed record WindowsWpdInformationTarget
{
    public string DeviceConnectionId { get; }
    public string ObjectId { get; }
    public WindowsWpdInformationObjectKind ObjectKind { get; }
    public WindowsWpdInformationFields Fields { get; }

    public WindowsWpdInformationTarget(string deviceConnectionId, string objectId,
        WindowsWpdInformationObjectKind objectKind, WindowsWpdInformationFields fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceConnectionId);
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        if (!Enum.IsDefined(objectKind)) throw new ArgumentOutOfRangeException(nameof(objectKind));
        if (fields == WindowsWpdInformationFields.None
            || (fields & ~WindowsWpdInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (objectKind == WindowsWpdInformationObjectKind.Device
            && (objectId != WindowsWpdInformationIdentity.DeviceObjectId
                || (fields & ~WindowsWpdInformationFields.Device) != 0))
            throw new ArgumentException("Device targets must select the exact WPD device root and device fields.", nameof(fields));
        if (objectKind == WindowsWpdInformationObjectKind.Storage
            && fields != WindowsWpdInformationFields.StorageCapacity)
            throw new ArgumentException("Storage targets select only the WPD storage capacity field.", nameof(fields));

        DeviceConnectionId = deviceConnectionId;
        ObjectId = objectId;
        ObjectKind = objectKind;
        Fields = fields;
    }
}

/// <summary>The explicit native read request. Full discovery is distinct from a
/// selected-object capture; selected captures carry exact catalog object identities.</summary>
public sealed record WindowsWpdInformationCaptureRequest
{
    public WindowsWpdInformationReadMode Mode { get; }
    public WindowsWpdInformationFields Fields { get; }
    public ImmutableArray<WindowsWpdInformationTarget> Targets { get; }

    internal WindowsWpdInformationCaptureRequest(WindowsWpdInformationReadMode mode,
        WindowsWpdInformationFields fields, ImmutableArray<WindowsWpdInformationTarget> targets)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (fields == WindowsWpdInformationFields.None || (fields & ~WindowsWpdInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (mode == WindowsWpdInformationReadMode.AllConnectedDevices && !targets.IsEmpty
            || mode == WindowsWpdInformationReadMode.SelectedObjects && targets.IsEmpty)
            throw new ArgumentException("The target list must match the explicit WPD read mode.", nameof(targets));
        if (targets.Any(target => (target.Fields & ~fields) != 0))
            throw new ArgumentException("A WPD target requests fields outside the capture request.", nameof(targets));
        Mode = mode;
        Fields = fields;
        Targets = targets;
    }
}

/// <summary>Explicit selected field scope and hard bounds. Construction is passive;
/// discovery and capture only read the fields selected here.</summary>
public sealed record WindowsWpdInformationScope
{
    public WindowsWpdInformationFields Fields { get; }
    public int MaximumDevices { get; }
    public int MaximumStorageObjectsPerDevice { get; }
    public int MaximumStorageObjects { get; }
    public int MaximumStringCharacters { get; }
    /// <summary>Maximum accepted source failure records: four per bounded device
    /// plus one per bounded storage object.</summary>
    public int MaximumFailureRecords => checked(MaximumDevices * 4 + MaximumStorageObjects);

    public WindowsWpdInformationScope(WindowsWpdInformationFields fields, int maximumDevices,
        int maximumStorageObjectsPerDevice, int maximumStorageObjects, int maximumStringCharacters)
    {
        if (fields == WindowsWpdInformationFields.None || (fields & ~WindowsWpdInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (maximumDevices is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        if (maximumStorageObjectsPerDevice is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumStorageObjectsPerDevice));
        if (maximumStorageObjects is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(maximumStorageObjects));
        if (maximumStringCharacters is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(maximumStringCharacters));
        Fields = fields;
        MaximumDevices = maximumDevices;
        MaximumStorageObjectsPerDevice = maximumStorageObjectsPerDevice;
        MaximumStorageObjects = maximumStorageObjects;
        MaximumStringCharacters = maximumStringCharacters;
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(4);
            foreach (var selectedField in new[]
            {
                WindowsWpdInformationFields.BatteryPercent,
                WindowsWpdInformationFields.DeviceId,
                WindowsWpdInformationFields.FirmwareVersion,
                WindowsWpdInformationFields.StorageCapacity
            })
                if ((Fields & selectedField) != 0) definitions.Add(DefinitionFor(selectedField));
            return definitions.ToImmutable();
        }
    }

    internal WindowsWpdInformationCaptureRequest DiscoveryRequest()
        => new(WindowsWpdInformationReadMode.AllConnectedDevices, Fields, []);

    internal WindowsWpdInformationCaptureRequest Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var targets = new Dictionary<(string ConnectionId, string ObjectId, WindowsWpdInformationObjectKind Kind),
            WindowsWpdInformationFields>();
        var fields = WindowsWpdInformationFields.None;
        foreach (var item in selection)
        {
            var field = DefinitionField(item.Definition);
            if (item.Field.FieldName != item.Definition.Name || !ReferenceEquals(item.Definition, DefinitionFor(field)))
                throw new ArgumentException("WPD selection must use the exact static field definition.", nameof(selection));
            if ((Fields & field) == 0)
                throw new ArgumentException("The selected WPD field is outside this scope.", nameof(selection));
            var identity = WindowsWpdInformationIdentity.Parse(item.Field.DeviceIdentity, MaximumStringCharacters);
            var expectedKind = field == WindowsWpdInformationFields.StorageCapacity
                ? WindowsWpdInformationObjectKind.Storage : WindowsWpdInformationObjectKind.Device;
            if (identity.Kind != expectedKind)
                throw new ArgumentException("The selected WPD field does not match its exact device or storage object.", nameof(selection));
            var key = (identity.DeviceConnectionId, identity.ObjectId, identity.Kind);
            targets[key] = targets.GetValueOrDefault(key) | field;
            fields |= field;
        }

        if (fields == WindowsWpdInformationFields.None)
            throw new ArgumentException("A selected WPD capture must contain at least one field.", nameof(selection));
        var exactTargets = targets.Select(static pair => new WindowsWpdInformationTarget(
            pair.Key.ConnectionId, pair.Key.ObjectId, pair.Key.Kind, pair.Value)).ToImmutableArray();
        return new(WindowsWpdInformationReadMode.SelectedObjects, fields, exactTargets);
    }

    internal static WindowsWpdInformationFields DefinitionField(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.MobileAllBatteryPercentReported))
            return WindowsWpdInformationFields.BatteryPercent;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.MobileAllDeviceIdDeclared))
            return WindowsWpdInformationFields.DeviceId;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.MobileAllFirmwareVersionDeclared))
            return WindowsWpdInformationFields.FirmwareVersion;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.MobileAllStorageCapacityReported))
            return WindowsWpdInformationFields.StorageCapacity;
        throw new ArgumentException("Unknown WPD information definition.", nameof(definition));
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsWpdInformationFields field) => field switch
    {
        WindowsWpdInformationFields.BatteryPercent => DeviceInformationFieldRegistry.MobileAllBatteryPercentReported,
        WindowsWpdInformationFields.DeviceId => DeviceInformationFieldRegistry.MobileAllDeviceIdDeclared,
        WindowsWpdInformationFields.FirmwareVersion => DeviceInformationFieldRegistry.MobileAllFirmwareVersionDeclared,
        WindowsWpdInformationFields.StorageCapacity => DeviceInformationFieldRegistry.MobileAllStorageCapacityReported,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
