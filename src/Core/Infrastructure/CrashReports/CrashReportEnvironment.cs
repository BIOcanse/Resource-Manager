using System.Reflection;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.CrashReports;

/// <summary>The facts of this installation the crash-dump feature reads: the package root (dumps go under
/// <c>UserData/&lt;folder_name&gt;</c>), this build's version (it decides the release channel) and the Application
/// log source the service logs under (the service log of a package).</summary>
public sealed record CrashReportEnvironment(string PackageRoot, string BuildVersion, string ServiceLogSource)
{
    public static CrashReportEnvironment Create(IHostEnvironment environment)
        => new(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), ResolveBuildVersion(),
            // UseWindowsService logs to the Application log under the application name.
            environment.ApplicationName);

    public string DumpFolder(CrashReportOptions options)
        => Path.GetFullPath(Path.Combine(PackageRoot, "UserData", options.FolderName));

    public string Channel(CrashReportOptions options)
        => BuildVersion.Contains(options.BetaVersionMarker, StringComparison.OrdinalIgnoreCase)
            ? ReleaseChannels.Beta : ReleaseChannels.Stable;

    // The stored mode with "default" resolved by the release channel, and which of the two decided it.
    public (string Effective, string Source, string Default, string Channel) ResolveMode(
        AppCrashDumpSettings settings, CrashReportOptions options)
    {
        var channel = Channel(options);
        var channelDefault = channel == ReleaseChannels.Beta ? options.BetaDefaultMode : options.StableDefaultMode;
        return settings.Mode == AppCrashDumpModes.Default
            ? (channelDefault, CrashDumpModeSources.ChannelDefault, channelDefault, channel)
            : (settings.Mode, CrashDumpModeSources.User, channelDefault, channel);
    }

    private static string ResolveBuildVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashReportEnvironment).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        // The SDK appends "+<commit>" to the informational version; the product version is the part before it.
        var metadata = version.IndexOf('+');
        return metadata >= 0 ? version[..metadata] : version;
    }
}
