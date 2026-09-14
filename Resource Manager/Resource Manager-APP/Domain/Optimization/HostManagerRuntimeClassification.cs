namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerRuntimeStates
{
    public const string Unknown = "Unknown";
    public const string ForegroundFocused = "ForegroundFocused";
    public const string ForegroundUnfocused = "ForegroundUnfocused";
    public const string BackgroundWindow = "BackgroundWindow";
    public const string TrayOnly = "TrayOnly";
    public const string BackgroundProcess = "BackgroundProcess";
    public const string NotRunning = "NotRunning";
}

public sealed record HostManagerIdleCapacitySnapshot(
    double MemoryFreeRatio,
    bool MemoryFreeRatioCurrent);
