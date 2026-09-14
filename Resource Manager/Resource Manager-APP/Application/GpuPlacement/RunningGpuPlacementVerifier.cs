namespace ResourceManager.App.Application.GpuPlacement;

public sealed record RunningGpuPlacementAdapterEvidence(
    int GpuIndex,
    double GpuUsagePercent,
    double VramUsedPercent);

public static class RunningGpuPlacementVerifier
{
    public static bool IsVerified(
        int expectedGpuIndex,
        IReadOnlyList<RunningGpuPlacementAdapterEvidence> evidence)
    {
        if (expectedGpuIndex < 0 || evidence.Count == 0)
        {
            return false;
        }

        var dominant = evidence
            .Where(static item => item.GpuIndex >= 0)
            .OrderByDescending(static item => Sanitize(item.GpuUsagePercent))
            .ThenByDescending(static item => Sanitize(item.VramUsedPercent))
            .ThenBy(static item => item.GpuIndex)
            .FirstOrDefault();
        return dominant is not null
            && dominant.GpuIndex == expectedGpuIndex
            && (Sanitize(dominant.GpuUsagePercent) > 0
                || Sanitize(dominant.VramUsedPercent) > 0);
    }

    private static double Sanitize(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    }
}
