using System.Globalization;
using System.Text;
using System.Text.Json;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private const string DrsObjectType = "gpu_driver_profile_setting";
    private static readonly UnicodeEncoding DrsStrictUnicode = new(false, false, true);

    internal static DeviceInformationSnapshot NvidiaDrsCurrent(DeviceInformationSnapshot current, NvidiaDrsInformationRequest scope)
    {
        var definition = DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue;
        return new(current.ObservedAt, scope.Fields.Select(field => new DeviceInformationObject(field.DeviceIdentity,
            DrsObjectType, [current.FindField(field) is { } prior && prior.Definition == definition
                ? prior : new DeviceInformationField(definition, null)])).ToArray());
    }

    internal static DeviceInformationSnapshot ProjectNvidiaDrs(NvidiaDrsInformationRequest selected,
        NvidiaDrsSnapshot read, DateTimeOffset observedAt, CancellationToken token)
    {
        if (read.Profiles.IsDefault || read.Profiles.Length != selected.Profiles.Length)
            throw new InvalidDataException("DRS source must return every selected profile exactly once.");
        var expected = selected.Profiles.ToDictionary(profile => profile.ProfileName, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var objects = new List<DeviceInformationObject>();
        foreach (var profile in read.Profiles)
        {
            token.ThrowIfCancellationRequested();
            if (profile is null || SafeDrsText(profile.ProfileName) is null || !expected.TryGetValue(profile.ProfileName, out var request)
                || !seen.Add(profile.ProfileName) || profile.Settings.IsDefault || profile.Settings.Length != request.SettingIds.Count)
                throw new InvalidDataException("DRS source returned an unexpected, duplicate or incomplete profile.");
            var expectedIds = request.SettingIds.ToHashSet();
            var ids = new HashSet<uint>();
            foreach (var setting in profile.Settings)
            {
                token.ThrowIfCancellationRequested();
                if (setting is null || !expectedIds.Contains(setting.SettingId) || !ids.Add(setting.SettingId))
                    throw new InvalidDataException("DRS source returned an unexpected or duplicate setting.");
                var value = setting.TypeRaw is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(new NvidiaDrsSettingValue(profile.ProfileName, setting.SettingId, SafeDrsText(setting.NameDeclared), setting.TypeRaw, setting.LocationRaw, setting.IsCurrentPredefinedRaw, setting.IsPredefinedValidRaw, profile.GpuSupportRaw, profile.IsPredefinedRaw, DrsValue(setting.TypeRaw.Value, setting.CurrentValue)), InformationJsonOptions);
                objects.Add(new(NvidiaDrsInformationRequest.Identity(profile.ProfileName, setting.SettingId), DrsObjectType,
                    [new DeviceInformationField(DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue, value)
                        { ObservedAt = value is null ? null : observedAt }]));
            }
        }
        return new(observedAt, objects);
    }

    private static object? DrsValue(uint type, NvidiaDrsCurrentValue? value) => (type, value) switch
    {
        (0, NvidiaDrsDwordValue number) => new NvidiaDrsDwordPayload("dword", number.Value),
        (4, NvidiaDrsQwordValue number) => new NvidiaDrsStringPayload("qword", number.Value.ToString(CultureInfo.InvariantCulture)),
        (3, NvidiaDrsUnicodeValue text) when SafeDrsText(text.Value) is { } valid => new NvidiaDrsStringPayload("unicode", valid),
        (1, NvidiaDrsBinaryValue bytes) when !bytes.Value.IsDefault && bytes.Value.Length <= 4096
            => new NvidiaDrsBinaryPayload("binary", bytes.Value.Length, Convert.ToBase64String(bytes.Value.AsSpan())),
        _ => null
    };

    private static string? SafeDrsText(string? value)
    {
        if (value is null || value.Length >= 2048 || value.Contains('\0')) return null;
        try { _ = DrsStrictUnicode.GetByteCount(value); return value; }
        catch (EncoderFallbackException) { return null; }
    }

    internal static DeviceInformationSnapshot MergeNvidiaDrs(DeviceInformationSnapshot current,
        NvidiaDrsInformationRequest selected, DeviceInformationSnapshot read, CancellationToken token)
    {
        if (read.Devices.Count != selected.Fields.Length) throw new InvalidDataException("DRS result is incomplete.");
        var expected = selected.Fields.ToHashSet();
        var objects = current.Devices.ToDictionary(device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var item in read.Devices)
        {
            token.ThrowIfCancellationRequested();
            var field = new FieldReference(item.DeviceIdentity, DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue.Name);
            if (item.ObjectType != DrsObjectType || item.Fields.Count != 1
                || item.Fields[0].Definition != DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue || !expected.Remove(field))
                throw new InvalidDataException("DRS object is outside the selected scope.");
            if (objects.TryGetValue(item.DeviceIdentity, out var prior) && prior.ObjectType != DrsObjectType)
                throw new InvalidDataException("DRS cannot overwrite another information object type.");
            objects[item.DeviceIdentity] = item;
        }
        return new(current.ObservedAt, objects.Values.OrderBy(item => item.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static DeviceInformationSnapshot NvidiaDrsUnavailable(DeviceInformationSnapshot current,
        NvidiaDrsInformationRequest selected, CancellationToken token)
        => MergeNvidiaDrs(current, selected, new(null, selected.Fields.Select(field => new DeviceInformationObject(
            field.DeviceIdentity, DrsObjectType, [new DeviceInformationField(DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue, null)]))
            .ToArray()), token);

    internal static DeviceInformationSnapshot PreserveNvidiaDrsInformation(DeviceInformationSnapshot projected, DeviceInformationSnapshot previous)
    {
        var identities = projected.Devices.Select(item => item.DeviceIdentity).ToHashSet(StringComparer.Ordinal);
        return new(projected.ObservedAt, projected.Devices.Concat(previous.Devices.Where(item => item.ObjectType == DrsObjectType
            && !identities.Contains(item.DeviceIdentity))).ToArray());
    }
}
