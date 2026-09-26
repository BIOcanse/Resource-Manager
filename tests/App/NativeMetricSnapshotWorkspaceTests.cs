using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotWorkspaceTests
{
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
    public void RecoveryCheckpointPersistsOnlyWhenCatalogOrFirstFrameAdvances()
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
                new NativeMetricSnapshotWorkspace(compiled, path);
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

            Assert.True(
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

    [Fact]
    public void CorruptPersistenceEnvelopeIsDiscardedBeforeFreshCatalogLoad()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

            using var workspace =
                new NativeMetricSnapshotWorkspace(plan, path);
            var projection = CreateProjection(
                plan,
                workspace.CreateCatalogHandleMap(),
                []);
            var header = workspace.ReplaceCatalog(
                projection,
                DateTimeOffset.UtcNow);

            Assert.False(File.Exists(path));
            Assert.Equal(
                NativeMetricSnapshotPhase.CatalogReady,
                (NativeMetricSnapshotPhase)header.Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeRejectedPersistenceIsDiscardedWithoutImportingItsEpochs()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"metric-native-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.bin");
        try
        {
            var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            NativeMetricSnapshotPersistenceImage image;
            using (var original =
                   new NativeMetricSnapshotWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    original.CreateCatalogHandleMap(),
                    []);
                _ = original.ReplaceCatalog(
                    projection,
                    DateTimeOffset.UtcNow);
                image = original.ExportAndPersist(
                    DateTimeOffset.UtcNow);
            }

            var rejectedHeader = image.Header;
            rejectedHeader.Phase = uint.MaxValue;
            rejectedHeader.LastOperationEpoch = ulong.MaxValue - 1;
            new NativeMetricSnapshotPersistenceStore(path).Save(
                plan.HotPublish.ConfigurationGeneration,
                image with { Header = rejectedHeader });

            using var restarted =
                new NativeMetricSnapshotWorkspace(plan, path);
            var restartedProjection = CreateProjection(
                plan,
                restarted.CreateCatalogHandleMap(),
                []);
            var header = restarted.ReplaceCatalog(
                restartedProjection,
                DateTimeOffset.UtcNow);

            Assert.False(File.Exists(path));
            Assert.Equal(
                NativeMetricSnapshotPhase.CatalogReady,
                (NativeMetricSnapshotPhase)header.Phase);
            Assert.True(header.LastOperationEpoch < ulong.MaxValue - 1);
        }
        finally
        {
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
            using (var first = new NativeMetricSnapshotWorkspace(original, path))
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
                new NativeMetricSnapshotWorkspace(upgraded, path);

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
        var workspace = new NativeMetricSnapshotWorkspace(plan, path);
        var holder =
            new HostManagerMetricSnapshotOwner.WorkspaceHolder(
                1,
                workspace);
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
            using (var first = new NativeMetricSnapshotWorkspace(plan, path))
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
                firstGpuUsageHandle = projection.MetricIds.Single(
                    static pair => pair.Value == "gpu.0.usage").Key;
                _ = first.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using (var restarted =
                   new NativeMetricSnapshotWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    restarted.CreateCatalogHandleMap(),
                    [
                        new(
                            0,
                            "GPU A",
                            "gpu-a",
                            0xA2UL,
                            2,
                            VendorNvidia,
                            true)
                    ]);
                var header = restarted.ReplaceCatalog(
                    projection,
                    DateTimeOffset.UtcNow);

                Assert.Equal(firstGeneration, header.CatalogGeneration);
                Assert.Equal(
                    firstGpuUsageHandle,
                    projection.MetricIds.Single(
                        static pair => pair.Value == "gpu.0.usage").Key);
                _ = restarted.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using (var drifted =
                   new NativeMetricSnapshotWorkspace(plan, path))
            {
                var projection = CreateProjection(
                    plan,
                    drifted.CreateCatalogHandleMap(),
                    [
                        new(
                            0,
                            "GPU A",
                            "gpu-a",
                            0xA3UL,
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
                    projection.MetricIds.Single(
                        static pair => pair.Value == "gpu.0.usage").Key);
                _ = drifted.ExportAndPersist(DateTimeOffset.UtcNow);
            }
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
            using (var original = new NativeMetricSnapshotWorkspace(compiled, path))
            {
                var catalog = CreateProjection(compiled, original.CreateCatalogHandleMap(),
                    [new(0, "GPU A", "gpu-a", 0xA1UL, 1, VendorNvidia, true)]);
                firstGeneration = original.ReplaceCatalog(catalog, DateTimeOffset.UtcNow).CatalogGeneration;
                firstHandle = Assert.Single(catalog.GpuScopeBindings.Values).ScopeHandle;
                CompleteGpuInventory(original, catalog, incarnation: 2);
                Assert.Equal(0xA1UL, Assert.Single(original.ReadCommitted().GpuInventory).AdapterLuidLow);
                _ = original.ExportAndPersist(DateTimeOffset.UtcNow);
            }

            using var restarted = new NativeMetricSnapshotWorkspace(compiled, path);
            var current = CreateProjection(compiled, restarted.CreateCatalogHandleMap(),
                [new(0, "GPU A", "gpu-a", restartedLuid, 2, VendorNvidia, true)]);
            _ = restarted.ReplaceCatalog(current, DateTimeOffset.UtcNow);
            Assert.Equal(restartedLuid == 0xA1UL ? 1 : 0, restarted.ReadCommitted().GpuInventory.Length);
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
                new NativeMetricSnapshotWorkspace(compiled, path);
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
                new NativeMetricSnapshotWorkspace(compiled, path);
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
        => NativeMetricSnapshotCatalogProjection.Create(
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
