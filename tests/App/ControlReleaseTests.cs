using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlReleaseTests
{
    [Theory]
    [InlineData(ControlValueKinds.Toggle)]
    [InlineData(ControlValueKinds.Curve)]
    public async Task RemovingLastNonNumericSettingReleasesExactlyTheOwnedCapability(string kind)
    {
        var writer = new Writer();
        var store = new Store(State("a", "owned"));
        var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(kind), [writer], new Access()));
        var result = await plane.ApplyDesiredStateAsync(new([]), default);
        Assert.Empty(result.Desired.Objects);
        Assert.Equal("owned", Assert.Single(writer.Released));
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(result.LastApply.Outcomes).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailureSurvivesReassertOfOtherSettings(bool throws)
    {
        var writer = new Writer { Refuse = true, Throws = throws };
        var store = new Store(State("a", "owned"));
        var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(ControlValueKinds.Toggle), [writer], new Access()));
        var result = await plane.ApplyDesiredStateAsync(State("a", "other"), default);
        Assert.Contains(result.LastApply.Outcomes, x => x.CapabilityId == "owned" && x.Status == ControlApplyStatuses.Failed);
        var reasserted = await plane.ReassertAsync(default);
        Assert.Contains(reasserted.Outcomes, x => x.CapabilityId == "owned" && x.Status == ControlApplyStatuses.Failed);
    }

    [Fact]
    public async Task ConcurrentObjectUpdatesAndReassertRunAsWholeTransactions()
    {
        var store = new Store(new([]));
        var layer = new BlockingLayer();
        var plane = new ControlPlane(store, layer);
        var first = plane.SetObjectSettingsAsync("a", [new("owned", Toggle: true)], default);
        await layer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = plane.SetObjectSettingsAsync("b", [new("owned", Toggle: true)], default);
        var reassert = plane.ReassertAsync(default);
        Assert.False(second.IsCompleted);
        Assert.False(reassert.IsCompleted);
        layer.Continue.SetResult();
        await Task.WhenAll(first, second, reassert).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, store.Value.Objects.Count);
        Assert.Equal(new[] { 1, 1, 2 }, layer.ObjectCounts);
    }

    [Fact]
    public async Task CardApplyScopesWritesAndReleasesAndPreservesOtherReceipts()
    {
        var store = new Store(new([]));
        var layer = new RecordingLayer();
        var plane = new ControlPlane(store, layer);
        await plane.SetObjectSettingsAsync("b", [new("owned", Toggle: true)], default);
        store.Value = store.Value with { PendingReleases = State("c", "owned").Objects };
        var applied = await plane.SetObjectSettingsAsync("a", [new("owned", Toggle: true)], default);
        Assert.Equal(new[] { "b", "a" }, layer.Written);
        Assert.Empty(layer.Released);
        Assert.Equal(2, applied.LastApply.Outcomes.Count);
        var released = await plane.SetObjectSettingsAsync("a", [], default);
        Assert.Equal(new[] { "a" }, layer.Released);
        Assert.Equal("c", Assert.Single(released.Desired.PendingReleases).ObjectId);
        Assert.Equal("b", Assert.Single(released.Desired.Objects).ObjectId);
        Assert.Contains(released.LastApply.Outcomes, outcome => outcome.ObjectId == "b");
        Assert.Equal(new[] { "b", "a" }, layer.Written);
    }

    private sealed class RecordingLayer : IControlWriteLayer
    {
        public List<string> Written = [];
        public List<string> Released = [];
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState state, CancellationToken token)
        {
            Written.AddRange(state.Objects.Select(x => x.ObjectId));
            return Task.FromResult(Report(state));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
        {
            Released.AddRange(removed.Objects.Select(x => x.ObjectId));
            Assert.Contains(remaining.Objects, x => x.ObjectId == "b");
            return Task.FromResult(Report(removed));
        }
        private static ControlApplyReport Report(ControlDesiredState state) => new(
            state.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Applied, null))).ToArray(),
            DateTimeOffset.UtcNow);
    }

    private static ControlDesiredState State(string id, string capability) => new([new(id, [new(capability, Toggle: true)])]);
    [Fact]
    public async Task FailedReleaseSurvivesRepeatedApplyAndSerializedRestartUntilRecovery()
    {
        var writer = new Writer { Refuse = true };
        var store = new Store(State("a", "owned"));
        var layer = new ControlWriteLayer(new Catalog(ControlValueKinds.Toggle), [writer], new Access());
        var plane = new ControlPlane(store, layer);
        await plane.ApplyDesiredStateAsync(ControlDesiredState.Empty, default);
        var repeated = await plane.ApplyDesiredStateAsync(ControlDesiredState.Empty, default);
        Assert.Single(repeated.Desired.PendingReleases);
        Assert.Equal(ControlApplyStatuses.Failed, Assert.Single(repeated.LastApply.Outcomes).Status);
        store.Value = System.Text.Json.JsonSerializer.Deserialize<ControlDesiredState>(
            System.Text.Json.JsonSerializer.Serialize(store.Value))!;
        writer.Refuse = false;
        var restarted = new ControlPlane(store, layer);
        var recovered = await restarted.ReassertAsync(default);
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(recovered.Outcomes).Status);
        Assert.Empty(store.Value.PendingReleases);
        Assert.Empty(store.Value.Objects);
        Assert.Equal(3, writer.Released.Count);
        await restarted.ReassertAsync(default);
        Assert.Equal(3, writer.Released.Count);
    }

    [Fact]
    public async Task NewSettingSupersedesPendingReleaseAndApiCannotInjectReleases()
    {
        var writer = new Writer { Refuse = true };
        var store = new Store(State("a", "owned"));
        var layer = new ControlWriteLayer(new Catalog(ControlValueKinds.Toggle), [writer], new Access());
        var plane = new ControlPlane(store, layer);
        await plane.ApplyDesiredStateAsync(ControlDesiredState.Empty, default);
        var request = State("a", "owned") with { PendingReleases = State("a", "other").Objects };
        await plane.ApplyDesiredStateAsync(request, default);
        Assert.Empty(store.Value.PendingReleases);
        Assert.Single(writer.Released);
    }
    private sealed class Store(ControlDesiredState initial) : IControlDesiredStateStore
    {
        public ControlDesiredState Value = initial;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Catalog(string kind) : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => new([
            new("a", "fan", "Fan", new("windows", "oem"), [
                new("owned", "Owned", kind, true), new("other", "Other", kind, true)])], DateTimeOffset.UtcNow);
    }
    private sealed class Writer : IControlWriter
    {
        public bool Refuse;
        public bool Throws;
        public List<string> Released = [];
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => ControlWriteAvailability.Yes();
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
            => Task.FromResult(new ControlApplyOutcome(target.Id, capability.Id, ControlApplyStatuses.Applied, null));
        public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> retained, CancellationToken token)
        {
            Released.Add(capability.Id);
            if (Throws) throw new IOException("release failed");
            return Task.FromResult(!Refuse);
        }
    }
    private sealed class BlockingLayer : IControlWriteLayer
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> ObjectCounts = [];
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
        public async Task<ControlApplyReport> WriteAsync(ControlDesiredState state, CancellationToken token)
        {
            ObjectCounts.Add(state.Objects.Count);
            if (ObjectCounts.Count == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(token); }
            return ControlApplyReport.Empty;
        }
    }
}
