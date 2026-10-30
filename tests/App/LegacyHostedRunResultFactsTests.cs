using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class LegacyHostedRunResultFactsTests
{
    [Theory]
    [InlineData(BlockUids.PortableSoftwareDiscoveryService)]
    [InlineData(BlockUids.RuntimeSpecializationCoordinator)]
    public async Task NewInputResetCannotBeOverwrittenByPriorRunFinishingCleanup(int id)
    {
        await using var native = CreateControl();
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var failures = new HostedRunResultFacts(authority, native);
        var oldInputs = failures.CaptureInputs(id);

        failures.Reset(id);
        failures.RecordEnd(id, oldInputs);
        Assert.False(ReadFailed(authority, id));

        var currentInputs = failures.CaptureInputs(id);
        Assert.NotEqual(oldInputs, currentInputs);
        failures.RecordEnd(id, currentInputs);
        Assert.True(ReadFailed(authority, id));

        failures.PlanChanged();
        Assert.False(ReadFailed(authority, id));
        failures.RecordEnd(id, currentInputs);
        Assert.False(ReadFailed(authority, id));
        failures.RecordEnd(id, failures.CaptureInputs(id));
        Assert.True(ReadFailed(authority, id));
    }

    [Fact]
    public async Task RepeatedHostAdmissionDoesNotClearFailureButWithdrawalAndReadmissionDoes()
    {
        const int id = BlockUids.PortableSoftwareDiscoveryService;
        await using var native = CreateControl();
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var failures = new HostedRunResultFacts(authority, native);
        var permits = new BackendHostPermitFacts(authority, native, [new HostedBusinessBlockAlias(id)], failures);
        var reason = BusinessBlockTestSupport.Reason();
        await permits.SetAsync(id, true, reason);
        var runInputs = failures.CaptureInputs(id);
        failures.RecordEnd(id, runInputs);
        Assert.True(ReadFailed(authority, id));

        await permits.SetAsync(id, true, reason);
        Assert.Equal(runInputs, failures.CaptureInputs(id));
        Assert.True(ReadFailed(authority, id));
        await permits.SetAsync(id, false, reason);
        Assert.True(ReadFailed(authority, id));
        await permits.SetAsync(id, true, reason);
        Assert.False(ReadFailed(authority, id));
        Assert.NotEqual(runInputs, failures.CaptureInputs(id));
        failures.RecordEnd(id, runInputs);
        Assert.False(ReadFailed(authority, id));
    }

    private static bool ReadFailed(StateAuthority authority, int id)
        => authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value;

    private static NativeControl CreateControl()
        => NativeBackendBlockTestSupport.CreateControl(BackendExpectedProductionTree.Parents(1).Keys
            .Select(id => new BusinessBlockRegistration(id,
                _ => throw new InvalidOperationException("This fact-owner fixture never starts a block."))));
}
