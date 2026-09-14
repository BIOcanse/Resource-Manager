using Microsoft.Win32;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Software;

public sealed class WindowsInstalledSoftwareInventory : IInstalledSoftwareInventory
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string? WindowsRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private IReadOnlyList<InstalledSoftwareEntry>? snapshot;

    public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: true, cancellationToken);
    }

    private async Task<IReadOnlyList<InstalledSoftwareEntry>> LoadAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref snapshot);
        if (!forceRefresh && cached is not null)
        {
            return cached;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref snapshot);
            if (!forceRefresh && cached is not null)
            {
                return cached;
            }

            var refreshed = ScanInstalledSoftware(cancellationToken);
            Volatile.Write(ref snapshot, refreshed);
            return refreshed;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static IReadOnlyList<InstalledSoftwareEntry> ScanInstalledSoftware(
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, InstalledSoftwareEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in InventoryRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadUninstallRoot(root.Hive, root.View, records, cancellationToken);
        }

        return NormalizeSharedInstallRoots(records.Values)
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Publisher, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<RegistryInventoryRoot> InventoryRoots()
    {
        yield return new RegistryInventoryRoot(RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return new RegistryInventoryRoot(RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return new RegistryInventoryRoot(RegistryHive.CurrentUser, RegistryView.Registry64);
        yield return new RegistryInventoryRoot(RegistryHive.CurrentUser, RegistryView.Registry32);
    }

    private static void ReadUninstallRoot(
        RegistryHive hive,
        RegistryView view,
        Dictionary<string, InstalledSoftwareEntry> records,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey is null)
                {
                    continue;
                }

                var entry = ReadEntry(hive, view, subKeyName, appKey);
                if (entry is null)
                {
                    continue;
                }

                var key = BuildDeduplicationKey(entry);
                if (!records.TryGetValue(key, out var existing) || PreferEntry(entry, existing))
                {
                    records[key] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
    }

    private static InstalledSoftwareEntry? ReadEntry(
        RegistryHive hive,
        RegistryView view,
        string subKeyName,
        RegistryKey appKey)
    {
        var name = ReadString(appKey, "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (ReadInt(appKey, "SystemComponent") == 1)
        {
            return null;
        }

        var releaseType = ReadString(appKey, "ReleaseType");
        if (releaseType is not null
            && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var registryPath = $@"{hive}\{view}\{UninstallSubKey}\{subKeyName}";
        var publisher = ReadString(appKey, "Publisher");
        var version = ReadString(appKey, "DisplayVersion");
        var installLocation = NormalizeInstallLocation(ReadString(appKey, "InstallLocation"));
        var displayIcon = ReadString(appKey, "DisplayIcon");
        var uninstallString = ReadString(appKey, "QuietUninstallString")
            ?? ReadString(appKey, "UninstallString");
        var rootPaths = BuildRootPaths(installLocation, uninstallString, displayIcon);
        if (!HasValidRegisteredLocation(rootPaths))
        {
            return null;
        }

        return new InstalledSoftwareEntry(
            $"windows-installed:{NormalizeId(registryPath)}",
            name.Trim(),
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            installLocation,
            rootPaths,
            string.IsNullOrWhiteSpace(uninstallString) ? null : uninstallString.Trim(),
            registryPath);
    }

    internal static bool HasValidRegisteredLocation(IReadOnlyList<string> rootPaths)
    {
        return rootPaths.Count == 0
            || rootPaths.Any(static path => Directory.Exists(path) || File.Exists(path));
    }

    private static IReadOnlyList<InstalledSoftwareEntry> NormalizeSharedInstallRoots(IEnumerable<InstalledSoftwareEntry> entries)
    {
        var normalizedEntries = entries.ToArray();
        var sharedInstallLocations = normalizedEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.InstallLocation))
            .GroupBy(static entry => NormalizePath(entry.InstallLocation), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Key is not null && group.Count() > 1 && group.Any(IsLauncherEntry))
            .Select(static group => group.Key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sharedInstallLocations.Count == 0)
        {
            return normalizedEntries;
        }

        return normalizedEntries
            .Select(entry =>
            {
                var installLocation = NormalizePath(entry.InstallLocation);
                if (installLocation is null
                    || !sharedInstallLocations.Contains(installLocation)
                    || IsLauncherEntry(entry))
                {
                    return entry;
                }

                var roots = entry.RootPaths
                    .Where(root => !installLocation.Equals(NormalizePath(root), StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return entry with { RootPaths = roots };
            })
            .ToArray();
    }

    private static bool IsLauncherEntry(InstalledSoftwareEntry entry)
    {
        return LooksLikeLauncherName(entry.Name)
            || LooksLikeLauncherCommand(entry.UninstallString);
    }

    private static bool LooksLikeLauncherName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("启动器", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Launcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLauncherCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var executable = ExtractExecutablePathFromCommandLine(value);
        var fileName = Path.GetFileNameWithoutExtension(executable);
        return fileName is not null
            && (fileName.Equals("launcher", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            && !value.Contains("--uninstall_game", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        return key.GetValue(name) as string;
    }

    private static int? ReadInt(RegistryKey key, string name)
    {
        return key.GetValue(name) switch
        {
            int value => value,
            string text when int.TryParse(text, out var value) => value,
            _ => null
        };
    }

    private static string? NormalizeInstallLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static IReadOnlyList<string> BuildRootPaths(string? installLocation, string? uninstallString, string? displayIcon)
    {
        var roots = new List<string>();
        AddRoot(roots, installLocation);

        var uninstallExecutable = ExtractExecutablePathFromCommandLine(uninstallString);
        var uninstallRoot = InferRootFromExecutablePath(uninstallExecutable);
        AddRoot(roots, uninstallRoot);

        var displayIconExecutable = ExtractExecutablePathFromCommandLine(displayIcon);
        var displayIconRoot = InferRootFromExecutablePath(displayIconExecutable);
        AddRoot(roots, displayIconRoot);

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null || IsWindowsOwnedPath(normalized))
        {
            return;
        }

        roots.Add(normalized);
    }

    private static string? ExtractExecutablePathFromCommandLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var command = Environment.ExpandEnvironmentVariables(value.Trim());
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        foreach (var extension in new[] { ".exe", ".msi", ".bat", ".cmd" })
        {
            var extensionIndex = command.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex <= 0)
            {
                continue;
            }

            return command[..(extensionIndex + extension.Length)].Trim();
        }

        return null;
    }

    private static string? InferRootFromExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath.Trim().Trim('"')))
        {
            return null;
        }

        var normalized = NormalizePath(executablePath);
        if (normalized is null)
        {
            return null;
        }

        var root = Path.HasExtension(normalized)
            ? Path.GetDirectoryName(normalized)
            : normalized;
        return NormalizePath(root);
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWindowsOwnedPath(string path)
    {
        return WindowsRoot is not null
            && (path.Equals(WindowsRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(WindowsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string BuildDeduplicationKey(InstalledSoftwareEntry entry)
    {
        var location = NormalizeKeyPart(entry.InstallLocation);
        var uninstall = NormalizeKeyPart(entry.UninstallString);
        var locationKey = !string.IsNullOrWhiteSpace(location) || !string.IsNullOrWhiteSpace(uninstall)
            ? $"{location}|{uninstall}"
            : "";

        return string.Join(
            "|",
            NormalizeKeyPart(entry.Name),
            NormalizeKeyPart(entry.Publisher),
            NormalizeKeyPart(entry.Version),
            locationKey);
    }

    private static bool PreferEntry(InstalledSoftwareEntry candidate, InstalledSoftwareEntry existing)
    {
        var candidateScore = EntryScore(candidate);
        var existingScore = EntryScore(existing);
        return candidateScore > existingScore;
    }

    private static int EntryScore(InstalledSoftwareEntry entry)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            score += 2;
        }

        if (entry.RegistryPath.Contains("LocalMachine", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        if (entry.RegistryPath.Contains("Registry64", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static string NormalizeKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().Trim('"').TrimEnd('\\', '/').ToLowerInvariant();
    }

    private sealed record RegistryInventoryRoot(RegistryHive Hive, RegistryView View);
}
