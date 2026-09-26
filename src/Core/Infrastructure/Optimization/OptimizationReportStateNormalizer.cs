using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class OptimizationReportStateNormalizer
{
    public static OptimizationReportStateDocument Normalize(OptimizationReportStateDocument? document)
    {
        if (document is null)
        {
            return Empty();
        }

        return new OptimizationReportStateDocument(
            2,
            document.Observations
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.OrderByDescending(static item => item.LastObservedAt).First())
                .OrderByDescending(static item => item.LastObservedAt)
                .ToArray());
    }

    public static OptimizationReportStateDocument Empty() => new(2, []);
}
