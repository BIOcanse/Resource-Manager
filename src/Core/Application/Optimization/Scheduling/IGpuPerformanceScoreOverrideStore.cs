using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization.Scheduling;

public interface IGpuPerformanceScoreOverrideStore
{
    IReadOnlyDictionary<string, double> LoadScores();

    GpuPerformanceScoreOverrideResult Load();

    GpuPerformanceScoreOverrideResult Save(GpuPerformanceScoreOverrideRequest request);

    GpuPerformanceScoreOverrideResult Reset();
}
