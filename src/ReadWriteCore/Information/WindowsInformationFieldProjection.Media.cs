using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Media;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions MediaJsonOptions = new(JsonSerializerDefaults.Web);

    internal static string AudioIdentityPrefix(WindowsAudioDataFlow direction)
        => $"windows-audio-endpoint|{WindowsMediaInformationScope.DirectionCode(direction)}|";

    internal static string AudioIdentity(WindowsAudioDataFlow direction, string endpointId)
        => AudioIdentityPrefix(direction) + endpointId;

    internal const string AudioDeviceIdentityPrefix = "windows-audio-device|";

    internal static string AudioDeviceIdentity(string deviceInstanceId)
        => AudioDeviceIdentityPrefix + deviceInstanceId;

    internal static string MftIdentityPrefix(WindowsMftCategory category)
        => $"windows-mft|{WindowsMediaInformationScope.CategoryCode(category)}|";

    internal static string MftIdentity(WindowsMftCategory category, Guid classId)
        => MftIdentityPrefix(category) + classId.ToString("B").ToLowerInvariant();

    internal static string DirectShowIdentityPrefix(WindowsDirectShowCategory category)
        => $"windows-directshow|{WindowsMediaInformationScope.DirectShowCategoryCode(category)}|";

    internal static string DirectShowIdentity(DeviceTopologyRegisteredDirectShowInformation filter)
        => DirectShowIdentityPrefix(filter.Category)
            + (filter.ClassId?.ToString("B").ToLowerInvariant() ?? "class-id-unavailable")
            + "|" + Uri.EscapeDataString(filter.MonikerDisplayName);

    internal static DeviceInformationObject ProjectAudio(DeviceTopologyAudioEndpointInformation endpoint,
        WindowsMediaInformationFields selected)
    {
        var fields = new List<DeviceInformationField>(3);
        if ((selected & WindowsMediaInformationFields.AudioEndpointId) != 0)
            fields.Add(Field(F.AudioAllEndpointIdDeclared, endpoint.EndpointId));
        if ((selected & WindowsMediaInformationFields.AudioDefaultRoles) != 0)
            fields.Add(Field(F.AudioAllDefaultRoleActual, endpoint.DefaultRoles is { } roles
                ? roles.Select(WindowsMediaInformationScope.RoleCode).ToArray() : null));
        if ((selected & WindowsMediaInformationFields.AudioMixFormat) != 0)
            fields.Add(Field(F.AudioAllMixFormatConfigured, endpoint.MixFormat));
        return new(AudioIdentity(endpoint.DataFlow, endpoint.EndpointId), "audio_endpoint", fields);
    }

    internal static DeviceInformationObject ProjectAudioDevice(DeviceTopologyAudioDeviceInformation device,
        WindowsMediaInformationFields selected)
    {
        var fields = new List<DeviceInformationField>(1);
        if ((selected & WindowsMediaInformationFields.AudioDriverVersion) != 0)
            fields.Add(Field(F.AudioAllDriverVersionDeclared, device.DriverVersion));
        return new(AudioDeviceIdentity(device.DeviceInstanceId), "audio_device", fields);
    }

    internal static DeviceInformationObject ProjectMft(DeviceTopologyRegisteredMftInformation transform,
        WindowsMediaInformationFields selected, WindowsCodecRegistryView registryView)
    {
        var fields = new List<DeviceInformationField>(2);
        if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
        {
            var media = transform.InputTypes is { } input && transform.OutputTypes is { } output
                ? new RegisteredMediaTypes(
                    "media-foundation-mft",
                    WindowsMediaInformationScope.CategoryCode(transform.Category),
                    WindowsMediaInformationScope.CategoryId(transform.Category).ToString("B").ToLowerInvariant(),
                    transform.ClassId.ToString("B").ToLowerInvariant(),
                    null, null, null,
                    [.. input.Select(static type => new RegisteredMediaTypeCode(type.MajorType.ToString("D"), type.SubType.ToString("D")))],
                    [.. output.Select(static type => new RegisteredMediaTypeCode(type.MajorType.ToString("D"), type.SubType.ToString("D")))])
                : null;
            fields.Add(Field(F.CodecAllMediaTypeCapability, media));
        }
        if ((selected & WindowsMediaInformationFields.CodecVersion) != 0)
            fields.Add(Field(F.CodecAllVersionDeclared, ProjectCodecVersion(transform.Version,
                WindowsCodecRegistrationSource.MediaFoundationMft, transform.ClassId, registryView)));
        return new(MftIdentity(transform.Category, transform.ClassId), "codec", fields);
    }

    internal static DeviceInformationObject ProjectDirectShow(DeviceTopologyRegisteredDirectShowInformation filter,
        WindowsMediaInformationFields selected, WindowsCodecRegistryView registryView)
    {
        if (string.IsNullOrWhiteSpace(filter.MonikerDisplayName))
            throw new InvalidDataException("A DirectShow moniker must have an exact display-name identity.");
        var fields = new List<DeviceInformationField>(2);
        if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
        {
            var media = new RegisteredMediaTypes(
                "directshow-category-moniker",
                WindowsMediaInformationScope.DirectShowCategoryCode(filter.Category),
                WindowsMediaInformationScope.DirectShowCategoryId(filter.Category).ToString("B").ToLowerInvariant(),
                filter.ClassId?.ToString("B").ToLowerInvariant(),
                filter.MonikerDisplayName,
                filter.FriendlyName,
                filter.DevicePath,
                ProjectTypes(filter.InputTypes),
                ProjectTypes(filter.OutputTypes));
            fields.Add(Field(F.CodecAllMediaTypeCapability, media));
        }
        if ((selected & WindowsMediaInformationFields.CodecVersion) != 0)
            fields.Add(Field(F.CodecAllVersionDeclared, ProjectCodecVersion(filter.Version,
                WindowsCodecRegistrationSource.DirectShowCategoryMoniker, filter.ClassId, registryView)));
        return new(DirectShowIdentity(filter), "codec", fields);
    }

    private static ImmutableArray<RegisteredMediaTypeCode>? ProjectTypes(
        ImmutableArray<DeviceTopologyRegisteredMediaType>? types)
        => types is { } values
            ? [.. values.Select(static type => new RegisteredMediaTypeCode(type.MajorType.ToString("D"), type.SubType.ToString("D")))]
            : null;

    private static string? ProjectCodecVersion(DeviceTopologyCodecModuleVersion? version,
        WindowsCodecRegistrationSource expectedSource, Guid? expectedClassId, WindowsCodecRegistryView expectedRegistryView)
    {
        if (version is null) return null;
        if (expectedClassId is not { } classId || version.Source != expectedSource
            || version.ClassId != classId || version.RegistryView != expectedRegistryView
            || string.IsNullOrWhiteSpace(version.RegisteredModulePath)
            || string.IsNullOrWhiteSpace(version.ResolvedModulePath)
            || string.IsNullOrWhiteSpace(version.FileVersion))
            throw new InvalidDataException("A codec version declaration must match the exact registered source, CLSID, registry view, and module evidence.");
        var source = version.Source switch
        {
            WindowsCodecRegistrationSource.MediaFoundationMft => "media-foundation-mft",
            WindowsCodecRegistrationSource.DirectShowCategoryMoniker => "directshow-category-moniker",
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
        var parts = new List<string>
        {
            $"FileVersion={version.FileVersion}",
            $"Source={source}",
            $"CLSID={version.ClassId:B}",
            $"RegistryView={WindowsMediaInformationScope.RegistryViewCode(version.RegistryView)}",
            $"InprocServer32={version.RegisteredModulePath}",
            $"ResolvedModule={version.ResolvedModulePath}"
        };
        if (!string.IsNullOrWhiteSpace(version.CompanyName)) parts.Add($"CompanyName={version.CompanyName}");
        if (!string.IsNullOrWhiteSpace(version.ProductName)) parts.Add($"ProductName={version.ProductName}");
        return string.Join("; ", parts);
    }

    internal static DeviceInformationSnapshot MediaCurrent(DeviceInformationSnapshot current,
        WindowsMediaInformationScope scope)
    {
        var names = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => names.Contains(field.Definition.Name)
                && IsIdentityInScope(device.DeviceIdentity, scope)).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static DeviceInformationSnapshot MergeMedia(DeviceInformationSnapshot current,
        WindowsMediaInformationScope scope, WindowsMediaInformationFields selected,
        WindowsMediaInformationReadResult read, DateTimeOffset observedAt)
    {
        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        var selectedDefinitions = scope.Definitions.Where(definition => (selected & FieldFor(definition)) != 0).ToArray();
        var expectedPrefixes = new List<string>();
        if ((selected & WindowsMediaInformationScope.AudioEndpointFields) != 0)
            expectedPrefixes.AddRange(scope.AudioDirections.Select(AudioIdentityPrefix));
        if ((selected & WindowsMediaInformationFields.AudioDriverVersion) != 0)
            expectedPrefixes.Add(AudioDeviceIdentityPrefix);
        if ((selected & WindowsMediaInformationFields.Codec) != 0)
            expectedPrefixes.AddRange(scope.CodecCategories.Select(MftIdentityPrefix));
        if ((selected & WindowsMediaInformationFields.Codec) != 0)
            expectedPrefixes.AddRange(scope.DirectShowCategories.Select(DirectShowIdentityPrefix));
        var received = read.Directories.ToDictionary(static directory => directory.IdentityPrefix, StringComparer.Ordinal);
        if (received.Keys.Any(prefix => !expectedPrefixes.Contains(prefix, StringComparer.Ordinal)))
            throw new InvalidOperationException("A media reader returned an unrequested directory.");

        foreach (var prefix in expectedPrefixes)
        {
            var directory = received.GetValueOrDefault(prefix)
                ?? new WindowsMediaDirectoryRead(prefix, false, []);
            var existing = objects.Values.Where(device => device.DeviceIdentity.StartsWith(directory.IdentityPrefix, StringComparison.Ordinal))
                .ToArray();
            if (!directory.DirectorySucceeded)
            {
                if (directory.Objects.Count != 0)
                    throw new InvalidOperationException("A failed media directory returned partial objects.");
                foreach (var device in existing)
                {
                    var fields = device.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                    foreach (var definition in selectedDefinitions)
                        if (IsDefinitionForDirectory(definition, directory.IdentityPrefix))
                            fields[definition.Name] = new(definition, null) { ObservedAt = null };
                    objects[device.DeviceIdentity] = device with { Fields = fields.Values.ToArray() };
                }
                continue;
            }

            var previousByIdentity = existing.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
            foreach (var device in existing) objects.Remove(device.DeviceIdentity);
            var observedIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observed in directory.Objects)
            {
                if (!observed.DeviceIdentity.StartsWith(directory.IdentityPrefix, StringComparison.Ordinal)
                    || observed.DeviceIdentity.Length == directory.IdentityPrefix.Length
                    || observed.ObjectType != ObjectTypeForDirectory(directory.IdentityPrefix)
                    || !observedIdentities.Add(observed.DeviceIdentity))
                    throw new InvalidOperationException("A media reader returned an object outside its exact directory.");
                var expectedNames = selectedDefinitions.Where(definition => IsDefinitionForDirectory(definition, directory.IdentityPrefix))
                    .Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
                var returnedNames = observed.Fields.Select(static field => field.Definition.Name).ToHashSet(StringComparer.Ordinal);
                if (returnedNames.Count != observed.Fields.Count || !returnedNames.SetEquals(expectedNames)
                    || observed.Fields.Any(field => !scope.Definitions.Any(definition => ReferenceEquals(definition, field.Definition))))
                    throw new InvalidOperationException("A media reader did not return exactly the selected static fields.");
                var fields = previousByIdentity.TryGetValue(observed.DeviceIdentity, out var sameIdentity)
                    ? sameIdentity.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal)
                    : new Dictionary<string, DeviceInformationField>(StringComparer.Ordinal);
                foreach (var field in observed.Fields)
                    fields[field.Definition.Name] = field with { ObservedAt = field.Value is null ? null : observedAt };
                objects[observed.DeviceIdentity] = observed with { Fields = fields.Values.ToArray() };
            }
        }
        return new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static DeviceInformationSnapshot PreserveMediaFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var mediaFields = device.Fields.Where(static field => IsMediaDefinition(field.Definition)).ToArray();
            if (mediaFields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var projectedDevice))
            {
                var fields = mediaFields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in projectedDevice.Fields) fields[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = projectedDevice with { Fields = fields.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = mediaFields });
        }
        return new(projected.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsMediaDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.AudioAllEndpointIdDeclared)
            || ReferenceEquals(definition, F.AudioAllDefaultRoleActual)
            || ReferenceEquals(definition, F.AudioAllMixFormatConfigured)
            || ReferenceEquals(definition, F.AudioAllDriverVersionDeclared)
            || ReferenceEquals(definition, F.CodecAllMediaTypeCapability)
            || ReferenceEquals(definition, F.CodecAllVersionDeclared);

    private static bool IsIdentityInScope(string identity, WindowsMediaInformationScope scope)
        => (scope.Fields & WindowsMediaInformationScope.AudioEndpointFields) != 0
                && scope.AudioDirections.Any(direction => identity.StartsWith(AudioIdentityPrefix(direction), StringComparison.Ordinal))
            || (scope.Fields & WindowsMediaInformationFields.AudioDriverVersion) != 0
                && identity.StartsWith(AudioDeviceIdentityPrefix, StringComparison.Ordinal)
            || scope.CodecCategories.Any(category => identity.StartsWith(MftIdentityPrefix(category), StringComparison.Ordinal))
            || scope.DirectShowCategories.Any(category => identity.StartsWith(DirectShowIdentityPrefix(category), StringComparison.Ordinal));

    private static bool IsDefinitionForDirectory(DeviceInformationDefinition definition, string identityPrefix)
        => identityPrefix.StartsWith("windows-audio-endpoint|", StringComparison.Ordinal)
            ? ReferenceEquals(definition, F.AudioAllEndpointIdDeclared) || ReferenceEquals(definition, F.AudioAllDefaultRoleActual)
                || ReferenceEquals(definition, F.AudioAllMixFormatConfigured)
            : identityPrefix.StartsWith(AudioDeviceIdentityPrefix, StringComparison.Ordinal)
                ? ReferenceEquals(definition, F.AudioAllDriverVersionDeclared)
                : (identityPrefix.StartsWith("windows-mft|", StringComparison.Ordinal)
                    || identityPrefix.StartsWith("windows-directshow|", StringComparison.Ordinal))
                    && (ReferenceEquals(definition, F.CodecAllMediaTypeCapability)
                        || ReferenceEquals(definition, F.CodecAllVersionDeclared));

    private static string ObjectTypeForDirectory(string identityPrefix)
        => identityPrefix.StartsWith("windows-audio-endpoint|", StringComparison.Ordinal) ? "audio_endpoint"
            : identityPrefix.StartsWith(AudioDeviceIdentityPrefix, StringComparison.Ordinal) ? "audio_device"
            : "codec";

    private static WindowsMediaInformationFields FieldFor(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, F.AudioAllEndpointIdDeclared)) return WindowsMediaInformationFields.AudioEndpointId;
        if (ReferenceEquals(definition, F.AudioAllDefaultRoleActual)) return WindowsMediaInformationFields.AudioDefaultRoles;
        if (ReferenceEquals(definition, F.AudioAllMixFormatConfigured)) return WindowsMediaInformationFields.AudioMixFormat;
        if (ReferenceEquals(definition, F.AudioAllDriverVersionDeclared)) return WindowsMediaInformationFields.AudioDriverVersion;
        if (ReferenceEquals(definition, F.CodecAllMediaTypeCapability)) return WindowsMediaInformationFields.CodecMediaTypes;
        if (ReferenceEquals(definition, F.CodecAllVersionDeclared)) return WindowsMediaInformationFields.CodecVersion;
        return WindowsMediaInformationFields.None;
    }

    private static DeviceInformationField Field<T>(DeviceInformationDefinition definition, T? value)
        => new(definition, value is null ? null : JsonSerializer.SerializeToElement(value, MediaJsonOptions))
        { ObservedAt = null };

    private sealed record RegisteredMediaTypes(string RegistrationSource, string Category, string CategoryClassId,
        string? ClassId, string? SourceIdentity, string? FriendlyName, string? DevicePath,
        ImmutableArray<RegisteredMediaTypeCode>? Input, ImmutableArray<RegisteredMediaTypeCode>? Output);
    private sealed record RegisteredMediaTypeCode(string MajorType, string SubType);
}
