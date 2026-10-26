using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotWorkspaceTests
{
    // Trace is process-global; other test classes can write during our assertions.
    // Queue snapshots are safe even when an already-dispatched callback finishes.
    private sealed class ConcurrentTraceCapture : TraceListener
    {
        private readonly ConcurrentQueue<string> messages = new();
        public override bool IsThreadSafe => true;
        public override void Write(string? message) { if (message is not null) messages.Enqueue(message); }
        public override void WriteLine(string? message) => Write(message + Environment.NewLine);
        internal string Read() => string.Concat(messages.ToArray());
    }

    [Fact]
    public void CompletionFrameUsesCommandTimeWithoutReplacingObservationTime()
    {
        var observationAt =
            DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var commandAt =
            DateTimeOffset.FromUnixTimeMilliseconds(2_000);

        Assert.Equal(
            commandAt,
            NativeMetricSnapshotWorkspace.ResolveCompletionCapturedAt(
                hasCpuCounter: false,
                observationAt,
                commandAt));
        Assert.Equal(
            observationAt,
            NativeMetricSnapshotWorkspace.ResolveCompletionCapturedAt(
                hasCpuCounter: true,
                observationAt,
                commandAt));
    }

    [Fact]
    public void RecoveryCheckpointPersistsOnlyWhenCatalogAdvances()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var compiled =
                HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            using var workspace =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            var projection = CreateProjection(
                compiled,
                workspace.CreateCatalogHandleMap(),
                []);
            _ = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);

            Assert.True(
                workspace.PersistRecoveryCheckpointIfAdvanced(
                    DateTimeOffset.UtcNow));
            Assert.False(
                workspace.PersistRecoveryCheckpointIfAdvanced(
                    DateTimeOffset.UtcNow));

            var metricHandle =
                projection.MetricHandles["memory.usage"];
            var runtimeModes = projection.Sources
                .Select(static source =>
                    new NativeMetricSnapshotSourceRuntimeMode(
                        source.SourceHandle,
                        1,
                        NativeMetricSnapshotZoneMode.Normal,
                        NativeMetricSnapshotSourceAvailability.Available))
                .ToArray();
            var plan = workspace.PlanCollection(
                [metricHandle],
                runtimeModes,
                includeGpuInventory: false,
                DateTimeOffset.UtcNow);
            var metricPlan = Assert.Single(
                plan.Metrics.Where(metric =>
                    metric.MetricHandle == metricHandle
                    && (metric.Flags
                        & (uint)NativeMetricSnapshotMetricPlanFlags
                            .Selected) != 0));
            var sourcePlan = Assert.Single(
                plan.Sources.Where(source =>
                    source.SourceHandle == metricPlan.SourceHandle));
            var binding =
                projection.RuleBindings[metricPlan.RuleHandle];
            var capturedAt = DateTimeOffset.UtcNow;
            _ = workspace.CompleteSource(
                plan,
                new NativeMetricSnapshotSourceCompletion(
                    sourcePlan.SourceHandle,
                    1,
                    1,
                    capturedAt,
                    NativeMetricSnapshotSourceStatus.Complete,
                    [
                        new NativeMetricSnapshotObservationInput
                        {
                            StructSize = checked((uint)Unsafe.SizeOf<
                                NativeMetricSnapshotObservationInput>()),
                            Flags = 0,
                            RuleHandle = metricPlan.RuleHandle,
                            MetricHandle = metricPlan.MetricHandle,
                            SourceHandle = metricPlan.SourceHandle,
                            ScopeHandle = metricPlan.ScopeHandle,
                            SourceObservationSequence = 1,
                            ObservedAtMilliseconds = checked(
                                (ulong)capturedAt
                                    .ToUnixTimeMilliseconds()),
                            ValueBits = ValueBits(
                                metricPlan.ValueKind,
                                1),
                            CapabilityMask = binding.CapabilityMask,
                            ValidMask = (ulong)(
                                NativeMetricSnapshotObservationValidity
                                    .Required
                                | NativeMetricSnapshotObservationValidity
                                    .Value),
                            Status = (uint)
                                NativeMetricSnapshotObservationStatus
                                    .Current,
                            ValueKind = metricPlan.ValueKind,
                            Quality = 100,
                            ReservedU32 = 0,
                            SampleDurationMilliseconds = 1
                        }
                    ],
                    null,
                    [],
                    0,
                    0),
                capturedAt);

            Assert.False(
                workspace.PersistRecoveryCheckpointIfAdvanced(
                    DateTimeOffset.UtcNow));
            Assert.False(
                workspace.PersistRecoveryCheckpointIfAdvanced(
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptPersistenceEnvelopeIsDiscardedBeforeFreshCatalogLoad(bool useWarningCallback)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        using var listener = new ConcurrentTraceCapture();
        Trace.Listeners.Add(listener);
        var warnings = new List<string>();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

            using var workspace =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path,
                    NativeMetricSnapshotScratch.CreateOrdinary,
                    useWarningCallback ? warnings.Add : null);
            var projection = CreateProjection(
                plan,
                workspace.CreateCatalogHandleMap(),
                []);
            var header = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);

            Assert.False(File.Exists(path));
            var diagnostics = useWarningCallback ? Assert.Single(warnings) : listener.Read();
            Assert.Contains("Discarding invalid metric-snapshot catalog persistence", diagnostics);
            Assert.Contains("persistence envelope is truncated", diagnostics);
            if (useWarningCallback) Assert.DoesNotContain("Discarding invalid metric-snapshot catalog persistence", listener.Read());
            Assert.Equal(
                NativeMetricSnapshotPhase.CatalogReady,
                (NativeMetricSnapshotPhase)header.Phase);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingVersionTwoEnvelopeStartsFreshWithVisibleDiagnostic(bool useWarningCallback)
    {
        var root = Path.Combine(Path.GetTempPath(), $"metric-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        using var listener = new ConcurrentTraceCapture();
        Trace.Listeners.Add(listener);
        var warnings = new List<string>();
        try
        {
            var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            NativeMetricSnapshotPersistenceImage image;
            using (var original = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path))
            {
                original.ReplaceCatalog(CreateProjection(plan, original.CreateCatalogHandleMap(), []), DateTimeOffset.UtcNow);
                image = original.ExportAndPersist(DateTimeOffset.UtcNow);
            }
            var envelope = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(sizeof(ulong)), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(sizeof(ulong) + sizeof(uint)), 0x000A_0000);
            using var legacy = new MemoryStream();
            legacy.Write(envelope.AsSpan(0, envelope.Length - SHA256.HashSizeInBytes));
            legacy.Write(MemoryMarshal.AsBytes(new[] { image.Header }.AsSpan()));
            legacy.Write(MemoryMarshal.AsBytes(image.Sources.AsSpan()));
            legacy.Write(MemoryMarshal.AsBytes(image.Rules.AsSpan()));
            legacy.Write(MemoryMarshal.AsBytes(image.GpuInventory.AsSpan()));
            var body = legacy.ToArray();
            legacy.Write(SHA256.HashData(body));
            File.WriteAllBytes(path, legacy.ToArray());

            using var restarted = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path,
                NativeMetricSnapshotScratch.CreateOrdinary,
                useWarningCallback ? warnings.Add : null);
            Assert.Empty(restarted.CreateCatalogHandleMap().Entries);
            var header = restarted.ReplaceCatalog(CreateProjection(plan, restarted.CreateCatalogHandleMap(), []), DateTimeOffset.UtcNow);
            Assert.Equal(NativeMetricSnapshotPhase.CatalogReady, (NativeMetricSnapshotPhase)header.Phase);
            var diagnostics = useWarningCallback ? Assert.Single(warnings) : listener.Read();
            Assert.Contains($"envelope version 2 at '{path}'", diagnostics);
            Assert.Contains("Sampling values will start empty", diagnostics);
            if (useWarningCallback) Assert.DoesNotContain($"envelope version 2 at '{path}'", listener.Read());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DifferentConfigurationGenerationDoesNotImportValidOldPersistence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-generation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var original = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            using (var first = NativeMetricSnapshotHostConfiguration.CreateWorkspace(original, path))
            {
                var projection = CreateProjection(
                    original,
                    first.CreateCatalogHandleMap(),
                    []);
                _ = first.ReplaceCatalog(projection, DateTimeOffset.UtcNow);
                _ = first.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            var upgraded = HostManagerTestPlanFactory.CreatePlan(profile =>
            {
                profile["profile_revision"] = JsonValue.Create(56);
            }).MetricSnapshot;
            using var replacement =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(upgraded, path);

            Assert.NotEqual(
                original.HotPublish.ConfigurationGeneration,
                upgraded.HotPublish.ConfigurationGeneration);
            Assert.Empty(replacement.CreateCatalogHandleMap().Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetiredHolderRejectsLatePublication()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-holder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path);
        var holder =
            new HostManagerMetricSnapshotOwner.WorkspaceHolder(
                1,
                workspace,
                new(0, NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [],
                    NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [],
                    NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []));
        holder.AddReference();
        try
        {
            Assert.True(holder.TryPublish(static _ => 1, out var published));
            Assert.Equal(1, published);

            holder.StopPublication();
            var invoked = false;
            Assert.False(holder.TryPublish(
                _ =>
                {
                    invoked = true;
                    return 2;
                },
                out _));
            Assert.False(invoked);
        }
        finally
        {
            holder.Release();
            holder.ReleaseOwnerReference();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RefreshTicketDoesNotConsumeLaterRequest()
    {
        var tracker =
            new HostManagerMetricSnapshotOwner.MonotonicRefreshTicket();
        var first = tracker.Request();
        var captured = tracker.CaptureRequested();
        var second = tracker.Request();

        Assert.Equal(first, captured);
        Assert.True(tracker.IsPending(captured));

        tracker.Confirm(captured);

        Assert.True(tracker.HasPending);
        Assert.True(tracker.IsPending(second));

        tracker.Confirm(second);

        Assert.False(tracker.HasPending);
    }

    [Fact]
    public void RestartRestoresExactCatalogGenerationAndTopologyDriftAdvances()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            ulong firstGeneration;
            ulong firstGpuUsageHandle;
            using (var first = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    first.CreateCatalogHandleMap(),
                    [
                        new(
                            0,
                            "GPU A",
                            "gpu-a",
                            0xA1UL,
                            1,
                            VendorNvidia,
                            true)
                    ]);
                var header = first.ReplaceCatalog(
                    projection,
                    DateTimeOffset.UtcNow);
                firstGeneration = header.CatalogGeneration;
                firstGpuUsageHandle = projection.MetricHandles["gpu.0.usage"];
                _ = first.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using (var restarted =
                   NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    restarted.CreateCatalogHandleMap(),
                    [
                        new(
                            0,
                            "GPU A",
                            "gpu-a",
                            0xA1UL,
                            2,
                            VendorNvidia,
                            true)
                    ]);
                // Catalog identity and stable handles survive restart independently of sampled inventory.
                var header = restarted.ReplaceCatalog(
                    projection,
                    DateTimeOffset.UtcNow);

                Assert.Equal(firstGeneration, header.CatalogGeneration);
                Assert.Equal(
                    firstGpuUsageHandle,
                    projection.MetricHandles["gpu.0.usage"]);
                _ = restarted.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using (var drifted =
                   NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    drifted.CreateCatalogHandleMap(),
                    [
                        new(
                            0,
                            "GPU A",
                            "gpu-a",
                            0xA1UL,
                            3,
                            VendorNvidia,
                            true),
                        new(
                            1,
                            "GPU B",
                            "gpu-b",
                            0xB3UL,
                            3,
                            VendorAmd,
                            false)
                    ]);
                var header = drifted.ReplaceCatalog(
                    projection,
                    DateTimeOffset.UtcNow);

                Assert.Equal(
                    checked(firstGeneration + 1),
                    header.CatalogGeneration);
                Assert.Equal(
                    firstGpuUsageHandle,
                    projection.MetricHandles["gpu.0.usage"]);
                _ = drifted.ExportAndPersist(DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestartKeepsCatalogButStartsValuesAndSequencesEmptyAfterClockRollback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-clock-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var checkpointAt = DateTimeOffset.FromUnixTimeMilliseconds(20_000);
        var observedAt = checkpointAt.AddMilliseconds(2);
        var completionAt = checkpointAt.AddMilliseconds(3);
        var rolledBackCommandAt = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        ulong metricHandle = 0;
        ulong expectedValueBits = 0;
        try
        {
            using (var original =
                   NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path))
            {
                var projection = CreateProjection(
                    compiled,
                    original.CreateCatalogHandleMap(),
                    []);
                _ = original.ReplaceCatalog(projection, checkpointAt);
                metricHandle = projection.MetricHandles["memory.usage"];
                var runtimeModes = projection.Sources
                    .Select(static source =>
                        new NativeMetricSnapshotSourceRuntimeMode(
                            source.SourceHandle,
                            10,
                            NativeMetricSnapshotZoneMode.Normal,
                            NativeMetricSnapshotSourceAvailability.Available))
                    .ToArray();
                var plan = original.PlanCollection(
                    [metricHandle],
                    runtimeModes,
                    includeGpuInventory: false,
                    checkpointAt.AddMilliseconds(1));
                var metricPlan = Assert.Single(
                    plan.Metrics.Where(metric =>
                        metric.MetricHandle == metricHandle
                        && (metric.Flags
                            & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0));
                var sourcePlan = Assert.Single(
                    plan.Sources.Where(source =>
                        source.SourceHandle == metricPlan.SourceHandle));
                var binding = projection.RuleBindings[metricPlan.RuleHandle];
                expectedValueBits = ValueBits(metricPlan.ValueKind, 123);
                var observation = new NativeMetricSnapshotObservationInput
                {
                    StructSize = checked((uint)Unsafe.SizeOf<
                        NativeMetricSnapshotObservationInput>()),
                    Flags = 0,
                    RuleHandle = metricPlan.RuleHandle,
                    MetricHandle = metricPlan.MetricHandle,
                    SourceHandle = metricPlan.SourceHandle,
                    ScopeHandle = metricPlan.ScopeHandle,
                    SourceObservationSequence = 1,
                    ObservedAtMilliseconds = checked(
                        (ulong)observedAt.ToUnixTimeMilliseconds()),
                    ValueBits = expectedValueBits,
                    CapabilityMask = binding.CapabilityMask,
                    ValidMask = (ulong)(
                        NativeMetricSnapshotObservationValidity.Required
                        | NativeMetricSnapshotObservationValidity.Value),
                    Status = (uint)NativeMetricSnapshotObservationStatus.Current,
                    ValueKind = metricPlan.ValueKind,
                    Quality = 100,
                    ReservedU32 = 0,
                    SampleDurationMilliseconds = 1
                };
                _ = original.CompleteSource(
                    plan,
                    new NativeMetricSnapshotSourceCompletion(
                        sourcePlan.SourceHandle,
                        1,
                        1,
                        observedAt,
                        NativeMetricSnapshotSourceStatus.Complete,
                        [observation],
                        null,
                        ImmutableArray<NativeMetricSnapshotGpuInventoryInput>.Empty,
                        0,
                        0),
                    completionAt);
                Assert.True(original.PersistRecoveryCheckpointIfAdvanced(completionAt));
            }

            using var restored =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            var restoredProjection = CreateProjection(
                compiled,
                restored.CreateCatalogHandleMap(),
                []);
            var restoredHeader = restored.ReplaceCatalog(
                restoredProjection,
                rolledBackCommandAt);
            Assert.Equal(
                NativeMetricSnapshotPhase.CatalogReady,
                (NativeMetricSnapshotPhase)restoredHeader.Phase);
            Assert.Equal(
                0UL,
                restoredHeader.CapturedAtMilliseconds);

            var committed = restored.ReadCommitted();
            var restoredMetric = Assert.Single(committed.Metrics.Where(metric =>
                metric.MetricHandle == metricHandle));
            Assert.Equal(0UL, restoredMetric.ValueBits);
            Assert.NotEqual((uint)NativeMetricSnapshotMetricStatus.Current, restoredMetric.Status);
            Assert.Equal(
                0UL,
                restoredMetric.ObservedAtMilliseconds);

            var restoredModes = restoredProjection.Sources
                .Select(static source =>
                    new NativeMetricSnapshotSourceRuntimeMode(
                        source.SourceHandle,
                        1,
                        NativeMetricSnapshotZoneMode.Normal,
                        NativeMetricSnapshotSourceAvailability.Available))
                .ToArray();
            var nextPlan = restored.PlanCollection(
                [metricHandle],
                restoredModes,
                includeGpuInventory: false,
                rolledBackCommandAt.AddMilliseconds(1));
            Assert.Single(nextPlan.Metrics.Where(metric =>
                metric.MetricHandle == metricHandle
                && (metric.Flags
                    & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0));

            var afterPlan = restored.ReadCommitted();
            Assert.Equal(
                0UL,
                afterPlan.Header.CapturedAtMilliseconds);
            Assert.Equal(
                0UL,
                Assert.Single(afterPlan.Metrics.Where(metric =>
                    metric.MetricHandle == metricHandle)).ObservedAtMilliseconds);

            var nextMetric = Assert.Single(nextPlan.Metrics.Where(metric =>
                metric.MetricHandle == metricHandle
                && (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0));
            var nextSource = Assert.Single(nextPlan.Sources.Where(source =>
                source.SourceHandle == nextMetric.SourceHandle));
            Assert.Equal(1UL, nextSource.CapabilityGeneration);
            var nextObservedAt = rolledBackCommandAt.AddMilliseconds(-100);
            var nextCommandAt = rolledBackCommandAt.AddMilliseconds(-200);
            var nextValue = NativeMetricSnapshotObservationProjection.CreateObservation(
                nextMetric,
                restoredProjection.RuleBindings[nextMetric.RuleHandle],
                new(nextSource.SourceHandle, 1, 1),
                nextObservedAt,
                321,
                sampleDurationMilliseconds: 1);
            restored.CompleteSource(nextPlan,
                new(nextSource.SourceHandle, 1, 1, nextObservedAt,
                    NativeMetricSnapshotSourceStatus.Complete, [nextValue], null, [], 0, 0),
                nextCommandAt);
            var afterRollback = Assert.Single(restored.ReadCommitted().Metrics.Where(metric =>
                metric.MetricHandle == metricHandle));
            Assert.Equal((uint)NativeMetricSnapshotMetricStatus.Current, afterRollback.Status);
            Assert.Equal(ValueBits(nextMetric.ValueKind, 321), afterRollback.ValueBits);
            Assert.Equal((ulong)nextObservedAt.ToUnixTimeMilliseconds(), afterRollback.ObservedAtMilliseconds);
            restored.ExportAndPersist(nextCommandAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0xA1UL)]
    [InlineData(0xA2UL)]
    public void RestartedGpuInventoryAcceptsCurrentRuntimeIdentity(ulong restartedLuid)
    {
        var root = Path.Combine(Path.GetTempPath(), $"metric-gpu-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            ulong firstGeneration;
            ulong firstHandle;
            using (var original = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path))
            {
                var catalog = CreateProjection(compiled, original.CreateCatalogHandleMap(),
                    [new(0, "GPU A", "gpu-a", 0xA1UL, 1, VendorNvidia, true)]);
                firstGeneration = original.ReplaceCatalog(catalog, DateTimeOffset.UtcNow).CatalogGeneration;
                firstHandle = Assert.Single(catalog.GpuScopeBindings.Values).ScopeHandle;
                CompleteGpuInventory(original, catalog, incarnation: 2);
                Assert.Equal(0xA1UL, Assert.Single(original.ReadCommitted().GpuInventory).AdapterLuidLow);
                _ = original.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using var restarted = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            var current = CreateProjection(compiled, restarted.CreateCatalogHandleMap(),
                [new(0, "GPU A", "gpu-a", restartedLuid, 2, VendorNvidia, true)]);
            _ = restarted.ReplaceCatalog(current, DateTimeOffset.UtcNow);
            Assert.Empty(restarted.ReadCommitted().GpuInventory);
            CompleteGpuInventory(restarted, current, incarnation: 3);
            var frame = restarted.ReadCommitted();
            var adapter = Assert.Single(frame.GpuInventory);
            Assert.Equal(restartedLuid, adapter.AdapterLuidLow);
            Assert.Equal(firstHandle, adapter.AdapterHandle);
            Assert.Equal(firstGeneration + (restartedLuid == 0xA1UL ? 0UL : 1UL), frame.Header.CatalogGeneration);
            if (restartedLuid != 0xA1UL)
            {
                Assert.True(restarted.PersistRecoveryCheckpointIfAdvanced(DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlanCompletionAndReadUseTheNativeCommittedSnapshot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-collection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var compiled =
                HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            using var workspace =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            var projection = CreateProjection(
                compiled,
                workspace.CreateCatalogHandleMap(),
                []);
            _ = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);
            var metricHandle =
                projection.MetricHandles["memory.usage"];
            var runtimeModes = projection.Sources
                .Select(static source =>
                    new NativeMetricSnapshotSourceRuntimeMode(
                        source.SourceHandle,
                        1,
                        NativeMetricSnapshotZoneMode.Normal,
                        NativeMetricSnapshotSourceAvailability.Available))
                .ToArray();
            var plan = workspace.PlanCollection(
                [metricHandle],
                runtimeModes,
                includeGpuInventory: false,
                DateTimeOffset.UtcNow);
            var metricPlan = Assert.Single(
                plan.Metrics.Where(metric =>
                    metric.MetricHandle == metricHandle
                    && (metric.Flags
                        & (uint)NativeMetricSnapshotMetricPlanFlags
                            .Selected) != 0));
            var sourcePlan = Assert.Single(
                plan.Sources.Where(source =>
                    source.SourceHandle == metricPlan.SourceHandle));
            var binding =
                projection.RuleBindings[metricPlan.RuleHandle];
            var capturedAt = DateTimeOffset.UtcNow;
            var observation = new NativeMetricSnapshotObservationInput
            {
                StructSize = checked((uint)Unsafe.SizeOf<
                    NativeMetricSnapshotObservationInput>()),
                Flags = 0,
                RuleHandle = metricPlan.RuleHandle,
                MetricHandle = metricPlan.MetricHandle,
                SourceHandle = metricPlan.SourceHandle,
                ScopeHandle = metricPlan.ScopeHandle,
                SourceObservationSequence = 1,
                ObservedAtMilliseconds = checked(
                    (ulong)capturedAt.ToUnixTimeMilliseconds()),
                ValueBits = ValueBits(metricPlan.ValueKind, 1),
                CapabilityMask = binding.CapabilityMask,
                ValidMask = (ulong)(
                    NativeMetricSnapshotObservationValidity.Required
                    | NativeMetricSnapshotObservationValidity.Value),
                Status =
                    (uint)NativeMetricSnapshotObservationStatus.Current,
                ValueKind = metricPlan.ValueKind,
                Quality = 100,
                ReservedU32 = 0,
                SampleDurationMilliseconds = 1
            };
            _ = workspace.CompleteSource(
                plan,
                new NativeMetricSnapshotSourceCompletion(
                    sourcePlan.SourceHandle,
                    1,
                    1,
                    capturedAt,
                    NativeMetricSnapshotSourceStatus.Complete,
                    [observation],
                    null,
                    ImmutableArray<
                        NativeMetricSnapshotGpuInventoryInput>.Empty,
                    0,
                    0),
                capturedAt);

            var committed = workspace.ReadCommitted();
            var metric = Assert.Single(
                committed.Metrics.Where(value =>
                    value.MetricHandle == metricHandle));

            Assert.Equal(
                NativeMetricSnapshotPhase.Ready,
                (NativeMetricSnapshotPhase)committed.Header.Phase);
            Assert.Equal(
                NativeMetricSnapshotMetricStatus.Current,
                (NativeMetricSnapshotMetricStatus)metric.Status);
            Assert.Equal(observation.ValueBits, metric.ValueBits);
            Assert.Equal(sourcePlan.SourceHandle, metric.SourceHandle);
            Assert.Equal(metricPlan.RuleHandle, metric.WinningRuleHandle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CommittedReadsAllocateOnlyRequiredCurrentRowsAndKeepIndependentSnapshots()
    {
        const int iterations = 32;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-committed-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
                compiled,
                Path.Combine(root, "state.bin"));
            var projection = CreateProjection(
                compiled,
                workspace.CreateCatalogHandleMap(),
                [new(0, "GPU A", "gpu-a", 0xA1UL, 1, VendorNvidia, true)]);
            _ = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);
            CompleteGpuInventory(workspace, projection, incarnation: 2);
            var header = workspace.ReadHeader();

            // Warm native interop and the current read path before measuring.
            _ = workspace.ReadCommitted();
            _ = workspace.ReadCommitted();
            _ = workspace.ReadSources();

            var fullHeaders = new NativeMetricSnapshotSnapshotHeader[iterations];
            NativeMetricSnapshotCommittedFrame? firstFullRead = null;
            NativeMetricSnapshotCommittedFrame? lastFullRead = null;
            var beforeFull = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < fullHeaders.Length; i++)
            {
                var read = workspace.ReadCommitted();
                fullHeaders[i] = read.Header;
                if (i == 0) firstFullRead = read;
                if (i == fullHeaders.Length - 1) lastFullRead = read;
            }
            var fullAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFull;

            var sourceReads = new ImmutableArray<NativeMetricSnapshotSourceOutput>[iterations];
            var sourceHeaders = new NativeMetricSnapshotSnapshotHeader[iterations];
            var beforeSources = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < sourceReads.Length; i++)
            {
                var sourceRead = workspace.ReadSources();
                sourceReads[i] = sourceRead.Sources;
                sourceHeaders[i] = sourceRead.Header;
            }
            var sourcesAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeSources;
            var first = firstFullRead!;
            var sourcesOnly = sourceReads[0];

            Assert.Equal((int)header.SourceCount, first.Sources.Length);
            Assert.Equal((int)header.MetricCount, first.Metrics.Length);
            Assert.Equal((int)header.GpuAdapterCount, first.GpuInventory.Length);
            Assert.Equal((int)header.RuleCount, first.Rules.Length);
            Assert.Equal(1, first.GpuInventory.Length);
            Assert.True(first.Sources.SequenceEqual(sourcesOnly));
            Assert.Equal(header.StateRevision, sourceHeaders[0].StateRevision);

            var sourcePayload = (long)header.SourceCount
                * Unsafe.SizeOf<NativeMetricSnapshotSourceOutput>();
            var fullPayload = sourcePayload
                + (long)header.MetricCount
                    * Unsafe.SizeOf<NativeMetricSnapshotMetricOutput>()
                + (long)header.GpuAdapterCount
                    * Unsafe.SizeOf<NativeMetricSnapshotGpuInventoryOutput>()
                + (long)header.RuleCount
                    * Unsafe.SizeOf<NativeMetricSnapshotRuleStateOutput>();
            Assert.True(fullPayload > 0);
            Assert.InRange(fullAllocated, fullPayload * iterations,
                (fullPayload + 4_096) * iterations);
            Assert.InRange(sourcesAllocated, sourcePayload * iterations,
                (sourcePayload + 1_024) * iterations);

            var second = lastFullRead!;
            Assert.All(fullHeaders, readHeader =>
            {
                Assert.Equal(header.StateRevision, readHeader.StateRevision);
                Assert.Equal((int)header.SourceCount, (int)readHeader.SourceCount);
                Assert.Equal((int)header.MetricCount, (int)readHeader.MetricCount);
                Assert.Equal((int)header.GpuAdapterCount, (int)readHeader.GpuAdapterCount);
                Assert.Equal((int)header.RuleCount, (int)readHeader.RuleCount);
            });
            for (var i = 0; i < sourceReads.Length; i++)
            {
                Assert.True(first.Sources.SequenceEqual(sourceReads[i]));
                Assert.Equal(header.StateRevision, sourceHeaders[i].StateRevision);
            }
            Assert.NotSame(
                ImmutableCollectionsMarshal.AsArray(first.Sources),
                ImmutableCollectionsMarshal.AsArray(second.Sources));
            Assert.NotSame(
                ImmutableCollectionsMarshal.AsArray(first.Metrics),
                ImmutableCollectionsMarshal.AsArray(second.Metrics));
            Assert.NotSame(
                ImmutableCollectionsMarshal.AsArray(first.GpuInventory),
                ImmutableCollectionsMarshal.AsArray(second.GpuInventory));
            Assert.NotSame(
                ImmutableCollectionsMarshal.AsArray(first.Rules),
                ImmutableCollectionsMarshal.AsArray(second.Rules));
            Assert.NotSame(
                ImmutableCollectionsMarshal.AsArray(sourceReads[0]),
                ImmutableCollectionsMarshal.AsArray(sourceReads[^1]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidObservationAbortKeepsTheNextPlanAvailable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-abort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var compiled =
                HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            using var workspace =
                NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            var projection = CreateProjection(
                compiled,
                workspace.CreateCatalogHandleMap(),
                []);
            _ = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);
            var metricHandle =
                projection.MetricHandles["memory.usage"];
            var runtimeModes = projection.Sources
                .Select(static source =>
                    new NativeMetricSnapshotSourceRuntimeMode(
                        source.SourceHandle,
                        1,
                        NativeMetricSnapshotZoneMode.Normal,
                        NativeMetricSnapshotSourceAvailability.Available))
                .ToArray();
            var first = workspace.PlanCollection(
                [metricHandle],
                runtimeModes,
                includeGpuInventory: false,
                DateTimeOffset.UtcNow);
            var metricPlan = Assert.Single(
                first.Metrics.Where(metric =>
                    metric.MetricHandle == metricHandle
                    && (metric.Flags
                        & (uint)NativeMetricSnapshotMetricPlanFlags
                            .Selected) != 0));
            var sourcePlan = Assert.Single(
                first.Sources.Where(source =>
                    source.SourceHandle == metricPlan.SourceHandle));
            var capturedAt = DateTimeOffset.UtcNow;

            Assert.Throws<InvalidOperationException>(() =>
                workspace.CompleteSource(
                    first,
                    new NativeMetricSnapshotSourceCompletion(
                        sourcePlan.SourceHandle,
                        1,
                        1,
                        capturedAt,
                        NativeMetricSnapshotSourceStatus.Complete,
                        [default],
                        null,
                        [],
                        0,
                        0),
                    capturedAt));

            var next = workspace.PlanCollection(
                [metricHandle],
                runtimeModes,
                includeGpuInventory: false,
                DateTimeOffset.UtcNow);

            Assert.NotEqual(0UL, next.Header.PlanEpoch);
            Assert.NotEmpty(next.Sources);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CompleteGpuInventory(
        NativeMetricSnapshotWorkspace workspace,
        NativeMetricSnapshotCatalogProjection catalog,
        ulong incarnation)
    {
        var modes = catalog.Sources.Select(static source =>
            new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
                NativeMetricSnapshotZoneMode.Normal,
                NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection([catalog.MetricHandles["gpu.0.usage"]],
            modes, includeGpuInventory: true, DateTimeOffset.UtcNow);
        var source = Assert.Single(catalog.Sources.Where(static source =>
            source.SourceRole == (uint)NativeMetricSnapshotSourceRole.GpuInventory));
        Assert.Contains(plan.Sources, item => item.SourceHandle == source.SourceHandle);
        var adapter = Assert.Single(catalog.GpuScopeBindings.Values);
        var capturedAt = DateTimeOffset.UtcNow;
        _ = workspace.CompleteSource(plan, new NativeMetricSnapshotSourceCompletion(
            source.SourceHandle, incarnation, 1, capturedAt, NativeMetricSnapshotSourceStatus.Complete,
            [], null,
            [new NativeMetricSnapshotGpuInventoryInput
            {
                StructSize = checked((uint)Unsafe.SizeOf<NativeMetricSnapshotGpuInventoryInput>()),
                AdapterHandle = adapter.ScopeHandle,
                SourceHandle = source.SourceHandle,
                SourceObservationSequence = 1,
                ObservedAtMilliseconds = checked((ulong)capturedAt.ToUnixTimeMilliseconds()),
                AdapterLuidLow = adapter.AdapterLuid,
                StableKeyHandle = adapter.ScopeHandle,
                CapabilityMask = source.CapabilityMask,
                TopologyFingerprint = 1,
                ValidMask = (ulong)NativeMetricSnapshotInventoryValidity.Required,
                Status = (uint)NativeMetricSnapshotInventoryStatus.Current
            }], 1, 0), DateTimeOffset.UtcNow);
    }

    private static NativeMetricSnapshotCatalogProjection CreateProjection(
        ResourceManager.App.Domain.RuntimeSpecialization
            .CompiledHostManagerMetricSnapshotPlan plan,
        NativeMetricSnapshotCatalogHandleMap handles,
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> gpuAdapters)
        => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            gpuAdapters,
            [],
            [],
            handles);

    private static ulong ValueBits(uint valueKind, ulong value)
        => (NativeMetricSnapshotValueKind)valueKind switch
        {
            NativeMetricSnapshotValueKind.Float64 =>
                BitConverter.DoubleToUInt64Bits(value),
            NativeMetricSnapshotValueKind.Signed64 => value,
            NativeMetricSnapshotValueKind.Unsigned64 => value,
            _ => throw new ArgumentOutOfRangeException(nameof(valueKind))
        };

    private const uint VendorNvidia = 1;
    private const uint VendorAmd = 2;
}
