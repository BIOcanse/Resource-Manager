using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions OpenClJsonOptions = InformationJsonContext.Default.Options;
    private static readonly UTF8Encoding OpenClStrictUtf8 = new(false, true);

    internal static string OpenClDeviceIdentity(WindowsOpenClDeviceIdentity identity)
        => $"opencl-device|luid:{identity.Adapter.DxgiLuid:X16}|node:{identity.Adapter.NodeMask:X8}"
            + $"|device:{identity.DeviceUuidHex}|driver:{identity.DriverUuidHex}";

    internal static string OpenClExtensionIdentity(WindowsOpenClDeviceIdentity identity, string extension)
        => $"opencl-extension|luid:{identity.Adapter.DxgiLuid:X16}|node:{identity.Adapter.NodeMask:X8}"
            + $"|device:{identity.DeviceUuidHex}|driver:{identity.DriverUuidHex}"
            + $"|token-utf8-hex:{Convert.ToHexString(OpenClStrictUtf8.GetBytes(extension))}";

    internal static DeviceInformationSnapshot OpenClCurrent(DeviceInformationSnapshot current,
        WindowsOpenClCapabilityScope scope)
    {
        var definitions = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => TryParseOpenClIdentity(device.DeviceIdentity, out var identity)
                && scope.Targets.Contains(identity.Adapter) && definitions.Contains(field.Definition.Name)).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeOpenCl(DeviceInformationSnapshot current,
        WindowsOpenClCapabilityScope scope, WindowsOpenClCapabilityFields selected,
        WindowsOpenClCapabilityReadResult read, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (selected == WindowsOpenClCapabilityFields.None || (selected & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selected));
        if (read.Devices.IsDefault) throw new InvalidOperationException("The OpenCL reader returned a default device list.");

        var definitions = DefinitionsFor(selected);
        var rowsByIdentity = new Dictionary<string, WindowsOpenClDeviceCapabilityRead>(StringComparer.Ordinal);
        if (!read.DirectorySucceeded)
        {
            if (!read.Devices.IsEmpty)
                throw new InvalidOperationException("An unavailable OpenCL directory cannot include partial device rows.");
        }
        else
        {
            if (read.Devices.Length > scope.MaximumDeviceCount)
                throw new InvalidOperationException("The OpenCL reader exceeded its configured device bound.");
            foreach (var row in read.Devices)
            {
                if (row?.Identity is null || !scope.Targets.Contains(row.Identity.Adapter)
                    || !IsValidOpenClRow(row, selected, scope.MaximumExtensionCount))
                    throw new InvalidOperationException("The OpenCL reader returned an invalid or foreign device row.");
                var identity = OpenClDeviceIdentity(row.Identity);
                if (!rowsByIdentity.TryAdd(identity, row))
                    throw new InvalidOperationException("The OpenCL reader returned a duplicate device identity.");
            }
        }

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        if (!read.DirectorySucceeded)
        {
            foreach (var device in objects.Values.ToArray())
            {
                if (!TryParseOpenClIdentity(device.DeviceIdentity, out var identity)
                    || !scope.Targets.Contains(identity.Adapter)) continue;
                foreach (var definition in definitions)
                    if (device.Fields.Any(field => ReferenceEquals(field.Definition, definition)))
                        SetOpenClField(objects, device.DeviceIdentity, device.ObjectType, definition, null, observedAt);
            }
            return OrderedSnapshot(current, objects);
        }

        foreach (var device in objects.Values.ToArray())
        {
            if (!TryParseOpenClIdentity(device.DeviceIdentity, out var parsed)
                || !scope.Targets.Contains(parsed.Adapter)) continue;
            var deviceKey = DeviceKey(parsed);
            rowsByIdentity.TryGetValue(deviceKey, out var row);

            if ((selected & WindowsOpenClCapabilityFields.DeviceVersion) != 0 && !parsed.IsExtension)
            {
                if (row is null) RemoveField(objects, device.DeviceIdentity, DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion);
            }

            if ((selected & WindowsOpenClCapabilityFields.Extensions) != 0 && parsed.IsExtension)
            {
                if (row is null) RemoveField(objects, device.DeviceIdentity, DeviceInformationFieldRegistry.GpuAllOpenClExtension);
                else if (row.Extensions is null)
                    SetOpenClField(objects, device.DeviceIdentity, "gpu_adapter_extension",
                        DeviceInformationFieldRegistry.GpuAllOpenClExtension, null, observedAt);
                else RemoveField(objects, device.DeviceIdentity, DeviceInformationFieldRegistry.GpuAllOpenClExtension);
            }
        }

        foreach (var row in rowsByIdentity.Values)
        {
            var deviceIdentity = OpenClDeviceIdentity(row.Identity);
            if ((selected & WindowsOpenClCapabilityFields.DeviceVersion) != 0)
                SetOpenClField(objects, deviceIdentity, "gpu_adapter",
                    DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion, row.DeviceVersion, observedAt);

            if ((selected & WindowsOpenClCapabilityFields.Extensions) != 0 && row.Extensions is { } extensions)
                foreach (var extension in extensions)
                    SetOpenClField(objects, OpenClExtensionIdentity(row.Identity, extension), "gpu_adapter_extension",
                        DeviceInformationFieldRegistry.GpuAllOpenClExtension, extension, observedAt);
        }

        return OrderedSnapshot(current, objects);
    }

    internal static bool IsValidOpenClRead(DeviceInformationSnapshot current,
        WindowsOpenClCapabilityScope scope, WindowsOpenClCapabilityFields selected,
        WindowsOpenClCapabilityReadResult read, DateTimeOffset observedAt)
    {
        try
        {
            _ = MergeOpenCl(current, scope, selected, read, observedAt);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
            or NullReferenceException or OverflowException)
        {
            return false;
        }
    }

    internal static DeviceInformationSnapshot PreserveOpenClCapabilityFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsOpenClDefinition(field.Definition)).ToArray();
            if (fields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var projectedDevice))
            {
                var byName = fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in projectedDevice.Fields) byName[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = projectedDevice with { Fields = byName.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = fields });
        }
        return new(projected.ObservedAt,
            objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsOpenClDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllOpenClExtension);

    private static ImmutableArray<DeviceInformationDefinition> DefinitionsFor(WindowsOpenClCapabilityFields selected)
    {
        var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(2);
        if ((selected & WindowsOpenClCapabilityFields.DeviceVersion) != 0)
            definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion);
        if ((selected & WindowsOpenClCapabilityFields.Extensions) != 0)
            definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenClExtension);
        return definitions.ToImmutable();
    }

    private static bool IsValidOpenClRow(WindowsOpenClDeviceCapabilityRead row,
        WindowsOpenClCapabilityFields selected, int maximumExtensionCount)
    {
        if ((selected & WindowsOpenClCapabilityFields.DeviceVersion) == 0)
        {
            if (row.DeviceVersion is not null) return false;
        }
        else if (row.DeviceVersion is { } version && (version.Length == 0 || version.Contains('\0')))
        {
            return false;
        }

        if ((selected & WindowsOpenClCapabilityFields.Extensions) == 0)
            return row.Extensions is null;
        if (row.Extensions is not { } extensions) return true;
        if (extensions.IsDefault || extensions.Length > maximumExtensionCount) return false;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
            if (!IsExtensionToken(extension) || !unique.Add(extension)) return false;
        return true;
    }

    private static bool IsExtensionToken(string? extension)
    {
        if (string.IsNullOrEmpty(extension) || extension.Contains(' ') || extension.Contains('\0')) return false;
        try { _ = OpenClStrictUtf8.GetByteCount(extension); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool IsUpperHex(string value)
        => value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool TryParseOpenClIdentity(string value, out ParsedOpenClIdentity parsed)
    {
        parsed = default;
        var parts = value.Split('|');
        var isExtension = parts.Length == 6 && parts[0] == "opencl-extension";
        if ((!isExtension && (parts.Length != 5 || parts[0] != "opencl-device"))
            || !parts[1].StartsWith("luid:", StringComparison.Ordinal)
            || !parts[2].StartsWith("node:", StringComparison.Ordinal)
            || !parts[3].StartsWith("device:", StringComparison.Ordinal)
            || !parts[4].StartsWith("driver:", StringComparison.Ordinal)
            || !ulong.TryParse(parts[1].AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var luid)
            || !uint.TryParse(parts[2].AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var node)
            || node == 0 || (node & (node - 1)) != 0
            || !WindowsOpenClDeviceIdentity.IsUuidHex(parts[3][7..])
            || !WindowsOpenClDeviceIdentity.IsUuidHex(parts[4][7..])) return false;

        const int tokenIdentityPrefixLength = 15;
        if (isExtension && (!parts[5].StartsWith("token-utf8-hex:", StringComparison.Ordinal)
            || parts[5].Length <= tokenIdentityPrefixLength
            || (parts[5].Length - tokenIdentityPrefixLength) % 2 != 0
            || !IsUpperHex(parts[5][tokenIdentityPrefixLength..]))) return false;
        parsed = new(isExtension, new WindowsOpenClAdapterTarget(luid, node), parts[3][7..], parts[4][7..]);
        return true;
    }

    private static string DeviceKey(ParsedOpenClIdentity identity)
        => $"opencl-device|luid:{identity.Adapter.DxgiLuid:X16}|node:{identity.Adapter.NodeMask:X8}"
            + $"|device:{identity.DeviceUuidHex}|driver:{identity.DriverUuidHex}";

    private static void SetOpenClField(Dictionary<string, DeviceInformationObject> objects, string identity,
        string objectType, DeviceInformationDefinition definition, string? value, DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The OpenCL identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        var jsonValue = value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(value, OpenClJsonOptions);
        fields[definition.Name] = new(definition, jsonValue)
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

    private static void RemoveField(Dictionary<string, DeviceInformationObject> objects, string identity,
        DeviceInformationDefinition definition)
    {
        if (!objects.TryGetValue(identity, out var current)) return;
        var remaining = current.Fields.Where(field => !ReferenceEquals(field.Definition, definition)).ToArray();
        if (remaining.Length == 0) objects.Remove(identity);
        else objects[identity] = current with { Fields = remaining };
    }

    private static DeviceInformationSnapshot OrderedSnapshot(DeviceInformationSnapshot current,
        Dictionary<string, DeviceInformationObject> objects)
        => new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());

    private readonly record struct ParsedOpenClIdentity(bool IsExtension, WindowsOpenClAdapterTarget Adapter,
        string DeviceUuidHex, string DriverUuidHex);
}
