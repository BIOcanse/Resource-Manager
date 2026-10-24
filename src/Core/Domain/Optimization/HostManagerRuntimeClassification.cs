namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerRuntimeStates
{
    public const string Unknown = "unknown";
    public const string ForegroundFocused = "foreground_focused";
    public const string ForegroundUnfocused = "foreground_unfocused";
    public const string BackgroundWindow = "background_window";
    public const string TrayOnly = "tray_only";
    public const string BackgroundProcess = "background_process";
    public const string NotRunning = "not_running";
}

public sealed record HostManagerIdleCapacitySnapshot(
    double MemoryFreeRatio,
    bool MemoryFreeRatioCurrent);
