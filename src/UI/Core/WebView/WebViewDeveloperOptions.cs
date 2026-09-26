namespace ResourceManager.NativeUi.WebView;

internal static class WebViewDeveloperOptions
{
    public const string RemoteDebuggingPortEnvironmentVariable =
        "RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT";
    public const string FrontendPerformanceCapacityEnvironmentVariable =
        "RESOURCE_MANAGER_WEBVIEW_FRONTEND_PERFORMANCE_CAPACITY";

    public static string AddLoopbackRemoteDebugging(
        string baseArguments,
        string? rawPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseArguments);
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            return baseArguments;
        }
        if (!int.TryParse(
                rawPort,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var port)
            || port is < 1024 or > 65535)
        {
            throw new InvalidOperationException(
                $"{RemoteDebuggingPortEnvironmentVariable} must be a port from 1024 through 65535.");
        }

        return $"{baseArguments} --remote-debugging-address=127.0.0.1 --remote-debugging-port={port}";
    }

    public static string? CreateFrontendPerformanceBootstrapScript(string? rawCapacity)
    {
        if (string.IsNullOrWhiteSpace(rawCapacity))
        {
            return null;
        }
        if (!int.TryParse(
                rawCapacity,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var capacity)
            || capacity is < 256 or > 100_000)
        {
            throw new InvalidOperationException(
                $"{FrontendPerformanceCapacityEnvironmentVariable} must be an integer from 256 through 100000.");
        }

        return $$"""
            Object.defineProperty(globalThis, "__resourceManagerFrontendPerformanceBootstrap", {
              configurable: true,
              enumerable: false,
              writable: false,
              value: Object.freeze({ schemaVersion: 1, enabled: true, capacity: {{capacity}} })
            });
            """;
    }
}
