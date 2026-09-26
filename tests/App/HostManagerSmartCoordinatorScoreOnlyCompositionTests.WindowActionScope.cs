using System.Text.Json;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("pending")]
    [InlineData("invalid")]
    [InlineData("policy")]
    public async Task WindowLedgerScopeClosureDistinguishesCompletedFactsFromUnsettledOwnership(string scenario)
    {
        var path = Path.Combine(NewRoot("scope-" + scenario), "recovery.json");
        var prepared = Prepared();
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            prepared.Window.Request.ProcessId, (ulong)prepared.Window.Request.CreationFileTimeUtc));
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority, rollbackStateStoreOverride: store);
        var record = GpuWindowActionRecord.Create(prepared, scenario == "completed" ? Restored(prepared) : null);
        if (scenario == "invalid") record = record with { Metadata = null };
        if (scenario == "policy") record = Policy(prepared);
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(record)] };
        await store.SaveAsync(state, CancellationToken.None);
        var before = File.ReadAllBytes(path);
        if (scenario == "completed")
            _ = await fixture.Coordinator.CloseProcessEffectValidationScopeAsync(validation.CreateCloseRequest(), CancellationToken.None);
        else
            Assert.Contains("hardware placement ownership or unconfirmed window actions",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                    validation.CreateCloseRequest(), CancellationToken.None))).Message);
        Assert.Equal(scenario == "completed", validation.Authority.Capture().IsProductionUnscoped);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
        Assert.Equal(before, File.ReadAllBytes(path));
        output.WriteLine("windowLedgerScope=" + JsonSerializer.Serialize(new { path, scenario,
            scopeClosed = validation.Authority.Capture().IsProductionUnscoped, unchangedCanonical = true,
            retained = ReadCanonical(path).AppliedPlacements }));
    }
}
