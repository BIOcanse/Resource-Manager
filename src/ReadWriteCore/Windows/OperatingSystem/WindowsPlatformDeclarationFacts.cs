using Microsoft.Win32;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Null SID selects the calling token user on explicit capture. A supplied
/// SID reads only that user's loaded hive and package registrations; no impersonation,
/// hive mounting, all-user inference or environment expansion occurs.</summary>
public sealed record WindowsPlatformDeclarationScope
{
    public bool IncludeMachineConfiguration { get; init; } = true;
    public string? UserSecurityId { get; init; }
    public bool IncludeUserConfiguration { get; init; } = true;
    /// <summary>Local scheduler namespace is independent of machine/user registry
    /// and folder scopes; actual task principals remain configuration declarations.</summary>
    public bool IncludeTaskSchedulerConfiguration { get; init; } = true;
}

public sealed record WindowsLicenseStatusRow(Guid ApplicationId, Guid ProductId, uint? LicenseStatus);
public sealed record WindowsConfiguredRegistryString(string Hive, RegistryView View, string? UserSecurityId,
    string KeyPath, string ValueName, RegistryValueKind ValueKind, string? Text);
public sealed record WindowsRegisteredPackageFamily(string UserSecurityId, string PackageFullName, string FamilyName);
public enum WindowsStartupConfigurationSource { StartupFolderFile, TaskSchedulerBootOrLogon }
/// <summary>Source kind defines payload semantics: startup-folder file/shortcut
/// target declaration or original task registration XML. Principal text is the
/// provider's declaration, not a resolved execution token or current-user guess.</summary>
public sealed record WindowsConfiguredStartupEntry(WindowsStartupConfigurationSource Source, string Scope,
    string SourceLocator, string ConfiguredPayload, string? PrincipalDeclaration = null);

/// <summary>Null category is a failed read; an empty category is a successful empty
/// enumeration. Facts have no current cache and no subscription lifetime.</summary>
public sealed record WindowsPlatformDeclarationFacts(
    IReadOnlyList<WindowsLicenseStatusRow>? Activation,
    IReadOnlyList<WindowsConfiguredRegistryString>? Environment,
    IReadOnlyList<WindowsConfiguredRegistryString>? Startup,
    IReadOnlyList<WindowsRegisteredPackageFamily>? Packages)
{
    public IReadOnlyList<WindowsLoginSession>? Sessions { get; init; }
    public IReadOnlyList<WindowsConfiguredStartupEntry>? StartupAdditional { get; init; }
}

internal static class WindowsPlatformDeclarationProjection
{
    internal static WindowsOperatingSystemInformationFields Category(DeviceInformationDefinition definition)
        => definition == DeviceInformationFieldRegistry.OsAllActivationStateReported ? WindowsOperatingSystemInformationFields.Activation
        : definition == DeviceInformationFieldRegistry.EnvironmentAllVariableConfigured ? WindowsOperatingSystemInformationFields.Environment
        : definition == DeviceInformationFieldRegistry.StartupAllEntryConfigured ? WindowsOperatingSystemInformationFields.Startup
        : definition == DeviceInformationFieldRegistry.SoftwareAllPackageFamilyDeclared ? WindowsOperatingSystemInformationFields.Packages
        : definition == DeviceInformationFieldRegistry.OsAllSessionIdActual ? WindowsOperatingSystemInformationFields.SessionId
        : WindowsOperatingSystemInformationFields.None;

    internal static IEnumerable<DeviceInformationObject> Project(WindowsPlatformDeclarationFacts facts,
        WindowsOperatingSystemInformationFields category, DateTimeOffset observedAt)
    {
        static string Part(string value) => Uri.EscapeDataString(value);
        DeviceInformationObject Object(string id, string type, DeviceInformationDefinition definition, string? value)
            => new(id, type, [new(definition, value is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(value))
                { ObservedAt = value is null ? null : observedAt }]);
        if (category == WindowsOperatingSystemInformationFields.Activation && facts.Activation is not null)
            foreach (var row in facts.Activation)
                yield return Object($"windows-license|application:{row.ApplicationId:D}|product:{row.ProductId:D}", "windows_license_product",
                    DeviceInformationFieldRegistry.OsAllActivationStateReported,
                    row.LicenseStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var strings = category == WindowsOperatingSystemInformationFields.Environment ? facts.Environment
            : category == WindowsOperatingSystemInformationFields.Startup ? facts.Startup : null;
        if (strings is not null)
            foreach (var row in strings)
                yield return Object($"windows-registry|{row.Hive}|{row.View}|sid:{Part(row.UserSecurityId ?? "machine")}|key:{Part(row.KeyPath)}|name:{Part(row.ValueName)}|kind:{row.ValueKind}",
                    category == WindowsOperatingSystemInformationFields.Environment ? "configured_environment_variable" : "configured_run_startup_entry",
                    category == WindowsOperatingSystemInformationFields.Environment ? DeviceInformationFieldRegistry.EnvironmentAllVariableConfigured
                        : DeviceInformationFieldRegistry.StartupAllEntryConfigured, row.Text);
        if (category == WindowsOperatingSystemInformationFields.Packages && facts.Packages is not null)
            foreach (var row in facts.Packages)
                yield return Object($"windows-package|sid:{Part(row.UserSecurityId)}|fullname:{Part(row.PackageFullName)}", "registered_msix_package",
                    DeviceInformationFieldRegistry.SoftwareAllPackageFamilyDeclared, row.FamilyName);
        if (category == WindowsOperatingSystemInformationFields.SessionId && facts.Sessions is not null)
            foreach (var row in facts.Sessions)
                yield return Object($"windows-wts-login|local|session:{row.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}|logon-filetime:{row.LogonFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    "local_windows_login_session", DeviceInformationFieldRegistry.OsAllSessionIdActual,
                    row.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (category == WindowsOperatingSystemInformationFields.Startup && facts.StartupAdditional is not null)
            foreach (var row in facts.StartupAdditional)
                yield return Object($"windows-startup|source:{row.Source}|scope:{Part(row.Scope)}|locator:{Part(row.SourceLocator)}",
                    row.Source == WindowsStartupConfigurationSource.StartupFolderFile ? "configured_startup_folder_entry" : "configured_boot_logon_task",
                    DeviceInformationFieldRegistry.StartupAllEntryConfigured, row.ConfiguredPayload);
    }

    internal static bool Succeeded(WindowsPlatformDeclarationFacts facts, WindowsOperatingSystemInformationFields category)
        => category switch
        {
            WindowsOperatingSystemInformationFields.Activation => facts.Activation is not null,
            WindowsOperatingSystemInformationFields.Environment => facts.Environment is not null,
            WindowsOperatingSystemInformationFields.Startup => facts.Startup is not null,
            WindowsOperatingSystemInformationFields.Packages => facts.Packages is not null,
            WindowsOperatingSystemInformationFields.SessionId => facts.Sessions is not null,
            _ => false
        };
}
