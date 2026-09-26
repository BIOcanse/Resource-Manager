using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAdversarialCorrectnessTests
{
    [Fact]
    public void DeadManagedGpuLauncherCannotReturnToProductionComposition()
    {
        var appRoot = FindAppRoot();
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Application",
            "GpuPlacement",
            "IManagedGpuPlacementLauncher.cs")));
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Infrastructure",
            "GpuPlacement",
            "WindowsManagedGpuPlacementLauncher.cs")));
        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "GpuPlacementServiceRegistration.cs"));
        Assert.DoesNotContain(
            "IManagedGpuPlacementLauncher",
            registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MetricCacheRemainsReadableUntilTheNextSuccessfulPublication()
    {
        var first = new HardwareMetricSnapshot(
            DateTimeOffset.UnixEpoch,
            null!,
            null!,
            null!,
            [],
            null!,
            new Dictionary<string, MetricValue>())
        {
            WorkspaceIdentity = 41,
            ConfigurationGeneration = 42,
            CatalogGeneration = 43,
            CommittedGeneration = 44
        };
        var next = first with
        {
            CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            WorkspaceIdentity = 45,
            CommittedGeneration = 1
        };
        var state = new LastSuccessfulHardwareMetricSnapshot();

        state.Publish(first);
        Assert.Same(first, state.Read());

        state.Publish(next);
        Assert.Same(next, state.Read());
    }

    [Fact]
    public void HostedCpuPublicationDoesNotExposeUnrequestedMemoryFromTheSameFrame()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var frame = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 37,
            memoryUsedBytes: 9_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage);

        var result = state.Publish(
            frame,
            MetricSampleRequest.ForIds(["cpu.usage"]));
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.True(result.IsSuccessful);
        Assert.Equal(37, published.Cpu.UsagePercent);
        Assert.Equal(0UL, published.Memory.UsedBytes);
        Assert.DoesNotContain("memory.usage", published.Items.Keys);
        Assert.DoesNotContain(
            SamplingDatasetIds.SystemMemoryUsage,
            published.Datasets.Keys);
    }

    [Fact]
    public void HostedCpuPublicationCannotOverwriteRetainedHostedMemory()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var memory = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 1,
            memoryUsedBytes: 2_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemMemoryUsage);
        var laterCpuFrame = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 88,
            memoryUsedBytes: 99_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage);

        Assert.True(state.Publish(
            memory,
            MetricSampleRequest.ForIds(["memory.usage"])).IsSuccessful);
        Assert.True(state.Publish(
            laterCpuFrame,
            MetricSampleRequest.ForIds(["cpu.usage"])).IsSuccessful);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.Equal(88, published.Cpu.UsagePercent);
        Assert.Equal(2_000UL, published.Memory.UsedBytes);
        Assert.Equal(
            1UL,
            published.Datasets[SamplingDatasetIds.SystemMemoryUsage]
                .SourceGeneration);
        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Datasets[SamplingDatasetIds.SystemMemoryUsage].Status);
    }

    [Fact]
    public void FailedAttemptAtomicallyReplacesCurrentSlotWithEmptyValue()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var current = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 0,
            memoryUsedBytes: 2_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemMemoryUsage);
        Assert.True(state.Publish(
            current,
            MetricSampleRequest.ForIds(["memory.usage"])).IsSuccessful);

        var attemptedAt = current.CapturedAt.AddSeconds(1);
        state.RecordFailure(
            MetricSampleRequest.ForIds(["memory.usage"]),
            attemptedAt,
            "fixture-failure");
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        var observation = published.Datasets[SamplingDatasetIds.SystemMemoryUsage];

        Assert.Equal(0UL, published.Memory.UsedBytes);
        Assert.Equal(SamplingObservationStatus.Unavailable, observation.Status);
        Assert.Equal(0UL, observation.SourceGeneration);
        Assert.Equal(0, observation.ObservedAtUtcTicks);
        Assert.Equal(attemptedAt.UtcTicks, observation.LastAttemptAtUtcTicks);
        Assert.Equal("fixture-failure", observation.FailureCode);
        Assert.Equal(DateTimeOffset.UnixEpoch, published.CapturedAt);
        Assert.False(published.TryGetCurrentDataset(
            SamplingDatasetIds.SystemMemoryUsage,
            out _));
    }

    [Fact]
    public void ColdFailedAttemptPublishesUnavailableDatasetMetadata()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var attemptedAt = DateTimeOffset.UnixEpoch.AddSeconds(9);

        state.RecordFailure(
            MetricSampleRequest.ForIds(["cpu.usage"]),
            attemptedAt,
            "fixture-cold-failure");

        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        var observation = published.Datasets[SamplingDatasetIds.SystemCpuUsage];
        Assert.Empty(published.Items);
        Assert.Equal(DateTimeOffset.UnixEpoch, published.CapturedAt);
        Assert.Equal(SamplingObservationStatus.Unavailable, observation.Status);
        Assert.Equal(attemptedAt.UtcTicks, observation.LastAttemptAtUtcTicks);
        Assert.Equal(0, observation.LastSuccessAtUtcTicks);
        Assert.Equal("fixture-cold-failure", observation.FailureCode);
    }

    [Fact]
    public void RequestedMetricMissingFromAttemptReplacesOnlyThatSlotWithEmptyValue()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var current = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 23,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        Assert.True(state.Publish(
            current,
            MetricSampleRequest.ForIds(["cpu.usage"])).IsSuccessful);
        var missingAttempt = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 99,
            memoryUsedBytes: 4_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemMemoryUsage);

        var result = state.Publish(
            missingAttempt,
            MetricSampleRequest.ForIds(["cpu.usage"]));

        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        var observation = published.Datasets[SamplingDatasetIds.SystemCpuUsage];
        Assert.False(result.IsSuccessful);
        Assert.Equal(0, published.Cpu.UsagePercent);
        Assert.Equal(DateTimeOffset.UnixEpoch, published.CapturedAt);
        Assert.Equal(SamplingObservationStatus.Unavailable, observation.Status);
        Assert.Equal(missingAttempt.CapturedAt.UtcTicks, observation.LastAttemptAtUtcTicks);
        Assert.Equal("hardware-dataset-missing", observation.FailureCode);
    }

    [Fact]
    public void SubscriptionDeadlineDoesNotExpireTheCurrentValue()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var current = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 37,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        var readyUntil = current.CapturedAt.AddSeconds(5);
        Assert.True(state.Publish(
            current,
            MetricSampleRequest.ForIds(["cpu.usage"]),
            readyUntil).IsSuccessful);

        var beforeExpiry = Assert.IsType<HardwareMetricSnapshot>(
            state.Read(readyUntil));
        Assert.True(beforeExpiry.TryGetCurrentDataset(
            SamplingDatasetIds.SystemCpuUsage,
            readyUntil,
            out _));

        var afterExpiry = Assert.IsType<HardwareMetricSnapshot>(
            state.Read(readyUntil.AddTicks(1)));
        var observation = afterExpiry.Datasets[SamplingDatasetIds.SystemCpuUsage];
        Assert.Equal(37, afterExpiry.Cpu.UsagePercent);
        Assert.Equal(SamplingObservationStatus.Current, observation.Status);
        Assert.Null(observation.FailureCode);
        Assert.True(afterExpiry.TryGetCurrentDataset(
            SamplingDatasetIds.SystemCpuUsage,
            readyUntil.AddTicks(1),
            out _));
    }

    [Fact]
    public void HardwareOwnerReplacementDoesNotInvalidateOtherCurrentSlots()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var first = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 10,
            memoryUsedBytes: 2_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage);
        Assert.True(state.Publish(first, MetricSampleRequest.All).IsSuccessful);
        var replacementBase = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 20,
            memoryUsedBytes: 9_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        var replacement = replacementBase with
        {
            WorkspaceIdentity = 2,
            Datasets = replacementBase.Datasets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { WorkspaceIdentity = 2 },
                StringComparer.OrdinalIgnoreCase)
        };

        Assert.True(state.Publish(
            replacement,
            MetricSampleRequest.ForIds(["cpu.usage"])).IsSuccessful);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.Equal(20, published.Cpu.UsagePercent);
        Assert.Equal(2_000UL, published.Memory.UsedBytes);
        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Datasets[SamplingDatasetIds.SystemMemoryUsage].Status);
        Assert.True(published.TryGetCurrentDataset(
            SamplingDatasetIds.SystemMemoryUsage,
            out _));
    }

    [Fact]
    public void CurrentOwnerCompletionNeedsNoPublicationRevisionCas()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var runId = state.OpenHostedRun();
        var schedule = HardwareSchedule(DateTimeOffset.UtcNow);
        var workspaceOwner = new SamplingOwnerToken(1, 1);
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var firstTicket));
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var secondTicket));
        var first = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 10,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        var second = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 90,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);

        Assert.True(state.TryPublishHosted(
            secondTicket,
            second,
            MetricSampleRequest.ForIds(["cpu.usage"]),
            second.CapturedAt.AddSeconds(5),
            out _));
        Assert.True(state.TryPublishHosted(
            firstTicket,
            first,
            MetricSampleRequest.ForIds(["cpu.usage"]),
            first.CapturedAt.AddSeconds(5),
            out _));

        Assert.Equal(10, Assert.IsType<HardwareMetricSnapshot>(state.Read()).Cpu.UsagePercent);
    }

    [Fact]
    public void RevokedHardwareOwnerRejectsLateCommitWithoutChangingCurrentValue()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var runId = state.OpenHostedRun();
        var schedule = HardwareSchedule(DateTimeOffset.UtcNow);
        var workspaceOwner = new SamplingOwnerToken(1, 1);
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var firstTicket));
        var first = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 10,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        Assert.True(state.TryPublishHosted(
            firstTicket,
            first,
            MetricSampleRequest.ForIds(["cpu.usage"]),
            first.CapturedAt.AddSeconds(5),
            out _));
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var staleTicket));

        workspaceOwner.Revoke();

        var retained = Assert.IsType<HardwareMetricSnapshot>(state.Read(first.CapturedAt));
        Assert.Equal(10, retained.Cpu.UsagePercent);
        Assert.Equal(
            SamplingObservationStatus.Current,
            retained.Datasets[SamplingDatasetIds.SystemCpuUsage].Status);
        Assert.Null(
            retained.Datasets[SamplingDatasetIds.SystemCpuUsage].FailureCode);
        Assert.False(state.TryPublishHosted(
            staleTicket,
            first with { CapturedAt = first.CapturedAt.AddSeconds(1) },
            MetricSampleRequest.ForIds(["cpu.usage"]),
            first.CapturedAt.AddSeconds(6),
            out _));
    }

    [Fact]
    public void ClosedHardwareRunRejectsPreparedPayloadWithoutChangingCurrentValue()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var runId = state.OpenHostedRun();
        var schedule = HardwareSchedule(DateTimeOffset.UtcNow);
        var workspaceOwner = new SamplingOwnerToken(1, 1);
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var firstTicket));
        var first = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 10,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        Assert.True(state.TryPublishHosted(
            firstTicket,
            first,
            MetricSampleRequest.ForIds(["cpu.usage"]),
            first.CapturedAt.AddSeconds(5),
            out _));
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var staleTicket));

        Assert.True(state.CloseHostedRun(
            runId,
            first.CapturedAt.AddSeconds(1),
            "hardware-owner-stopped"));
        Assert.False(state.TryPublishHosted(
            staleTicket,
            first with { CapturedAt = first.CapturedAt.AddSeconds(2) },
            MetricSampleRequest.ForIds(["cpu.usage"]),
            first.CapturedAt.AddSeconds(7),
            out _));
        var retained = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        Assert.Equal(10, retained.Cpu.UsagePercent);
        Assert.Equal(
            SamplingObservationStatus.Current,
            retained.Datasets[SamplingDatasetIds.SystemCpuUsage].Status);
    }

    [Fact]
    public void RetainedCandidateIsAnEmptyCompletionForItsExactSlot()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var current = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 25,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage);
        var retained = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 99,
            memoryUsedBytes: 0,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage) with
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemCpuUsage] =
                    CreateDatasetObservation(
                        SamplingDatasetIds.SystemCpuUsage,
                        SamplingObservationStatus.RetainedLastGood,
                        2)
            }
        };

        Assert.True(state.Publish(
            current,
            MetricSampleRequest.ForIds(["cpu.usage"])).IsSuccessful);
        var result = state.Publish(
            retained,
            MetricSampleRequest.ForIds(["cpu.usage"]));
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, published.Cpu.UsagePercent);
        Assert.Equal(0UL, published.Datasets[
            SamplingDatasetIds.SystemCpuUsage].SourceGeneration);
        Assert.Equal(
            SamplingObservationStatus.Unavailable,
            published.Datasets[SamplingDatasetIds.SystemCpuUsage].Status);
    }

    [Fact]
    public void MixedPublicationSettlesEachExactMetricIndependently()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var frame = CreateDatasetHardwareSnapshot(
            generation: 7,
            cpuUsage: 42,
            memoryUsedBytes: 9_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage) with
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemCpuUsage] =
                    CreateDatasetObservation(
                        SamplingDatasetIds.SystemCpuUsage,
                        SamplingObservationStatus.Current,
                        7),
                [SamplingDatasetIds.SystemMemoryUsage] =
                    CreateDatasetObservation(
                        SamplingDatasetIds.SystemMemoryUsage,
                        SamplingObservationStatus.RetainedLastGood,
                        7)
            }
        };

        var result = state.Publish(frame, MetricSampleRequest.All);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.False(result.IsSuccessful);
        Assert.Equal(2, result.RequestedDatasetCount);
        Assert.Equal(
            [SamplingDatasetIds.SystemCpuUsage],
            result.CommittedDatasetIds);
        Assert.Equal(
            [SamplingDatasetIds.SystemMemoryUsage],
            result.RejectedDatasetIds);
        Assert.Equal(42, published.Cpu.UsagePercent);
        Assert.Equal(0UL, published.Memory.UsedBytes);
        Assert.Equal(
            SamplingObservationStatus.Unavailable,
            published.Datasets[SamplingDatasetIds.SystemMemoryUsage].Status);
        Assert.Equal(
            SamplingObservationStatus.Unavailable,
            published.Memory.ObservationStatus);
    }

    [Fact]
    public void HostedGpuUsagePublicationDoesNotOverwriteHostedVram()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var capacity = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 0,
            memoryUsedBytes: 0,
            gpuUsage: 10,
            gpuMemoryUsedBytes: 2_000,
            "gpu.0.vram",
            SamplingDatasetIds.SystemGpuInventory);
        var laterUsage = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 0,
            memoryUsedBytes: 0,
            gpuUsage: 90,
            gpuMemoryUsedBytes: 9_000,
            "gpu.0.usage",
            SamplingDatasetIds.SystemGpuInventory);

        Assert.True(state.Publish(
            capacity,
            MetricSampleRequest.ForIds(["gpu.0.vram"])).IsSuccessful);
        Assert.True(state.Publish(
            laterUsage,
            MetricSampleRequest.ForIds(["gpu.0.usage"])).IsSuccessful);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        var gpu = Assert.Single(published.Gpus);

        Assert.Equal(90, gpu.UsagePercent);
        Assert.Equal(2_000UL, gpu.UsedMemoryBytes);
        Assert.Equal(SamplingObservationStatus.Current, published.GpuInventory.Status);
    }

    [Fact]
    public void NewGpuTopologyDoesNotClearIndependentGpuMetricSlotsBeforeTheyPublish()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var original = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 0,
            memoryUsedBytes: 0,
            gpuUsage: 75,
            gpuMemoryUsedBytes: 8_000,
            SamplingDatasetIds.SystemGpuInventory,
            "gpu.0.usage",
            "gpu.0.vram");
        Assert.True(state.Publish(
            original,
            MetricSampleRequest.CatalogProbe).IsSuccessful);

        var replacement = CreateDatasetHardwareSnapshot(
            generation: 2,
            cpuUsage: 0,
            memoryUsedBytes: 0,
            gpuUsage: 11,
            gpuMemoryUsedBytes: 1_000,
            SamplingDatasetIds.SystemGpuInventory);
        replacement = replacement with
        {
            Gpus = replacement.Gpus
                .Select(static gpu => gpu with
                {
                    Name = "Replacement GPU",
                    IdentityKey = "gpu-replacement"
                })
                .ToArray(),
            GpuInventory = replacement.GpuInventory with
            {
                TopologyFingerprint = 2,
                Adapters = replacement.GpuInventory.Adapters
                    .Select(static adapter => adapter with
                    {
                        AdapterKey = 2
                    })
                    .ToArray()
            }
        };

        Assert.True(state.Publish(
            replacement,
            MetricSampleRequest.CatalogProbe).IsSuccessful);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());

        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Datasets["gpu.0.usage"].Status);
        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Datasets["gpu.0.vram"].Status);
        Assert.Contains("gpu.0.usage", published.Items.Keys);
        Assert.Contains("gpu.0.vram", published.Items.Keys);
        Assert.Equal(75, Assert.Single(published.Gpus).UsagePercent);
        Assert.Equal(8_000UL, Assert.Single(published.Gpus).UsedMemoryBytes);
        Assert.Equal(2UL, Assert.Single(published.GpuInventory.Adapters).AdapterKey);
        Assert.Equal(
            SamplingObservationStatus.Partial,
            published.GpuInventory.Status);
        Assert.Equal(
            SamplingObservationStatus.NotRequested,
            Assert.Single(published.GpuInventory.Adapters).UsageStatus);
        Assert.Equal(
            SamplingObservationStatus.NotRequested,
            Assert.Single(published.GpuInventory.Adapters).CapacityStatus);
    }

    [Fact]
    public void HostedPhysicalCapturePublishesOnlyTheNativeDueDataset()
    {
        var state = new LastSuccessfulHardwareMetricSnapshot();
        var runId = state.OpenHostedRun();
        var capturedAt = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var schedule = HardwareSchedule(capturedAt);
        var workspaceOwner = new SamplingOwnerToken(1, 1);
        Assert.True(state.TryCaptureHostedTicket(
            runId,
            workspaceOwner,
            schedule,
            out var ticket));
        var physicalCapture = CreateDatasetHardwareSnapshot(
            generation: 1,
            cpuUsage: 42,
            memoryUsedBytes: 9_000,
            gpuUsage: 0,
            gpuMemoryUsedBytes: 0,
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage);

        Assert.True(state.TryPublishHosted(
            ticket,
            physicalCapture,
            MetricSampleRequest.All,
            [SamplingDatasetIds.SystemCpuUsage],
            capturedAt.AddSeconds(5),
            out var result));

        Assert.True(result.IsSuccessful);
        Assert.Equal(
            [SamplingDatasetIds.SystemCpuUsage],
            result.CommittedDatasetIds);
        var published = Assert.IsType<HardwareMetricSnapshot>(state.Read());
        Assert.Contains(SamplingDatasetIds.SystemCpuUsage, published.Datasets);
        Assert.DoesNotContain(
            SamplingDatasetIds.SystemMemoryUsage,
            published.Datasets);
        Assert.Equal(42, published.Cpu.UsagePercent);
    }

    [Fact]
    public async Task MetricDemandWakeIsSaturatingUnderConcurrentReaders()
    {
        using var signal = new SemaphoreSlim(0, 1);
        using var start = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                WindowsHardwareMetricSampler.SignalDemand(signal);
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(callers);

        Assert.Equal(1, signal.CurrentCount);
    }

    [Fact]
    public void MetricDemandWakeIgnoresDisposedSignalDuringOwnerTeardown()
    {
        var signal = new SemaphoreSlim(0, 1);
        signal.Dispose();

        var exception = Record.Exception(
            () => WindowsHardwareMetricSampler.SignalDemand(signal));

        Assert.Null(exception);
    }

    [Fact]
    public void SmartCycle_ConsumesTheCapturedPublicationInsteadOfRereadingCurrent()
    {
        var appRoot = FindAppRoot();
        var files = new[]
        {
            "HostManagerSmartCoordinator.NativeRuntime.cs",
            "HostManagerSmartCoordinator.Sampling.cs",
            "HostManagerSmartCoordinator.NativeProjection.cs"
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(
                appRoot,
                "Infrastructure",
                "Optimization",
                file));
            Assert.DoesNotContain("runtimePlanProvider.Current", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RuntimeRebuildFailureCannotMasqueradeAsThePreviousPlan()
    {
        var source = File.ReadAllText(Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "RuntimeSpecialization",
            "RuntimeSpecializationCoordinator.cs"));

        Assert.DoesNotContain(
            "Keeping previous plan",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "return planProvider.Current",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoringSourceChangeUsesOneCoalescingPumpInsteadOfTaskPerEvent()
    {
        var source = File.ReadAllText(Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "RuntimeSpecialization",
            "RuntimeSpecializationCoordinator.cs"));

        Assert.DoesNotContain(
            "Task.Run(RebuildForMonitoringSourceChangeAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RunMonitoringSourceChangePumpAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "targetSequence == Volatile.Read(ref processedChangedSequence)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyManualOptimizationControlPlaneIsNotComposedOrRegistered()
    {
        var appRoot = FindAppRoot();
        var endpoints = File.ReadAllText(Path.Combine(
            appRoot,
            "Endpoints",
            "OptimizationEndpoints.cs"));
        Assert.DoesNotContain(
            "MapOptimizationGradeEndpoints",
            endpoints,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapOptimizationProtectionEndpoints",
            endpoints,
            StringComparison.Ordinal);

        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "OptimizationServiceRegistration.cs"));
        foreach (var retiredRegistration in new[]
        {
            "IOptimizationA1Store",
            "IOptimizationA2Store",
            "IOptimizationLevel1Store",
            "IOptimizationLevel2Store",
            "IOptimizationLevel3Store",
            "IOptimizationLevel4Store",
            "IOptimizationProtectionPlacementStore",
            "IOptimizationA1Service",
            "IOptimizationA2Service",
            "IOptimizationLevel1Service",
            "IOptimizationLevel2Service",
            "IOptimizationLevel3Service",
            "IOptimizationLevel4Service",
            "IOptimizationProtectionPlacementService",
            "IProcessFreezeController"
        })
        {
            Assert.DoesNotContain(
                retiredRegistration,
                registration,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, true)]
    public void OperationDeadlineUsesTheNativeHalfOpenBoundary(
        ulong now,
        ulong deadline,
        bool expectedExpired)
    {
        Assert.Equal(
            expectedExpired,
            HostManagerOperationCoordinatorOwner.IsActionExpired(now, deadline));
    }

    [Fact]
    public void MemoryCleanupMarksAttemptsOnlyAfterTheBatchWriterReturns()
    {
        var source = File.ReadAllText(Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.Execution.MemoryCleanup.cs"));
        var writerCall = source.IndexOf(
            "processPolicyWriter.TryApplyBatch(requests)",
            StringComparison.Ordinal);
        var attemptedFeedback = source.IndexOf(
            "feedback[index] = feedback[index] with { Attempted = true }",
            StringComparison.Ordinal);

        Assert.True(writerCall >= 0);
        Assert.True(attemptedFeedback > writerCall);
    }

    [Theory]
    [InlineData((int)HostManagerOperationEffectOutcome.RetryableFailure)]
    [InlineData((int)HostManagerOperationEffectOutcome.TerminalFailure)]
    [InlineData((int)HostManagerOperationEffectOutcome.Canceled)]
    [InlineData((int)HostManagerOperationEffectOutcome.Uncertain)]
    public void StartedOperationCannotReopenAutomaticEffectExecution(
        int outcomeValue)
    {
        var outcome = (HostManagerOperationEffectOutcome)outcomeValue;
        var normalized =
            HostManagerOperationCoordinatorOwner.NormalizeStartedCompletion(
                new HostManagerOperationEffectCompletion(outcome, "evidence"));

        Assert.Equal(
            HostManagerOperationEffectOutcome.Uncertain,
            normalized.Outcome);
        Assert.Equal("evidence", normalized.Message);
    }

    [Fact]
    public void StartedOperationMayOnlyCommitExplicitSuccess()
    {
        var completion = new HostManagerOperationEffectCompletion(
            HostManagerOperationEffectOutcome.Succeeded,
            "done");

        Assert.Same(
            completion,
            HostManagerOperationCoordinatorOwner.NormalizeStartedCompletion(
                completion));
    }

    [Fact]
    public async Task JournalCommitReportsPostReplaceVerificationFailureAsAmbiguous()
    {
        var root = CreateTemporaryRoot();
        var temporaryPath = Path.Combine(root, "journal.tmp");
        var journalPath = Path.Combine(root, "journal.bin");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3, 4]);
            var exception = await Assert.ThrowsAsync<NativeTransactionJournalCommitException>(
                () => WindowsNativeTransactionJournalFileCommitter.Instance
                    .CommitAsync(
                        temporaryPath,
                        journalPath,
                        new byte[] { 1, 2, 3, 5 },
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal(
                NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                exception.Outcome);
            Assert.True(
                NativeTransactionJournalCommitException.IsCommitAmbiguous(exception));
            Assert.Equal(
                new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(journalPath));
            Assert.False(File.Exists(temporaryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JournalCommitReportsReplaceFailureAsNotCommitted()
    {
        var root = CreateTemporaryRoot();
        var temporaryPath = Path.Combine(root, "missing.tmp");
        var journalPath = Path.Combine(root, "journal.bin");
        try
        {
            var exception = await Assert.ThrowsAsync<NativeTransactionJournalCommitException>(
                () => WindowsNativeTransactionJournalFileCommitter.Instance
                    .CommitAsync(
                        temporaryPath,
                        journalPath,
                        new byte[] { 1 },
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal(
                NativeTransactionJournalCommitOutcome.NotCommitted,
                exception.Outcome);
            Assert.False(
                NativeTransactionJournalCommitException.IsCommitAmbiguous(exception));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PayloadPrepareCleanupRequiresOwnedUncommittedAndNonAmbiguousFailure()
    {
        var notCommitted = new NativeTransactionJournalCommitException(
            NativeTransactionJournalCommitOutcome.NotCommitted,
            new IOException("before canonical replace"));
        var ambiguous = new NativeTransactionJournalPersistenceException(
            new NativeTransactionJournalCommitException(
                NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                new IOException("after canonical replace")));
        var aggregateAmbiguous = new AggregateException(
            notCommitted,
            ambiguous);
        var unknown = new IOException("unclassified prepare failure");

        Assert.True(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: false,
                notCommitted));
        Assert.True(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: true,
                unknown));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: false,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: true,
                notCommitted));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: true,
                prepareRejectedWithoutMutation: true,
                notCommitted));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: false,
                ambiguous));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: true,
                ambiguous));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: false,
                aggregateAmbiguous));
        Assert.False(
            NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                payloadOwnedByAttempt: true,
                prepareCompleted: false,
                prepareRejectedWithoutMutation: false,
                unknown));
    }

    [Fact]
    public void DurableRootManifestDistinguishesVirginMissingAndInitializedMissing()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "canonical.bin");
        try
        {
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            manifest.RequireVirginOrInitializing();
            manifest.EnsureInitializing();
            manifest.RequireVirginOrInitializing();

            var firstImage = new byte[] { 1, 2, 3, 4 };
            var firstTransition = manifest.BeginCanonicalTransition(firstImage);
            manifest.FinalizeCanonicalTransition(firstTransition);
            Assert.Throws<IOException>(
                manifest.RequireVirginOrInitializing);

            var reopened = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            reopened.ValidateOrAdoptCanonical(firstImage);

            var secondImage = new byte[] { 5, 6, 7, 8 };
            _ = reopened.BeginCanonicalTransition(secondImage);
            reopened.ValidateOrAdoptCanonical(secondImage);
            reopened.ValidateOrAdoptCanonical(secondImage);

            _ = reopened.BeginCanonicalTransition(
                new byte[] { 9, 10, 11, 12 });
            reopened.ValidateOrAdoptCanonical(secondImage);
            reopened.ValidateOrAdoptCanonical(secondImage);
            Assert.Throws<InvalidDataException>(
                () => reopened.ValidateOrAdoptCanonical(
                    new byte[] { 13, 14, 15, 16 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableRootManifestRejectsCorruptState()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "canonical.bin");
        try
        {
            File.WriteAllBytes(
                canonicalPath + ".root.manifest",
                new byte[96]);
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");

            Assert.Throws<InvalidDataException>(
                manifest.RequireVirginOrInitializing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyInitializedDurableRootManifestMigratesWithoutChangingAuthority()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "canonical.bin");
        var manifestPath = canonicalPath + ".root.manifest";
        try
        {
            var canonicalImage = new byte[] { 1, 3, 5, 7, 9 };
            File.WriteAllBytes(canonicalPath, canonicalImage);
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            manifest.EnsureInitializing();
            var transition = manifest.BeginCanonicalTransition(canonicalImage);
            manifest.FinalizeCanonicalTransition(transition);

            var currentImage = File.ReadAllBytes(manifestPath);
            var expectedRootIncarnation = new Guid(currentImage.AsSpan(48, 16));
            var expectedCanonicalSha256 = SHA256.HashData(canonicalImage);
            File.WriteAllBytes(
                manifestPath,
                ConvertCurrentRootManifestToLegacy(currentImage));

            var reopened = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            reopened.ValidateExistingCanonical(canonicalImage);

            var migratedImage = File.ReadAllBytes(manifestPath);
            Assert.Equal(144, migratedImage.Length);
            Assert.True(migratedImage.AsSpan(0, 8).SequenceEqual("RMROOT02"u8));
            Assert.Equal(
                2u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    migratedImage.AsSpan(8, 4)));
            Assert.Equal(
                expectedRootIncarnation,
                new Guid(migratedImage.AsSpan(48, 16)));
            Assert.True(
                migratedImage.AsSpan(64, SHA256.HashSizeInBytes)
                    .SequenceEqual(expectedCanonicalSha256));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyInitializingDurableRootManifestMigratesWithoutCreatingCanonical()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "canonical.bin");
        var manifestPath = canonicalPath + ".root.manifest";
        try
        {
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            manifest.EnsureInitializing();
            var currentImage = File.ReadAllBytes(manifestPath);
            var expectedRootIncarnation = new Guid(currentImage.AsSpan(48, 16));
            File.WriteAllBytes(
                manifestPath,
                ConvertCurrentRootManifestToLegacy(currentImage));

            var reopened = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            reopened.RequireVirginOrInitializing();

            var migratedImage = File.ReadAllBytes(manifestPath);
            Assert.Equal(144, migratedImage.Length);
            Assert.Equal(
                1u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    migratedImage.AsSpan(12, 4)));
            Assert.Equal(
                expectedRootIncarnation,
                new Guid(migratedImage.AsSpan(48, 16)));
            Assert.False(File.Exists(canonicalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyDurableRootDigestMismatchFailsWithoutMigration()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "canonical.bin");
        var manifestPath = canonicalPath + ".root.manifest";
        try
        {
            var canonicalImage = new byte[] { 2, 4, 6, 8 };
            File.WriteAllBytes(canonicalPath, canonicalImage);
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            manifest.EnsureInitializing();
            var transition = manifest.BeginCanonicalTransition(canonicalImage);
            manifest.FinalizeCanonicalTransition(transition);
            var legacyImage = ConvertCurrentRootManifestToLegacy(
                File.ReadAllBytes(manifestPath));
            legacyImage[64] ^= 0xFF;
            File.WriteAllBytes(manifestPath, legacyImage);

            var reopened = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");
            var exception = Assert.Throws<InvalidDataException>(
                () => reopened.ValidateExistingCanonical(canonicalImage));

            Assert.Contains("does not match the canonical file", exception.Message);
            Assert.Equal(legacyImage, File.ReadAllBytes(manifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableRootManifestDoesNotTreatMissingDirectoryAsVirgin()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(
            root,
            "missing",
            "canonical.bin");
        try
        {
            var manifest = new HostManagerDurableRootManifest(
                canonicalPath,
                "adversarial-test");

            Assert.Throws<IOException>(
                manifest.RequireVirginOrInitializing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectAttributionBindingRequiresExactProcessInstance()
    {
        var exact = new RuntimeProcessIdentity(
            42,
            null,
            "app",
            "C:\\app.exe",
            false,
            StartKey: 1001);
        var reusedPid = exact with { StartKey = 1002 };
        var unavailable = exact with { StartKey = null };

        Assert.True(
            NativeRuntimeProcessAttributionResolver
                .MatchesDirectProcessInstance(42, 1001, exact));
        Assert.False(
            NativeRuntimeProcessAttributionResolver
                .MatchesDirectProcessInstance(42, 1001, reusedPid));
        Assert.False(
            NativeRuntimeProcessAttributionResolver
                .MatchesDirectProcessInstance(42, 1001, unavailable));
    }

    [Fact]
    public void ProcessIdentityPathsDoNotDowngradeToPidOrNameAuthority()
    {
        var appRoot = FindAppRoot();
        var resolver = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "ProcessAttribution",
            "NativeRuntimeProcessAttributionResolver.cs"));
        var serviceResolver = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "ProcessIdentity",
            "WindowsServiceIdentityResolver.cs"));
        var processSampler = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "ResourceBreakdown",
            "WindowsResourceBreakdownSampler.ProcessSampling.cs"));

        Assert.Contains("binding.StartKey", resolver, StringComparison.Ordinal);
        Assert.Contains("process.StartKey", serviceResolver, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessId", processSampler, StringComparison.Ordinal);
        Assert.DoesNotContain("SelfProcessNames", processSampler, StringComparison.Ordinal);
    }

    private static NativeItemSamplingSubscriptionScheduleReceipt HardwareSchedule(
        DateTimeOffset now)
        => new(
            NativeItemSamplingSubscriptionScheduleOrigin.Plan,
            1,
            1,
            1,
            1,
            now,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            now.AddSeconds(5),
            now,
            1,
            1,
            1,
            0);

    private static HardwareMetricSnapshot CreateDatasetHardwareSnapshot(
        ulong generation,
        double cpuUsage,
        ulong memoryUsedBytes,
        double gpuUsage,
        ulong gpuMemoryUsedBytes,
        params string[] currentDatasets)
    {
        var capturedAt = DateTimeOffset.UnixEpoch.AddSeconds(
            checked((long)generation));
        var datasets = currentDatasets.ToDictionary(
            static id => id,
            id => CreateDatasetObservation(
                id,
                SamplingObservationStatus.Current,
                generation),
            StringComparer.OrdinalIgnoreCase);
        var gpuSensors = new GpuSensorMetrics(
            new HardwareSensorProviderState("test", "Active", null),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var gpu = new GpuMetrics(
            0,
            "GPU 0",
            gpuUsage,
            0,
            0,
            0,
            0,
            gpuMemoryUsedBytes,
            10_000,
            gpuMemoryUsedBytes / 100d,
            gpuSensors,
            "test",
            true,
            "gpu-0");
        var inventory = new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.Current,
            generation,
            capturedAt.UtcTicks,
            1,
            0,
            0,
            1,
            [new SchedulingGpuAdapterObservation(
                0,
                1,
                SchedulingGpuCapabilityMask.Usage
                    | SchedulingGpuCapabilityMask.DedicatedMemory,
                SchedulingGpuMetricMask.Usage
                    | SchedulingGpuMetricMask.UsedDedicatedMemory
                    | SchedulingGpuMetricMask.TotalDedicatedMemory,
                SamplingObservationStatus.Current,
                SamplingObservationStatus.Current,
                gpuUsage,
                gpuMemoryUsedBytes,
                10_000,
                generation,
                capturedAt.UtcTicks)]);
        var cpuSensors = new CpuSensorMetrics(
            new HardwareSensorProviderState("test", "Active", null),
            null,
            null,
            null,
            null);
        var memory = new MemoryMetrics(
            memoryUsedBytes,
            100_000,
            memoryUsedBytes / 1_000d,
            true,
            "test memory")
        {
            ObservationStatus = SamplingObservationStatus.Current
        };
        var pageFile = new VirtualMemoryMetrics(
            0,
            100_000,
            0,
            "test page file",
            true)
        {
            ObservationStatus = SamplingObservationStatus.NotRequested
        };
        var items = new Dictionary<string, MetricValue>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["cpu.usage"] = new(
                "cpu.usage",
                "CPU",
                "CPU",
                $"{cpuUsage}%",
                cpuUsage,
                "%",
                cpuUsage,
                "test"),
            ["memory.usage"] = new(
                "memory.usage",
                "Memory",
                "Memory",
                memoryUsedBytes.ToString(),
                memoryUsedBytes,
                "B",
                memory.UsagePercent,
                "test"),
            ["gpu.0.usage"] = new(
                "gpu.0.usage",
                "GPU",
                "GPU",
                $"{gpuUsage}%",
                gpuUsage,
                "%",
                gpuUsage,
                "test"),
            ["gpu.0.vram"] = new(
                "gpu.0.vram",
                "VRAM",
                "GPU",
                gpuMemoryUsedBytes.ToString(),
                gpuMemoryUsedBytes,
                "B",
                gpu.MemoryUsagePercent,
                "test")
        };
        return new HardwareMetricSnapshot(
            capturedAt,
            new CpuMetrics(
                "CPU",
                cpuUsage,
                true,
                CpuMetricObservationStatus.Complete,
                generation,
                1_000,
                0,
                0,
                0,
                "test",
                cpuSensors),
            memory,
            pageFile,
            [gpu],
            inventory,
            items)
        {
            Datasets = datasets,
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = generation
        };
    }

    private static HardwareMetricDatasetObservation CreateDatasetObservation(
        string datasetId,
        SamplingObservationStatus status,
        ulong generation)
        => new(
            datasetId,
            status,
            generation,
            DateTimeOffset.UnixEpoch.AddSeconds(checked((long)generation)).UtcTicks,
            1,
            1,
            1,
            generation);

    private static byte[] ConvertCurrentRootManifestToLegacy(byte[] currentImage)
    {
        Assert.Equal(144, currentImage.Length);
        var legacyImage = currentImage.AsSpan(0, 96).ToArray();
        "RMROOT01"u8.CopyTo(legacyImage);
        BinaryPrimitives.WriteUInt32LittleEndian(
            legacyImage.AsSpan(8, 4),
            1);
        return legacyImage;
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ResourceManager.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(
            Path.Combine(sourceDirectory, "..", "..", "Resource Manager", "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
