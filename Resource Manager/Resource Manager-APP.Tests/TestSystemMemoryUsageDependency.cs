using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

internal static class TestSystemMemoryUsageDependency
{
    internal static SystemMemoryUsageDependency Create(
        ulong denominatorBytes = 16UL * 1024 * 1024 * 1024,
        ulong sourceGeneration = 1,
        long observedAtUtcTicks = 1,
        ulong workspaceIdentity = 1,
        ulong configurationGeneration = 1,
        ulong catalogGeneration = 1,
        ulong committedGeneration = 0,
        long readyUntilUtcTicks = 0)
        => new(
            SamplingDatasetIds.SystemMemoryUsage,
            denominatorBytes,
            sourceGeneration,
            observedAtUtcTicks,
            workspaceIdentity,
            configurationGeneration,
            catalogGeneration,
            committedGeneration == 0
                ? sourceGeneration
                : committedGeneration,
            readyUntilUtcTicks);
}
