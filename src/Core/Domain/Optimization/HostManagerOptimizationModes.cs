using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerOptimizationModes
{
    public static string Normalize(string? mode)
        => AppOptimizationModes.Normalize(mode);
}
