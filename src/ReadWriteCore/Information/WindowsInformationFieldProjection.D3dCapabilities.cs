using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions D3dJsonOptions = InformationJsonContext.Default.Options;

    internal static string D3dAdapterIdentity(WindowsD3dAdapterIdentity adapter, WindowsD3dApiVersion apiVersion)
        => $"windows-d3d-adapter|{WindowsD3dCapabilityScope.ApiCode(apiVersion)}|luid:{adapter.Luid:X16}"
            + $"|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}";

    internal static string D3dFormatIdentity(WindowsD3dAdapterIdentity adapter,
        WindowsD3dApiVersion apiVersion, WindowsD3dFormat format)
        => $"{D3dAdapterIdentity(adapter, apiVersion)}|dxgi-format:{(uint)format}";

    internal static DeviceInformationSnapshot D3dCurrent(DeviceInformationSnapshot current,
        WindowsD3dCapabilityScope scope)
    {
        var identities = RequestedD3dIdentities(scope);
        var definitions = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => definitions.Contains(field.Definition.Name)
                && (identities.Contains(device.DeviceIdentity)
                    || IsRequestedD3dVideoDecoderIdentity(scope, device.DeviceIdentity, field.Definition)
                    || IsRequestedD3dVideoProcessorIdentity(scope, device.DeviceIdentity, field.Definition))).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeD3d(DeviceInformationSnapshot current,
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selected,
        WindowsD3dCapabilityReadResult read, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(read);
        if ((selected & ~scope.Fields) != 0 || selected == WindowsD3dCapabilityFields.None)
            throw new ArgumentOutOfRangeException(nameof(selected));

        var rows = new Dictionary<WindowsD3dAdapterIdentity, WindowsD3dAdapterCapabilityRead>();
        foreach (var row in read.Adapters)
        {
            if (!rows.TryAdd(row.Adapter, row) || row.ApiVersion != scope.ApiVersion)
                throw new InvalidOperationException("The D3D reader returned a duplicate adapter or a different API version.");
        }
        if (rows.Count != scope.Adapters.Length || scope.Adapters.Any(adapter => !rows.ContainsKey(adapter)))
            throw new InvalidOperationException("The D3D reader did not return exactly the requested adapter identities.");

        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var adapter in scope.Adapters)
        {
            var row = rows[adapter];
            WindowsD3dFeatureLevel? featureLevel = row.FeatureLevel;
            if ((selected & WindowsD3dCapabilityFields.FeatureLevel) != 0)
            {
                if (featureLevel is { } level && !WindowsD3dCapabilityScope.IsFeatureLevelAllowed(scope.ApiVersion, level))
                    featureLevel = null;
                SetField(objects, D3dAdapterIdentity(adapter, scope.ApiVersion), "gpu_adapter",
                    DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel,
                    featureLevel is { } observed
                        ? ((uint)observed).ToString(CultureInfo.InvariantCulture)
                        : null, observedAt);
            }
            else if (featureLevel is not null)
            {
                throw new InvalidOperationException("The D3D reader returned an unrequested feature level.");
            }

            if ((selected & WindowsD3dCapabilityFields.RaytracingTier) != 0)
                SetField(objects, D3dAdapterIdentity(adapter, scope.ApiVersion), "gpu_adapter",
                    DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier,
                    row.RaytracingTier?.ToString(CultureInfo.InvariantCulture), observedAt);
            else if (row.RaytracingTier is not null)
                throw new InvalidOperationException("The D3D reader returned an unrequested raytracing tier.");

            var expectedFormats = (selected & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                ? scope.Formats : ImmutableArray<WindowsD3dFormat>.Empty;
            if ((selected & WindowsD3dCapabilityFields.FormatSupportFlags) == 0 && !row.FormatSupport.IsEmpty)
                throw new InvalidOperationException("The D3D reader returned unrequested format support values.");
            var formats = new Dictionary<WindowsD3dFormat, ulong?>();
            foreach (var format in row.FormatSupport)
            {
                if (!Enum.IsDefined(format.Format) || !formats.TryAdd(format.Format, format.Flags))
                    throw new InvalidOperationException("The D3D reader returned an invalid or duplicate format support value.");
            }
            if (formats.Count != expectedFormats.Length || expectedFormats.Any(format => !formats.ContainsKey(format)))
                throw new InvalidOperationException("The D3D reader did not return exactly the requested DXGI formats.");
            foreach (var format in expectedFormats)
            {
                var flags = formats[format];
                SetField(objects, D3dFormatIdentity(adapter, scope.ApiVersion, format), "gpu_adapter_format",
                    DeviceInformationFieldRegistry.GpuAllD3dFormatSupportFlags,
                    flags?.ToString(CultureInfo.InvariantCulture), observedAt);
            }
        }
        MergeD3dVideoDecoderFields(objects, scope, selected, rows, observedAt);
        MergeD3dVideoDecoderConfigurations(objects, scope, selected, rows, observedAt);
        MergeD3dVideoProcessorCapabilities(objects, scope, selected, rows, observedAt);
        return new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsValidD3dRead(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected, WindowsD3dCapabilityReadResult read, DateTimeOffset observedAt)
    {
        try
        {
            _ = MergeD3d(DeviceInformationSnapshot.Empty, scope, selected, read, observedAt);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    internal static DeviceInformationSnapshot PreserveD3dCapabilityFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsD3dDefinition(field.Definition)).ToArray();
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

    internal static bool IsD3dDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllD3dFormatSupportFlags)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps)
            || ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps);

    private static HashSet<string> RequestedD3dIdentities(WindowsD3dCapabilityScope scope)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adapter in scope.Adapters)
        {
            if ((scope.Fields & (WindowsD3dCapabilityFields.FeatureLevel | WindowsD3dCapabilityFields.RaytracingTier)) != 0)
                identities.Add(D3dAdapterIdentity(adapter, scope.ApiVersion));
            if ((scope.Fields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0)
                foreach (var format in scope.Formats)
                    identities.Add(D3dFormatIdentity(adapter, scope.ApiVersion, format));
            if ((scope.Fields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0)
                foreach (var descriptor in scope.DecoderConfigurationDescriptors)
                    identities.Add(D3dVideoDecoderConfigurationIdentity(adapter, descriptor));
            if ((scope.Fields & (WindowsD3dCapabilityFields.VideoProcessorCaps
                | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps)) != 0)
                foreach (var descriptor in scope.VideoProcessorDescriptors)
                    identities.Add(D3dVideoProcessorIdentity(adapter, descriptor));
        }
        return identities;
    }

    private static void SetField(Dictionary<string, DeviceInformationObject> objects, string identity,
        string objectType, DeviceInformationDefinition definition, string? value, DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The D3D identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        var field = new DeviceInformationField(definition,
            value is null ? null : JsonSerializer.SerializeToElement(value, D3dJsonOptions))
        { ObservedAt = value is null ? null : observedAt };
        fields[definition.Name] = field;
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }

    private static void SetStructuredField(Dictionary<string, DeviceInformationObject> objects, string identity,
        string objectType, DeviceInformationDefinition definition, System.Text.Json.JsonElement? value,
        DateTimeOffset observedAt)
    {
        var fields = objects.TryGetValue(identity, out var current)
            ? current.ObjectType == objectType
                ? current.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                : throw new InvalidOperationException("The D3D identity already exists with a different object type.")
            : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
        fields[definition.Name] = new DeviceInformationField(definition, value)
        { ObservedAt = value is null ? null : observedAt };
        objects[identity] = new(identity, objectType, fields.Values.ToArray());
    }
}
