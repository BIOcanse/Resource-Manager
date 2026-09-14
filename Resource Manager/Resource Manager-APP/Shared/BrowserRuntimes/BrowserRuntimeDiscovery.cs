using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ResourceManager.Shared.BrowserRuntimes;

public static class BrowserRuntimeKinds
{
    public const string WebView2Runtime = "WebView2Runtime";
    public const string ChromiumBrowser = "ChromiumBrowser";
    public const string GeckoBrowser = "GeckoBrowser";
}

public sealed record BrowserRuntimeCandidate(
    string Id,
    string Name,
    string Kind,
    string Version,
    string ExecutablePath,
    string RuntimeDirectory,
    string Source,
    bool NativeWebView2Compatible,
    bool Selected = false);

public sealed record BrowserRuntimeDiscoveryResult(
    BrowserRuntimeCandidate? SharedRuntime,
    BrowserRuntimeCandidate? BrowserFallback,
    IReadOnlyList<BrowserRuntimeCandidate> Candidates,
    DateTimeOffset CapturedAt);

public static class BrowserRuntimeDiscovery
{
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private static readonly string[] ChromiumEvidenceTokens =
    [
        "chrome",
        "chromium",
        "msedge",
        "edge",
        "brave",
        "vivaldi",
        "opera",
        "arc",
        "yandex",
        "thorium",
        "centbrowser",
        "coc coc",
        "avast secure browser",
        "ccleaner browser",
        "360se",
        "360chrome",
        "360 browser",
        "360安全浏览器",
        "360极速浏览器",
        "qqbrowser",
        "qq browser",
        "qq浏览器",
        "sogouexplorer",
        "sogou browser",
        "搜狗浏览器",
        "quark",
        "夸克",
        "2345explorer",
        "2345 browser",
        "maxthon",
        "傲游",
        "liebao",
        "猎豹浏览器",
        "ucbrowser",
        "baidubrowser"
    ];
    private static readonly string[] GeckoEvidenceTokens =
    [
        "firefox",
        "mozilla firefox",
        "waterfox",
        "librewolf",
        "floorp",
        "zen browser"
    ];

    public static BrowserRuntimeDiscoveryResult Discover(string? packageRoot = null)
    {
        var candidates = new Dictionary<string, BrowserRuntimeCandidate>(StringComparer.OrdinalIgnoreCase);
        AddManagedWebView2Runtimes(candidates, packageRoot);
        AddInstalledWebView2Runtimes(candidates);
        AddRegisteredBrowsers(candidates);
        AddRegisteredStartMenuBrowsers(candidates);
        AddKnownBrowsers(candidates);
        return BrowserRuntimeSelection.Select(candidates.Values);
    }

    private static void AddManagedWebView2Runtimes(
        IDictionary<string, BrowserRuntimeCandidate> candidates,
        string? packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return;
        }

        string root;
        try
        {
            root = Path.Combine(Path.GetFullPath(packageRoot), "Dependencies", "shared-webview2-runtime");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var runtimeDirectory in EnumerateRuntimeDirectories(root))
        {
            AddWebView2RuntimeCandidate(
                candidates,
                runtimeDirectory,
                "Resource Manager 共享 WebView2 Runtime",
                version: null,
                source: "Managed");
        }
    }

    private static IEnumerable<string> EnumerateRuntimeDirectories(string root)
    {
        if (IsWebView2RuntimeDirectory(root))
        {
            yield return root;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (IsWebView2RuntimeDirectory(child))
            {
                yield return child;
            }
        }
    }

    private static void AddInstalledWebView2Runtimes(
        IDictionary<string, BrowserRuntimeCandidate> candidates)
    {
        AddInstalledWebView2Runtime(candidates, RegistryHive.LocalMachine, RegistryView.Registry32, "SystemMachine");
        AddInstalledWebView2Runtime(candidates, RegistryHive.LocalMachine, RegistryView.Registry64, "SystemMachine");
        AddInstalledWebView2Runtime(candidates, RegistryHive.CurrentUser, RegistryView.Registry32, "SystemUser");
        AddInstalledWebView2Runtime(candidates, RegistryHive.CurrentUser, RegistryView.Registry64, "SystemUser");
    }

    private static void AddInstalledWebView2Runtime(
        IDictionary<string, BrowserRuntimeCandidate> candidates,
        RegistryHive hive,
        RegistryView view,
        string source)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}");
            if (key is null)
            {
                return;
            }

            var version = CleanText(key.GetValue("pv") as string);
            if (version is null || version == "0.0.0.0")
            {
                return;
            }

            var location = CleanPath(key.GetValue("location") as string);
            var runtimeDirectory = ResolveInstalledRuntimeDirectory(location, version);
            if (runtimeDirectory is null)
            {
                return;
            }

            AddWebView2RuntimeCandidate(
                candidates,
                runtimeDirectory,
                CleanText(key.GetValue("name") as string) ?? "Microsoft Edge WebView2 Runtime",
                version,
                source);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
        }
    }

    private static string? ResolveInstalledRuntimeDirectory(string? location, string version)
    {
        var roots = new List<string?>
        {
            location,
            string.IsNullOrWhiteSpace(location) ? null : Path.Combine(location, version),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft",
                "EdgeWebView",
                "Application",
                version)
        };

        return roots
            .Select(CleanPath)
            .FirstOrDefault(static path => path is not null && IsWebView2RuntimeDirectory(path));
    }

    private static void AddWebView2RuntimeCandidate(
        IDictionary<string, BrowserRuntimeCandidate> candidates,
        string runtimeDirectory,
        string name,
        string? version,
        string source)
    {
        if (!IsWebView2RuntimeDirectory(runtimeDirectory))
        {
            return;
        }

        var executablePath = Path.Combine(runtimeDirectory, "msedgewebview2.exe");
        var resolvedVersion = CleanText(version)
            ?? ReadVersion(executablePath)
            ?? Path.GetFileName(runtimeDirectory);
        AddCandidate(candidates, new BrowserRuntimeCandidate(
            StableId("webview2", executablePath),
            name,
            BrowserRuntimeKinds.WebView2Runtime,
            resolvedVersion,
            executablePath,
            runtimeDirectory,
            source,
            NativeWebView2Compatible: true));
    }

    private static bool IsWebView2RuntimeDirectory(string path)
    {
        try
        {
            return Directory.Exists(path)
                && File.Exists(Path.Combine(path, "msedgewebview2.exe"))
                && File.Exists(Path.Combine(path, "msedge.dll"))
                && File.Exists(Path.Combine(path, "icudtl.dat"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return false;
        }
    }

    private static void AddRegisteredBrowsers(
        IDictionary<string, BrowserRuntimeCandidate> candidates)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in appPaths.GetSubKeyNames())
                    {
                        using var appPath = appPaths.OpenSubKey(subKeyName);
                        AddBrowserCandidate(
                            candidates,
                            appPath?.GetValue(null) as string,
                            "BrowserRegistration",
                            fallbackName: Path.GetFileNameWithoutExtension(subKeyName));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                }
            }
        }
    }

    private static void AddRegisteredStartMenuBrowsers(
        IDictionary<string, BrowserRuntimeCandidate> candidates)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var browserClients = baseKey.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                    if (browserClients is null)
                    {
                        continue;
                    }

                    foreach (var clientKeyName in browserClients.GetSubKeyNames())
                    {
                        using var client = browserClients.OpenSubKey(clientKeyName);
                        using var command = client?.OpenSubKey(@"shell\open\command");
                        AddBrowserCandidate(
                            candidates,
                            command?.GetValue(null) as string,
                            "BrowserRegistration",
                            CleanText(client?.GetValue(null) as string) ?? clientKeyName);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                }
            }
        }
    }

    private static void AddKnownBrowsers(
        IDictionary<string, BrowserRuntimeCandidate> candidates)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var knownPaths = new (string Name, string Path)[]
        {
            ("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
            ("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")),
            ("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")),
            ("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")),
            ("Chromium", Path.Combine(programFiles, "Chromium", "Application", "chrome.exe")),
            ("Chromium", Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")),
            ("Brave", Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            ("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            ("Vivaldi", Path.Combine(programFiles, "Vivaldi", "Application", "vivaldi.exe")),
            ("Vivaldi", Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe")),
            ("Opera", Path.Combine(localAppData, "Programs", "Opera", "launcher.exe")),
            ("Opera GX", Path.Combine(localAppData, "Programs", "Opera GX", "launcher.exe")),
            ("Arc", Path.Combine(localAppData, "Programs", "Arc", "Arc.exe")),
            ("360 安全浏览器", Path.Combine(programFilesX86, "360", "360se6", "Application", "360se.exe")),
            ("360 安全浏览器", Path.Combine(programFiles, "360", "360se6", "Application", "360se.exe")),
            ("360 安全浏览器", Path.Combine(appData, "360se6", "Application", "360se.exe")),
            ("360 极速浏览器", Path.Combine(programFilesX86, "360", "360Chrome", "Chrome", "Application", "360chrome.exe")),
            ("360 极速浏览器", Path.Combine(programFiles, "360", "360Chrome", "Chrome", "Application", "360chrome.exe")),
            ("360 极速浏览器", Path.Combine(localAppData, "360Chrome", "Chrome", "Application", "360chrome.exe")),
            ("360 极速浏览器 X", Path.Combine(localAppData, "360ChromeX", "Chrome", "Application", "360chrome.exe")),
            ("QQ 浏览器", Path.Combine(programFilesX86, "Tencent", "QQBrowser", "QQBrowser.exe")),
            ("QQ 浏览器", Path.Combine(programFiles, "Tencent", "QQBrowser", "QQBrowser.exe")),
            ("QQ 浏览器", Path.Combine(localAppData, "Tencent", "QQBrowser", "Application", "QQBrowser.exe")),
            ("搜狗高速浏览器", Path.Combine(programFilesX86, "SogouExplorer", "SogouExplorer.exe")),
            ("搜狗高速浏览器", Path.Combine(programFiles, "SogouExplorer", "SogouExplorer.exe")),
            ("搜狗高速浏览器", Path.Combine(localAppData, "SogouExplorer", "SogouExplorer.exe")),
            ("夸克浏览器", Path.Combine(localAppData, "Programs", "Quark", "quark.exe")),
            ("夸克浏览器", Path.Combine(localAppData, "Quark", "quark.exe")),
            ("2345 浏览器", Path.Combine(programFilesX86, "2345Soft", "2345Explorer", "2345Explorer.exe")),
            ("2345 浏览器", Path.Combine(programFiles, "2345Soft", "2345Explorer", "2345Explorer.exe")),
            ("Mozilla Firefox", Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe")),
            ("Mozilla Firefox", Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")),
            ("Waterfox", Path.Combine(programFiles, "Waterfox", "waterfox.exe")),
            ("LibreWolf", Path.Combine(programFiles, "LibreWolf", "librewolf.exe")),
            ("Floorp", Path.Combine(programFiles, "Ablaze Floorp", "floorp.exe")),
            ("Zen Browser", Path.Combine(localAppData, "Programs", "Zen Browser", "zen.exe"))
        };

        foreach (var (name, path) in knownPaths)
        {
            AddBrowserCandidate(candidates, path, "KnownInstall", name);
        }
    }

    private static void AddBrowserCandidate(
        IDictionary<string, BrowserRuntimeCandidate> candidates,
        string? rawPath,
        string source,
        string? fallbackName)
    {
        var executablePath = CleanExecutablePath(rawPath);
        if (executablePath is null
            || !File.Exists(executablePath)
            || Path.GetFileName(executablePath).Contains("webview2", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FileVersionInfo? versionInfo = null;
        try
        {
            versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        var evidence = string.Join(' ',
            Path.GetFileName(executablePath),
            fallbackName,
            versionInfo?.ProductName,
            executablePath);
        var kind = ResolveBrowserKind(executablePath, evidence);
        if (kind is null)
        {
            return;
        }

        var name = CleanText(versionInfo?.ProductName)
            ?? CleanText(fallbackName)
            ?? Path.GetFileNameWithoutExtension(executablePath);
        var version = CleanText(versionInfo?.ProductVersion)
            ?? CleanText(versionInfo?.FileVersion)
            ?? "0.0.0.0";
        var runtimeDirectory = Path.GetDirectoryName(executablePath);
        if (runtimeDirectory is null)
        {
            return;
        }

        AddCandidate(candidates, new BrowserRuntimeCandidate(
            StableId("browser", executablePath),
            name,
            kind,
            version,
            executablePath,
            runtimeDirectory,
            source,
            NativeWebView2Compatible: false));
    }

    private static void AddCandidate(
        IDictionary<string, BrowserRuntimeCandidate> candidates,
        BrowserRuntimeCandidate candidate)
    {
        var key = CleanPath(candidate.ExecutablePath);
        if (key is not null)
        {
            candidates.TryAdd(key, candidate);
        }
    }

    internal static string? ResolveBrowserKind(string executablePath, string evidence)
    {
        var isKnownGeckoBrowser = GeckoEvidenceTokens.Any(token =>
            BrowserRuntimeEvidence.ContainsBoundedToken(evidence, token));
        if (isKnownGeckoBrowser && HasGeckoBrowserLayout(executablePath))
        {
            return BrowserRuntimeKinds.GeckoBrowser;
        }

        return ChromiumEvidenceTokens.Any(token =>
                BrowserRuntimeEvidence.ContainsBoundedToken(evidence, token))
            || HasChromiumBrowserLayout(executablePath)
                ? BrowserRuntimeKinds.ChromiumBrowser
                : null;
    }

    private static bool HasChromiumBrowserLayout(string executablePath)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (executableDirectory is null)
        {
            return false;
        }

        try
        {
            if (IsChromiumBrowserRuntimeDirectory(executableDirectory))
            {
                return true;
            }

            return Directory.EnumerateDirectories(executableDirectory)
                .Where(static path => Version.TryParse(Path.GetFileName(path), out _))
                .Take(8)
                .Any(IsChromiumBrowserRuntimeDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsChromiumBrowserRuntimeDirectory(string directory)
        => File.Exists(Path.Combine(directory, "icudtl.dat"))
            && Directory.Exists(Path.Combine(directory, "Locales"))
            && (File.Exists(Path.Combine(directory, "chrome.dll"))
                || File.Exists(Path.Combine(directory, "msedge.dll")));

    private static bool HasGeckoBrowserLayout(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null)
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory, "xul.dll"))
                && File.Exists(Path.Combine(directory, "mozglue.dll"))
                && File.Exists(Path.Combine(directory, "omni.ja"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return false;
        }
    }

    private static string StableId(string prefix, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 5)).ToLowerInvariant()}";
    }

    private static string? ReadVersion(string executablePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            return CleanText(versionInfo.ProductVersion) ?? CleanText(versionInfo.FileVersion);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CleanExecutablePath(string? value)
    {
        var cleaned = CleanText(value);
        if (cleaned is null)
        {
            return null;
        }

        cleaned = Environment.ExpandEnvironmentVariables(cleaned);
        if (cleaned.StartsWith('"'))
        {
            var closingQuote = cleaned.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                cleaned = cleaned[1..closingQuote];
            }
        }
        else if (!File.Exists(cleaned))
        {
            var executableEnd = cleaned.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                cleaned = cleaned[..(executableEnd + 4)];
            }
        }

        return CleanPath(cleaned);
    }

    private static string? CleanPath(string? value)
    {
        var cleaned = CleanText(value)?.Trim('"');
        if (cleaned is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(cleaned))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? CleanText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class BrowserRuntimeEvidence
{
    public static bool ContainsBoundedToken(string evidence, string token)
    {
        var searchStart = 0;
        while (searchStart < evidence.Length)
        {
            var index = evidence.IndexOf(token, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + token.Length;
            var startsAtBoundary = index == 0 || !char.IsLetterOrDigit(evidence[index - 1]);
            var endsAtBoundary = end == evidence.Length || !char.IsLetterOrDigit(evidence[end]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            searchStart = index + 1;
        }

        return false;
    }
}
