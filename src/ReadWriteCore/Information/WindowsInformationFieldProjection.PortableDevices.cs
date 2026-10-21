using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions WpdJsonOptions = new(JsonSerializerDefaults.Web);

    internal static DeviceInformationSnapshot WpdCurrent(DeviceInformationSnapshot current,
        WindowsWpdInformationScope scope)
    {
        var names = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => names.Contains(field.Definition.Name)).ToArray()
        }).Where(device => IsWpdIdentity(device.DeviceIdentity) && device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeWpd(DeviceInformationSnapshot current,
        WindowsWpdInformationScope scope, WindowsWpdInformationCaptureRequest request,
        WindowsWpdInformationReadResult read, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(read);
        if ((request.Fields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(request));

        ValidateWpdRead(scope, request, read, cancellationToken);
        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        var selectedByIdentity = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
            ? null
            : request.Targets.ToDictionary(static target => WpdIdentity(target), static target => target.Fields,
                StringComparer.Ordinal);

        if (!read.DeviceDirectorySucceeded)
        {
            var failedTargets = selectedByIdentity is null
                ? objects.Values.Where(static device => IsWpdIdentity(device.DeviceIdentity))
                    .Select(static device => device.DeviceIdentity).ToArray()
                : selectedByIdentity.Keys.ToArray();
            foreach (var identity in failedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!objects.TryGetValue(identity, out var existing)) continue;
                var selected = selectedByIdentity?.GetValueOrDefault(identity)
                    ?? request.Fields & WpdFieldsFor(existing.ObjectType);
                if (selected == WindowsWpdInformationFields.None) continue;
                objects[identity] = WpdClearFields(existing, selected);
            }
            return WpdSnapshot(current, objects);
        }

        if (selectedByIdentity is null)
        {
            var failedStorageConnections = read.Devices.Where(static device => device.StorageDirectorySucceeded == false)
                .Select(static device => device.DeviceConnectionId).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in objects.Values.Where(static device => IsWpdIdentity(device.DeviceIdentity)).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existing.ObjectType == "mobile_storage"
                    && (request.Fields & WindowsWpdInformationFields.StorageCapacity) != 0
                    && failedStorageConnections.Contains(
                        WindowsWpdInformationIdentity.Parse(existing.DeviceIdentity, scope.MaximumStringCharacters).DeviceConnectionId))
                {
                    objects[existing.DeviceIdentity] = WpdClearFields(existing, WindowsWpdInformationFields.StorageCapacity);
                    continue;
                }
                var remaining = existing.Fields.Where(field => !IsSelectedWpdField(field.Definition, request.Fields)).ToArray();
                if (remaining.Length == 0) objects.Remove(existing.DeviceIdentity);
                else objects[existing.DeviceIdentity] = existing with { Fields = remaining };
            }
        }
        else
        {
            foreach (var pair in selectedByIdentity)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (objects.TryGetValue(pair.Key, out var existing))
                    objects[pair.Key] = WpdClearFields(existing, pair.Value);
            }
        }

        foreach (var device in read.Devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectedDevice = SelectedWpdFields(request, device.DeviceConnectionId,
                WindowsWpdInformationObjectKind.Device, WindowsWpdInformationIdentity.DeviceObjectId);
            if (selectedDevice != WindowsWpdInformationFields.None)
            {
                var projected = ProjectWpdDevice(device, selectedDevice, read.Failures, observedAt);
                WpdMergeObject(objects, projected);
            }

            if ((request.Fields & WindowsWpdInformationFields.StorageCapacity) == 0
                || device.StorageDirectorySucceeded != true)
                continue;
            foreach (var storage in device.Storages ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selectedStorage = SelectedWpdFields(request, storage.DeviceConnectionId,
                    WindowsWpdInformationObjectKind.Storage, storage.ObjectId);
                if (selectedStorage == WindowsWpdInformationFields.None) continue;
                var projected = ProjectWpdStorage(storage, read.Failures, observedAt);
                WpdMergeObject(objects, projected);
            }
        }

        if (selectedByIdentity is not null)
        {
            foreach (var target in request.Targets.Where(static target => target.ObjectKind == WindowsWpdInformationObjectKind.Storage))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceRead = read.Devices.Single(device => device.DeviceConnectionId == target.DeviceConnectionId);
                if (deviceRead.StorageDirectorySucceeded == true
                    && !(deviceRead.Storages ?? []).Any(storage => storage.ObjectId == target.ObjectId)
                    && !read.Failures.Any(failure => failure.DeviceConnectionId == target.DeviceConnectionId
                        && failure.ObjectId == target.ObjectId && (failure.FailedFields & target.Fields) != 0))
                    WpdRemoveFields(objects, WpdIdentity(target), target.Fields);
            }
        }

        return WpdSnapshot(current, objects);
    }

    internal static DeviceInformationSnapshot PreserveWpdFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsWpdDefinition(field.Definition)).ToArray();
            if (fields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var current))
            {
                var merged = fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in current.Fields) merged[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = current with { Fields = merged.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = fields });
        }
        return new(projected.ObservedAt,
            objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsWpdDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.MobileAllBatteryPercentReported)
            || ReferenceEquals(definition, F.MobileAllDeviceIdDeclared)
            || ReferenceEquals(definition, F.MobileAllFirmwareVersionDeclared)
            || ReferenceEquals(definition, F.MobileAllStorageCapacityReported);

    private static DeviceInformationObject ProjectWpdDevice(DeviceTopologyWpdDeviceInformation device,
        WindowsWpdInformationFields selected, IReadOnlyList<WindowsWpdInformationReadFailure> failures,
        DateTimeOffset observedAt)
    {
        var identity = WindowsWpdInformationIdentity.Device(device.DeviceConnectionId);
        var fields = new List<DeviceInformationField>(3);
        if ((selected & WindowsWpdInformationFields.BatteryPercent) != 0)
        {
            var value = HasWpdFailure(failures, device.DeviceConnectionId, WindowsWpdInformationIdentity.DeviceObjectId,
                WindowsWpdInformationFields.BatteryPercent) ? null : device.BatteryPercent;
            fields.Add(WpdField(F.MobileAllBatteryPercentReported, value, observedAt));
        }
        if ((selected & WindowsWpdInformationFields.DeviceId) != 0)
        {
            var value = HasWpdFailure(failures, device.DeviceConnectionId, WindowsWpdInformationIdentity.DeviceObjectId,
                WindowsWpdInformationFields.DeviceId) ? null : device.PersistentUniqueId;
            fields.Add(WpdField(F.MobileAllDeviceIdDeclared, value, observedAt));
        }
        if ((selected & WindowsWpdInformationFields.FirmwareVersion) != 0)
        {
            var value = HasWpdFailure(failures, device.DeviceConnectionId, WindowsWpdInformationIdentity.DeviceObjectId,
                WindowsWpdInformationFields.FirmwareVersion) ? null : device.FirmwareVersion;
            fields.Add(WpdField(F.MobileAllFirmwareVersionDeclared, value, observedAt));
        }
        return new(identity, "mobile_device", fields);
    }

    private static DeviceInformationObject ProjectWpdStorage(DeviceTopologyWpdStorageInformation storage,
        IReadOnlyList<WindowsWpdInformationReadFailure> failures, DateTimeOffset observedAt)
    {
        var identity = WindowsWpdInformationIdentity.Storage(storage.DeviceConnectionId, storage.ObjectId);
        var capacity = HasWpdFailure(failures, storage.DeviceConnectionId, storage.ObjectId,
            WindowsWpdInformationFields.StorageCapacity) ? null : storage.CapacityBytes;
        return new(identity, "mobile_storage",
            [WpdField(F.MobileAllStorageCapacityReported, capacity, observedAt)]);
    }

    private static bool HasWpdFailure(IReadOnlyList<WindowsWpdInformationReadFailure> failures,
        string connectionId, string? objectId, WindowsWpdInformationFields field)
        => failures.Any(failure => failure.DeviceConnectionId == connectionId
            && (failure.ObjectId == objectId || failure.ObjectId is null && objectId == WindowsWpdInformationIdentity.DeviceObjectId)
            && (failure.FailedFields & field) != 0);

    private static WindowsWpdInformationFields SelectedWpdFields(WindowsWpdInformationCaptureRequest request,
        string connectionId, WindowsWpdInformationObjectKind kind, string objectId)
    {
        if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
            return request.Fields & (kind == WindowsWpdInformationObjectKind.Device
                ? WindowsWpdInformationFields.Device : WindowsWpdInformationFields.StorageCapacity);
        var fields = WindowsWpdInformationFields.None;
        foreach (var target in request.Targets)
            if (target.DeviceConnectionId == connectionId && target.ObjectKind == kind && target.ObjectId == objectId)
                fields |= target.Fields;
        return fields;
    }

    private static void ValidateWpdRead(WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, WindowsWpdInformationReadResult read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read.Devices);
        ArgumentNullException.ThrowIfNull(read.Failures);
        cancellationToken.ThrowIfCancellationRequested();
        if (read.Failures.Count > scope.MaximumFailureRecords)
            throw new InvalidOperationException("The WPD source exceeded the explicit failure-record bound.");
        if (!read.DeviceDirectorySucceeded)
        {
            if (read.Devices.Count != 0 || read.Failures.Count != 0
                || string.IsNullOrWhiteSpace(read.DeviceDirectoryFailureCode)
                || read.DeviceDirectoryFailureCode.Length > 128)
                throw new InvalidOperationException("A failed WPD manager directory must report one failure and no partial devices.");
            return;
        }
        if (read.DeviceDirectoryFailureCode is not null)
            throw new InvalidOperationException("A successful WPD manager directory returned a failure code.");

        var targetCount = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
            ? scope.MaximumDevices : request.Targets.Select(static target => target.DeviceConnectionId)
                .Distinct(StringComparer.Ordinal).Count();
        if (targetCount > scope.MaximumDevices || read.Devices.Count > scope.MaximumDevices)
            throw new InvalidOperationException("The WPD source exceeded the explicit device bound.");

        var deviceIds = new HashSet<string>(StringComparer.Ordinal);
        var perDeviceStorageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var storageIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in read.Devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWpdString(device.DeviceConnectionId, scope.MaximumStringCharacters, "connection ID");
            if (device.DeviceObjectId != WindowsWpdInformationIdentity.DeviceObjectId
                || !deviceIds.Add(device.DeviceConnectionId))
                throw new InvalidOperationException("The WPD source returned a duplicate or non-root device identity.");
            if (request.Mode == WindowsWpdInformationReadMode.SelectedObjects
                && !request.Targets.Any(target => target.DeviceConnectionId == device.DeviceConnectionId))
                throw new InvalidOperationException("The WPD source returned a device outside the exact selected objects.");

            var selectedDevice = SelectedWpdFields(request, device.DeviceConnectionId,
                WindowsWpdInformationObjectKind.Device, WindowsWpdInformationIdentity.DeviceObjectId);
            if ((selectedDevice & WindowsWpdInformationFields.DeviceId) == 0 && device.PersistentUniqueId is not null
                || (selectedDevice & WindowsWpdInformationFields.FirmwareVersion) == 0 && device.FirmwareVersion is not null
                || (selectedDevice & WindowsWpdInformationFields.BatteryPercent) == 0 && device.BatteryPercent is not null)
                throw new InvalidOperationException("The WPD source returned an unselected device property.");
            ValidateWpdString(device.PersistentUniqueId, scope.MaximumStringCharacters, "persistent unique ID");
            ValidateWpdString(device.FirmwareVersion, scope.MaximumStringCharacters, "firmware version");
            if (device.BatteryPercent is > 100)
                throw new InvalidOperationException("WPD_DEVICE_POWER_LEVEL is outside its documented 0–100 range.");

            var storageSelected = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
                ? (request.Fields & WindowsWpdInformationFields.StorageCapacity) != 0
                : request.Targets.Any(target => target.DeviceConnectionId == device.DeviceConnectionId
                    && target.ObjectKind == WindowsWpdInformationObjectKind.Storage);
            if (!storageSelected)
            {
                if (device.StorageDirectorySucceeded is not null || device.Storages is { Count: > 0 })
                    throw new InvalidOperationException("The WPD source returned an unselected storage directory.");
                continue;
            }

            if (device.StorageDirectorySucceeded is null)
                throw new InvalidOperationException("The WPD source omitted the selected storage-directory status.");
            ArgumentNullException.ThrowIfNull(device.Storages);
            if (!device.StorageDirectorySucceeded.Value && device.Storages.Count != 0)
                throw new InvalidOperationException("A failed WPD storage directory returned partial objects.");
            if (!device.StorageDirectorySucceeded.Value
                && !read.Failures.Any(failure => failure.DeviceConnectionId == device.DeviceConnectionId
                    && failure.ObjectId is null
                    && (failure.FailedFields & WindowsWpdInformationFields.StorageCapacity) != 0))
                throw new InvalidOperationException("A failed WPD storage directory omitted its observable source failure.");
            if (!device.StorageDirectorySucceeded.Value) continue;

            if (device.Storages.Count > scope.MaximumStorageObjectsPerDevice)
                throw new InvalidOperationException("The WPD source exceeded the per-device storage-object bound.");
            var count = device.Storages.Count;
            perDeviceStorageCounts[device.DeviceConnectionId] = count;
            foreach (var storage in device.Storages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (storage.DeviceConnectionId != device.DeviceConnectionId)
                    throw new InvalidOperationException("A WPD storage object was returned for another device connection.");
                ValidateWpdString(storage.ObjectId, scope.MaximumStringCharacters, "storage object ID");
                if (!storageIdentities.Add(WindowsWpdInformationIdentity.Storage(storage.DeviceConnectionId, storage.ObjectId)))
                    throw new InvalidOperationException("The WPD source returned a duplicate storage object ID.");
                if (request.Mode == WindowsWpdInformationReadMode.SelectedObjects
                    && !request.Targets.Any(target => target.ObjectKind == WindowsWpdInformationObjectKind.Storage
                        && target.DeviceConnectionId == storage.DeviceConnectionId && target.ObjectId == storage.ObjectId))
                    throw new InvalidOperationException("The WPD source returned an unselected storage object.");
            }
        }

        if (perDeviceStorageCounts.Values.Sum() > scope.MaximumStorageObjects)
            throw new InvalidOperationException("The WPD source exceeded the total storage-object bound.");
        if (request.Mode == WindowsWpdInformationReadMode.SelectedObjects)
        {
            var requestedConnections = request.Targets.Select(static target => target.DeviceConnectionId)
                .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (!requestedConnections.SetEquals(deviceIds))
                throw new InvalidOperationException("The WPD source omitted or added an exact selected connection.");
        }
        foreach (var failure in read.Failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWpdString(failure.DeviceConnectionId, scope.MaximumStringCharacters, "failure connection ID");
            ValidateWpdString(failure.ObjectId, scope.MaximumStringCharacters, "failure object ID");
            if (string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > 128
                || failure.FailedFields == WindowsWpdInformationFields.None
                || (failure.FailedFields & ~request.Fields) != 0
                || !deviceIds.Contains(failure.DeviceConnectionId))
                throw new InvalidOperationException("The WPD source returned an invalid property failure.");
            var rootFailure = failure.ObjectId == WindowsWpdInformationIdentity.DeviceObjectId;
            var storageDirectoryFailure = failure.ObjectId is null
                && failure.FailedFields == WindowsWpdInformationFields.StorageCapacity;
            var selectedForFailure = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
                ? request.Fields
                : failure.ObjectId == WindowsWpdInformationIdentity.DeviceObjectId
                    ? SelectedWpdFields(request, failure.DeviceConnectionId,
                        WindowsWpdInformationObjectKind.Device, WindowsWpdInformationIdentity.DeviceObjectId)
                    : SelectedWpdStorageFields(request, failure.DeviceConnectionId);
            if (rootFailure && (failure.FailedFields & ~WindowsWpdInformationFields.Device) != 0
                || storageDirectoryFailure && (failure.FailedFields & ~WindowsWpdInformationFields.StorageCapacity) != 0
                || failure.ObjectId is null && !storageDirectoryFailure
                || failure.ObjectId is not null && !rootFailure
                    && failure.FailedFields != WindowsWpdInformationFields.StorageCapacity
                || request.Mode == WindowsWpdInformationReadMode.SelectedObjects
                    && (failure.FailedFields & ~selectedForFailure) != 0)
                throw new InvalidOperationException("The WPD source attached a property failure to the wrong object kind.");
            if (failure.ObjectId is not null && !rootFailure
                && !storageIdentities.Contains(WindowsWpdInformationIdentity.Storage(failure.DeviceConnectionId, failure.ObjectId))
                && !(failure.Code == "selected-storage-object-not-present"
                    && request.Targets.Any(target => target.ObjectKind == WindowsWpdInformationObjectKind.Storage
                        && target.DeviceConnectionId == failure.DeviceConnectionId && target.ObjectId == failure.ObjectId)))
                throw new InvalidOperationException("The WPD source returned a failure for an unknown storage object.");
        }
    }

    private static WindowsWpdInformationFields SelectedWpdStorageFields(
        WindowsWpdInformationCaptureRequest request, string connectionId)
    {
        if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
            return request.Fields & WindowsWpdInformationFields.StorageCapacity;
        var fields = WindowsWpdInformationFields.None;
        foreach (var target in request.Targets)
            if (target.DeviceConnectionId == connectionId
                && target.ObjectKind == WindowsWpdInformationObjectKind.Storage)
                fields |= target.Fields;
        return fields;
    }

    private static void ValidateWpdString(string? value, int maximumCharacters, string name)
    {
        if (value is not null && (value.Length == 0 || value.Length > maximumCharacters))
            throw new InvalidOperationException($"The WPD source returned an invalid {name}.");
    }

    private static string WpdIdentity(WindowsWpdInformationTarget target)
        => target.ObjectKind == WindowsWpdInformationObjectKind.Device
            ? WindowsWpdInformationIdentity.Device(target.DeviceConnectionId)
            : WindowsWpdInformationIdentity.Storage(target.DeviceConnectionId, target.ObjectId);

    private static bool IsWpdIdentity(string identity)
        => identity.StartsWith(WindowsWpdInformationIdentity.DevicePrefix, StringComparison.Ordinal)
            || identity.StartsWith(WindowsWpdInformationIdentity.StoragePrefix, StringComparison.Ordinal);

    private static bool IsSelectedWpdField(DeviceInformationDefinition definition,
        WindowsWpdInformationFields selected)
    {
        if (!IsWpdDefinition(definition)) return false;
        return (WindowsWpdInformationScope.DefinitionField(definition) & selected) != 0;
    }

    private static WindowsWpdInformationFields WpdFieldsFor(string objectType)
        => objectType == "mobile_storage" ? WindowsWpdInformationFields.StorageCapacity
            : objectType == "mobile_device" ? WindowsWpdInformationFields.Device
            : WindowsWpdInformationFields.None;

    private static DeviceInformationObject WpdClearFields(DeviceInformationObject device,
        WindowsWpdInformationFields selected)
    {
        var fields = device.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
        foreach (var definition in new[]
        {
            F.MobileAllBatteryPercentReported,
            F.MobileAllDeviceIdDeclared,
            F.MobileAllFirmwareVersionDeclared,
            F.MobileAllStorageCapacityReported
        })
            if ((WindowsWpdInformationScope.DefinitionField(definition) & selected) != 0)
                fields[definition.Name] = new(definition, null) { ObservedAt = null };
        return device with { Fields = fields.Values.ToArray() };
    }

    private static void WpdMergeObject(Dictionary<string, DeviceInformationObject> objects,
        DeviceInformationObject projected)
    {
        if (!objects.TryGetValue(projected.DeviceIdentity, out var existing))
        {
            objects.Add(projected.DeviceIdentity, projected);
            return;
        }
        if (existing.ObjectType != projected.ObjectType)
            throw new InvalidOperationException("A WPD object identity changed object type.");
        var fields = existing.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
        foreach (var field in projected.Fields) fields[field.Definition.Name] = field;
        objects[projected.DeviceIdentity] = projected with { Fields = fields.Values.ToArray() };
    }

    private static void WpdRemoveFields(Dictionary<string, DeviceInformationObject> objects,
        string identity, WindowsWpdInformationFields selected)
    {
        if (!objects.TryGetValue(identity, out var existing)) return;
        var remaining = existing.Fields.Where(field => !IsSelectedWpdField(field.Definition, selected)).ToArray();
        if (remaining.Length == 0) objects.Remove(identity);
        else objects[identity] = existing with { Fields = remaining };
    }

    private static DeviceInformationSnapshot WpdSnapshot(DeviceInformationSnapshot current,
        Dictionary<string, DeviceInformationObject> objects)
        => new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());

    private static DeviceInformationField WpdField<T>(DeviceInformationDefinition definition, T? value,
        DateTimeOffset observedAt)
    {
        if (value is null) return new(definition, null) { ObservedAt = null };
        var jsonValue = value is ulong unsignedValue
            ? JsonSerializer.SerializeToElement(unsignedValue.ToString(CultureInfo.InvariantCulture), WpdJsonOptions)
            : JsonSerializer.SerializeToElement(value, WpdJsonOptions);
        return new(definition, jsonValue) { ObservedAt = observedAt };
    }
}
