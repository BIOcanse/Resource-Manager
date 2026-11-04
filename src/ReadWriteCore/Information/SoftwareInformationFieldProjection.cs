using System.Text.Json;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Projects the original filtered inventory. RegistryPath includes hive/view
/// and any selected user SID. InstallLocation never borrows inferred RootPaths.</summary>
public static class SoftwareInformationFieldProjection
{
    public static DeviceInformationSnapshot Create(IReadOnlyList<InstalledSoftwareEntry> entries, DateTimeOffset observedAt)
        => new(observedAt, entries.Select(entry => new DeviceInformationObject(
            "windows-uninstall|" + entry.RegistryPath, "software_registration",
            [Text(DeviceInformationFieldRegistry.SoftwareAllNameDeclared, entry.Name),
             Text(DeviceInformationFieldRegistry.SoftwareAllVersionDeclared, entry.Version),
             Text(DeviceInformationFieldRegistry.SoftwareAllInstallPathDeclared, entry.InstallLocation),
             new(DeviceInformationFieldRegistry.SoftwareAllReportedSize, entry.ReportedSizeBytes is { } bytes
                 ? JsonSerializer.SerializeToElement(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null)])).ToArray());

    public static DeviceInformationSnapshot Clear(DeviceInformationSnapshot previous)
        => new(null, previous.Devices.Select(device => device with
        { Fields = device.Fields.Select(field => field with { Value = null }).ToArray() }).ToArray());

    private static DeviceInformationField Text(DeviceInformationDefinition definition, string? value)
        => new(definition, value is null ? null : JsonSerializer.SerializeToElement(value));
}

