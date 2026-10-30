using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareProtectionTests
{
    [Fact]
    public async Task NoFreezeProtectionPersistsAndCanBeRemovedWithoutChangingOtherTargets()
    {
        var store = new Store();
        var service = new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty);
        await service.SetSoftwareProtectionAsync("other", true, default);
        var selected = await service.SetSoftwareProtectionAsync("APP", true, default);
        Assert.Equal(OptimizationProtectionLevels.NoFreeze, selected!.ProtectionLevel);
        Assert.Equal("app", selected.SoftwareId);
        await service.SetSoftwareProtectionAsync("app", true, default);
        // The three system defaults are always in the list next to the user's entries.
        Assert.Equal(2 + 3, service.ProtectedTargetCount);
        var reloaded = new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty);
        Assert.Equal(2, ProtectionTestEntries.User(await reloaded.GetProtectedTargetsAsync(default)).Length);
        Assert.Null(await reloaded.SetSoftwareProtectionAsync("app", false, default));
        Assert.Equal("other", Assert.Single(ProtectionTestEntries.User(await reloaded.GetProtectedTargetsAsync(default))).SoftwareId);
        var final = new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty);
        Assert.Equal("other", Assert.Single(ProtectionTestEntries.User(await final.GetProtectedTargetsAsync(default))).SoftwareId);
    }

    [Fact]
    public async Task UnknownSoftwareAndSaveFailureLeaveExistingProtectionIntact()
    {
        var store = new Store();
        var service = new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty);
        await service.SetSoftwareProtectionAsync("app", true, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetSoftwareProtectionAsync("unknown", false, default));
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.SetSoftwareProtectionAsync("app", false, default));
        Assert.Equal("app", Assert.Single(ProtectionTestEntries.User(await service.GetProtectedTargetsAsync(default))).SoftwareId);
        Assert.Single(store.Targets);
    }

    private sealed class Store : IOptimizationProtectionStore
    {
        public IReadOnlyList<ProtectedOptimizationTarget> Targets = [];
        public bool Fail;
        public Task<IReadOnlyList<ProtectedOptimizationTarget>> LoadAsync(CancellationToken token) => Task.FromResult(Targets);
        public Task SaveAsync(IReadOnlyList<ProtectedOptimizationTarget> targets, CancellationToken token)
        {
            if (Fail) throw new IOException("fixture save failure");
            Targets = targets.ToArray();
            return Task.CompletedTask;
        }
    }
    private sealed class Registry : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SoftwareRecord>>(
            [Record("app"), Record("other")]);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token) => GetSoftwareAsync(token);
        private static SoftwareRecord Record(string id) => new(id, id, "General", "ready", [], [], new(false, "None", "missing_uninstall_entry"), null);
    }
}
