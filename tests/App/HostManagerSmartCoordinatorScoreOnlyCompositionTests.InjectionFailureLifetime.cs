using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.GpuPlacement;
using static Resource_Manager_APP.Tests.GpuPlacementInjectionFailureLifetimeTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("exited", false, 1)]
    [InlineData("pid-reused", false, 1)]
    [InlineData("unavailable", true, 1)]
    [InlineData("missing-but-still-live", true, 1)]
    [InlineData("inventory-live", true, 0)]
    [InlineData("incomplete", true, 0)]
    [InlineData("not-current", true, 0)]
    [InlineData("invalid-identity", true, 0)]
    public async Task InjectionFailureCacheUsesExistingProcessExitOwner(
        string scenario, bool retained, int expectedReads)
    {
        const int failedProcessId = 9042;
        const ulong failedStartKey = 100;
        var failed = new GpuPlacementProcessInstance(failedProcessId, failedStartKey, "test", @"C:\test.exe");
        var injector = CreateInjector();
        SeedFailure(injector, failed);
        var service = new WindowsRunningGpuPlacementActionService(
            NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            null!, injector, null!, null!, null!);
        var inventoryStart = scenario == "inventory-live" ? failedStartKey : failedStartKey + 1;
        var facts = CreateCompleteProcessFacts(failedProcessId, inventoryStart, "software:test", 20, 10, 5);
        facts = scenario switch
        {
            "incomplete" => facts with { EnumeratedCount = facts.EnumeratedCount + 1, SkippedCount = 1 },
            "not-current" => facts with { InventoryStatus = SamplingObservationStatus.Unavailable },
            "invalid-identity" => facts with { Processes = [facts.Processes[0] with { ProcessStartKey = 0 }] },
            _ => facts
        };
        var reads = 0;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false, processFactsSnapshot: facts,
            runningGpuActions: service, processRecoveryRead: processId =>
            {
                Assert.Equal(failedProcessId, processId);
                reads++;
                return scenario switch
                {
                    "unavailable" => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "denied"),
                    "missing-but-still-live" => Live(processId, failedStartKey),
                    "pid-reused" => Live(processId, failedStartKey + 1),
                    _ => Exited()
                };
            });

        await fixture.RunRealtimeCycleAsync();

        Assert.Equal(retained, injector.GetKnownProcessFailure(failed) is not null);
        Assert.Equal(expectedReads, reads);
    }
}
