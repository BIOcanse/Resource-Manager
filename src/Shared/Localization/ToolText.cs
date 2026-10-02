using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Shared.Localization;

/// <summary>Static copy for the independent installation and update interfaces.</summary>
public sealed record ToolText
{
    private static readonly ConcurrentDictionary<string, ToolText> Cache = new(StringComparer.Ordinal);
    [JsonIgnore] public string Language { get; init; } = AppLanguage.Fallback;
    public required string InstallerTitle { get; init; }
    public required string InstallerFailed { get; init; }
    public required string InstallLocationFormat { get; init; }
    public required string InstallActions { get; init; }
    public required string InstallFiles { get; init; }
    public required string InstallRegistry { get; init; }
    public required string InstallService { get; init; }
    public required string InstallManager { get; init; }
    public required string InstallShortcuts { get; init; }
    public required string InstallConsent { get; init; }
    public required string InstallCompletedFormat { get; init; }
    public required string UserUnknown { get; init; }
    public required string ElevationIdentity { get; init; }
    public required string InstallerChildFailed { get; init; }
    public required string ManagerTitle { get; init; }
    public required string Heading { get; init; }
    public required string Intro { get; init; }
    public required string ReadingInstallation { get; init; }
    public required string InstallStatusFormat { get; init; }
    public required string ManagerFailureFormat { get; init; }
    public required string NoRecovery { get; init; }
    public required string RecoveryFoundFormat { get; init; }
    public required string Recover { get; init; }
    public required string PackageHeading { get; init; }
    public required string PackagePlaceholder { get; init; }
    public required string Browse { get; init; }
    public required string VersionRules { get; init; }
    public required string Update { get; init; }
    public required string Repair { get; init; }
    public required string PackageActions { get; init; }
    public required string RecoveryActions { get; init; }
    public required string Continue { get; init; }
    public required string Cancel { get; init; }
    public required string Completed { get; init; }
    public required string CloseManager { get; init; }
    public required string SelfReplaceNotice { get; init; }
    public required string ManagerPathUnknown { get; init; }
    public required string ManagerChildFailed { get; init; }
    public required string OperationFailed { get; init; }
    public required string OperationSucceeded { get; init; }
    public required string NotInstalled { get; init; }
    public required string RepairSummaryFormat { get; init; }
    public required string UpdateSummaryFormat { get; init; }

    public required string VerifiedPackageFormat { get; init; }
    public required string PreflightFormat { get; init; }
    public required string RecoveredCountFormat { get; init; }
    public required string RepairedFormat { get; init; }
    public required string UpdatedFormat { get; init; }
    public required string PendingTransaction { get; init; }
    public required string InvalidCommand { get; init; }
    public required string ExternalManagerRequired { get; init; }
    public required string MissingManager { get; init; }
    public required string BusyOperation { get; init; }
    public required string LegacyPlanFormat { get; init; }
    public required string RegistrationFailedFormat { get; init; }
    public required string SelfUpdateFailedFormat { get; init; }

    public string Format(string template, params object[] values) =>
        string.Format(CultureInfo.GetCultureInfo(Language), template, values);

    public static ToolText For(string? language)
    {
        var resolved = AppLanguage.Resolve(language);
        return Cache.GetOrAdd(resolved, id =>
        {
            using var stream = typeof(ToolText).Assembly.GetManifestResourceStream(
                "ResourceManager.Shared.Localization.ToolLocales." + id + ".json")
                ?? throw new InvalidOperationException("Missing tool locale: " + id);
            return (JsonSerializer.Deserialize<ToolText>(stream)
                ?? throw new InvalidOperationException("Invalid tool locale: " + id)) with { Language = id };
        });
    }

    public static ToolText FromInstallRoot(string? root) =>
        For(root is null ? AppLanguage.System : AppLanguage.ReadPreference(Path.Combine(root, "Config", "app-settings.json")));
}
