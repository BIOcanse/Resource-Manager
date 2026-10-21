using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal static string VulkanAdapterIdentity(WindowsD3dAdapterIdentity adapter)
        => $"windows-vulkan-adapter|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}";

    internal static string VulkanExtensionIdentity(WindowsD3dAdapterIdentity adapter, string extension)
        => $"{VulkanAdapterIdentity(adapter)}|extension:{extension}";

    internal static string VulkanFeatureIdentity(WindowsD3dAdapterIdentity adapter, string feature)
        => $"{VulkanAdapterIdentity(adapter)}|feature:core-1.0:{feature}";

    internal static DeviceInformationSnapshot VulkanCurrent(DeviceInformationSnapshot current,
        WindowsVulkanCapabilityScope scope)
    {
        var prefixes = scope.Adapters.Select(VulkanAdapterIdentity).ToArray();
        var definitions = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => IsInScope(device.DeviceIdentity, prefixes)
                && definitions.Contains(field.Definition.Name)).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeVulkan(DeviceInformationSnapshot current,
        WindowsVulkanCapabilityScope scope, WindowsVulkanCapabilitySelection selection,
        WindowsVulkanCapabilityReadResult read, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (selection.QueryFields == WindowsVulkanCapabilityFields.None
            || (selection.QueryFields & ~scope.Fields) != 0 || selection.Definitions.IsDefault
            || selection.ExactFields.IsDefault)
            throw new ArgumentOutOfRangeException(nameof(selection));
        if (read.Adapters.IsDefault) throw new InvalidOperationException("The Vulkan reader returned a default adapter list.");

        var rows = new Dictionary<WindowsD3dAdapterIdentity, WindowsVulkanAdapterCapabilityRead>();
        if (!read.DirectorySucceeded)
        {
            if (!read.Adapters.IsEmpty)
                throw new InvalidOperationException("An unavailable Vulkan directory cannot include partial adapter rows.");
        }
        else
        {
            if (read.Adapters.Length != scope.Adapters.Length)
                throw new InvalidOperationException("The Vulkan reader must return one row per selected adapter.");
            foreach (var row in read.Adapters)
            {
                if (row is null || !scope.Adapters.Contains(row.Adapter)
                    || !IsValidVulkanRow(row, selection, scope.MaximumExtensionCount))
                    throw new InvalidOperationException("The Vulkan reader returned an invalid or foreign adapter row.");
                if (!rows.TryAdd(row.Adapter, row))
                    throw new InvalidOperationException("The Vulkan reader returned a duplicate adapter identity.");
            }
            if (scope.Adapters.Any(adapter => !rows.ContainsKey(adapter)))
                throw new InvalidOperationException("The Vulkan reader omitted a selected adapter row.");
        }

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in objects.Values.ToArray())
        {
            if (!IsInScope(device.DeviceIdentity, scope.Adapters.Select(VulkanAdapterIdentity))) continue;
            var remaining = device.Fields.Where(field => !selection.Includes(
                new FieldReference(device.DeviceIdentity, field.Definition.Name), field.Definition)).ToArray();
            if (remaining.Length == device.Fields.Count) continue;
            if (remaining.Length == 0) objects.Remove(device.DeviceIdentity);
            else objects[device.DeviceIdentity] = device with { Fields = remaining };
        }

        foreach (var adapter in scope.Adapters)
        {
            var row = read.DirectorySucceeded ? rows[adapter] : null;
            var adapterIdentity = VulkanAdapterIdentity(adapter);
            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanApiVersion.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanApiVersion))
                SetVulkanField(objects, VulkanAdapterIdentity(adapter), "gpu_adapter",
                    DeviceInformationFieldRegistry.GpuAllVulkanApiVersion, row?.ApiVersion, observedAt);

            if (row?.Extensions is { } extensions)
                foreach (var extension in extensions)
                {
                    var extensionIdentity = VulkanExtensionIdentity(adapter, extension);
                    if (!selection.Includes(new(extensionIdentity,
                            DeviceInformationFieldRegistry.GpuAllVulkanExtension.Name),
                            DeviceInformationFieldRegistry.GpuAllVulkanExtension)) continue;
                    SetVulkanField(objects, extensionIdentity, "gpu_adapter_extension",
                        DeviceInformationFieldRegistry.GpuAllVulkanExtension, extension, observedAt);
                }

            if (row?.CoreFeatures is { } features)
            {
                foreach (var feature in features)
                {
                    var featureIdentity = VulkanFeatureIdentity(adapter, feature.Name);
                    if (!selection.Includes(new(featureIdentity,
                            DeviceInformationFieldRegistry.GpuAllVulkanFeature.Name),
                            DeviceInformationFieldRegistry.GpuAllVulkanFeature)) continue;
                    SetVulkanFeature(objects, featureIdentity, feature.Supported, observedAt);
                }
            }

            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D))
                SetVulkanLimit(objects, VulkanAdapterIdentity(adapter),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D,
                    row?.CoreLimits?.MaxImageDimension1D, observedAt);
            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D))
                SetVulkanLimit(objects, VulkanAdapterIdentity(adapter),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D,
                    row?.CoreLimits?.MaxImageDimension2D, observedAt);
            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D))
                SetVulkanLimit(objects, VulkanAdapterIdentity(adapter),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D,
                    row?.CoreLimits?.MaxImageDimension3D, observedAt);
            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube))
                SetVulkanLimit(objects, VulkanAdapterIdentity(adapter),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube,
                    row?.CoreLimits?.MaxImageDimensionCube, observedAt);
            if (selection.Includes(new(adapterIdentity, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers.Name),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers))
                SetVulkanLimit(objects, VulkanAdapterIdentity(adapter),
                    DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers,
                    row?.CoreLimits?.MaxImageArrayLayers, observedAt);

            if ((selection.QueryFields & WindowsVulkanCapabilityFields.CoreLimits) != 0)
                SetVulkanCoreLimits(objects, adapterIdentity, selection, row?.CoreLimits, observedAt);
        }

        return new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsValidVulkanRead(WindowsVulkanCapabilityScope scope,
        WindowsVulkanCapabilitySelection selection, WindowsVulkanCapabilityReadResult read,
        DateTimeOffset observedAt)
    {
        try
        {
            _ = MergeVulkan(DeviceInformationSnapshot.Empty, scope, selection, read, observedAt);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    internal static DeviceInformationSnapshot PreserveVulkanCapabilityFields(
        DeviceInformationSnapshot projected, DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsVulkanDefinition(field.Definition)).ToArray();
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

    internal static bool IsVulkanDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanApiVersion)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanExtension)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanFeature)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers)
            || WindowsVulkanCoreLimitsCatalog.TryGetBinding(definition, out _);

    private static bool IsValidVulkanRow(WindowsVulkanAdapterCapabilityRead row,
        WindowsVulkanCapabilitySelection selection, int maximumExtensionCount)
    {
        var selected = selection.QueryFields;
        if ((selected & WindowsVulkanCapabilityFields.ApiVersion) == 0)
        {
            if (row.ApiVersion is not null) return false;
        }
        else if (row.ApiVersion is { } version && !IsVulkanVersion(version)) return false;

        if ((selected & WindowsVulkanCapabilityFields.Extensions) == 0)
        {
            if (row.Extensions is not null) return false;
        }
        else if (row.Extensions is { } extensions)
        {
            if (extensions.IsDefault || extensions.Length > maximumExtensionCount) return false;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var extension in extensions)
                if (!IsVulkanExtensionName(extension) || !unique.Add(extension)) return false;
        }

        if ((selected & WindowsVulkanCapabilityFields.CoreFeatures) == 0)
        {
            if (row.CoreFeatures is not null) return false;
        }
        else if (row.CoreFeatures is { } features)
        {
            if (features.IsDefault || features.Length != WindowsVulkanCore10FeatureCatalog.Names.Length) return false;
            for (var index = 0; index < features.Length; index++)
                if (features[index] is null || !string.Equals(features[index].Name,
                        WindowsVulkanCore10FeatureCatalog.Names[index], StringComparison.Ordinal)) return false;
        }

        var selectedLimits = WindowsVulkanCapabilityFields.ImageDimensionLimits
            | WindowsVulkanCapabilityFields.CubeArrayLimits
            | WindowsVulkanCapabilityFields.CoreLimits;
        if ((selected & selectedLimits) == 0)
            return row.CoreLimits is null;
        if (row.CoreLimits is not { } limits) return true;
        if ((selected & WindowsVulkanCapabilityFields.CoreLimits) != 0)
        {
            foreach (var definition in selection.Definitions)
            {
                if (!WindowsVulkanCoreLimitsCatalog.TryGetBinding(definition, out var binding)) continue;
                if (!WindowsVulkanCoreLimitsCatalog.IsValidValue(binding, limits)) return false;
            }
            return ((selected & WindowsVulkanCapabilityFields.MaxImageDimension1D) == 0
                    || limits.MaxImageDimension1D.HasValue)
                && ((selected & WindowsVulkanCapabilityFields.MaxImageDimension2D) == 0
                    || limits.MaxImageDimension2D.HasValue)
                && ((selected & WindowsVulkanCapabilityFields.MaxImageDimension3D) == 0
                    || limits.MaxImageDimension3D.HasValue)
                && ((selected & WindowsVulkanCapabilityFields.MaxImageDimensionCube) == 0
                    || limits.MaxImageDimensionCube.HasValue)
                && ((selected & WindowsVulkanCapabilityFields.MaxImageArrayLayers) == 0
                    || limits.MaxImageArrayLayers.HasValue);
        }
        return MatchesSelectedLimit(selected, WindowsVulkanCapabilityFields.MaxImageDimension1D, limits.MaxImageDimension1D)
            && MatchesSelectedLimit(selected, WindowsVulkanCapabilityFields.MaxImageDimension2D, limits.MaxImageDimension2D)
            && MatchesSelectedLimit(selected, WindowsVulkanCapabilityFields.MaxImageDimension3D, limits.MaxImageDimension3D)
            && MatchesSelectedLimit(selected, WindowsVulkanCapabilityFields.MaxImageDimensionCube, limits.MaxImageDimensionCube)
            && MatchesSelectedLimit(selected, WindowsVulkanCapabilityFields.MaxImageArrayLayers, limits.MaxImageArrayLayers)
            && WindowsVulkanCoreLimitsCatalog.Bindings.All(binding => binding.ReadValue(limits) is null);
    }

    private static bool MatchesSelectedLimit(WindowsVulkanCapabilityFields selected,
        WindowsVulkanCapabilityFields field, uint? value)
        => (selected & field) != 0 ? value.HasValue : !value.HasValue;

    private static bool IsVulkanVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3
            && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major is > 0 and <= 127
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor <= 1023
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) && patch <= 4095;
    }

    private static bool IsVulkanExtensionName(string? name)
        => name is { Length: > 0 } && name.All(static character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsInScope(string identity, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => identity.Equals(prefix, StringComparison.Ordinal)
            || identity.StartsWith(prefix + "|", StringComparison.Ordinal));

    private static void SetVulkanField(Dictionary<string, DeviceInformationObject> objects,
        string identity, string objectType, DeviceInformationDefinition definition,
        string? value, DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The Vulkan identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new(definition,
            value is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(value, JsonOptions))
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

    private static void SetVulkanFeature(Dictionary<string, DeviceInformationObject> objects,
        string identity, bool? supported, DateTimeOffset observedAt)
    {
        const string objectType = "gpu_adapter_feature";
        var definition = DeviceInformationFieldRegistry.GpuAllVulkanFeature;
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The Vulkan feature identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new(definition,
            supported is { } value ? System.Text.Json.JsonSerializer.SerializeToElement(value, JsonOptions) : null)
        { ObservedAt = supported is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

    private static void SetVulkanLimit(Dictionary<string, DeviceInformationObject> objects,
        string identity, DeviceInformationDefinition definition, uint? value, DateTimeOffset observedAt)
    {
        const string objectType = "gpu_adapter";
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The Vulkan limit identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new(definition,
            value is { } actual ? System.Text.Json.JsonSerializer.SerializeToElement(actual, JsonOptions) : null)
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

    private static void SetVulkanCoreLimits(Dictionary<string, DeviceInformationObject> objects,
        string identity, WindowsVulkanCapabilitySelection selection, WindowsVulkanCoreLimitsRead? limits,
        DateTimeOffset observedAt)
    {
        const string objectType = "gpu_adapter";
        Dictionary<string, DeviceInformationField>? fields = null;
        foreach (var binding in WindowsVulkanCoreLimitsCatalog.Bindings)
        {
            var definition = binding.Definition;
            if (!selection.Includes(new(identity, definition.Name), definition)) continue;

            fields ??= objects.TryGetValue(identity, out var current)
                ? current.ObjectType == objectType
                    ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                    : throw new InvalidOperationException("The Vulkan identity already exists with a different object type.")
                : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);

            var value = limits is null ? null : binding.ReadValue(limits);
            object? serializedValue = binding.Shape switch
            {
                WindowsVulkanCoreLimitValueShape.UInt64 when value is ulong unsigned64
                    => unsigned64.ToString(CultureInfo.InvariantCulture),
                WindowsVulkanCoreLimitValueShape.NativeSize when value is nuint nativeSize
                    => nativeSize.ToString(CultureInfo.InvariantCulture),
                _ => value
            };
            var json = serializedValue is null ? (System.Text.Json.JsonElement?)null
                : System.Text.Json.JsonSerializer.SerializeToElement(serializedValue, serializedValue.GetType(), JsonOptions);
            fields[definition.Name] = new(definition, json)
            { ObservedAt = value is null ? null : observedAt };
        }

        if (fields is not null)
            objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }
}
