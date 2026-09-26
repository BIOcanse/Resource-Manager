using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAdversarialCorrectnessTests
{
    [Fact]
    public void HardwareHistoryAdvancesOnlyAtTheCompletedItemPublication()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 5), new(SamplingDatasetIds.SystemMemoryUsage, 2)]));
        var first = HistoryHardwareSnapshot(1, 50);
        owner.Publish(first, MetricSampleRequest.ForIds([SamplingDatasetIds.SystemCpuUsage]));
        var firstView = owner.Read()!;
        owner.Publish(HistoryHardwareSnapshot(2, 95), MetricSampleRequest.ForIds([SamplingDatasetIds.SystemCpuUsage]));
        for (var index = 0; index < 10; index++)
        {
            var current = owner.Read(DateTimeOffset.MaxValue)!;
            Assert.Equal(new double?[] { 50, 95 }, current.History[SamplingDatasetIds.SystemCpuUsage].Select(static sample => sample.Value));
            Assert.Empty(current.History[SamplingDatasetIds.SystemMemoryUsage]);
        }
        owner.Publish(HistoryHardwareSnapshot(3, 95), MetricSampleRequest.ForIds([SamplingDatasetIds.SystemCpuUsage]));
        owner.Publish(HistoryHardwareSnapshot(4, 20), MetricSampleRequest.ForIds([SamplingDatasetIds.SystemMemoryUsage]));
        var final = owner.Read()!;
        Assert.Equal(new double?[] { 50, 95, 95 }, final.History[SamplingDatasetIds.SystemCpuUsage].Select(static sample => sample.Value));
        Assert.Single(final.History[SamplingDatasetIds.SystemMemoryUsage]);
        Assert.Equal(50, Assert.Single(firstView.History[SamplingDatasetIds.SystemCpuUsage]).Value);
        Assert.False(JsonSerializer.Serialize(final).Contains("history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HardwareHistoryKeepsNullCompletionAndCurrentValueInOneSnapshot()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([new(SamplingDatasetIds.SystemCpuUsage, 4)]));
        var request = MetricSampleRequest.ForIds([SamplingDatasetIds.SystemCpuUsage]);
        owner.Publish(HistoryHardwareSnapshot(1, 50), request);
        owner.RecordFailure(request, DateTimeOffset.UnixEpoch.AddSeconds(2), "test-reader-error");
        var missing = owner.Read()!;
        Assert.False(missing.Cpu.IsUsageAvailable);
        Assert.Null(missing.History[SamplingDatasetIds.SystemCpuUsage][^1].Value);
        owner.Publish(HistoryHardwareSnapshot(3, 0), request);
        Assert.Equal(new double?[] { 50, null, 0 }, owner.Read()!.History[SamplingDatasetIds.SystemCpuUsage].Select(static sample => sample.Value));
    }

    [Fact]
    public void HistoryCapacityChangesPreserveCurrentSlotsAndUnrelatedItems()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(HistoryHardwareSnapshot(1, 10), MetricSampleRequest.All);
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 3), new(SamplingDatasetIds.SystemMemoryUsage, 2)]));
        var request = MetricSampleRequest.ForIds([SamplingDatasetIds.SystemCpuUsage]);
        owner.Publish(HistoryHardwareSnapshot(2, 20), request);
        owner.Publish(HistoryHardwareSnapshot(3, 30), request);
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 1), new(SamplingDatasetIds.SystemMemoryUsage, 2)]));
        var shrunk = owner.Read()!;
        Assert.Equal(30, Assert.Single(shrunk.History[SamplingDatasetIds.SystemCpuUsage]).Value);
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 5), new(SamplingDatasetIds.SystemMemoryUsage, 2)]));
        Assert.Single(owner.Read()!.History[SamplingDatasetIds.SystemCpuUsage]);
        Assert.Equal<HardwareMetricHistorySample>(shrunk.History[SamplingDatasetIds.SystemMemoryUsage], owner.Read()!.History[SamplingDatasetIds.SystemMemoryUsage]);
        owner.ConfigureHistory(CompiledDataHistoryPlan.Empty);
        Assert.Empty(owner.Read()!.History);
        Assert.Equal(30, owner.Read()!.Cpu.UsagePercent);
        Assert.Equal(1_000UL, owner.Read()!.Memory.UsedBytes);
    }

    [Fact]
    public void HostedHistoryRejectsRetiredTicketsAndIgnoresUnsettledPhysicalFields()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 3), new(SamplingDatasetIds.SystemMemoryUsage, 3)]));
        var run = owner.OpenHostedRun();
        var workspace = new SamplingOwnerToken(1, 1);
        Assert.True(owner.TryCaptureHostedTicket(run, workspace, HardwareSchedule(DateTimeOffset.UtcNow), out var ticket));
        Assert.True(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(1, 50), MetricSampleRequest.All,
            [SamplingDatasetIds.SystemCpuUsage], null, out var first));
        Assert.True(first.IsSuccessful);
        Assert.Empty(owner.Read()!.History[SamplingDatasetIds.SystemMemoryUsage]);
        Assert.True(owner.CloseHostedRun(run, DateTimeOffset.UtcNow, "test-stop"));
        Assert.False(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(2, 95), MetricSampleRequest.All,
            [SamplingDatasetIds.SystemCpuUsage], null, out _));
        Assert.False(owner.TryRecordHostedFailure(ticket, MetricSampleRequest.All,
            [SamplingDatasetIds.SystemCpuUsage], DateTimeOffset.UtcNow, "late-error"));
        Assert.Single(owner.Read()!.History[SamplingDatasetIds.SystemCpuUsage]);
    }

    [Fact]
    public void UnrequestedDatasetsDoNotCreateDerivedHistory()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory(CompiledDataHistoryPlan.Compile([
            new(SamplingDatasetIds.SystemCpuUsage, 5), new(SamplingDatasetIds.SystemMemoryUsage, 5)]));
        var run = owner.OpenHostedRun();
        var workspace = new SamplingOwnerToken(1, 1);
        Assert.True(owner.TryCaptureHostedTicket(run, workspace, HardwareSchedule(DateTimeOffset.UtcNow), out var ticket));
        var raw = HistoryHardwareSnapshot(1, 50);
        Assert.True(owner.TryPublishHosted(ticket, raw, MetricSampleRequest.All,
            [SamplingDatasetIds.SystemGpuInventory, "gpu.0.usage"], null, out _));
        Assert.Empty(owner.Read()!.History[SamplingDatasetIds.SystemMemoryUsage]);
        Assert.False(owner.Read()!.GpuInventory.IsCurrentComplete());
        Assert.Equal(SamplingObservationStatus.NotRequested, Assert.Single(owner.Read()!.GpuInventory.Adapters).CapacityStatus);
        Assert.True(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(2, 95), MetricSampleRequest.All,
            [SamplingDatasetIds.SystemCpuUsage], null, out _));
        Assert.True(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(3, 95), MetricSampleRequest.All,
            ["gpu.0.temperature"], null, out _));
        Assert.Empty(owner.Read()!.History[SamplingDatasetIds.SystemMemoryUsage]);
        Assert.True(owner.TryRecordHostedFailure(ticket, MetricSampleRequest.All,
            ["gpu.0.usage"], DateTimeOffset.UnixEpoch.AddSeconds(4), "gpu-empty"));
        var final = owner.Read()!;
        Assert.Empty(final.History[SamplingDatasetIds.SystemMemoryUsage]);
        Assert.Single(final.History[SamplingDatasetIds.SystemCpuUsage]);
        Assert.True(owner.CloseHostedRun(run, DateTimeOffset.UtcNow, "test-stop"));
    }

    private static HardwareMetricSnapshot HistoryHardwareSnapshot(ulong round, double cpu)
        => CreateDatasetHardwareSnapshot(round, cpu, 1_000, 20, 2_000,
            SamplingDatasetIds.SystemCpuUsage, SamplingDatasetIds.SystemMemoryUsage,
            SamplingDatasetIds.SystemGpuInventory, "gpu.0.usage", "gpu.0.vram", "gpu.0.temperature");
}
