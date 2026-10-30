using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotCaptureAllocationTests(ITestOutputHelper output)
{
    // 10-30: an ordinary capture allocates KiB, 1024 KiB being the ceiling. Plan
    // outputs, committed rows and the reading table are reused workspace and
    // capture scratch, so a second capture of one metric on the same catalog only
    // allocates the new snapshot's own tables and the values that changed.
    private const long SecondCaptureLimitBytes = 512 * 1024;
    private const ulong Gib = 1UL << 30;

    [Fact]
    public void SecondCommitCaptureOnTheSameCatalogStaysWithinKiBAndLeavesThePublishedSnapshotUnchanged()
    {
        using var workspace = Workspace();
        var catalog = workspace.ReadCatalog();
        var binding = catalog.MetricBindings.Values.First(static row => row.FieldCode == MetricFieldCode.CommitUsed);
        var modes = catalog.Sources.Select(static row => new NativeMetricSnapshotSourceRuntimeState(row.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var capture = new Capture(workspace, binding.MetricHandle, modes);

        var first = capture.Run(1, 10 * Gib);
        var firstCommit = first.Items[binding.MetricId];
        var firstObservedAt = firstCommit.ObservedAt;
        var firstItems = first.Items.ToArray();
        var firstDatasets = first.Datasets.ToArray();
        Assert.Equal(new MetricInteger(10 * Gib), firstCommit.IntegerValue);

        var second = capture.Run(2, 11 * Gib);
        var allocated = capture.PlanBytes + capture.CompleteBytes + capture.ProjectBytes;

        // The published snapshot is immutable: the next capture neither rewrites
        // its tables nor the values they hold.
        Assert.Same(firstCommit, first.Items[binding.MetricId]);
        Assert.Equal(new MetricInteger(10 * Gib), first.Items[binding.MetricId].IntegerValue);
        Assert.Equal(firstObservedAt, first.Items[binding.MetricId].ObservedAt);
        Assert.Equal(firstItems.Length, first.Items.Count);
        foreach (var (id, value) in firstItems) Assert.Same(value, first.Items[id]);
        Assert.Equal(firstDatasets.Length, first.Datasets.Count);
        foreach (var (id, value) in firstDatasets) Assert.Same(value, first.Datasets[id]);
        Assert.Equal(new MetricInteger(11 * Gib), second.Items[binding.MetricId].IntegerValue);
        Assert.NotSame(firstCommit, second.Items[binding.MetricId]);

        // Reused values are exactly what a fresh projection of the same committed
        // frame produces; only changed items are new objects.
        var committed = workspace.ReadCommitted();
        var fresh = capture.Tracker.ApplyProjectedFields(NativeMetricSnapshotCommittedProjection.Create(
            committed, 1, capture.Tracker.Select(committed, 1)));
        Assert.Equal(fresh.Items.Count, second.Items.Count);
        foreach (var (id, value) in fresh.Items) Assert.Equal(value, second.Items[id]);
        Assert.Equal(fresh.Datasets.Count, second.Datasets.Count);
        foreach (var (id, value) in fresh.Datasets) Assert.Equal(value, second.Datasets[id]);
        var reused = 0;
        foreach (var (id, value) in second.Items)
        {
            if (first.Items.TryGetValue(id, out var prior) && ReferenceEquals(prior, value)) reused++;
        }
        Assert.InRange(reused, 1, second.Items.Count - 1);

        // The pre-change path for comparison: independent full-row arrays
        // (rule states included) and a projection without reuse; the plan
        // outputs used to be capacity-sized arrays per call.
        var legacyStarted = GC.GetAllocatedBytesForCurrentThread();
        var legacyFrame = workspace.ReadCommitted();
        var legacyRead = GC.GetAllocatedBytesForCurrentThread() - legacyStarted;
        var header = legacyFrame.Header;
        _ = NativeMetricSnapshotCommittedProjection.Create(legacyFrame, 1, capture.Tracker.Select(legacyFrame, 1));
        var legacyReadAndProject = GC.GetAllocatedBytesForCurrentThread() - legacyStarted;
        var capacity = workspace.Capacity;
        var legacyPlanScratch = (long)capacity.SourcePlanCapacity * Unsafe.SizeOf<NativeMetricSnapshotSourcePlanOutput>()
            + (long)capacity.MetricPlanCapacity * Unsafe.SizeOf<NativeMetricSnapshotMetricPlanOutput>();
        var legacyReadingRows = ((long)header.MetricCount + catalog.LegacyMetricHandles.Count)
            * Unsafe.SizeOf<NativeMetricSnapshotMetricOutput>();
        output.WriteLine($"catalog: {header.MetricCount} metrics, {header.RuleCount} rules, {header.SourceCount} sources");
        output.WriteLine($"second commit capture: plan {capture.PlanBytes} B, complete {capture.CompleteBytes} B, " +
            $"read+project {capture.ProjectBytes} B, total {allocated} B; {reused}/{second.Items.Count} items reused");
        output.WriteLine($"pre-change shape: full read {legacyRead} B, read+fresh projection {legacyReadAndProject} B, " +
            $"capacity plan outputs {legacyPlanScratch} B, row copies in the reading table {legacyReadingRows} B, " +
            $"total about {legacyReadAndProject + legacyPlanScratch + legacyReadingRows} B");

        Assert.InRange(allocated, 0, SecondCaptureLimitBytes);
    }

    private static NativeMetricSnapshotWorkspace Workspace()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"capture-allocations-{Guid.NewGuid():N}.bin"));
        workspace.ReplaceCatalog(NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [], [], [], workspace.CreateCatalogHandleMap()), DateTimeOffset.UtcNow);
        return workspace;
    }

    // The steps WindowsHardwareMetricCapture runs for one admitted collection:
    // plan, complete the planned sources, then observe, select and project the
    // committed frame inside the workspace read.
    private sealed class Capture(NativeMetricSnapshotWorkspace workspace, ulong metricHandle,
        NativeMetricSnapshotSourceRuntimeState[] modes)
    {
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private readonly NativeMetricSnapshotCommittedProjection.Reuse reuse = new();

        internal MetricReadingSourceTracker Tracker { get; } = new();
        internal long PlanBytes { get; private set; }
        internal long CompleteBytes { get; private set; }
        internal long ProjectBytes { get; private set; }

        internal HardwareMetricSnapshot Run(ulong sequence, ulong commitBytes)
        {
            var at = started.AddSeconds((long)sequence);
            var begin = GC.GetAllocatedBytesForCurrentThread();
            var plan = workspace.PlanCollection([metricHandle], modes, false, at);
            var planned = GC.GetAllocatedBytesForCurrentThread();
            var completions = new List<NativeMetricSnapshotSourceCompletion>(plan.Sources.Length);
            foreach (var source in plan.Sources)
            {
                ImmutableArray<NativeMetricSnapshotObservationInput> observations = [];
                foreach (var metric in plan.Metrics)
                {
                    if (metric.MetricHandle != metricHandle || metric.SourceHandle != source.SourceHandle) continue;
                    observations = [NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(metric,
                        plan.Catalog.RuleBindings[metric.RuleHandle], new(source.SourceHandle, 1, sequence), at, commitBytes)];
                    break;
                }
                var completion = new NativeMetricSnapshotSourceCompletion(source.SourceHandle, 1, sequence, at,
                    NativeMetricSnapshotSourceStatus.Complete, observations, null, [], 0, 0);
                workspace.CompleteSource(plan, completion, at);
                completions.Add(completion);
            }
            var completed = GC.GetAllocatedBytesForCurrentThread();
            var snapshot = workspace.ReadCommitted((Capture: this, Completions: completions),
                static (frame, state) =>
                {
                    state.Capture.Tracker.Observe(frame, 1, state.Completions);
                    var selection = state.Capture.Tracker.Select(frame, 1);
                    return NativeMetricSnapshotCommittedProjection.Create(frame: frame, workspaceIdentity: 1,
                        sourceSelection: selection, previous: state.Capture.reuse);
                });
            snapshot = Tracker.ApplyProjectedFields(snapshot);
            var projected = GC.GetAllocatedBytesForCurrentThread();
            PlanBytes = planned - begin;
            CompleteBytes = completed - planned;
            ProjectBytes = projected - completed;
            return snapshot;
        }
    }
}
