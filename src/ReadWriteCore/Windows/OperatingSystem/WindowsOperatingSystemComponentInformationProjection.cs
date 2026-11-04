using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class WindowsOperatingSystemComponentInformationProjection
{
    internal static WindowsOperatingSystemInformationFields Category(DeviceInformationDefinition definition)
        => definition == DeviceInformationFieldRegistry.OsAllFeatureStateActual
            ? WindowsOperatingSystemInformationFields.Features
            : definition == DeviceInformationFieldRegistry.OsAllPackageStateActual
                ? WindowsOperatingSystemInformationFields.ComponentPackages
                : definition == DeviceInformationFieldRegistry.DriverAllPackageIdentityDeclared
                    ? WindowsOperatingSystemInformationFields.DriverPackages
                    : WindowsOperatingSystemInformationFields.None;

    internal static bool HasCanonicalIdentity(string identity, WindowsOperatingSystemInformationFields category)
    {
        var prefix = category switch
        {
            WindowsOperatingSystemInformationFields.Features => "windows-os-component|feature:",
            WindowsOperatingSystemInformationFields.ComponentPackages => "windows-os-component|package:",
            WindowsOperatingSystemInformationFields.DriverPackages => "windows-os-component|driver-package:",
            _ => null
        };
        return prefix is not null && identity.StartsWith(prefix, StringComparison.Ordinal)
            && identity.Length > prefix.Length;
    }

    internal static bool Succeeded(WindowsOperatingSystemComponentInformationReadResult result,
        WindowsOperatingSystemInformationFields category) => category switch
    {
        WindowsOperatingSystemInformationFields.Features => result.Features is not null,
        WindowsOperatingSystemInformationFields.ComponentPackages => result.Packages is not null,
        WindowsOperatingSystemInformationFields.DriverPackages => result.DriverPackages is not null,
        _ => false
    };

    internal static IReadOnlyList<DeviceInformationObject> Project(
        WindowsOperatingSystemComponentInformationReadResult result,
        WindowsOperatingSystemInformationFields category,
        WindowsOperatingSystemComponentInformationScope scope,
        DateTimeOffset observedAt)
    {
        var devices = new List<DeviceInformationObject>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = 0;

        void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > scope.MaximumStringCharacters
                || !names.Add(name))
                throw new InvalidOperationException("The component reader returned a missing, oversized or duplicate identity.");
            characters = checked(characters + name.Length);
            if (characters > scope.MaximumCharactersPerCategory)
                throw new InvalidOperationException("The component reader exceeded the caller's text bound.");
        }

        switch (category)
        {
            case WindowsOperatingSystemInformationFields.Features:
            {
                var rows = result.Features ?? throw new InvalidOperationException("Feature projection requires a successful read.");
                if (rows.Count > scope.MaximumFeatures)
                    throw new InvalidOperationException("The component reader exceeded the feature count bound.");
                foreach (var row in rows)
                {
                    ValidateName(row.FeatureName);
                    devices.Add(Object("feature", row.FeatureName, "os_feature",
                        DeviceInformationFieldRegistry.OsAllFeatureStateActual,
                        row.State.ToString(CultureInfo.InvariantCulture), observedAt));
                }
                break;
            }
            case WindowsOperatingSystemInformationFields.ComponentPackages:
            {
                var rows = result.Packages ?? throw new InvalidOperationException("Package projection requires a successful read.");
                if (rows.Count > scope.MaximumPackages)
                    throw new InvalidOperationException("The component reader exceeded the package count bound.");
                foreach (var row in rows)
                {
                    ValidateName(row.PackageName);
                    devices.Add(Object("package", row.PackageName, "os_package",
                        DeviceInformationFieldRegistry.OsAllPackageStateActual,
                        row.State.ToString(CultureInfo.InvariantCulture), observedAt));
                }
                break;
            }
            case WindowsOperatingSystemInformationFields.DriverPackages:
            {
                var rows = result.DriverPackages ?? throw new InvalidOperationException("Driver-package projection requires a successful read.");
                if (rows.Count > scope.MaximumDriverPackages)
                    throw new InvalidOperationException("The component reader exceeded the driver-package count bound.");
                foreach (var row in rows)
                {
                    ValidateName(row.PublishedName);
                    devices.Add(Object("driver-package", row.PublishedName, "driver_package",
                        DeviceInformationFieldRegistry.DriverAllPackageIdentityDeclared,
                        row.PublishedName, observedAt));
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }
        return devices;
    }

    private static DeviceInformationObject Object(string identityKind, string identityValue, string objectType,
        DeviceInformationDefinition definition, string value, DateTimeOffset observedAt)
    {
        var encoded = Uri.EscapeDataString(identityValue);
        var identity = $"windows-os-component|{identityKind}:{encoded}";
        var field = new DeviceInformationField(definition, JsonSerializer.SerializeToElement(value))
        { ObservedAt = observedAt };
        return new(identity, objectType, [field]);
    }
}
