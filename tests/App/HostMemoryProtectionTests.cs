using BlockUids = ResourceManager.App.StateItems.BlockUids;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryProtectionTests
{
    private const ulong ReserveSize = 1024 * 1024;
    [Fact]
    public async Task WireDefaultsPointReplacementAndEnableRejectionUseExistingSettingsCommit()
    {
        await using var fixture = await Fixture.StartAsync();
        using var point = await fixture.Client.GetAsync("/api/memory-protection/normal-running-point");
        Assert.Equal("null", await point.Content.ReadAsStringAsync());
        var status = await fixture.Status();
        using var statusWire = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/memory-protection"));
        Assert.False(statusWire.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(200UL, statusWire.RootElement.GetProperty("currentCommit").GetProperty("currentBytes").GetUInt64());
        Assert.Equal(JsonValueKind.Null, statusWire.RootElement.GetProperty("normalRunningPoint").ValueKind);
        Assert.False(status.Enabled);
        Assert.Equal(95, status.ThresholdPercent);
        Assert.Equal(1, status.CheckIntervalSeconds);
        Assert.Empty(status.Reserve.Entries);
        Assert.False(status.Reserve.Built);
        Assert.Equal(0UL, status.Reserve.ConfiguredSizeBytes);
        Assert.Equal(0UL, status.Reserve.RemainingBytes);
        Assert.Equal("reserve-not-configured", status.Reserve.Reason);
        Assert.False(statusWire.RootElement.TryGetProperty("dumpDirectory", out _));
        Assert.Equal(0UL, statusWire.RootElement.GetProperty("reserve").GetProperty("configuredSizeBytes").GetUInt64());
        Assert.Equal(950m, status.TriggerBytes);
        Assert.Equal(750m, status.RemainingBytes);
        Assert.Equal("no-normal-running-point", status.InactiveReason);
        Assert.Equal(0, fixture.Source.Active);

        using var rejected = await fixture.Patch(new { enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var rejection = (await rejected.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!;
        Assert.Equal("no-normal-running-point", rejection.RuntimeFailureCode);
        Assert.False((await fixture.Stored()).Settings.MemoryProtection!.Enabled);
        Assert.Equal(0, fixture.Source.Active);

        var first = await fixture.CreatePoint(ensureReserve: false);
        Assert.Equal(1, first.ProcessCount);
        Assert.Equal(200UL, first.Commit.CurrentBytes);
        fixture.Source.Publish(300, 30, [Process(10, "20", 123)]);
        var next = await fixture.CreatePoint(ensureReserve: false);
        using var pointWire = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/memory-protection/normal-running-point"));
        var processWire = pointWire.RootElement.GetProperty("processes")[0];
        Assert.Equal("20", processWire.GetProperty("processStartKey").GetString());
        Assert.Equal(123UL, processWire.GetProperty("privateCommitBytes").GetUInt64());
        Assert.False(processWire.TryGetProperty("isResourceManager", out _));
        Assert.Equal("20", Assert.Single(next.Processes).ProcessStartKey);
        Assert.Equal(next.At, fixture.Protection.ReadPoint()!.At);
        Assert.Equal(next.Processes, fixture.Protection.ReadPoint()!.Processes);
        Assert.NotEqual(first, next);
        using var response = await fixture.Patch(new { thresholdPercent = 90, checkIntervalSeconds = 2,
            reserves = new[] { new { location = fixture.ReserveDirectory, sizeBytes = ReserveSize } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        status = await fixture.Status();
        Assert.Equal(900m, status.TriggerBytes);
        Assert.Equal(600m, status.RemainingBytes);
        Assert.Equal(2, status.CheckIntervalSeconds);
        Assert.Equal(fixture.ReserveDirectory, Assert.Single(status.Reserve.Entries).Location);
        Assert.Equal(ReserveSize, status.Reserve.ConfiguredSizeBytes);
        Assert.True(status.Reserve.Built);
        Assert.Equal(ReserveSize, status.Reserve.RemainingBytes);
        var stored = await fixture.Stored();
        Assert.Equal("1.0.36", stored.Settings.Version);
        using var settingsWire = JsonDocument.Parse(JsonSerializer.Serialize(stored.Settings.MemoryProtection,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var reserveWire = Assert.Single(settingsWire.RootElement.GetProperty("reserves").EnumerateArray());
        Assert.Equal(fixture.ReserveDirectory, reserveWire.GetProperty("location").GetString());
        Assert.Equal(ReserveSize, reserveWire.GetProperty("sizeBytes").GetUInt64());
        Assert.False(settingsWire.RootElement.TryGetProperty("reserveLocation", out _));
        Assert.False(settingsWire.RootElement.TryGetProperty("reserveSizeBytes", out _));
        Assert.False(settingsWire.RootElement.TryGetProperty("dumpDirectory", out _));
    }

    [Theory]
    [InlineData(0, 1, "invalid-threshold-percent")]
    [InlineData(101, 1, "invalid-threshold-percent")]
    [InlineData(95, 0, "invalid-check-interval")]
    public async Task InvalidSettingsAreRejectedBeforeCommit(double threshold, double interval, string reason)
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new { thresholdPercent = threshold, checkIntervalSeconds = interval });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
    }

    [Fact]
    public async Task PointWithoutConfiguredReserveCannotEnableAndUnconfiguredRebuildDoesNotSample()
    {
        await using var fixture = await Fixture.StartAsync();
        using (var rebuild = await fixture.Client.PostAsync("/api/memory-protection/reserve/rebuild", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, rebuild.StatusCode);
            var result = (await rebuild.Content.ReadFromJsonAsync<HostMemoryProtectionStatus>())!;
            Assert.False(result.Reserve.Built);
            Assert.Equal("reserve-not-configured", result.Reserve.Reason);
            Assert.Equal(200UL, result.CurrentCommit!.CurrentBytes);
        }
        Assert.Equal(0, fixture.Source.Deliveries);
        await fixture.CreatePoint(ensureReserve: false);
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new { enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("reserve-not-configured", (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
        Assert.False((await fixture.Status()).Enabled);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Empty(fixture.Executor.Opened);
        Assert.Empty(fixture.Executor.Executed);
    }

    [Fact]
    public async Task NonalignedReserveSettingIsRejectedWithActualClusterSizeBeforeCommit()
    {
        await using var fixture = await Fixture.StartAsync();
        Directory.CreateDirectory(fixture.ReserveDirectory);
        var cluster = HostMemoryDiskReserveNative.Volume(fixture.ReserveDirectory).Cluster;
        Assert.True(cluster > 1);
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new
        {
            reserves = new[] { new { location = fixture.ReserveDirectory, sizeBytes = cluster + 1 } }
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!;
        Assert.Equal($"reserve-size-must-be-whole-clusters:cluster-bytes={cluster}", result.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
        Assert.Empty((await fixture.Status()).Reserve.Entries);
        Assert.Equal(0, fixture.Source.Deliveries);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Empty(fixture.Executor.Opened);
        Assert.Empty(fixture.Executor.Executed);
    }

    [Fact]
    public async Task ImpossibleReserveStoresDisabledIntentButCannotEnableOrRun()
    {
        await using var fixture = await Fixture.StartAsync();
        const ulong impossibleSize = (ulong)long.MaxValue - 65535;
        await fixture.BuildReserve(impossibleSize);
        var stored = await fixture.Stored();
        Assert.False(stored.Settings.MemoryProtection!.Enabled);
        Assert.Equal(impossibleSize, Assert.Single(stored.Settings.MemoryProtection.Reserves!).SizeBytes);
        Assert.Equal(fixture.ReserveDirectory, Assert.Single(stored.Settings.MemoryProtection.Reserves!).Location);
        var status = await fixture.Status();
        Assert.False(status.Reserve.Built);
        Assert.Equal(impossibleSize, status.Reserve.ConfiguredSizeBytes);
        Assert.Equal(0UL, status.Reserve.RemainingBytes);
        Assert.Equal("insufficient-disk-space-for-reserve", status.Reserve.Reason);
        Assert.Equal(0, fixture.Source.Deliveries);
        using (var rebuild = await fixture.Client.PostAsync("/api/memory-protection/reserve/rebuild", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, rebuild.StatusCode);
            var result = (await rebuild.Content.ReadFromJsonAsync<HostMemoryProtectionStatus>())!;
            Assert.Equal(status.Reserve.ConfiguredSizeBytes, result.Reserve.ConfiguredSizeBytes);
            Assert.Equal(status.Reserve.Built, result.Reserve.Built);
            Assert.Equal(status.Reserve.Reason, result.Reserve.Reason);
            Assert.Equal(status.Reserve.RemainingBytes, result.Reserve.RemainingBytes);
            Assert.Equal(status.Reserve.Entries.ToArray(), result.Reserve.Entries.ToArray());
        }
        Assert.Equal(0, fixture.Source.Deliveries);
        await fixture.CreatePoint(ensureReserve: false);
        var deliveries = fixture.Source.Deliveries;
        using var enabled = await fixture.Patch(new { enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, enabled.StatusCode);
        Assert.Equal("insufficient-disk-space-for-reserve", (await enabled.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.False((await fixture.Stored()).Settings.MemoryProtection!.Enabled);
        fixture.Source.Publish(990, 99, [Process(100, "1", 1000)]);
        Assert.Equal(deliveries, fixture.Source.Deliveries);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection));
        Assert.Empty(fixture.Executor.Opened);
        Assert.Empty(fixture.Executor.Executed);
    }

    [Fact]
    public async Task EnableCannotChangeReserveConfigurationBeforeItsNewReserveExists()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new { enabled = true,
            reserves = new[] { new { location = fixture.ReserveDirectory, sizeBytes = ReserveSize * 2 } } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("disk-reserve-not-built", (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
        Assert.Equal(ReserveSize, (await fixture.Status()).Reserve.ConfiguredSizeBytes);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Empty(fixture.Executor.Executed);
    }

    [Fact]
    public async Task MultipleReserveLocationsPublishTotalsAndOrderedPerEntryCapacity()
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.Patch(new
        {
            reserves = new[]
            {
                new { location = fixture.ReserveDirectory, sizeBytes = ReserveSize },
                new { location = fixture.SecondReserveDirectory, sizeBytes = ReserveSize * 2 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await fixture.Status();
        Assert.False(status.Enabled);
        Assert.True(status.Reserve.Built);
        Assert.Equal(ReserveSize * 3, status.Reserve.ConfiguredSizeBytes);
        Assert.Equal(ReserveSize * 3, status.Reserve.RemainingBytes);
        Assert.Collection(status.Reserve.Entries,
            first => { Assert.Equal(fixture.ReserveDirectory, first.Location); Assert.Equal(ReserveSize, first.ConfiguredSizeBytes); },
            second => { Assert.Equal(fixture.SecondReserveDirectory, second.Location); Assert.Equal(ReserveSize * 2, second.ConfiguredSizeBytes); });
        Assert.All(status.Reserve.Entries, entry =>
        {
            Assert.True(entry.Built);
            Assert.Null(entry.Reason);
            Assert.True(entry.ClusterBytes > 0);
            Assert.Equal(0UL, entry.ConfiguredSizeBytes % entry.ClusterBytes);
            Assert.Equal(entry.ConfiguredSizeBytes, entry.RemainingBytes);
        });
        using var wire = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/memory-protection"));
        var reserve = wire.RootElement.GetProperty("reserve");
        Assert.Equal(ReserveSize * 3, reserve.GetProperty("remainingBytes").GetUInt64());
        Assert.Equal(2, reserve.GetProperty("entries").GetArrayLength());
        Assert.Equal(fixture.SecondReserveDirectory, reserve.GetProperty("entries")[1].GetProperty("location").GetString());
        Assert.True(reserve.GetProperty("entries")[0].GetProperty("clusterBytes").GetUInt64() > 0);
        Assert.False(wire.RootElement.TryGetProperty("dumpDirectory", out _));
        Assert.Equal(0, fixture.Source.Deliveries);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Empty(fixture.Executor.Opened);
    }

    [Fact]
    public async Task ConsumedReserveIsReportedAndOnlyExplicitRebuildRefillsIt()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        fixture.Executor.SliceBytes = 8193;
        using (var enabled = await fixture.Patch(new { enabled = true })) Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(20, "1", 1000)]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 1);
        using (var disabled = await fixture.Patch(new { enabled = false })) Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await Until(() => fixture.Source.Active == 0);
        var sample = Assert.Single(fixture.Executor.ReserveSamples);
        Assert.Equal(ReserveSize, sample.Before);
        Assert.InRange(sample.AfterWrite, 1UL, ReserveSize - 1);
        Assert.Equal(sample.AfterWrite, sample.AfterOverwrite);
        Assert.Equal(sample.AfterWrite, sample.AfterShrink);
        var consumed = await fixture.Status();
        Assert.Equal(sample.AfterWrite, consumed.Reserve.RemainingBytes);
        Assert.True(consumed.Reserve.Built);
        using (var wire = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/memory-protection")))
            Assert.Equal(sample.AfterWrite, wire.RootElement.GetProperty("reserve").GetProperty("remainingBytes").GetUInt64());
        var action = Assert.Single(consumed.Actions);
        Assert.Equal(8193UL, action.BytesWritten);
        var slicePath = Assert.Single(action.DumpPaths);
        Assert.Equal(1, new FileInfo(slicePath).Length);

        await fixture.BuildReserve();
        using (var frequency = await fixture.Patch(new { checkIntervalSeconds = 2 })) Assert.Equal(HttpStatusCode.OK, frequency.StatusCode);
        Assert.Equal(sample.AfterWrite, (await fixture.Status()).Reserve.RemainingBytes);
        var deliveries = fixture.Source.Deliveries;
        using var rebuilt = await fixture.Client.PostAsync("/api/memory-protection/reserve/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, rebuilt.StatusCode);
        var result = (await rebuilt.Content.ReadFromJsonAsync<HostMemoryProtectionStatus>())!;
        Assert.True(result.Reserve.Built);
        Assert.Equal(ReserveSize, result.Reserve.RemainingBytes);
        Assert.Equal(consumed.NormalRunningPoint, result.NormalRunningPoint);
        Assert.Equal(deliveries, fixture.Source.Deliveries);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Equal(1, new FileInfo(slicePath).Length);
    }

    [Fact]
    public async Task ProductionParentReadsBothFactsAndSelectionUsesGrowthExactIdentityAndExclusions()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Source.Publish(200, 20, [Process(10, "1", 900), Process(11, "1", 10), Process(12, "old", 1000)]);
        await fixture.CreatePoint();
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection));
        fixture.Executor.Excluded[40] = "cannot-open";
        fixture.Executor.Excluded[41] = "critical-process";
        using var enabled = await fixture.Patch(new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96,
            [Process(10, "1", 950), Process(11, "1", 200), Process(12, "new", 300), Process(40, "1", 5000),
                Process(41, "1", 4000), Process(42, "1", 6000) with { IsResourceManager = true }]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 1);
        var action = Assert.Single(fixture.Protection.ReadStatus().Actions);
        Assert.Equal(12, action.ProcessId);
        Assert.Equal("new", action.ProcessStartKey);
        Assert.Equal(0UL, action.BaselineBytes);
        Assert.Equal(300m, action.GrowthBytes);
        Assert.DoesNotContain(42, fixture.Executor.Opened);
        var excluded = fixture.Protection.ReadStatus().ExcludedCandidates;
        Assert.Contains(excluded, item => item.ProcessId == 40 && item.Reason == "cannot-open");
        Assert.Contains(excluded, item => item.ProcessId == 41 && item.Reason == "critical-process");
        Assert.Contains(excluded, item => item.ProcessId == 42 && item.Reason == "resource-manager-process");

        fixture.Source.Publish(970, 97, [Process(40, "1", 8000), Process(11, "1", 210)]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 2);
        Assert.Equal(1, fixture.Executor.Opened.Count(id => id == 40));
        Assert.Equal(11, fixture.Protection.ReadStatus().Actions[0].ProcessId);
        var decision = fixture.Scheduler.Read().CurrentDecisions.Single(item => item.ParentId == BlockUids.Program);
        Assert.Contains(decision.Facts, item => item.Key == HostMemoryProtectionService.EnabledFact && item.Value);
        Assert.Contains(decision.Facts, item => item.Key == HostMemoryProtectionService.PointFact && item.Value);
        Assert.Contains(decision.Facts, item => item.Key == HostMemoryProtectionService.ReserveFact && item.Value);

        using var disabled = await fixture.Patch(new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await Until(() => fixture.Source.Active == 0 && fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection) == BusinessBlockState.Stopped);
        fixture.Source.Publish(999, 99, [Process(11, "1", 9000)]);
        Assert.Equal(2, fixture.Protection.ReadStatus().Actions.Count);
    }

    [Fact]
    public async Task SuspendFailureTriesNextCandidateAndDisableDrainsOneInFlightAction()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        fixture.Executor.SuspendFailures.Add(20);
        fixture.Executor.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var enabled = await fixture.Patch(new { enabled = true });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(20, "1", 800), Process(21, "1", 700), Process(22, "1", 600)]);
        await Until(() => fixture.Executor.Executed.Contains(21));
        using var disabled = await fixture.Patch(new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.NotEqual(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection));
        Assert.DoesNotContain(22, fixture.Executor.Executed);
        fixture.Executor.Block.SetResult();
        await Until(() => fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection) == BusinessBlockState.Stopped);
        Assert.Equal(2, fixture.Protection.ReadStatus().Actions.Count);
        Assert.Contains(fixture.Protection.ReadStatus().Actions, item => item.Outcome == "suspend-failed");
        Assert.Equal(1, fixture.Executor.MaxConcurrent);
        Assert.Equal(0, fixture.Source.Active);
    }

    [Fact]
    public async Task EachDeliveryComparesThresholdAndRecordsHaveAnUpperBound()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        using var enabled = await fixture.Patch(new { enabled = true });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(940, 94, [Process(10, "1", 9999)]);
        await Until(() => fixture.Source.Deliveries >= 3);
        Assert.Empty(fixture.Executor.Executed);
        for (var index = 0; index < HostMemoryProtectionService.RecordLimit + 2; index++)
        {
            fixture.Source.Publish(950, 95, [Process(100 + index, "1", (ulong)(index + 1))]);
            var count = index + 1;
            await Until(() => fixture.Executor.Executed.Count == count && fixture.Protection.ReadStatus().Actions.Count == Math.Min(count, HostMemoryProtectionService.RecordLimit));
        }
        var status = fixture.Protection.ReadStatus();
        Assert.Equal(HostMemoryProtectionService.RecordLimit, status.Actions.Count);
        Assert.Equal(165, status.Actions[0].ProcessId);
        Assert.Equal(102, status.Actions[^1].ProcessId);
    }

    [Theory]
    [InlineData("dumpThenTerminate", true, true)]
    [InlineData("dumpThenTerminate", false, false)]
    [InlineData("terminateOnly", true, false)]
    [InlineData("terminateOnly", false, false)]
    public async Task ActionModeAndSoftwareFlagReachExecutorWithoutExcludingTheSoftware(string mode, bool flagged, bool expectedNoDump)
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SoftwarePolicies.SaveSoftwarePolicyAsync(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("test-game", "Test game") with { HostMemoryProtectionNoDump = flagged },
            CancellationToken.None);
        await fixture.CreatePoint();
        using var enabled = await fixture.Patch(new { enabled = true, actionMode = mode });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(mode, (await fixture.Status()).ActionMode);
        Assert.Equal(mode, (await fixture.Stored()).Settings.MemoryProtection!.ActionMode);
        fixture.Source.Publish(960, 96, [Process(20, "1", 800) with { SoftwareId = "TEST-GAME" }, Process(21, "1", 700)]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 1);
        Assert.Equal(20, Assert.Single(fixture.Executor.Executed));
        Assert.Empty(fixture.Protection.ReadStatus().ExcludedCandidates);
        Assert.Equal(new HostMemoryActionOptions(mode, expectedNoDump), Assert.Single(fixture.Executor.Options));
    }

    [Fact]
    public async Task InvalidActionModeIsRejectedBeforeSettingsCommit()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Stored();
        Assert.Equal("dumpThenTerminate", before.Settings.MemoryProtection!.ActionMode);
        Assert.Equal("dumpThenTerminate", (await fixture.Status()).ActionMode);
        using var rejected = await fixture.Patch(new { actionMode = "invalid" });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var result = (await rejected.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!;
        Assert.Equal("invalid-action-mode", result.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
    }

    private static HostMemoryProcess Process(int pid, string key, ulong bytes) => new(pid, key, "fake-" + pid, bytes);
    [Fact]
    public async Task SavedEnablementAfterRestartDoesNotRunWithoutAnInMemoryPoint()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.BuildReserve();
        // This is the production startup-publication path with a persisted switch, using only fake sources.
        await fixture.Protection.ApplySettingsAsync(new(Enabled: true,
            Reserves: [new(fixture.ReserveDirectory, ReserveSize)]));
        var status = fixture.Protection.ReadStatus();
        Assert.True(status.Enabled);
        Assert.Equal("no-normal-running-point", status.InactiveReason);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection));
        Assert.Equal(0, fixture.Source.Active);
        await fixture.CreatePoint(ensureReserve: false);
        await Until(() => fixture.Source.Active == 1);
        Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BlockUids.HostMemoryProtection));
    }

    [Fact]
    public async Task ReplacingPointClearsTheFailedOpenExclusions()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        fixture.Executor.Excluded[40] = "cannot-open";
        using var enabled = await fixture.Patch(new { enabled = true });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(40, "1", 1000), Process(41, "1", 100)]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 1);
        using var disabled = await fixture.Patch(new { enabled = false });
        await Until(() => fixture.Source.Active == 0);
        fixture.Source.Publish(200, 20, [Process(40, "1", 1000), Process(41, "1", 100)]);
        await fixture.CreatePoint();
        using var reenabled = await fixture.Patch(new { enabled = true });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(40, "1", 2000), Process(41, "1", 200)]);
        await Until(() => fixture.Protection.ReadStatus().Actions.Count == 2);
        Assert.Equal(2, fixture.Executor.Opened.Count(id => id == 40));
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture(WebApplication app, string root, FakeSource source, FakeExecutor executor) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        public FakeSource Source => source;
        public FakeExecutor Executor => executor;
        public string ReserveDirectory => Path.Combine(root, "Slices");
        public string SecondReserveDirectory => Path.Combine(root, "SlicesSecond");
        public HostMemoryProtectionService Protection => app.Services.GetRequiredService<HostMemoryProtectionService>();
        public BusinessBlockScheduler Scheduler => app.Services.GetRequiredService<BusinessBlockScheduler>();
        public IGpuPlacementPolicyStore SoftwarePolicies => app.Services.GetRequiredService<IGpuPlacementPolicyStore>();
        public Task<AppSettingsUpdateResult> Stored() => app.Services.GetRequiredService<IAppSettingsStore>().LoadReadOnlyAsync(CancellationToken.None);
        public async Task<HttpResponseMessage> Patch(object settings)
            => await Client.PatchAsJsonAsync("/api/memory-protection", new { expectedRevision = (await Stored()).Revision, changes = new { memoryProtection = settings } });
        public async Task BuildReserve(ulong size = ReserveSize)
        {
            using var response = await Patch(new { reserves = new[] { new { location = ReserveDirectory, sizeBytes = size } } });
            response.EnsureSuccessStatusCode();
        }
        public async Task<NormalRunningPoint> CreatePoint(bool ensureReserve = true)
        {
            if (ensureReserve) await BuildReserve();
            using var response = await Client.PostAsync("/api/memory-protection/normal-running-point", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<NormalRunningPoint>())!;
        }
        public async Task<HostMemoryProtectionStatus> Status() => (await Client.GetFromJsonAsync<HostMemoryProtectionStatus>("/api/memory-protection"))!;

        public static async Task<Fixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "rm-memory-protection-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var capabilities = new StartupCapabilitySet("memory-protection-test", StartupCapability.RuntimeEffectOwners | StartupCapability.MutablePersistence);
            builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
            builder.Logging.ClearProviders();
            // Keep the production DI, settings compiler, block declaration and rule. No native hosted producer runs.
            builder.Services.RemoveAll<IHostedService>();
            var source = new FakeSource(); var executor = new FakeExecutor();
            builder.Services.AddSingleton<IHostMemoryProtectionSource>(source);
            builder.Services.AddSingleton<IHostMemoryProtectionExecutor>(executor);
            builder.Services.AddSingleton<ICpuTopologySampler>(new CpuSampler());
            var app = builder.Build();
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapHostMemoryProtectionEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, capabilities]);
            await app.StartAsync();
            await app.Services.GetRequiredService<IRuntimeSpecializationCoordinator>().RebuildAsync("test-startup", CancellationToken.None);
            return new(app, root, source, executor);
        }
        public async ValueTask DisposeAsync()
        {
            executor.Block?.TrySetResult();
            await Scheduler.ShutdownAsync(new(BusinessBlockRequestKind.Configuration, "test", "shutdown", 1));
            await Until(() => Scheduler.ReadState(BlockUids.HostMemoryProtection) == BusinessBlockState.Stopped);
            Client.Dispose();
            await app.StopAsync(); await app.DisposeAsync();
            // All handles have closed. These are the only flat directories this fixture creates.
            foreach (var name in new[] { "Config", "Slices", "SlicesSecond" })
            {
                var directory = Path.Combine(root, name);
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
                Directory.Delete(directory);
            }
            var profiles = Path.Combine(root, "UserData", "SoftwareProfiles");
            if (Directory.Exists(profiles))
            {
                foreach (var path in Directory.EnumerateFiles(profiles)) File.Delete(path);
                Directory.Delete(profiles);
                Directory.Delete(Path.Combine(root, "UserData"));
            }
            Directory.Delete(root);
        }
    }

    private sealed class CpuSampler : ICpuTopologySampler
    {
        public void ResetUsageBaseline() { }
        public CpuTopologySnapshot CaptureSnapshot() => throw new InvalidOperationException("No test usage sampling.");
        public CpuTopologySnapshot CaptureTopology() => CpuBaselineRatioCatalogTests.CreateTopology("Intel Core i9-14900K", "Intel", [24]);
    }
    private sealed class FakeSource : IHostMemoryProtectionSource
    {
        private readonly ConcurrentDictionary<Guid, Channel<HostMemoryReading>> readers = new();
        private HostMemoryReading current = new(DateTimeOffset.UtcNow, new(200, 1000, 20), [Process(10, "1", 100)]);
        public int Active => readers.Count;
        public int Deliveries;
        public HostMemoryCommit? ReadCurrentCommit() => Volatile.Read(ref current).Commit;
        public void Publish(ulong bytes, double percent, IReadOnlyList<HostMemoryProcess> processes)
        {
            var next = new HostMemoryReading(DateTimeOffset.UtcNow, new(bytes, 1000, percent), processes);
            Volatile.Write(ref current, next);
            foreach (var channel in readers.Values) channel.Writer.TryWrite(next);
        }
        public async IAsyncEnumerable<HostMemoryReading> SubscribeAsync(string holder, TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Assert.StartsWith("host-memory-protection", holder);
            var key = Guid.NewGuid(); var channel = Channel.CreateUnbounded<HostMemoryReading>();
            readers[key] = channel;
            using var registration = token.Register(() => { readers.TryRemove(key, out _); channel.Writer.TryComplete(); });
            try
            {
                Interlocked.Increment(ref Deliveries);
                yield return Volatile.Read(ref current) with { At = DateTimeOffset.UtcNow };
                await foreach (var value in channel.Reader.ReadAllAsync(token)) { Interlocked.Increment(ref Deliveries); yield return value; }
            }
            finally { readers.TryRemove(key, out _); }
        }
    }
    private sealed class FakeExecutor : IHostMemoryProtectionExecutor
    {
        public ConcurrentQueue<int> Opened = new(), Executed = new();
        public ConcurrentQueue<HostMemoryActionOptions> Options = new();
        public Dictionary<int, string> Excluded = new();
        public HashSet<int> SuspendFailures = [];
        public TaskCompletionSource? Block;
        public int SliceBytes;
        public ConcurrentQueue<(ulong Before, ulong AfterWrite, ulong AfterOverwrite, ulong AfterShrink)> ReserveSamples = new();
        private int concurrent;
        public int MaxConcurrent;
        public IHostMemoryProtectionTarget Open(HostMemoryProcess process)
        { Opened.Enqueue(process.ProcessId); return new Target(this, process.ProcessId); }
        private sealed class Target(FakeExecutor owner, int pid) : IHostMemoryProtectionTarget
        {
            public string? ExclusionReason => owner.Excluded.GetValueOrDefault(pid);
            public HostMemoryActionResult Execute(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions? options = null)
            {
                owner.Executed.Enqueue(pid);
                owner.Options.Enqueue(options ?? new());
                if (owner.SuspendFailures.Contains(pid)) return new("suspend-failed", "test", [], 0) { SuspendFailed = true };
                var active = Interlocked.Increment(ref owner.concurrent);
                owner.MaxConcurrent = Math.Max(owner.MaxConcurrent, active);
                try
                {
                    owner.Block?.Task.GetAwaiter().GetResult();
                    using var lease = reserve.TryAcquire() ?? throw new InvalidOperationException("Test reserve must be ready.");
                    var name = "test-" + pid + ".dmp";
                    if (owner.SliceBytes != 0)
                    {
                        var before = lease.RemainingBytes;
                        using var slice = lease.CreateSlice(name);
                        slice.Write(new byte[owner.SliceBytes]);
                        var afterWrite = lease.RemainingBytes;
                        slice.Position = 0;
                        slice.WriteByte(123);
                        var afterOverwrite = lease.RemainingBytes;
                        slice.SetLength(1);
                        owner.ReserveSamples.Enqueue((before, afterWrite, afterOverwrite, lease.RemainingBytes));
                    }
                    return new("completed", null, lease.SlicePaths.ToArray(), (ulong)owner.SliceBytes);
                }
                finally { Interlocked.Decrement(ref owner.concurrent); }
            }
            public void Dispose() { }
        }
    }
}
