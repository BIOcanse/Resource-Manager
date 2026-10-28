using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareControlBehaviorTests
{
    private static ControlDesiredState Desired(params string[] ids) => new([new("device", ids.Select(id => new ControlSetting(id, Number: 42)).ToArray())]);

    [Fact]
    public async Task StartupAppliesSavedChoiceOnceThenOnlySuccessfulItemsAreReasserted()
    {
        var store = new Store(Desired("good", "failed"));
        var writes = new Layer { Fail = "failed" };
        var plane = new ControlPlane(store, writes);
        await plane.InitializeAsync(default);
        await plane.InitializeAsync(default);
        await plane.ReassertAsync(default);
        Assert.Equal(new[] { "good", "failed", "good" }, writes.Written);
        Assert.Contains((await plane.ReadStateAsync(default)).LastApply.Outcomes, x => x.CapabilityId == "failed" && x.Status == ControlApplyStatuses.Failed);
        writes.Fail = null;
        await plane.SetObjectSettingsAsync("device", store.Value.Objects[0].Settings, default);
        await plane.ReassertAsync(default);
        Assert.Equal(3, writes.Written.Count(x => x == "failed"));
    }

    [Fact]
    public async Task ReassertFailureStopsRetryingUntilExplicitApply()
    {
        var store = new Store(Desired("good"));
        var writes = new Layer();
        var plane = new ControlPlane(store, writes);
        await plane.InitializeAsync(default);
        writes.Fail = "good";
        await plane.ReassertAsync(default);
        writes.Fail = null;
        await plane.ReassertAsync(default);
        Assert.Equal(2, writes.Written.Count);
        Assert.Empty(writes.Released);
        await plane.SetObjectSettingsAsync("device", store.Value.Objects[0].Settings, default);
        await plane.ReassertAsync(default);
        Assert.Equal(4, writes.Written.Count);
    }

    [Fact]
    public async Task RestartAppliesAllPersistedChoicesOnceIncludingPreviouslyFailedItems()
    {
        var store = new Store(Desired("good", "failed"));
        var writes = new Layer { Fail = "failed" };
        await new ControlPlane(store, writes).InitializeAsync(default);
        writes.Fail = null;
        var restarted = new ControlPlane(store, writes);
        await restarted.InitializeAsync(default);
        await restarted.InitializeAsync(default);
        Assert.Equal(new[] { "good", "failed", "good", "failed" }, writes.Written);
        Assert.Empty(writes.Released);
    }

    [Fact]
    public async Task ReadOnlyPlaneNeverPersistsOrWritesEvenWhenCalledDirectly()
    {
        var store = new Store(Desired("good"));
        var writes = new Layer();
        var plane = new ControlPlane(store, writes, capabilities: StartupCapabilitySet.NormalReadOnly);
        await plane.InitializeAsync(default);
        await plane.ReassertAsync(default);
        var rejected = await plane.ApplyDesiredStateAsync(Desired("replacement"), default);
        await plane.SetObjectSettingsAsync("device", [], default);
        Assert.Equal("startup-profile-read-only", Assert.Single(rejected.LastApply.Outcomes).Reason?.Code);
        Assert.Empty(writes.Written);
        Assert.Empty(writes.Released);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task StartupWaitsForExistingCatalogReadinessAndCancelsCleanly()
    {
        var writes = new Layer();
        var plane = new ControlPlane(new Store(Desired("good")), writes, catalog: new HardwareMetricCatalogState());
        using var cancellation = new CancellationTokenSource();
        var starting = plane.InitializeAsync(cancellation.Token);
        Assert.False(starting.IsCompleted);
        Assert.Empty(writes.Written);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Empty(writes.Written);
    }

    [Fact]
    public async Task CatalogPublicationAppliesAllSavedObjectsEvenAfterAnEarlyExplicitEdit()
    {
        var catalog = new HardwareMetricCatalogState();
        var store = new Store(new([new("other", [new("saved", Number: 42)])]));
        var writes = new Layer();
        var plane = new ControlPlane(store, writes, catalog: catalog);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startup = plane.InitializeAsync(cancellation.Token);
        Assert.False(startup.IsCompleted);
        await plane.SetObjectSettingsAsync("device", [new("edited", Number: 42)], default);
        catalog.PublishCatalogProbe(new HardwareMetricSnapshot(DateTimeOffset.UtcNow,
            new CpuMetrics("CPU", 25, true, CpuMetricObservationStatus.Complete, 1, 1, 3000, 5000, 60, "fixture",
                new CpuSensorMetrics(new HardwareSensorProviderState("fixture", HardwareSensorProviderStates.Unavailable, null), null, null, null, null)),
            new MemoryMetrics(UsedBytes: 8, TotalBytes: 32, UsagePercent: 25, IsUsageAvailable: true), new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
            [], null!, new Dictionary<string, MetricReadingValue>()) { CatalogGeneration = 1 });
        await startup;
        await plane.InitializeAsync(default);
        Assert.Equal(new[] { "edited", "saved", "edited" }, writes.Written);
    }

    [Fact]
    public async Task ReassertionUsesSettingsAndReschedulesWhenTheRuntimePlanChanges()
    {
        var settings = new Settings { Interval = 86400 };
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var plane = new ScheduledPlane();
        using var loop = new ControlDesiredStateReassertion(plane, settings, plans);
        // Saved-intent application is finite bootstrap work; the worker only maintains it.
        await plane.InitializeAsync(default);
        await loop.StartAsync(default);
        await plane.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(plane.Reasserted.Task.IsCompleted);
        settings.Interval = 1;
        plans.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        await plane.Reasserted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.StopAsync(default);
        Assert.Equal(1, plane.Initializations);
    }

    [Fact]
    public void HardwareSettingsRoundTripMigrationPatchAndBounds()
    {
        var defaults = AppSettingsDefaults.Create();
        Assert.Equal(new AppHardwareControlSettings(120), defaults.Control);
        using var changes = JsonDocument.Parse("""{"control":{"reassertionIntervalSeconds":17}}""");
        var patched = AppSettingsPatchApplier.Apply(defaults, changes.RootElement);
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(patched, JsonSerializerOptions.Web));
        Assert.Equal(new AppHardwareControlSettings(17), AppSettingsMigrator.MigrateForRead(persisted.RootElement).Control);
        Assert.False(AppSettingsMigrator.RequiresRewrite(persisted.RootElement));
        Assert.Equal(1, AppSettingsNormalizer.Normalize(patched with { Control = new(0) }).Control!.ReassertionIntervalSeconds);
        Assert.Equal(86400, AppSettingsNormalizer.Normalize(patched with { Control = new(int.MaxValue) }).Control!.ReassertionIntervalSeconds);
    }

    private sealed class Store(ControlDesiredState value) : IControlDesiredStateStore
    {
        public ControlDesiredState Value = value;
        public int Saves;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState next, CancellationToken token) { Saves++; Value = next; return Task.CompletedTask; }
    }

    private sealed class Layer : IControlWriteLayer
    {
        public string? Fail;
        public List<string> Written = [];
        public List<string> Released = [];
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            Written.AddRange(desired.Objects.SelectMany(x => x.Settings.Select(x => x.CapabilityId)));
            return Task.FromResult(Report(desired));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
        {
            Released.AddRange(removed.Objects.SelectMany(x => x.Settings.Select(x => x.CapabilityId)));
            return Task.FromResult(Report(removed));
        }
        private ControlApplyReport Report(ControlDesiredState desired) => new(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
            new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, setting.CapabilityId == Fail ? ControlApplyStatuses.Failed : ControlApplyStatuses.Applied))).ToArray(), DateTimeOffset.UtcNow);
    }

    private sealed class Settings : IAppSettingsStore
    {
        public int Interval = 120;
        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken token) => Task.FromResult(new AppSettingsUpdateResult(
            AppSettingsDefaults.Create() with { Control = new(Interval) }, DateTimeOffset.UtcNow, "fake",
            new(AppSettingsSourceKind.TestFixture, "test", "", "", false)));
        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken token) => LoadReadOnlyAsync(token);
        public Task<AppSettingsUpdateResult> SaveAsync(AppSettings settings, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> SaveValidatedAsync(AppSettings settings, Func<AppSettingsUpdateResult, CancellationToken, Task> validate, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ScheduledPlane : IControlPlane
    {
        public Task ForgetInstanceAsync(string id, CancellationToken token) => Task.CompletedTask;
        public Task<ControlStateView> RetryObjectAsync(string id, CancellationToken token) => throw new NotSupportedException();

        public int Initializations;
        public TaskCompletionSource Initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reasserted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken token) { Initializations++; Initialized.TrySetResult(); return Task.CompletedTask; }
        public Task<ControlApplyReport> ReassertAsync(CancellationToken token) { Reasserted.TrySetResult(); return Task.FromResult(ControlApplyReport.Empty); }
        public Task<ControlStateView> ReadStateAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> SetObjectSettingsAsync(string id, IReadOnlyList<ControlSetting> settings, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> ApplyDesiredStateAsync(ControlDesiredState desired, CancellationToken token) => throw new NotSupportedException();
    }
}
