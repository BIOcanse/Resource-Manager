using System.Collections.Frozen;
using ResourceManager.App.Infrastructure.Monitoring;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private const AtaSmartAttributeInformationFields AtaSmartAccessAllFields =
        AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw
        | AtaSmartAttributeInformationFields.Threshold;

    internal static AtaSmartAttributeInformationRequest CopyAtaSmartAccessRequest(
        AtaSmartAttributeInformationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Devices);
        if (request.MaximumDeviceCount <= 0 || request.Devices.Count > request.MaximumDeviceCount)
            throw new ArgumentOutOfRangeException(nameof(request));
        foreach (var (identity, fields) in request.Devices)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A physical-disk identity is required.", nameof(request));
            if ((fields & ~AtaSmartAccessAllFields) != 0) throw new ArgumentOutOfRangeException(nameof(request));
        }
        return new(request.Devices.ToFrozenDictionary(StringComparer.Ordinal), request.MaximumDeviceCount);
    }

    internal static AtaSmartAttributeInformationRequest SelectAtaSmartAccessRequest(
        AtaSmartAttributeInformationRequest scope, IReadOnlyList<InformationFieldSelection> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var devices = new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal);
        foreach (var item in selection)
        {
            var flag = AtaSmartAccessFlag(item.Definition);
            if (item.Field.FieldName != item.Definition.Name
                || !scope.Devices.TryGetValue(item.Field.DeviceIdentity, out var configured)
                || (configured & flag) == 0)
                throw new ArgumentException("ATA selection is outside the configured exact disk and field scope.", nameof(selection));
            devices[item.Field.DeviceIdentity] = devices.GetValueOrDefault(item.Field.DeviceIdentity) | flag;
        }
        return new(devices.ToFrozenDictionary(StringComparer.Ordinal), scope.MaximumDeviceCount);
    }

    internal static AtaSmartAttributeInformationRequest ActiveAtaSmartAccessRequest(
        AtaSmartAttributeInformationRequest request)
        => new(request.Devices.Where(static pair => pair.Value != AtaSmartAttributeInformationFields.None)
            .ToFrozenDictionary(StringComparer.Ordinal), request.MaximumDeviceCount);

    internal static DeviceInformationSnapshot AtaSmartAccessCurrent(
        DeviceInformationSnapshot current, AtaSmartAttributeInformationRequest scope)
    {
        var currentObjects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        var devices = new List<DeviceInformationObject>();
        foreach (var (identity, configured) in scope.Devices.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var definitions = AtaSmartAccessDefinitions(configured).ToArray();
            if (definitions.Length == 0) continue;
            currentObjects.TryGetValue(identity, out var existing);
            var fields = definitions.Select(definition =>
            {
                var prior = existing?.Fields.FirstOrDefault(field => field.Definition == definition);
                return prior ?? new DeviceInformationField(definition, null);
            }).ToArray();
            devices.Add(new(identity, "physical_disk", fields));
        }
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeAtaSmartAccess(
        DeviceInformationSnapshot current,
        AtaSmartAttributeInformationRequest selected,
        DeviceInformationSnapshot read,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var expectedDevices = selected.Devices.Where(static pair => pair.Value != AtaSmartAttributeInformationFields.None)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var observed = new Dictionary<string, DeviceInformationObject>(StringComparer.Ordinal);
        foreach (var device in read.Devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device.ObjectType != "physical_disk" || !expectedDevices.ContainsKey(device.DeviceIdentity)
                || !observed.TryAdd(device.DeviceIdentity, device))
                throw new InvalidDataException("ATA source returned an unrequested or duplicate physical disk.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in device.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var flag = AtaSmartAccessFlag(field.Definition);
                if (!names.Add(field.Definition.Name)
                    || (expectedDevices[device.DeviceIdentity] & flag) == 0 && field.Value is not null)
                    throw new InvalidDataException("ATA source returned duplicate or unselected values.");
            }
            foreach (var definition in AtaSmartAccessDefinitions(expectedDevices[device.DeviceIdentity]))
                if (!names.Contains(definition.Name))
                    throw new InvalidDataException("ATA source omitted a selected field from its exact disk result.");
        }
        if (observed.Count != expectedDevices.Count)
            throw new InvalidDataException("ATA source did not return every selected physical-disk identity.");

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var (identity, flags) in expectedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prior = objects.GetValueOrDefault(identity);
            if (prior is not null && prior.ObjectType != "physical_disk")
                throw new InvalidDataException("ATA fields cannot be published onto a different object type.");
            var fields = prior?.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                ?? new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
            var observedFields = observed[identity].Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
            foreach (var definition in AtaSmartAccessDefinitions(flags))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = observedFields.GetValueOrDefault(definition.Name)?.Value;
                fields[definition.Name] = new(definition, value) { ObservedAt = value is null ? null : observedAt };
            }
            objects[identity] = new(identity, "physical_disk", fields.Values.ToArray());
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static DeviceInformationSnapshot AtaSmartAccessUnavailable(
        DeviceInformationSnapshot current,
        AtaSmartAttributeInformationRequest selected,
        CancellationToken cancellationToken)
    {
        var unavailable = new DeviceInformationSnapshot(null,
            selected.Devices.Where(static pair => pair.Value != AtaSmartAttributeInformationFields.None)
                .Select(pair => new DeviceInformationObject(pair.Key, "physical_disk",
                    AtaSmartAccessDefinitions(pair.Value).Select(static definition =>
                        new DeviceInformationField(definition, null)).ToArray())).ToArray());
        return MergeAtaSmartAccess(current, selected, unavailable, DateTimeOffset.UtcNow, cancellationToken);
    }

    internal static DeviceInformationSnapshot PreserveAtaSmartInformationFields(
        DeviceInformationSnapshot projected, DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var ataFields = device.Fields.Where(static field => IsAtaSmartAccessDefinition(field.Definition)).ToArray();
            if (ataFields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var projectedDevice))
            {
                var fields = ataFields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in projectedDevice.Fields) fields[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = projectedDevice with { Fields = fields.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = ataFields });
        }
        return new(projected.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsAtaSmartAccessDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.StorageAllAtaSmartAttributeNormalized)
            || ReferenceEquals(definition, F.StorageAllAtaSmartAttributeRaw)
            || ReferenceEquals(definition, F.StorageAllAtaSmartAttributeThreshold);

    private static IEnumerable<DeviceInformationDefinition> AtaSmartAccessDefinitions(
        AtaSmartAttributeInformationFields fields)
    {
        if ((fields & AtaSmartAttributeInformationFields.Normalized) != 0) yield return F.StorageAllAtaSmartAttributeNormalized;
        if ((fields & AtaSmartAttributeInformationFields.Raw) != 0) yield return F.StorageAllAtaSmartAttributeRaw;
        if ((fields & AtaSmartAttributeInformationFields.Threshold) != 0) yield return F.StorageAllAtaSmartAttributeThreshold;
    }

    private static AtaSmartAttributeInformationFields AtaSmartAccessFlag(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.StorageAllAtaSmartAttributeNormalized) ? AtaSmartAttributeInformationFields.Normalized
            : ReferenceEquals(definition, F.StorageAllAtaSmartAttributeRaw) ? AtaSmartAttributeInformationFields.Raw
            : ReferenceEquals(definition, F.StorageAllAtaSmartAttributeThreshold) ? AtaSmartAttributeInformationFields.Threshold
            : throw new ArgumentException("Definition is not an ATA SMART information field.", nameof(definition));
}
