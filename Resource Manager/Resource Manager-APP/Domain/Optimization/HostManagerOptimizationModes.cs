using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerOptimizationModes
{
    public static string Normalize(string? mode)
    {
        return mode switch
        {
            AppOptimizationModes.MemoryOnly => AppOptimizationModes.MemoryOnly,
            AppOptimizationModes.Smart => AppOptimizationModes.Smart,
            _ => AppOptimizationModes.Normal
        };
    }
}
