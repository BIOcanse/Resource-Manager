using System.Security;
using Microsoft.Win32;
using ResourceManager.App.Application.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed class WindowsGameListSource : IWindowsGameListSource
{
    private readonly ILogger<WindowsGameListSource> logger;
    private readonly Func<string, string[]> readChildren;
    private readonly Func<string, string?> readExecutablePath;
    private readonly object gate = new();
    private Dictionary<string, string> lastFailures = new(StringComparer.OrdinalIgnoreCase);

    public WindowsGameListSource(ILogger<WindowsGameListSource> logger)
        : this(logger, ReadChildren, ReadExecutablePath) { }

    internal WindowsGameListSource(ILogger<WindowsGameListSource> logger,
        Func<string, string[]> readChildren, Func<string, string?> readExecutablePath)
    {
        this.logger = logger;
        this.readChildren = readChildren;
        this.readExecutablePath = readExecutablePath;
    }

    public IReadOnlyList<string> ReadExecutablePaths()
    {
        if (!OperatingSystem.IsWindows()) return [];
        lock (gate)
        {
            var paths = new List<string>();
            var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // LocalSystem's HKCU is not the interactive user's hive. Only inspect
            // already loaded user hives; never load profiles or inspect _Classes.
            foreach (var sid in Read(string.Empty, () => readChildren(string.Empty), [], failures))
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                    || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                var childrenPath = $@"{sid}\System\GameConfigStore\Children";
                foreach (var childName in Read(childrenPath, () => readChildren(childrenPath), [], failures))
                {
                    var childPath = $@"{childrenPath}\{childName}";
                    var path = Read(childPath, () => readExecutablePath(childPath), null, failures);
                    if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                }
            }
            lastFailures = failures;
            return paths;
        }
    }

    private T Read<T>(string path, Func<T> read, T empty, Dictionary<string, string> failures)
    {
        try { return read(); }
        catch (Exception error) when (IsRegistryReadError(error))
        {
            var fingerprint = $"{error.GetType().FullName}|{error.HResult}|{error.Message}";
            failures[path] = fingerprint;
            if (!lastFailures.TryGetValue(path, out var previous) || previous != fingerprint)
                logger.LogWarning(error, "Cannot read Windows game list registry key HKEY_USERS\\{RegistryPath}", path);
            return empty;
        }
    }

    private static string[] ReadChildren(string path)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        if (path.Length == 0) return users.GetSubKeyNames();
        using var key = users.OpenSubKey(path);
        return key?.GetSubKeyNames() ?? [];
    }

    private static string? ReadExecutablePath(string path)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using var key = users.OpenSubKey(path);
        return key?.GetValue("MatchedExeFullPath") as string;
    }

    private static bool IsRegistryReadError(Exception error)
        => error is IOException or UnauthorizedAccessException or SecurityException;
}
