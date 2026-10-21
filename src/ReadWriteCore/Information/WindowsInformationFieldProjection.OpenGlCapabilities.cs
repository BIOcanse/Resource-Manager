using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal static string OpenGlAdapterIdentity(WindowsOpenGlAdapterTarget target)
        => $"windows-opengl-context|luid:{target.Adapter.Luid:X16}|pci:{target.Adapter.PciVendorId:X4}:{target.Adapter.PciDeviceId:X4}|node-mask:{target.NodeMask:X8}";

    internal static string OpenGlExtensionIdentity(WindowsOpenGlAdapterTarget target, string extension)
        => $"{OpenGlAdapterIdentity(target)}|extension:{extension}";

    internal static DeviceInformationSnapshot OpenGlCurrent(DeviceInformationSnapshot current,
        WindowsOpenGlCapabilityScope scope)
    {
        var prefixes = scope.Targets.Select(OpenGlAdapterIdentity).ToArray();
        var definitions = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => OpenGlIsInScope(device.DeviceIdentity, prefixes)
                && definitions.Contains(field.Definition.Name)).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeOpenGl(DeviceInformationSnapshot current,
        WindowsOpenGlCapabilityScope scope, WindowsOpenGlCapabilityFields selected,
        WindowsOpenGlCapabilityReadResult read, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (selected == WindowsOpenGlCapabilityFields.None || (selected & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selected));
        if (read.Targets.IsDefault) throw new InvalidOperationException("The OpenGL reader returned a default target list.");

        var rows = new Dictionary<WindowsOpenGlAdapterTarget, WindowsOpenGlAdapterCapabilityRead>();
        if (!read.DirectorySucceeded)
        {
            if (!read.Targets.IsEmpty)
                throw new InvalidOperationException("An unavailable OpenGL directory cannot include partial target rows.");
        }
        else
        {
            if (read.Targets.Length != scope.Targets.Length)
                throw new InvalidOperationException("The OpenGL reader must return one row per selected context target.");
            foreach (var row in read.Targets)
            {
                if (row is null || !scope.Targets.Contains(row.Target)
                    || !IsValidOpenGlRow(row, selected, scope.MaximumExtensionCount,
                        scope.MaximumTextBytes, scope.MaximumAggregateTextBytes))
                    throw new InvalidOperationException("The OpenGL reader returned an invalid or foreign context row.");
                if (!rows.TryAdd(row.Target, row))
                    throw new InvalidOperationException("The OpenGL reader returned a duplicate context target.");
            }
            if (scope.Targets.Any(target => !rows.ContainsKey(target)))
                throw new InvalidOperationException("The OpenGL reader omitted a selected context target.");
        }

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        var definitions = DefinitionsFor(selected);
        foreach (var device in objects.Values.ToArray())
        {
            if (!OpenGlIsInScope(device.DeviceIdentity, scope.Targets.Select(OpenGlAdapterIdentity))) continue;
            var remaining = device.Fields.Where(field => !definitions.Contains(field.Definition)).ToArray();
            if (remaining.Length == device.Fields.Count) continue;
            if (remaining.Length == 0) objects.Remove(device.DeviceIdentity);
            else objects[device.DeviceIdentity] = device with { Fields = remaining };
        }

        foreach (var target in scope.Targets)
        {
            var row = read.DirectorySucceeded ? rows[target] : null;
            var identityMatches = row is not null && row.ObservedLuid == target.Adapter.Luid
                && row.ObservedNodeMask == target.NodeMask;
            if ((selected & WindowsOpenGlCapabilityFields.Version) != 0)
                SetOpenGlField(objects, OpenGlAdapterIdentity(target), "gpu_adapter",
                    DeviceInformationFieldRegistry.GpuAllOpenGlVersion,
                    identityMatches ? row?.Version : null, observedAt);

            if ((selected & WindowsOpenGlCapabilityFields.Extensions) != 0 && identityMatches
                && row?.Extensions is { } extensions)
                foreach (var extension in extensions)
                    SetOpenGlField(objects, OpenGlExtensionIdentity(target, extension), "gpu_adapter_extension",
                        DeviceInformationFieldRegistry.GpuAllOpenGlExtension, extension, observedAt);

            foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
            {
                if ((selected & spec.Field) == 0) continue;
                var value = identityMatches && row?.Limits is { } limits
                    ? limits[spec.Limit]
                    : new WindowsOpenGlLimitRead(WindowsOpenGlLimitReadStatus.Unavailable);
                SetOpenGlLimitField(objects, OpenGlAdapterIdentity(target), spec.Definition,
                    LimitValueElement(spec, value), observedAt);
            }
        }

        return new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsValidOpenGlRead(WindowsOpenGlCapabilityScope scope,
        WindowsOpenGlCapabilityFields selected, WindowsOpenGlCapabilityReadResult read, DateTimeOffset observedAt)
    {
        try
        {
            _ = MergeOpenGl(DeviceInformationSnapshot.Empty, scope, selected, read, observedAt);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    internal static DeviceInformationSnapshot PreserveOpenGlCapabilityFields(
        DeviceInformationSnapshot projected, DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsOpenGlDefinition(field.Definition)).ToArray();
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

    internal static bool IsOpenGlDefinition(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllOpenGlVersion)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllOpenGlExtension)) return true;
        foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
            if (ReferenceEquals(spec.Definition, definition)) return true;
        return false;
    }

    private static ImmutableArray<DeviceInformationDefinition> DefinitionsFor(WindowsOpenGlCapabilityFields selected)
    {
        var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(15);
        if ((selected & WindowsOpenGlCapabilityFields.Version) != 0)
            definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenGlVersion);
        if ((selected & WindowsOpenGlCapabilityFields.Extensions) != 0)
            definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenGlExtension);
        foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
            if ((selected & spec.Field) != 0) definitions.Add(spec.Definition);
        return definitions.ToImmutable();
    }

    private static bool IsValidOpenGlRow(WindowsOpenGlAdapterCapabilityRead row,
        WindowsOpenGlCapabilityFields selected, int maximumExtensionCount, int maximumTextBytes,
        int maximumAggregateTextBytes)
    {
        if ((row.ObservedLuid is null) != (row.ObservedNodeMask is null)) return false;
        var identityMatches = row.ObservedLuid == row.Target.Adapter.Luid
            && row.ObservedNodeMask == row.Target.NodeMask;
        if (!identityMatches && (row.Version is not null || row.Extensions is not null)) return false;

        if ((selected & WindowsOpenGlCapabilityFields.Version) == 0)
        {
            if (row.Version is not null) return false;
        }
        else if (row.Version is { } version && !IsOpenGlVersion(version, maximumTextBytes)) return false;

        if ((selected & WindowsOpenGlCapabilityFields.Extensions) == 0)
        {
            if (row.Extensions is not null) return false;
        }
        else if (row.Extensions is { } extensions)
        {
            if (extensions.IsDefault || extensions.Length > maximumExtensionCount) return false;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var aggregateBytes = 0;
            foreach (var extension in extensions)
            {
                if (!IsOpenGlExtensionName(extension) || extension.Length > maximumTextBytes
                    || !unique.Add(extension)
                    || extension.Length + 1 > maximumAggregateTextBytes - aggregateBytes) return false;
                aggregateBytes += extension.Length + 1;
            }
        }

        var selectedLimits = WindowsOpenGlCoreLimitsCatalog.Specs
            .Where(spec => (selected & spec.Field) != 0).ToArray();
        if (selectedLimits.Length == 0)
        {
            if (row.Limits is not null) return false;
        }
        else
        {
            var limitValues = row.Limits;
            if (limitValues is null || limitValues.Count != selectedLimits.Length) return false;
            foreach (var spec in selectedLimits)
                if (!limitValues.TryGetValue(spec.Limit, out var value)
                    || !WindowsOpenGlCoreLimitsCatalog.IsValidRead(spec.Limit, value)
                    || !identityMatches && value.Status != WindowsOpenGlLimitReadStatus.Unavailable) return false;
            if (limitValues.Keys.Any(limit => selectedLimits.All(spec => spec.Limit != limit))) return false;
        }
        return true;
    }

    private static JsonElement? LimitValueElement(WindowsOpenGlCoreLimitSpec spec, WindowsOpenGlLimitRead value)
    {
        if (value.Status != WindowsOpenGlLimitReadStatus.Queried) return null;
        return spec.ValueKind switch
        {
            WindowsOpenGlLimitValueKind.Unsigned32 => System.Text.Json.JsonSerializer.SerializeToElement(
                value.Unsigned32Value!.Value, JsonOptions),
            WindowsOpenGlLimitValueKind.Unsigned64 => System.Text.Json.JsonSerializer.SerializeToElement(
                value.Unsigned64Value!.Value.ToString(CultureInfo.InvariantCulture), JsonOptions),
            WindowsOpenGlLimitValueKind.Number => System.Text.Json.JsonSerializer.SerializeToElement(
                value.NumberValue!.Value, JsonOptions),
            _ => throw new InvalidOperationException("Unknown OpenGL limit value type.")
        };
    }

    private static void SetOpenGlLimitField(Dictionary<string, DeviceInformationObject> objects,
        string identity, DeviceInformationDefinition definition, JsonElement? value, DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == "gpu_adapter"
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The OpenGL identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new(definition, value)
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, "gpu_adapter", fields.Values.ToArray());
    }

    private static bool IsOpenGlVersion(string version, int maximumTextBytes)
    {
        if (version.Length == 0 || version.Length > maximumTextBytes
            || version.StartsWith("OpenGL ES", StringComparison.Ordinal)) return false;
        var index = 0;
        while (index < version.Length && char.IsAsciiDigit(version[index])) index++;
        if (index == 0 || index >= version.Length || version[index++] != '.') return false;
        var minorStart = index;
        while (index < version.Length && char.IsAsciiDigit(version[index])) index++;
        if (index == minorStart) return false;
        var minorEnd = index;
        if (index < version.Length && version[index] == '.')
        {
            var releaseStart = ++index;
            while (index < version.Length && char.IsAsciiDigit(version[index])) index++;
            if (index == releaseStart) return false;
        }
        return uint.TryParse(version.AsSpan(0, version.IndexOf('.')), NumberStyles.None,
                   CultureInfo.InvariantCulture, out var major) && major > 0
            && uint.TryParse(version.AsSpan(minorStart, minorEnd - minorStart), NumberStyles.None,
                   CultureInfo.InvariantCulture, out _)
            && (index == version.Length || char.IsWhiteSpace(version[index]));
    }

    private static bool IsOpenGlExtensionName(string? name)
        => name is { Length: > 0 } && name.All(static character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool OpenGlIsInScope(string identity, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => identity.Equals(prefix, StringComparison.Ordinal)
            || identity.StartsWith(prefix + "|", StringComparison.Ordinal));

    private static void SetOpenGlField(Dictionary<string, DeviceInformationObject> objects,
        string identity, string objectType, DeviceInformationDefinition definition,
        string? value, DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The OpenGL identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new(definition,
            value is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(value, JsonOptions))
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

}
