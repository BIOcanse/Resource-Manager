using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuCorePerformanceOverrideStore
{
    CpuPerformanceOverrides LoadConfiguration(string cpuName);

    IReadOnlyDictionary<int, double> LoadScores(string cpuName);

    void SaveBaselineRatio(double ratio);

    void ResetBaselineRatio();

    CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request);

    CpuCorePerformanceOverrideResult Reset(string cpuName);
}
