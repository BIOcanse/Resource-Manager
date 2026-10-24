using System.Security;
using Microsoft.Win32;
using ResourceManager.App.Application.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed class WindowsGameListSource(ILogger<WindowsGameListSource> logger) : IWindowsGameListSource
{
    public IReadOnlyList<string> ReadExecutablePaths()
    {
        var paths = new List<string>();
        if (!OperatingSystem.IsWindows()) return paths;

        try
        {
            // LocalSystem's HKCU is not the interactive user's hive. Only inspect
            // already loaded user hives; never load profiles or inspect _Classes.
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            foreach (var sid in users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                    || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var children = users.OpenSubKey($@"{sid}\System\GameConfigStore\Children");
                    if (children is null) continue;
                    foreach (var childName in children.GetSubKeyNames())
                    {
                        try
                        {
                            using var child = children.OpenSubKey(childName);
                            if (child?.GetValue("MatchedExeFullPath") is string path
                                && !string.IsNullOrWhiteSpace(path)) paths.Add(path);
                        }
                        catch (Exception error) when (IsRegistryReadError(error))
                        {
                            logger.LogWarning(error, "Cannot read Windows game list entry {Sid}/{Entry}", sid, childName);
                        }
                    }
                }
                catch (Exception error) when (IsRegistryReadError(error))
                {
                    logger.LogWarning(error, "Cannot read Windows game list for loaded user {Sid}", sid);
                }
            }
        }
        catch (Exception error) when (IsRegistryReadError(error))
        {
            logger.LogWarning(error, "Cannot enumerate loaded user hives for the Windows game list");
        }
        return paths;
    }

    private static bool IsRegistryReadError(Exception error)
        => error is IOException or UnauthorizedAccessException or SecurityException;
}
