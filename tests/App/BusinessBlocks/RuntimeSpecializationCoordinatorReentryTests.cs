using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class RuntimeSpecializationCoordinatorReentryTests
{
    [Fact]
    public async Task CanceledStartReleasesRunOwnershipBeforeAnotherStart()
    {
        // The canceled preparation gate must be observed before any compiler/store access.
        var coordinator = new RuntimeSpecializationCoordinator(null!, null!, null!, null!,
            new MonitoringSourceZoneRegistry([]), NullLogger<RuntimeSpecializationCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(cancellation.Token));
        }
        finally { await coordinator.StopAsync(default); }
    }
}
