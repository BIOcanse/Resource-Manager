using System.Text.Json;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Updates;

namespace Resource_Manager_APP.Tests;

public sealed partial class AutomaticProductUpdateTests
{
    [Fact]
    public async Task CapturedConnectivityCallbackCannotPersistScheduleAfterStop()
    {
        using var fixture = new Fixture { Mode = "notify", Listing = Catalog("0.2.4") };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Connectivity.Changed += () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(CancellationToken.None);
        await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
        var notification = Task.Run(() => fixture.Connectivity.Set(false));
        string? stoppedState = null;
        var changesAfterStop = 0;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            stoppedState = File.ReadAllText(Path.Combine(fixture.Root, "Config", "product-update-state.json"));
            coordinator.Changed += () => Interlocked.Increment(ref changesAfterStop);
        }
        finally
        {
            release.Set();
            await notification.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
        }
        Assert.Equal(stoppedState, File.ReadAllText(Path.Combine(fixture.Root, "Config", "product-update-state.json")));
        Assert.Equal(0, Volatile.Read(ref changesAfterStop));
        Assert.Equal("scheduled", coordinator.Status.AutomaticCheckStatus);
    }

    private sealed class Connectivity : IInternetConnectivity
    {
        public bool IsOnline { get; private set; } = true;
        public event Action? Changed;
        public void Set(bool value) { IsOnline = value; Changed?.Invoke(); }
    }

    [Fact]
    public async Task NotifyWithoutIntervalChecksOnceAndNeverPrepares()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromDays(30));
        await service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal("0.3.0", coordinator.Status.AvailableVersion);
        Assert.Empty(fixture.Prepared);
        Assert.Null(coordinator.Status.NextCheckAt);
        Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, coordinator.Status.OnlineTimeSinceLastCheckSeconds);
    }

    [Fact]
    public async Task NotifyLoopDoesNotRecheckOnClockAdvanceOrUnchangedPublication()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await foreach (var status in coordinator.SubscribeAsync(timeout.Token))
                if (status.AutomaticCheckStatus == "idle" && status.AvailableVersion == "0.3.0") break;
            fixture.Clock.Advance(TimeSpan.FromDays(30));
            var reads = fixture.SettingsReads;
            fixture.PublishSettings();
            await fixture.WaitForSettingsReadsAsync(reads + 1);
        }
        finally { await service.StopAsync(default); }
        Assert.Equal(1, fixture.Checks);
        Assert.Empty(fixture.Prepared);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task OfflineStartupWaitsForNotificationAndStopDetaches()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        fixture.Connectivity.Set(false);
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(default);
        try
        {
            await fixture.WaitForSettingsReadsAsync(2); // Waiting-state publication wakes the loop.
            Assert.Equal(0, fixture.Checks);
            Assert.Equal("waitingForInternet", coordinator.Status.AutomaticCheckStatus);
            Assert.Null(coordinator.Status.NextCheckAt);
            fixture.Connectivity.Set(true);
            await fixture.WaitForSettingsReadsAsync(3);
            // Await a state publication rather than a real-time polling loop.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await foreach (var status in coordinator.SubscribeAsync(timeout.Token))
                if (status.AvailableVersion == "0.3.0") break;
            Assert.Equal(1, fixture.Checks);
            Assert.Empty(fixture.Prepared);
        }
        finally { await service.StopAsync(default); }
        fixture.Connectivity.Set(false);
        fixture.Connectivity.Set(true);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task IntervalUsesOnlyOnlineTimeAndClampsBelowOneDay()
    {
        using var fixture = new Fixture { Mode = "notify", CheckIntervalHours = 1 };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(default);
        try
        {
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
            var first = fixture.Clock.GetUtcNow();
            fixture.Clock.Advance(TimeSpan.FromHours(10));
            Assert.Equal(36000, coordinator.Status.OnlineTimeSinceLastCheckSeconds);
            Assert.Equal(first.AddHours(24), coordinator.Status.NextCheckAt);
            fixture.Connectivity.Set(false);
            Assert.Null(coordinator.Status.NextCheckAt);
            Assert.Equal("waitingForInternet", coordinator.Status.AutomaticCheckStatus);
            fixture.Clock.Advance(TimeSpan.FromDays(7));
            Assert.Equal(36000, coordinator.Status.OnlineTimeSinceLastCheckSeconds);
            Assert.Equal(1, fixture.Checks);
            fixture.Connectivity.Set(true);
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(14));
            Assert.Equal(fixture.Clock.GetUtcNow().AddHours(14), coordinator.Status.NextCheckAt);
            fixture.Clock.Advance(TimeSpan.FromHours(14));
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
            Assert.Equal(2, fixture.Checks);
            Assert.Equal(0, coordinator.Status.OnlineTimeSinceLastCheckSeconds);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task ManualOfflineCheckWorksInOffAndRecordsVersionAndFailureKeepsIt()
    {
        using var fixture = new Fixture { Enabled = false };
        fixture.Connectivity.Set(false);
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(default);
        Assert.Equal(0, fixture.Checks);
        await coordinator.CheckCatalogAsync(default);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal("0.3.0", coordinator.Status.AvailableVersion);
        fixture.Listing = fixture.Listing with { Status = "unavailable", Complete = false };
        await coordinator.CheckCatalogAsync(default);
        await coordinator.CheckAutomaticAsync(default, prepareUpdate: false);
        Assert.Equal("failed", coordinator.Status.CheckResult);
        Assert.Equal("0.3.0", coordinator.Status.AvailableVersion);
        Assert.Equal("0.3.0", fixture.Store.Read()!.AvailableVersion);
        Assert.Equal("0.3.0", fixture.Coordinator().Status.AvailableVersion);
        Assert.Empty(fixture.Prepared);
    }

    [Fact]
    public void OlderStateWithoutAvailableVersionLoadsAndWireTimesAreUtc()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Config"));
        File.WriteAllText(Path.Combine(fixture.Root, "Config", "product-update-state.json"),
            """{"stage":"idle","updatedAt":"2026-10-25T09:00:00-05:00","lastCheckedAt":"2026-10-25T08:00:00-05:00"}""");
        var coordinator = fixture.Coordinator();
        Assert.Null(coordinator.Status.AvailableVersion);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(coordinator.Status, JsonSerializerOptions.Web));
        Assert.Equal(TimeSpan.Zero, wire.RootElement.GetProperty("updatedAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, wire.RootElement.GetProperty("lastCheckedAt").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task StateSubscriptionStartsCurrentAndCoalescesChanges()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var subscription = coordinator.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Null(subscription.Current.AvailableVersion);
        var pending = subscription.MoveNextAsync().AsTask();
        await coordinator.CheckCatalogAsync(default);
        Assert.True(await pending);
        Assert.Equal("0.3.0", subscription.Current.AvailableVersion);
        fixture.Listing = Catalog("0.2.4", Option("0.3.1", "stable"));
        await coordinator.CheckCatalogAsync(default);
        fixture.Listing = Catalog("0.2.4", Option("0.3.2", "stable"));
        await coordinator.CheckCatalogAsync(default);
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal("0.3.2", subscription.Current.AvailableVersion);
    }

    [Fact]
    public async Task SwitchingNotifyToAutomaticPreparesKnownVersionWithoutAnotherCheck()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        fixture.PackageUsage.InUse = true;
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(default);
        fixture.Mode = "automatic";
        await service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(["0.3.0"], fixture.Prepared);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
    }

    [Fact]
    public async Task EnablingIntervalCountsOnlineTimeSinceLastCheckAndManualCheckResetsIt()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromHours(20));
        fixture.PeriodicCheckEnabled = true;
        await service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(4), coordinator.Status.NextCheckAt);
        await coordinator.CheckCatalogAsync(default);
        Assert.Equal(0, coordinator.Status.OnlineTimeSinceLastCheckSeconds);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(24), coordinator.Status.NextCheckAt);
    }

    [Fact]
    public async Task TurningOffWhileWaitingPreventsConnectivityFromStartingCheck()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        fixture.Connectivity.Set(false);
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(default);
        try
        {
            await fixture.WaitForSettingsReadsAsync(2);
            fixture.Mode = "off";
            fixture.PublishSettings();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await foreach (var status in coordinator.SubscribeAsync(timeout.Token))
                if (status.AutomaticCheckStatus == "disabled") break;
            fixture.Connectivity.Set(true);
            fixture.Clock.Advance(TimeSpan.FromDays(10));
        }
        finally { await service.StopAsync(default); }
        Assert.Equal(0, fixture.Checks);
        Assert.Equal("disabled", coordinator.Status.AutomaticCheckStatus);
    }

    [Fact]
    public async Task StateChangeEventIncludesNonStageChangesAndSkipsIdenticalSaves()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        var changes = 0;
        coordinator.Changed += () => changes++;
        await coordinator.CheckCatalogAsync(default);
        Assert.Equal(1, changes);
        Assert.Equal("idle", coordinator.Status.Stage);
        await coordinator.CheckCatalogAsync(default); // Same value and injected instant.
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task SuccessfulCheckWithoutTargetsClearsPreviousAvailableVersion()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        await coordinator.CheckCatalogAsync(default);
        Assert.Equal("0.3.0", coordinator.Status.AvailableVersion);
        fixture.Listing = Catalog("0.2.4");
        await coordinator.CheckAutomaticAsync(default, prepareUpdate: false);
        Assert.Null(coordinator.Status.AvailableVersion);
        Assert.Null(fixture.Store.Read()!.AvailableVersion);
        Assert.Equal("noUpdate", coordinator.Status.CheckResult);
    }

    [Fact]
    public async Task FailedStartupCheckRetainsKnownVersionWithoutPreparingAnOldCheckResult()
    {
        using var fixture = new Fixture { Mode = "notify", PeriodicCheckEnabled = false };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(default);
        fixture.Mode = "off";
        await service.RunOnceAsync(default);
        fixture.Mode = "automatic";
        fixture.Listing = fixture.Listing with { Status = "unavailable", Complete = false };
        await service.RunOnceAsync(default);
        await service.RunOnceAsync(default); // State-change wakes must not prepare it either.
        Assert.Equal("0.3.0", coordinator.Status.AvailableVersion);
        Assert.Equal("failed", coordinator.Status.CheckResult);
        Assert.Empty(fixture.Prepared);
    }
}
