using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class HardwarePublicationReasonTests
{
    [Theory]
    [InlineData(SamplingObservationStatus.Unavailable, "hardware-dataset-unavailable")]
    [InlineData(SamplingObservationStatus.NotRequested, "hardware-dataset-not-requested")]
    [InlineData(SamplingObservationStatus.Unsupported, "hardware-dataset-unsupported")]
    [InlineData(SamplingObservationStatus.Partial, "hardware-dataset-partial")]
    [InlineData(SamplingObservationStatus.Warming, "hardware-dataset-warming")]
    [InlineData(SamplingObservationStatus.Invalid, "hardware-dataset-invalid")]
    [InlineData(SamplingObservationStatus.RetainedLastGood, "hardware-dataset-non-current")]
    public void HostedPublicationReportsEachNonCurrentReasonWithoutDiscardingOtherDatasets(
        SamplingObservationStatus status, string reason)
    {
        using var workspace = Workspace();
        var snapshot = Snapshot(workspace);
        var at = snapshot.CapturedAt.UtcTicks;
        snapshot = snapshot with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
        {
            ["failed"] = new("failed", status, 1, at, 1, 1, 1, 1),
            ["healthy"] = new("healthy", SamplingObservationStatus.Current, 1, at, 1, 1, 1, 1)
        } };
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var run = owner.OpenHostedRun();
        Assert.True(owner.TryCaptureHostedTicket(run, new Permit(), 1, 1, out var ticket));
        Assert.True(owner.TryPublishHosted(ticket, snapshot, MetricSampleRequest.All,
            ["failed", "healthy", "missing"], null, out var publication));
        Assert.Equal(reason, publication.RejectionReasons["failed"]);
        Assert.Equal("hardware-dataset-missing", publication.RejectionReasons["missing"]);
        Assert.Equal(["healthy"], publication.CommittedDatasetIds);
        Assert.Equal(reason, owner.Read()!.Datasets["failed"].FailureCode);
    }

    [Theory]
    [InlineData(NativeMetricSnapshotMetricStatus.Unavailable, NativeMetricSnapshotSourceStatus.Unavailable, "hardware-source-unavailable")]
    [InlineData(NativeMetricSnapshotMetricStatus.Unavailable, NativeMetricSnapshotSourceStatus.Complete, "hardware-native-unavailable")]
    [InlineData(NativeMetricSnapshotMetricStatus.Unsupported, NativeMetricSnapshotSourceStatus.Complete, "hardware-native-unsupported")]
    [InlineData(NativeMetricSnapshotMetricStatus.Skipped, NativeMetricSnapshotSourceStatus.Skipped, "hardware-native-not-requested")]
    public void NativeProjectionPreservesSourceVersusFieldFailureInPublication(
        NativeMetricSnapshotMetricStatus status, NativeMetricSnapshotSourceStatus sourceStatus, string reason)
    {
        using var workspace = Workspace();
        var frame = workspace.ReadCommitted();
        var binding = frame.Catalog.MetricBindings.Values.First(row => row.FieldCode == MetricFieldCode.CpuActualPower);
        var frameWithStatus = frame with
        {
            Metrics = [.. frame.Metrics.Select(row =>
            { if (row.MetricHandle == binding.MetricHandle) row.Status = (uint)status; return row; })],
            Sources = [.. frame.Sources.Select(row =>
            { if (row.SourceHandle == binding.SourceHandle) row.Status = (uint)sourceStatus; return row; })]
        };
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: frameWithStatus, workspaceIdentity: 1);
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var publication = owner.Publish(snapshot, MetricSampleRequest.ForIds([binding.MetricId]));
        Assert.Equal(reason, publication.RejectionReasons[binding.MetricId]);
    }

    [Fact]
    public void InitialCpuCounterHasNoIntervalAndPublishesBaselineReasonThenRecovers()
    {
        using var workspace = Workspace();
        var catalog = workspace.ReadCatalog();
        var binding = catalog.MetricBindings.Values.Single(row => row.FieldCode == MetricFieldCode.CpuUsage);
        var modes = catalog.Sources.Select(row => new NativeMetricSnapshotSourceRuntimeState(row.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        for (ulong sequence = 1; sequence <= 2; sequence++)
        {
            var at = DateTimeOffset.UtcNow.AddSeconds((long)sequence);
            var plan = workspace.PlanCollection([binding.MetricHandle], modes, false, at);
            var source = Assert.Single(plan.Sources);
            var metric = Assert.Single(plan.Metrics, row => row.MetricHandle == binding.MetricHandle);
            var counter = NativeMetricSnapshotObservationProjection.CreateCpuCounter(metric, binding,
                new(source.SourceHandle, 1, sequence), new(true, sequence * 10, sequence * 100,
                    sequence * 100, sequence * 1000, 1000, at));
            workspace.CompleteSource(plan, new(source.SourceHandle, 1, sequence, at,
                NativeMetricSnapshotSourceStatus.Complete, [], counter, [], 0, 0), at);
            var publication = owner.Publish(Snapshot(workspace), MetricSampleRequest.ForIds([binding.MetricId]));
            if (sequence == 1)
            {
                Assert.Equal("hardware-counter-baseline", publication.RejectionReasons[binding.MetricId]);
                Assert.Equal("hardware-counter-baseline", publication.RejectionReasons["cpu.usage"]);
            }
            else Assert.Contains(binding.MetricId, publication.CommittedDatasetIds);
        }
    }

    [Fact]
    public void WarningContainsReasonAndRecoveryIsAlsoWarningWithExistingSuppression()
    {
        var logger = new RecordingLogger();
        var rejected = new HashSet<string>();
        var failure = new HardwareMetricPublicationResult(false, [], ["cpu.usage"])
        { RejectionReasons = new Dictionary<string, string> { ["cpu.usage"] = "hardware-counter-baseline" } };
        WindowsHardwareMetricSampler.ReportPublication(logger, rejected, failure);
        WindowsHardwareMetricSampler.ReportPublication(logger, rejected, failure);
        WindowsHardwareMetricSampler.ReportPublication(logger, rejected, new(true, ["cpu.usage"], []));
        WindowsHardwareMetricSampler.ReportPublication(logger, rejected, failure);
        Assert.Equal(3, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal(LogLevel.Warning, message.Level));
        Assert.Contains("cpu.usage[hardware-counter-baseline]", logger.Messages[0].Text);
        Assert.Contains("published previously rejected", logger.Messages[1].Text);
    }

    private static NativeMetricSnapshotWorkspace Workspace()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"publication-reasons-{Guid.NewGuid():N}.bin"));
        workspace.ReplaceCatalog(NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [], [], [], workspace.CreateCatalogHandleMap()), DateTimeOffset.UtcNow);
        return workspace;
    }
    private static HardwareMetricSnapshot Snapshot(NativeMetricSnapshotWorkspace workspace)
        => NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action publish) { publish(); return true; }
    }
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => Messages.Add((level, formatter(state, error)));
    }
}
