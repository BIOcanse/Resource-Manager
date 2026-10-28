using System.Net;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Infrastructure.Updates;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed partial class AutomaticProductUpdateTests
{
    [Fact]
    public async Task GenericPackageLeasesPermitPreparationAndDeferApplicationUntilBothClientsExit()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true
            && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        using var fixture = new Fixture();
        PackageUseGuard.Prepare(fixture.Root);
        fixture.PackageUsage.ReadUsage = () =>
        {
            using var maintenance = PackageUseGuard.TryAcquireMaintenance(fixture.Root);
            return maintenance is null;
        };
        using var first = PackageUseGuard.Acquire(fixture.Root);
        using var second = PackageUseGuard.Acquire(fixture.Root);
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(["0.3.0"], fixture.Prepared);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
        Assert.Equal(0, fixture.Launches);
        first.Dispose();
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Launches);
        second.Dispose();
        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task DisabledSwitchPreventsChecksPreparationAndApplication()
    {
        using var fixture = new Fixture { Enabled = false };
        fixture.Store.Write(new("pendingApply", "0.3.0", fixture.Workspace, null, fixture.Clock.GetUtcNow()));
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Checks);
        Assert.Empty(fixture.Prepared);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
    }

    [Fact]
    public async Task ChecksOnStartAndAtMinimumDayIntervalAndAfterReenabling()
    {
        using var fixture = new Fixture();
        fixture.Listing = Catalog("0.2.4");
        using var service = fixture.Service(fixture.Coordinator());
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Checks);
        fixture.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Checks);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Checks);
        fixture.Enabled = false;
        await service.RunOnceAsync(CancellationToken.None);
        fixture.Enabled = true;
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, fixture.Checks);
    }

    [Theory]
    [InlineData("0.2.4", "0.3.0")]
    [InlineData("0.2.4-beta.1", "0.3.0-beta.2")]
    public void AutomaticSelectionRequiresSameChannelStrictlyNewerSelectableVersion(string installed, string expected)
    {
        var listing = Catalog(installed,
            Option("0.4.0", "stable", selectable: false),
            Option("0.4.0-beta.1", "preview", selectable: false),
            Option("0.3.0-beta.1", "preview"), Option("0.3.0", "stable"),
            Option("0.3.0-beta.2", "preview"), Option(installed, installed.Contains("beta") ? "preview" : "stable"),
            Option("0.1.0", "stable"), Option("0.1.0-beta.1", "preview"));
        Assert.Equal(expected, ProductUpdateCoordinator.SelectAutomaticTarget(listing)?.Version);
        Assert.Null(ProductUpdateCoordinator.SelectAutomaticTarget(listing with { InstalledVersion = null }));
    }

    [Fact]
    public async Task PackageUseDefersPreparedUpdateUntilSafeWindowAndLaunchesOnlyOnce()
    {
        using var fixture = new Fixture();
        fixture.PackageUsage.InUse = true;
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
        Assert.Equal(["0.3.0"], fixture.Prepared);
        Assert.Equal(0, fixture.Launches);
        fixture.PackageUsage.InUse = false;
        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal("waitingForExit", coordinator.Status.Stage);
    }

    [Fact]
    public async Task PendingPreparedPackageSurvivesRestartAndAppliesWithoutRedownload()
    {
        using var fixture = new Fixture();
        fixture.PackageUsage.InUse = true;
        using (var first = fixture.Service(fixture.Coordinator()))
            await first.RunOnceAsync(CancellationToken.None);
        var restarted = fixture.Coordinator();
        Assert.Equal("pendingApply", restarted.Status.Stage);
        Assert.Equal("0.3.0", restarted.Status.TargetVersion);
        fixture.PackageUsage.InUse = false;
        using var second = fixture.Service(restarted);
        await second.RunOnceAsync(CancellationToken.None);
        Assert.Single(fixture.Prepared);
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task FailedTargetIsNotRetriedAfterRestartButNewerReleaseIsPrepared()
    {
        using var fixture = new Fixture { PreparationError = new ProductUpdateFailure("update.checksumMismatch") };
        var coordinator = fixture.Coordinator();
        await coordinator.CheckAutomaticAsync(CancellationToken.None);
        Assert.Equal("error", coordinator.Status.Stage);
        Assert.Equal("update.checksumMismatch", coordinator.Status.ErrorCode);
        var restarted = fixture.Coordinator();
        Assert.Equal("0.3.0", restarted.Status.FailedVersion);
        Assert.Equal("update.checksumMismatch", restarted.Status.ErrorCode);
        fixture.PreparationError = null;
        await restarted.CheckAutomaticAsync(CancellationToken.None);
        Assert.Single(fixture.Prepared);
        fixture.Listing = Catalog("0.2.4", Option("0.3.0", "stable"), Option("0.3.1", "stable"));
        await restarted.CheckAutomaticAsync(CancellationToken.None);
        Assert.Equal(["0.3.0", "0.3.1"], fixture.Prepared);
        Assert.Equal("pendingApply", restarted.Status.Stage);
        Assert.Equal("0.3.1", restarted.Status.TargetVersion);
    }

    [Fact]
    public async Task FailedManifestVersionAlsoSuppressesPrefixedReleaseTag()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new("error", "0.3.0", null, "update.applyFailed", fixture.Clock.GetUtcNow())
            { FailedVersion = "0.3.0", ErrorCode = "update.applyFailed" });
        fixture.Listing = Catalog("0.2.4", Option("v0.3.0", "stable"));
        await fixture.Coordinator().CheckAutomaticAsync(CancellationToken.None);
        Assert.Empty(fixture.Prepared);
        Assert.Equal("0.3.0", fixture.Coordinator().Status.FailedVersion);
    }

    [Fact]
    public async Task IncompatibleNewestReleaseAllowsNextCompatibleAndRemembersRejectionAfterRestart()
    {
        using var fixture = new Fixture();
        fixture.Listing = Catalog("0.2.4", Option("0.4.0", "stable"), Option("0.3.1", "stable"), Option("0.5.0-beta.1", "preview"));
        fixture.PreparationErrorForOption = option => option.Version == "0.4.0"
            ? new UpdatePackageIncompatibleException("unsupported settings schema") : null;
        var first = fixture.Coordinator();
        await first.CheckAutomaticAsync(CancellationToken.None);
        Assert.Equal(["0.4.0", "0.3.1"], fixture.Prepared);
        Assert.Equal("pendingApply", first.Status.Stage);
        Assert.Equal("0.3.1", first.Status.TargetVersion);
        Assert.Equal(["0.4.0"], first.Status.IncompatibleVersions);
        Assert.Null(first.Status.FailedVersion);

        // Conclude this preparation before another check, while preserving its
        // recorded admission rejection across a fresh coordinator instance.
        fixture.Store.Write(first.Status with { Stage = "completed", Workspace = null });
        var restarted = fixture.Coordinator();
        await restarted.CheckAutomaticAsync(CancellationToken.None);
        Assert.Equal(["0.4.0", "0.3.1", "0.3.1"], fixture.Prepared);
        Assert.Equal(["0.4.0"], restarted.Status.IncompatibleVersions);
    }

    [Fact]
    public async Task CancelledPreparationDoesNotFailTargetAndCanRetryWhenSwitchReenabled()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.PrepareOverride = (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromCanceled<PreparedProductUpdate>(token);
        };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(cancellation.Token));
        Assert.Equal("idle", coordinator.Status.Stage);
        Assert.Null(coordinator.Status.FailedVersion);
        Assert.Null(coordinator.Status.ErrorCode);
        fixture.Enabled = false;
        await service.RunOnceAsync(CancellationToken.None);
        fixture.Enabled = true;
        fixture.PackageUsage.InUse = true;
        fixture.PrepareOverride = null;
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(["0.3.0", "0.3.0"], fixture.Prepared);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
        Assert.Null(coordinator.Status.FailedVersion);
    }

    [Fact]
    public async Task ControllerStopCancelsActivePreparationWhenSwitchTurnsOff()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PrepareOverride = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("Preparation unexpectedly continued.");
        };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, fixture.Clock.ActiveTimers);
            fixture.Enabled = false;
            fixture.PublishSettings();
            Assert.False(fixture.Plans.DecisionFacts.AutomaticUpdatesEnabled);
            await service.StopAsync(CancellationToken.None);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await service.StopAsync(CancellationToken.None); }
        Assert.Equal("idle", coordinator.Status.Stage);
        Assert.Null(coordinator.Status.FailedVersion);
        Assert.Equal(0, fixture.Launches);
    }

    [Fact]
    public async Task DisabledFiniteRunHasNoTimersPackageUseChecksOrBackgroundSettingsReads()
    {
        using var fixture = new Fixture { Enabled = false };
        using var service = fixture.Service(fixture.Coordinator());
        await service.RunOnceAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(30));
        fixture.PublishSettings();
        Assert.False(fixture.Plans.DecisionFacts.AutomaticUpdatesEnabled);
        Assert.Equal(1, fixture.SettingsReads);
        Assert.Equal(0, fixture.Checks);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, fixture.SettingsReads);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        Assert.Equal(0, fixture.PackageUsage.Checks);
        fixture.PublishSettings();
        Assert.Equal(2, fixture.SettingsReads); // Finite calls never attach a publication callback.
    }

    [Fact]
    public async Task ControllerStartAfterEnablingChecksWithoutWaitingForTimer()
    {
        using var fixture = new Fixture { Enabled = false, Listing = Catalog("0.2.4") };
        using var service = fixture.Service(fixture.Coordinator());
        Assert.Equal(0, fixture.SettingsReads);
        Assert.Equal(0, fixture.Checks);
        fixture.Enabled = true;
        fixture.PublishSettings();
        Assert.True(fixture.Plans.DecisionFacts.AutomaticUpdatesEnabled);
        Assert.Equal(0, fixture.SettingsReads);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
            Assert.Equal(1, fixture.Checks);
            Assert.True(fixture.SettingsReads >= 1);
            Assert.Equal(0, fixture.PackageUsage.Checks);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PendingPackageUseIsCheckedOnlyAtSixtySecondIntervalThenAppliesOnce()
    {
        using var fixture = new Fixture();
        fixture.PackageUsage.InUse = true;
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.Clock.WaitForTimerAsync(AutomaticProductUpdateService.PackageUseRecheckInterval);
            Assert.Equal("pendingApply", coordinator.Status.Stage);
            Assert.Equal(1, fixture.PackageUsage.Checks);
            var readsAfterStageChanges = fixture.SettingsReads;
            fixture.Clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(1, fixture.PackageUsage.Checks);
            Assert.Equal(readsAfterStageChanges, fixture.SettingsReads);
            fixture.PackageUsage.InUse = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Launched.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await service.StopAsync(CancellationToken.None); }
        Assert.Equal(2, fixture.PackageUsage.Checks);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal("waitingForExit", coordinator.Status.Stage);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPreparationWakesSleepingEnabledOwnerAndUsesSafeWindow(bool packageInUse)
    {
        using var fixture = new Fixture { Listing = Catalog("0.2.4") };
        fixture.PackageUsage.InUse = packageInUse;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<PreparedProductUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PrepareOverride = (_, _) => { entered.TrySetResult(); return finish.Task; };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
            var before = fixture.Clock.GetUtcNow();
            var readsBeforeSubmit = fixture.SettingsReads;
            fixture.Listing = Catalog("0.2.4", Option("0.3.0", "stable"));
            coordinator.Submit("version:0.3.0");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.WaitForSettingsReadsAsync(readsBeforeSubmit + 1);
            Assert.Equal(0, fixture.Clock.ActiveTimers); // Busy manual prepare has no deadline.
            Assert.Equal(0, fixture.PackageUsage.Checks);
            finish.SetResult(new("0.3.0", fixture.Workspace, Path.Combine(fixture.Workspace, "package")));

            if (packageInUse)
            {
                await fixture.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(before, fixture.Clock.GetUtcNow());
                Assert.Equal("pendingApply", coordinator.Status.Stage);
                Assert.Equal(1, fixture.PackageUsage.Checks);
                Assert.Equal(0, fixture.Launches);
                fixture.Clock.Advance(TimeSpan.FromSeconds(59));
                Assert.Equal(1, fixture.PackageUsage.Checks);
                fixture.PackageUsage.InUse = false;
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            await fixture.Launched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(before + (packageInUse ? TimeSpan.FromSeconds(60) : TimeSpan.Zero), fixture.Clock.GetUtcNow());
        }
        finally
        {
            finish.TrySetResult(new("0.3.0", fixture.Workspace, Path.Combine(fixture.Workspace, "package")));
            await service.StopAsync(CancellationToken.None);
        }
        Assert.Equal(0, fixture.Plans.Current.Version); // No settings publication was needed.
        Assert.Equal(packageInUse ? 2 : 1, fixture.PackageUsage.Checks);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal("waitingForExit", coordinator.Status.Stage);
    }

    [Fact]
    public async Task ManualPendingUpdateRemainsAvailableWithAutomaticWorkerStopped()
    {
        using var fixture = new Fixture { Enabled = false };
        var coordinator = fixture.Coordinator();
        using var service = fixture.Service(coordinator);
        await service.RunOnceAsync(CancellationToken.None);
        coordinator.Submit("version:0.3.0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var status in coordinator.SubscribeAsync(timeout.Token))
            if (status.Stage == "pendingApply") break;
        fixture.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(1, fixture.SettingsReads);
        Assert.Equal(0, fixture.PackageUsage.Checks);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        Assert.Equal(0, fixture.Plans.Current.Version);
        Assert.Equal("pendingApply", coordinator.Status.Stage);
        Assert.Single(fixture.Prepared);

        // The explicit user action remains available even with automatic update off.
        await coordinator.ApplyAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal("waitingForExit", coordinator.Status.Stage);
    }

    [Fact]
    public async Task IntervalPublicationReschedulesFromLastCheckWithoutPollingSettings()
    {
        using var fixture = new Fixture { Listing = Catalog("0.2.4") };
        using var service = fixture.Service(fixture.Coordinator());
        await service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(24));
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            fixture.CheckIntervalHours = 48;
            fixture.PublishSettings();
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(47));
            Assert.Equal(1, fixture.Checks);
            Assert.True(fixture.SettingsReads >= 2);
            fixture.Clock.Advance(TimeSpan.FromHours(47) - TimeSpan.FromMinutes(1));
            Assert.Equal(1, fixture.Checks);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Clock.WaitForTimerAsync(TimeSpan.FromHours(48));
            Assert.Equal(2, fixture.Checks);
            Assert.True(fixture.SettingsReads >= 2);
            Assert.Equal(0, fixture.PackageUsage.Checks);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnavailableCatalogRetainsExplicitFailureRatherThanReportingNoUpdate()
    {
        using var fixture = new Fixture();
        fixture.Listing = fixture.Listing with { Status = "stale", Complete = false };
        var coordinator = fixture.Coordinator();
        await coordinator.CheckAutomaticAsync(CancellationToken.None);
        Assert.Equal("failed", coordinator.Status.CheckResult);
        Assert.Equal("update.catalogUnavailable", coordinator.Status.ErrorCode);
        Assert.Empty(fixture.Prepared);
        Assert.Equal("update.catalogUnavailable", fixture.Coordinator().Status.ErrorCode);
    }

    [Fact]
    public async Task DownloadRejectsMismatchedSha256Digest()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new ArchiveHandler());
        var failure = await Assert.ThrowsAsync<ProductUpdateFailure>(() => ProductUpdateCoordinator.DownloadAndVerifyAsync(
            client, Option("0.3.0", "stable").DownloadUrl,
            Path.Combine(fixture.Root, "mismatch.zip"), new string('0', 64), CancellationToken.None));
        Assert.Equal("update.checksumMismatch", failure.Code);
    }

    [Theory]
    [InlineData(true, null, "completed")]
    [InlineData(false, "update.managerStartFailed", "error")]
    public void ManagerResultSurvivesServiceRestart(bool success, string? failureCode, string expectedStage)
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new("waitingForExit", "0.3.0", fixture.Workspace, null, fixture.Clock.GetUtcNow()));
        var directory = UpdateManagerPaths.InstalledDirectory(fixture.Root);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "last-operation-result.json"), JsonSerializer.Serialize(new
        {
            Arguments = new[] { "--apply", Path.Combine(fixture.Workspace, "package"), fixture.Root },
            FinishedAt = fixture.Clock.GetUtcNow().AddMinutes(1), Success = success, FailureCode = failureCode
        }));
        var status = fixture.Coordinator().Status;
        Assert.Equal(expectedStage, status.Stage);
        Assert.Equal(failureCode, status.ErrorCode);
        Assert.Equal(failureCode, status.Error);
        if (!success) Assert.Equal("0.3.0", status.FailedVersion);
    }

    [Theory]
    [InlineData("committed", "completed", null)]
    [InlineData("rolledBack", "rolledBack", "update.rolledBack")]
    [InlineData("recovered", "rolledBack", "update.rolledBack")]
    [InlineData("recoveryFailed", "error", "update.recoveryRequired")]
    public void DurableTransactionJournalProjectsFinalOutcomeAfterRestart(string journalStage, string expectedStage, string? code)
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new("waitingForExit", "0.3.0", fixture.Workspace, null, fixture.Clock.GetUtcNow()));
        var path = Path.Combine(fixture.Workspace, "update-transaction.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { InstallRoot = fixture.Root, targetVersion = "0.3.0", stage = journalStage }));
        File.SetLastWriteTimeUtc(path, fixture.Clock.GetUtcNow().AddMinutes(1).UtcDateTime);
        var status = fixture.Coordinator().Status;
        Assert.Equal(expectedStage, status.Stage);
        Assert.Equal(code, status.ErrorCode);
        Assert.Equal(code, status.Error);
        if (code is not null) Assert.Equal("0.3.0", status.FailedVersion);
    }

    [Theory]
    [InlineData(false, "error", "update.managerStartFailed")]
    [InlineData(true, "rolledBack", "update.rolledBack")]
    public void RollbackJournalOnlyRefinesNewerFailureResultForTheSamePackageAttempt(bool matchingSource, string expectedStage, string code)
    {
        using var fixture = new Fixture();
        var newSource = Path.Combine(fixture.Workspace, "package");
        fixture.Store.Write(new("waitingForExit", "v0.3.0", fixture.Workspace, null, fixture.Clock.GetUtcNow()));
        var manager = UpdateManagerPaths.InstalledDirectory(fixture.Root);
        Directory.CreateDirectory(manager);
        File.WriteAllText(Path.Combine(manager, "last-operation-result.json"), JsonSerializer.Serialize(new
        {
            Arguments = new[] { "--apply", newSource, fixture.Root },
            FinishedAt = fixture.Clock.GetUtcNow().AddMinutes(2), Success = false,
            FailureCode = "update.managerStartFailed", TargetVersion = "0.3.0"
        }));
        var oldTransaction = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "ResourceManager-update-old-attempt");
        Directory.CreateDirectory(oldTransaction);
        var journal = Path.Combine(oldTransaction, "update-transaction.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new
        {
            InstallRoot = fixture.Root, targetVersion = "v0.3.0", stage = "rolledBack",
            sourcePackageRoot = matchingSource ? newSource : Path.Combine(oldTransaction, "old-download", "package")
        }));
        File.SetLastWriteTimeUtc(journal, fixture.Clock.GetUtcNow().AddMinutes(1).UtcDateTime);
        var restored = fixture.Coordinator().Status;
        Assert.Equal(expectedStage, restored.Stage);
        Assert.Equal(code, restored.ErrorCode);
        Assert.Equal("0.3.0", ProductUpdateCoordinator.VersionIdentity(restored.FailedVersion));
    }

    private static ProductVersionCatalog Catalog(string installed, params ProductVersionOption[] options)
        => new("resource-manager", "product", installed, "loaded", true, DateTimeOffset.UtcNow, options, null);

    private static ProductVersionOption Option(string version, string channel, bool selectable = true)
    {
        var name = $"ResourceManager-{version}-win-x64.zip";
        var url = $"https://github.com/BIOcanse/Resource-Manager/releases/download/{version}/{name}";
        return new("version:" + version, version, "0.3", channel, null, name, url, url + ".sha256", selectable,
            selectable ? ProductVersionEligibility.Eligible : ProductVersionEligibility.NotNewer);
    }

    private sealed class MutableClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-1);
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveTimers { get { lock (gate) return timers.Count(timer => !timer.Disposed && timer.Due is not null); } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer;
            lock (gate)
            {
                timer = new(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
            }
            return timer;
        }
        public async Task WaitForTimerAsync(TimeSpan remaining)
        {
            for (var attempts = 0; attempts < 100; attempts++)
            {
                Task signal;
                lock (gate)
                {
                    if (timers.Any(timer => !timer.Disposed && timer.Due == now + remaining)) return;
                    signal = changed.Task;
                }
                await signal.WaitAsync(TimeSpan.FromSeconds(3));
            }
            throw new InvalidOperationException("Timer did not settle at the expected deadline.");
        }
        private void NotifyChanged()
        {
            var previous = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> ready;
            lock (gate)
            {
                now += amount;
                ready = timers.Where(timer => !timer.Disposed && timer.Due is { } due && due <= now).ToList();
                foreach (var timer in ready)
                    timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : null;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(MutableClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public bool Disposed { get; private set; }
            public DateTimeOffset? Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                    Period = period;
                    owner.NotifyChanged();
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) { Disposed = true; Due = null; owner.NotifyChanged(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class PackageUsage : IProductPackageUsage
    {
        public bool InUse { get; set; }
        public int Checks { get; private set; }
        public Func<bool>? ReadUsage { get; set; }
        public bool AnyInUse() { Checks++; return ReadUsage?.Invoke() ?? InUse; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string parent = Path.Combine(Path.GetTempPath(), "rm-auto-update-test-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Workspace { get; }
        public ProductUpdateStateStore Store { get; }
        public MutableClock Clock { get; } = new();
        public PackageUsage PackageUsage { get; } = new();
        public string Mode { get; set; } = AppUpdateModes.Automatic;
        public bool Enabled { get => Mode != AppUpdateModes.Off; set => Mode = value ? AppUpdateModes.Automatic : AppUpdateModes.Off; }
        public bool PeriodicCheckEnabled { get; set; } = true;
        public int CheckIntervalHours { get; set; } = 24;
        public Connectivity Connectivity { get; } = new();
        private int settingsReads;
        private readonly SemaphoreSlim settingsRead = new(0);
        public int SettingsReads => Volatile.Read(ref settingsReads);
        public Action? SettingsReadObserved { get; set; }
        public RuntimePlanProvider Plans { get; } = new(new HostManagerDeploymentState());
        public TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Checks { get; private set; }
        public int Launches { get; private set; }
        public List<string> Prepared { get; } = [];
        public Exception? PreparationError { get; set; }
        public Func<ProductVersionOption, Exception?>? PreparationErrorForOption { get; set; }
        public Func<ProductVersionOption, CancellationToken, Task<PreparedProductUpdate>>? PrepareOverride { get; set; }
        public ProductVersionCatalog Listing { get; set; } = Catalog("0.2.4", Option("0.3.0", "stable"));

        public Fixture()
        {
            Root = Path.Combine(parent, "install");
            Workspace = Path.Combine(parent, "ResourceManager-update-test");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Workspace);
            Store = new(Root);
        }

        public ProductUpdateCoordinator Coordinator() => new(
            _ => { Checks++; return Task.FromResult(Listing); },
            (option, token) =>
            {
                Prepared.Add(option.Version);
                if (PrepareOverride is { } custom) return custom(option, token);
                return (PreparationErrorForOption?.Invoke(option) ?? PreparationError) is { } error ? Task.FromException<PreparedProductUpdate>(error)
                    : Task.FromResult(new PreparedProductUpdate(option.Version, Workspace, Path.Combine(Workspace, "package")));
            },
            (_, _) => { Launches++; Launched.TrySetResult(); return Task.CompletedTask; }, Store, Clock, NullLogger<ProductUpdateCoordinator>.Instance);

        public void PublishSettings() => Assert.False(Plans.Publish(
            Plans.Current with
            {
                Version = Plans.Current.Version + 1,
                AutomaticUpdatesEnabled = Enabled && PeriodicCheckEnabled,
                HostManager = Plans.Current.Version == 0 ? HostManagerTestPlanFactory.CreatePlan() : Plans.Current.HostManager
            }).HasDeliveryFailures);

        public async Task WaitForSettingsReadsAsync(int count)
        {
            while (SettingsReads < count)
                Assert.True(await settingsRead.WaitAsync(TimeSpan.FromSeconds(3)), "Settings publication was not consumed.");
        }

        public AutomaticProductUpdateService Service(ProductUpdateCoordinator coordinator)
            => new(_ =>
                {
                    var settings = new AppUpdateSettings(Mode, PeriodicCheckEnabled, CheckIntervalHours);
                    Interlocked.Increment(ref settingsReads);
                    SettingsReadObserved?.Invoke();
                    settingsRead.Release();
                    return Task.FromResult(settings);
                }, coordinator, PackageUsage, Plans, Clock,
                NullLogger<AutomaticProductUpdateService>.Instance, Connectivity);

        public void Dispose()
        {
            settingsRead.Dispose();
            // Owned test files only; keep cleanup iterative and bounded.
            var pending = new Stack<string>();
            var directories = new List<string>();
            pending.Push(parent);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                directories.Add(directory);
                if (directories.Count > 100) throw new InvalidOperationException("Test fixture cleanup exceeded its bound.");
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
        }
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }
}
