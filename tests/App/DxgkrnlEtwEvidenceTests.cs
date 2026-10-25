using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class DxgkrnlEtwEvidenceTests
{
    [Fact]
    public async Task EvidenceReadIsPureWhileProcessIdentityIsCheckedAtIngestionAndOutput()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var source = harness.Zone;
        ulong birth = 100;
        harness.CurrentProcessBirth = pid => pid == 41 ? birth : 300;
        var now = DateTimeOffset.UtcNow;
        var eventTime = checked((ulong)now.UtcDateTime.ToFileTimeUtc());
        source.ObserveProcessMemory(41, "old.exe", 64 * 1024 * 1024, "usage", eventTime, now);
        source.ObserveProcessMemory(42, "healthy.exe", 32 * 1024 * 1024, "usage", eventTime, now);

        var readsAtIngestion = harness.ProcessMetadataOpenCount;
        var currentAtIngestion = source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45));
        Assert.Equal(2, currentAtIngestion.Count);
        Assert.Equal(readsAtIngestion, harness.ProcessMetadataOpenCount);

        var oldInstance = Assert.Single(currentAtIngestion, evidence => evidence.ProcessId == 41);
        birth = 200;
        source.ObserveProcessMemory(41, "late-old.exe", 48 * 1024 * 1024, "usage", 199, now);

        // A read returns the immutable event facts without reopening the process handle.
        var beforeIdentityValidation = source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45));
        Assert.Contains(beforeIdentityValidation, evidence => evidence.ProcessId == 41);
        Assert.False(source.MatchesCurrentProcess(oldInstance));
        Assert.Equal(readsAtIngestion + 2, harness.ProcessMetadataOpenCount);

        source.ObserveProcessMemory(41, "replacement.exe", 16 * 1024 * 1024, "usage", 201, now);
        Assert.Equal(readsAtIngestion + 3, harness.ProcessMetadataOpenCount);
        var replacement = source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45))
            .Single(row => row.ProcessId == 41);
        Assert.Equal(200UL, replacement.ProcessStartKey);
        Assert.Equal("replacement", replacement.ProcessName);
        Assert.Equal(16 * 1024 * 1024, replacement.ValueBytes);
        Assert.Equal(2, currentAtIngestion.Count);
        Assert.Equal(100UL, oldInstance.ProcessStartKey);
    }

    [Fact]
    public async Task ResidualProductionDropsEvidenceForAReusedProcessId()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var now = DateTimeOffset.UtcNow;
        var eventTime = checked((ulong)now.UtcDateTime.ToFileTimeUtc());
        harness.Zone.ObserveProcessMemory(41, "game.exe", 64 * 1024 * 1024,
            "usage", eventTime, now);
        var provider = new DxgkrnlVidMmEtwResidualBreakdownProvider(harness.Zone);
        var request = new ResourceResidualBreakdownRequest(
            ResourceBreakdownMetricIds.GpuPrefix + "0.vram",
            64 * 1024 * 1024,
            8d * 1024 * 1024 * 1024,
            1,
            new HashSet<int>());

        var openCountBeforeProduction = harness.ProcessMetadataOpenCount;
        var original = provider.CreateResidualSegments(request);
        Assert.Contains(original, segment => segment.ProcessId == 41);
        Assert.Equal(openCountBeforeProduction + 1, harness.ProcessMetadataOpenCount);

        harness.CurrentProcessBirth = static _ => 200;
        var replacement = provider.CreateResidualSegments(request);
        Assert.DoesNotContain(replacement, segment => segment.ProcessId == 41);
        Assert.Equal(openCountBeforeProduction + 2, harness.ProcessMetadataOpenCount);
    }

    [Fact]
    public async Task CurrentEvidenceSnapshotFiltersWindowPidAndValueWithoutMutatingEarlierSnapshots()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var now = DateTimeOffset.UtcNow;
        var eventTime = checked((ulong)now.UtcDateTime.ToFileTimeUtc());
        harness.Zone.ObserveProcessMemory(41, "old.exe", 4096, "usage", eventTime, now - TimeSpan.FromSeconds(46));
        harness.Zone.ObserveProcessMemory(4, "system.exe", 4096, "usage", eventTime, now);
        harness.Zone.ObserveProcessMemory(42, "zero.exe", 0, "usage", eventTime, now);
        harness.Zone.ObserveProcessMemory(43, "current.exe", 8192, "usage", eventTime, now);
        var openCountAfterObservation = harness.ProcessMetadataOpenCount;

        var snapshot = harness.Zone.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45));
        var current = Assert.Single(snapshot);
        Assert.Equal(43, current.ProcessId);
        Assert.Equal(openCountAfterObservation, harness.ProcessMetadataOpenCount);

        harness.Zone.ObserveProcessMemory(44, "later.exe", 8192, "usage", eventTime, now);
        Assert.Single(snapshot);
        Assert.Equal(2, harness.Zone.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45)).Count);
        Assert.Equal(openCountAfterObservation + 1, harness.ProcessMetadataOpenCount);
    }
}
