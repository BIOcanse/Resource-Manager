using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlRevocationRetryTests
{
    [Fact]
    public async Task ForgetDisconnectedInstanceRevokesReplayAndReceiptsButKeepsOtherReleases()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-control-revoke-" + Guid.NewGuid().ToString("N"));
        var store = new Store();
        var layer = new Layer();
        var plane = new ControlPlane(store, layer);
        var catalog = new Catalog();
        var registry = new JsonControlInstanceRegistry(new Environment(root), catalog, plane);
        try
        {
            await registry.ReadAsync(default);
            await plane.SetObjectSettingsAsync("a", [new("good", Toggle: true)], default);
            await plane.SetObjectSettingsAsync("b", [new("good", Toggle: true)], default);
            store.Value = store.Value with { PendingReleases = [new("c", [new("release", Toggle: true)])] };
            catalog.Present = false;
            await registry.ForgetAsync("a", default);
            catalog.Present = true;
            layer.Written.Clear();
            await plane.ReassertAsync(default);
            Assert.Equal(new[] { "b/good" }, layer.Written);
            var state = await plane.ReadStateAsync(default);
            Assert.DoesNotContain(state.Desired.Objects, target => target.ObjectId == "a");
            Assert.DoesNotContain(state.LastApply.Outcomes, outcome => outcome.ObjectId == "a");
            Assert.Equal("c", Assert.Single(state.Desired.PendingReleases).ObjectId);
            Assert.Empty(layer.Released);
        }
        finally
        {
            File.Delete(Path.Combine(root, "UserData", "Control", "instances.json"));
            foreach (var path in new[] { Path.Combine(root, "UserData", "Control"), Path.Combine(root, "UserData"), root })
                if (Directory.Exists(path)) Directory.Delete(path);
        }
    }

    [Theory]
    [InlineData(ControlApplyStatuses.Failed)]
    [InlineData(ControlApplyStatuses.Unsupported)]
    public async Task ExplicitRetryWritesExactlyTheFailedSavedItemOnce(string status)
    {
        var store = new Store();
        var layer = new Layer { Failure = status };
        var plane = new ControlPlane(store, layer);
        await plane.SetObjectSettingsAsync("a", [new("good", Toggle: true), new("bad", Toggle: false)], default);
        layer.Written.Clear();
        await plane.ReassertAsync(default);
        Assert.Equal(new[] { "a/good" }, layer.Written);
        layer.Written.Clear();
        layer.Failure = null;
        var result = await plane.RetryObjectAsync("a", default);
        Assert.Equal(new[] { "a/bad" }, layer.Written);
        Assert.Equal(2, result.LastApply.Outcomes.Count);
        Assert.All(result.LastApply.Outcomes, outcome => Assert.Equal(ControlApplyStatuses.Applied, outcome.Status));
        await plane.RetryObjectAsync("a", default);
        Assert.Equal(new[] { "a/bad" }, layer.Written);
    }

    [Fact]
    public async Task RetryPendingReleaseDoesNotWriteOtherSavedSettingsOrReleaseOtherInstances()
    {
        var store = new Store();
        var layer = new Layer();
        var plane = new ControlPlane(store, layer);
        await plane.SetObjectSettingsAsync("a", [new("good", Toggle: true)], default);
        store.Value = store.Value with { PendingReleases = [new("a", [new("release", Toggle: true)]), new("b", [new("release", Toggle: true)])] };
        layer.Written.Clear();
        var state = await plane.RetryObjectAsync("a", default);
        Assert.Empty(layer.Written);
        Assert.Equal(new[] { "a/release" }, layer.Released);
        Assert.Equal("b", Assert.Single(state.Desired.PendingReleases).ObjectId);
        Assert.Equal(2, state.LastApply.Outcomes.Count);
    }

    [Fact]
    public async Task ForgetWaitsForAnInflightWriteAndRevokesItsReceipt()
    {
        var layer = new Layer { Block = true };
        var plane = new ControlPlane(new Store(), layer);
        var apply = plane.SetObjectSettingsAsync("a", [new("good", Toggle: true)], default);
        await layer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forget = plane.ForgetInstanceAsync("a", default);
        Assert.False(forget.IsCompleted);
        layer.Continue.SetResult();
        await Task.WhenAll(apply, forget).WaitAsync(TimeSpan.FromSeconds(5));
        layer.Written.Clear();
        await plane.ReassertAsync(default);
        Assert.Empty(layer.Written);
        Assert.Empty((await plane.ReadStateAsync(default)).LastApply.Outcomes);
    }

    private sealed class Store : IControlDesiredStateStore
    {
        public ControlDesiredState Value = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState desired, CancellationToken token) { Value = desired; return Task.CompletedTask; }
    }

    private sealed class Layer : IControlWriteLayer
    {
        public string? Failure;
        public bool Block;
        public List<string> Written = [];
        public List<string> Released = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            Written.AddRange(desired.Objects.SelectMany(target => target.Settings.Select(setting => target.ObjectId + "/" + setting.CapabilityId)));
            if (Block) { Entered.TrySetResult(); await Continue.Task.WaitAsync(token); }
            return Report(desired);
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
        {
            Released.AddRange(removed.Objects.SelectMany(target => target.Settings.Select(setting => target.ObjectId + "/" + setting.CapabilityId)));
            return Task.FromResult(Report(removed));
        }
        private ControlApplyReport Report(ControlDesiredState desired) => new(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
            new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, setting.CapabilityId == "bad" && Failure is not null ? Failure : ControlApplyStatuses.Applied))).ToArray(), DateTimeOffset.UtcNow);
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public bool Present = true;
        public ControlObjectCatalog ReadObjects() => new(Present ? [new("a", "fan", "Fan A", new("windows", "fake"), [])] : [], DateTimeOffset.UtcNow);
    }
    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
