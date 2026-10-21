using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal static string D3dVideoDecoderProfileIdentity(WindowsD3dAdapterIdentity adapter, Guid profileGuid)
        => $"windows-d3d-video-decoder|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}|profile:{profileGuid:D}";

    internal static string D3dVideoDecoderFormatIdentity(WindowsD3dAdapterIdentity adapter,
        Guid profileGuid, WindowsD3dFormat format)
        => $"{D3dVideoDecoderProfileIdentity(adapter, profileGuid)}|dxgi-format:{(uint)format}";

    private static bool IsRequestedD3dVideoDecoderIdentity(WindowsD3dCapabilityScope scope,
        string identity, DeviceInformationDefinition definition)
    {
        foreach (var adapter in scope.Adapters)
        {
            if (ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid)
                && (scope.Fields & WindowsD3dCapabilityFields.DecoderProfiles) != 0
                && TryGetVideoDecoderProfile(adapter, identity, out _))
                return true;
            if (ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat)
                && (scope.Fields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0
                && TryGetVideoDecoderFormat(adapter, identity, out _, out var format)
                && scope.Formats.Contains(format))
                return true;
            if (ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration)
                && IsRequestedD3dVideoDecoderConfigurationIdentity(scope, adapter, identity))
                return true;
        }
        return false;
    }

    private static void MergeD3dVideoDecoderFields(
        Dictionary<string, DeviceInformationObject> objects,
        WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected,
        IReadOnlyDictionary<WindowsD3dAdapterIdentity, WindowsD3dAdapterCapabilityRead> rows,
        DateTimeOffset observedAt)
    {
        var profileSelected = (selected & WindowsD3dCapabilityFields.DecoderProfiles) != 0;
        var outputFormatSelected = (selected & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0;
        if (!profileSelected && !outputFormatSelected) return;

        foreach (var adapter in scope.Adapters)
        {
            var row = rows[adapter];
            ValidateVideoDecoderRow(scope, selected, row);
            var profiles = row.DecoderProfiles.IsDefault ? ImmutableArray<WindowsD3dVideoDecoderProfileRead>.Empty
                : row.DecoderProfiles;
            if (row.DecoderProfilesComplete != true)
            {
                if (profileSelected)
                    SetExistingVideoDecoderFieldsUnavailable(objects, adapter, profileGuid: null,
                        scope.Formats, DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid, observedAt);
                if (outputFormatSelected)
                    SetExistingVideoDecoderFieldsUnavailable(objects, adapter, profileGuid: null,
                        scope.Formats, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat, observedAt);
                continue;
            }

            var profileGuids = profiles.Select(static profile => profile.ProfileGuid).ToHashSet();
            foreach (var staleIdentity in objects.Keys.Where(identity =>
                TryGetVideoDecoderFormat(adapter, identity, out var existingProfile, out _)
                    && !profileGuids.Contains(existingProfile)).ToArray())
                RemoveField(objects, staleIdentity, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat.Name);
            if (profileSelected)
            {
                foreach (var staleIdentity in objects.Keys.Where(identity =>
                    TryGetVideoDecoderProfile(adapter, identity, out var existingProfile)
                        && !profileGuids.Contains(existingProfile)).ToArray())
                    RemoveField(objects, staleIdentity, DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid.Name);

                foreach (var profile in profiles)
                {
                    SetField(objects, D3dVideoDecoderProfileIdentity(adapter, profile.ProfileGuid),
                        "gpu_adapter_decoder_profile", DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid,
                        profile.ProfileGuid.ToString("D"), observedAt);
                }
            }

            if (!outputFormatSelected) continue;
            foreach (var profile in profiles)
            {
                if (profile.OutputFormatsComplete != true)
                {
                    SetExistingVideoDecoderFieldsUnavailable(objects, adapter, profile.ProfileGuid,
                        scope.Formats, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat, observedAt);
                    continue;
                }
                foreach (var formatRead in profile.OutputFormats)
                {
                    var identity = D3dVideoDecoderFormatIdentity(adapter, profile.ProfileGuid, formatRead.Format);
                    switch (formatRead.Supported)
                    {
                        case true:
                            SetField(objects, identity, "gpu_adapter_decoder_format",
                                DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat,
                                ((uint)formatRead.Format).ToString(CultureInfo.InvariantCulture), observedAt);
                            break;
                        case false:
                            RemoveField(objects, identity, DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat.Name);
                            break;
                        default:
                            SetField(objects, identity, "gpu_adapter_decoder_format",
                                DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat, null, observedAt);
                            break;
                    }
                }
            }
        }
    }

    private static void ValidateVideoDecoderRow(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected, WindowsD3dAdapterCapabilityRead row)
    {
        var profileSelected = (selected & WindowsD3dCapabilityFields.DecoderProfiles) != 0;
        var outputFormatSelected = (selected & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0;
        var videoSelected = profileSelected || outputFormatSelected;
        var profiles = row.DecoderProfiles.IsDefault ? ImmutableArray<WindowsD3dVideoDecoderProfileRead>.Empty
            : row.DecoderProfiles;
        if (!videoSelected)
        {
            if (row.DecoderProfilesComplete is not null || !profiles.IsEmpty)
                throw new InvalidOperationException("The D3D reader returned unrequested decoder profile data.");
            return;
        }

        if (row.DecoderProfilesComplete is null)
            throw new InvalidOperationException("The D3D reader omitted decoder profile enumeration status.");
        if (row.DecoderProfilesComplete == false)
        {
            if (!profiles.IsEmpty)
                throw new InvalidOperationException("An incomplete D3D decoder enumeration must not publish partial profiles.");
            return;
        }
        if (profiles.Length > scope.MaximumVideoDecoderProfileCount)
            throw new InvalidOperationException("The D3D reader exceeded the configured decoder profile bound.");
        var formatQueryWithinBound = (ulong)profiles.Length * (ulong)scope.Formats.Length
            <= (ulong)scope.MaximumVideoDecoderFormatQueryCount;

        var profileGuids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (!profileGuids.Add(profile.ProfileGuid))
                throw new InvalidOperationException("The D3D reader returned a duplicate decoder profile GUID.");
            var outputFormats = profile.OutputFormats.IsDefault
                ? ImmutableArray<WindowsD3dVideoDecoderFormatRead>.Empty : profile.OutputFormats;
            if (!outputFormatSelected)
            {
                if (profile.OutputFormatsComplete is not null || !outputFormats.IsEmpty)
                    throw new InvalidOperationException("The D3D reader returned unrequested decoder output formats.");
                continue;
            }

            if (profile.OutputFormatsComplete is null)
                throw new InvalidOperationException("The D3D reader omitted per-profile format query status.");
            if (!formatQueryWithinBound
                && (profile.OutputFormatsComplete != false || !outputFormats.IsEmpty))
                throw new InvalidOperationException("The D3D reader exceeded the configured decoder pair-query bound.");
            if (profile.OutputFormatsComplete == false)
            {
                if (!outputFormats.IsEmpty)
                    throw new InvalidOperationException("An incomplete D3D format query must not publish partial candidates.");
                continue;
            }
            var formats = new HashSet<WindowsD3dFormat>();
            foreach (var outputFormat in outputFormats)
            {
                if (!Enum.IsDefined(outputFormat.Format) || !formats.Add(outputFormat.Format))
                    throw new InvalidOperationException("The D3D reader returned an invalid or duplicate decoder format.");
            }
            if (formats.Count != scope.Formats.Length || scope.Formats.Any(format => !formats.Contains(format)))
                throw new InvalidOperationException("The D3D reader did not return exactly the requested decoder format candidates.");
        }
    }

    private static void SetExistingVideoDecoderFieldsUnavailable(
        Dictionary<string, DeviceInformationObject> objects,
        WindowsD3dAdapterIdentity adapter, Guid? profileGuid,
        ImmutableArray<WindowsD3dFormat> selectedFormats,
        DeviceInformationDefinition definition, DateTimeOffset observedAt)
    {
        var isProfileDefinition = ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid);
        foreach (var identity in objects.Keys.ToArray())
        {
            bool matches;
            if (isProfileDefinition)
            {
                matches = TryGetVideoDecoderProfile(adapter, identity, out var existingProfile)
                    && (profileGuid is null || existingProfile == profileGuid.Value);
            }
            else
            {
                matches = TryGetVideoDecoderFormat(adapter, identity, out var existingProfile, out var format)
                    && (profileGuid is null || existingProfile == profileGuid.Value)
                    && selectedFormats.Contains(format);
            }
            if (matches)
                SetField(objects, identity, isProfileDefinition ? "gpu_adapter_decoder_profile" : "gpu_adapter_decoder_format",
                    definition, null, observedAt);
        }
    }

    private static bool TryGetVideoDecoderProfile(WindowsD3dAdapterIdentity adapter,
        string identity, out Guid profileGuid)
    {
        var prefix = $"windows-d3d-video-decoder|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}|profile:";
        profileGuid = default;
        if (!identity.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(identity[prefix.Length..], "D", out profileGuid))
            return false;
        return string.Equals(identity, D3dVideoDecoderProfileIdentity(adapter, profileGuid), StringComparison.Ordinal);
    }

    private static bool TryGetVideoDecoderFormat(WindowsD3dAdapterIdentity adapter,
        string identity, out Guid profileGuid, out WindowsD3dFormat format)
    {
        profileGuid = default;
        format = default;
        var prefix = $"windows-d3d-video-decoder|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}|profile:";
        const string formatMarker = "|dxgi-format:";
        if (!identity.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var markerIndex = identity.IndexOf(formatMarker, prefix.Length, StringComparison.Ordinal);
        if (markerIndex < 0
            || !Guid.TryParseExact(identity[prefix.Length..markerIndex], "D", out profileGuid)
            || !uint.TryParse(identity[(markerIndex + formatMarker.Length)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var numericFormat)
            || !Enum.IsDefined((WindowsD3dFormat)numericFormat))
            return false;
        format = (WindowsD3dFormat)numericFormat;
        return string.Equals(identity, D3dVideoDecoderFormatIdentity(adapter, profileGuid, format), StringComparison.Ordinal);
    }

    private static void RemoveField(Dictionary<string, DeviceInformationObject> objects,
        string identity, string fieldName)
    {
        if (!objects.TryGetValue(identity, out var current)) return;
        var fields = current.Fields.Where(field => !string.Equals(field.Definition.Name, fieldName, StringComparison.Ordinal)).ToArray();
        if (fields.Length == current.Fields.Count) return;
        if (fields.Length == 0) objects.Remove(identity);
        else objects[identity] = current with { Fields = fields };
    }
}
