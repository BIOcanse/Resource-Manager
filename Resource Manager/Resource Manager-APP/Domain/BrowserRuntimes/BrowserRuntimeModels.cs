namespace ResourceManager.App.Domain.BrowserRuntimes;

public sealed record BrowserRuntimeEntry(
    string Id,
    string Name,
    string Kind,
    string Version,
    string ExecutablePath,
    string RuntimeDirectory,
    string Source,
    bool NativeWebView2Compatible,
    bool Selected);

public sealed record BrowserRuntimeSnapshot(
    BrowserRuntimeEntry? SharedRuntime,
    BrowserRuntimeEntry? BrowserFallback,
    IReadOnlyList<BrowserRuntimeEntry> Candidates,
    string InstallComponentId,
    DateTimeOffset CapturedAt);
