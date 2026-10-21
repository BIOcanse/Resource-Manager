using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAdversarialCorrectnessTests
{
    [Fact]
    public void MetricCurrentHistoryAndPublicationPermissionAreIndependentLibraryContracts()
    {
        var library = typeof(ReadWriteCore).Assembly;
        foreach (var type in new[] { typeof(LastSuccessfulHardwareMetricSnapshot), typeof(IMetricPublicationPermit),
            typeof(HardwareMetricHostedPublicationTicket), typeof(HardwareMetricPublicationResult),
            typeof(HistoryRequirement), typeof(CompiledDataHistoryPlan), typeof(HardwareMetricHistorySample),
            typeof(MetricDatasetIds) })
            Assert.Equal(library, type.Assembly);
        Assert.DoesNotContain(library.GetReferencedAssemblies(), reference => reference.Name == "ResourceManager");
    }

    [Fact]
    public void PublicHistoryConfigurationUsesOriginalNormalizationBeforeMutatingCurrent()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(HistoryHardwareSnapshot(1, 50), MetricSampleRequest.All);
        owner.ConfigureHistory([new HistoryRequirement(" cpu.usage ", 2), new("CPU.USAGE", 4)]);
        Assert.Equal(4, CompiledDataHistoryPlan.Compile([new(" cpu.usage ", 2), new("CPU.USAGE", 4)])
            .GetRetainedRounds(MetricDatasetIds.SystemCpuUsage));
        var before = owner.Read()!;
        Assert.Single(before.History[MetricDatasetIds.SystemCpuUsage]);
        Assert.Throws<ArgumentOutOfRangeException>(() => owner.ConfigureHistory([
            new HistoryRequirement(MetricDatasetIds.SystemMemoryUsage, 3), new(MetricDatasetIds.SystemCpuUsage, 0)]));
        Assert.Same(before, owner.Read());
        Assert.False(owner.Read()!.History.ContainsKey(MetricDatasetIds.SystemMemoryUsage));
        for (var count = 2; count <= 6; count++)
            owner.Publish(HistoryHardwareSnapshot((ulong)count, count * 10), MetricSampleRequest.ForIds([MetricDatasetIds.SystemCpuUsage]));
        Assert.Equal(new double?[] { 30, 40, 50, 60 }, owner.Read()!.History[MetricDatasetIds.SystemCpuUsage]
            .Select(static sample => sample.Value).ToArray());
    }

    [Fact]
    public void CallerOwnedPermissionAndClosedRunPreventLateSdkPublication()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory([new HistoryRequirement(MetricDatasetIds.SystemCpuUsage, 3)]);
        var permit = new MetricSdkTestPermission(1, 1);
        var run = owner.OpenHostedRun();
        Assert.False(owner.TryCaptureHostedTicket(run, new MetricSdkTestPermission(0, 0), 0, 0, out _));
        Assert.False(owner.TryCaptureHostedTicket(run, permit, 2, 1, out _));
        Assert.True(owner.TryCaptureHostedTicket(run, permit, 1, 1, out var ticket));
        var request = MetricSampleRequest.ForIds([MetricDatasetIds.SystemCpuUsage]);
        Assert.True(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(1, 50), request, null, out _));
        var accepted = owner.Read()!;
        permit.Revoke();
        Assert.False(owner.TryPublishHosted(ticket, HistoryHardwareSnapshot(2, 90), request, null, out _));
        Assert.False(owner.TryRecordHostedFailure(ticket, request, DateTimeOffset.UtcNow, "late-source-failure"));
        Assert.Same(accepted, owner.Read());
        Assert.True(owner.CloseHostedRun(run, DateTimeOffset.UtcNow, "test-stop"));
        var nextPermit = new MetricSdkTestPermission(1, 1);
        var nextRun = owner.OpenHostedRun();
        Assert.False(owner.TryCaptureHostedTicket(run, nextPermit, 1, 1, out _));
        Assert.True(owner.TryCaptureHostedTicket(nextRun, nextPermit, 1, 1, out var nextTicket));
        Assert.True(owner.TryRecordHostedFailure(nextTicket, request, DateTimeOffset.UtcNow, "current-source-failure"));
        Assert.Equal(SamplingObservationStatus.Unavailable, owner.Read()!.Datasets[MetricDatasetIds.SystemCpuUsage].Status);
        Assert.Null(owner.Read()!.History[MetricDatasetIds.SystemCpuUsage][1].Value);
    }

    private sealed class MetricSdkTestPermission(ulong workspace, ulong configuration) : IMetricPublicationPermit
    {
        private readonly object publicationGate = new();
        private bool active = true;
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => configuration;
        public bool IsActive => Volatile.Read(ref active);
        public bool TryPublish(Action commit)
        {
            lock (publicationGate)
            {
                if (!active) return false;
                commit();
                return true;
            }
        }
        internal void Revoke() { lock (publicationGate) Volatile.Write(ref active, false); }
    }
}
