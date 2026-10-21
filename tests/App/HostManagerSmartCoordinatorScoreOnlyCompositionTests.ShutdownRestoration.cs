using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShutdownRestoresOwnedMemoryAndRetainsFailedRestoration(bool succeeds)
    {
        const int processId = 24_049;
        var startedAt = CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(processId, startKey);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Smart,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, startedAt)));
        var seeded = await fixture.SeedProcessOwnershipAsync(processId, startKey, softwareId, memoryPolicy: true, actionId: 901);
        var currentPriority = 3U;
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => new ProcessResourcePolicySnapshot(id, "fixture", @"c:\tests\fixture.exe",
            startedAt, "Normal", 1, 1, MemoryPriority: currentPriority);
        var requested = new List<uint>();
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            requested.Add(Assert.IsType<uint>(request.MemoryPriority));
            if (succeeds) currentPriority = 5;
            return [new ProcessResourcePolicyBatchWriteResult(processId,
                [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.MemoryPriority, succeeds, "fixture")])];
        };
        fixture.MetricSampler.ObservationAvailable = false;
        await fixture.Coordinator.StopAsync(default);
        Assert.Equal([5U], requested);
        Assert.Equal(!succeeds, File.Exists(seeded.PayloadPath));
    }
}
