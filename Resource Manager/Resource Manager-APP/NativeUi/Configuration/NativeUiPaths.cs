namespace ResourceManager.NativeUi;

internal static class NativeUiPaths
{
    private static readonly Lazy<string> PackageRootPath = new(ResolvePackageRoot);

    public static string PackageRoot => PackageRootPath.Value;

    public static string WebViewUserDataFolder =>
        Path.Combine(PackageRoot, "Config", "WebView2");

    public static string AppSettingsPath =>
        Path.Combine(PackageRoot, "Config", "app-settings.json");

    public static string LoopbackApiTokenPath =>
        Path.Combine(PackageRoot, "Config", "Runtime", "loopback-api-token");

    public static string WebViewStartupFailurePath =>
        Path.Combine(PackageRoot, "Config", "Diagnostics", "NativeUiStartup", "webview-failure.json");

    public static string DiagnosticsFolder =>
        Path.Combine(PackageRoot, "Config", "Diagnostics");

    private static string ResolvePackageRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Config"))
                && (Directory.Exists(Path.Combine(current.FullName, "Resource Manager-APP"))
                    || Directory.Exists(Path.Combine(current.FullName, "Bin"))
                    || Directory.Exists(Path.Combine(current.FullName, "Dependencies"))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
