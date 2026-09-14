using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const int ScoreOnlyPerformanceProcessCount = 256;
    private const int ScoreOnlyPerformanceWarmupCycleCount = 8;
    private const int ScoreOnlyPerformanceDefaultCycleCount = 16;
    private const int ScoreOnlyPerformanceEvidenceCycleCount = 128;
    private const long ScoreOnlyPerformanceMaximumFileBytes = 32L * 1024 * 1024;
    private const long ScoreOnlyPerformanceMaximumManagedRetainedGrowthBytes = 8L * 1024 * 1024;
    private const string ScoreOnlyPerformanceEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_SCORE_ONLY_PERFORMANCE_EVIDENCE_ROOT";
    private const string ScoreOnlyPerformanceRetentionMeasurementBoundary =
        "isolated-filtered-testhost-warm-coordinator-before-and-after-corpus-after-forced-full-gc-with-writer-detached";

    private static readonly JsonSerializerOptions ScoreOnlyPerformanceJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    [Fact]
    public async Task ScoreOnlyCycleAt256ProcessBoundaryProducesBoundedPerformanceCorpusWithoutEffects()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable(
            ScoreOnlyPerformanceEvidenceRootEnvironmentVariable);
        var measuredCycleCount = string.IsNullOrWhiteSpace(evidenceRoot)
            ? ScoreOnlyPerformanceDefaultCycleCount
            : ScoreOnlyPerformanceEvidenceCycleCount;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateScaleProcessFacts(ScoreOnlyPerformanceProcessCount),
            performanceLogEnabled: true,
            smartCoordinatorMaximumProcesses: ScoreOnlyPerformanceProcessCount);
        var durableBefore = CaptureDurableFiles(fixture.Root);

        for (var cycle = 0; cycle < ScoreOnlyPerformanceWarmupCycleCount; cycle++)
        {
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        }

        fixture.DebugLogWriter.Records.Clear();
        fixture.DebugLogWriter.PrepareRecordCapacity(measuredCycleCount);
        var normalReleaseEvidenceBefore = CapturePrivateState(
            fixture.Coordinator,
            "hostPublicResourceNormalReleaseEvidence");
        var nativeWorkspaceBefore =
            ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace")
            ?? throw new InvalidDataException(
                "The warm score-only native workspace is unavailable.");
        var computeScoringWorkspaceBefore =
            ReadPrivateField<object>(fixture.Coordinator, "computeScoringWorkspace")
            ?? throw new InvalidDataException(
                "The warm score-only compute workspace is unavailable.");
        var memoryModeWorkspaceBefore = ReadPrivateField<object>(
            fixture.Coordinator,
            "memoryModeWorkspace");
        var placementWorkspaceBefore = ReadPrivateField<object>(
            fixture.Coordinator,
            "placementCoordinatorWorkspace");
        var nativeCapacityBefore = fixture.Workspace.Capacity;
        var nativeSessionBefore = ReadRequiredPrivateReference(
            nativeWorkspaceBefore,
            "session");
        var nativeInputRowsBefore = ReadRequiredPrivateReference(
            nativeWorkspaceBefore,
            "inputRows");
        var nativeActionsBefore = ReadRequiredPrivateReference(
            nativeWorkspaceBefore,
            "actions");
        var nativeFeedbackRowsBefore = ReadRequiredPrivateReference(
            nativeWorkspaceBefore,
            "feedbackRows");
        var nativeSnapshotRowsBefore = ReadRequiredPrivateReference(
            nativeWorkspaceBefore,
            "snapshotRows");
        var recorderCapacityBefore = fixture.DebugLogWriter.Records.Capacity;
        Assert.Same(fixture.Workspace, nativeWorkspaceBefore);
        AssertNativeCapacityPositive(nativeCapacityBefore);

        var managedHeapBytesBefore = CaptureManagedHeapBytesAfterFullGc();
        var corpusIdentity = await RunScoreOnlyPerformanceCorpusAsync(
            fixture,
            evidenceRoot,
            measuredCycleCount);
        var managedHeapBytesAfter = CaptureManagedHeapBytesAfterFullGc();
        var managedRetainedGrowthBytes = managedHeapBytesAfter - managedHeapBytesBefore;
        var nativeCapacityAfter = fixture.Workspace.Capacity;

        Assert.Same(
            nativeWorkspaceBefore,
            ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                fixture.Coordinator,
                "nativeWorkspace"));
        Assert.Same(
            computeScoringWorkspaceBefore,
            ReadPrivateField<object>(fixture.Coordinator, "computeScoringWorkspace"));
        Assert.Same(
            memoryModeWorkspaceBefore,
            ReadPrivateField<object>(fixture.Coordinator, "memoryModeWorkspace"));
        Assert.Same(
            placementWorkspaceBefore,
            ReadPrivateField<object>(fixture.Coordinator, "placementCoordinatorWorkspace"));
        Assert.Same(nativeSessionBefore, ReadRequiredPrivateReference(nativeWorkspaceBefore, "session"));
        Assert.Same(nativeInputRowsBefore, ReadRequiredPrivateReference(nativeWorkspaceBefore, "inputRows"));
        Assert.Same(nativeActionsBefore, ReadRequiredPrivateReference(nativeWorkspaceBefore, "actions"));
        Assert.Same(nativeFeedbackRowsBefore, ReadRequiredPrivateReference(nativeWorkspaceBefore, "feedbackRows"));
        Assert.Same(nativeSnapshotRowsBefore, ReadRequiredPrivateReference(nativeWorkspaceBefore, "snapshotRows"));
        Assert.Equal(nativeCapacityBefore, nativeCapacityAfter);
        Assert.Equal(recorderCapacityBefore, fixture.DebugLogWriter.Records.Capacity);
        Assert.Empty(fixture.DebugLogWriter.Records);
        Assert.True(fixture.DebugLogWriter.ForwardingWriterDetached);
        Assert.True(
            managedRetainedGrowthBytes <= ScoreOnlyPerformanceMaximumManagedRetainedGrowthBytes,
            $"Managed retained growth {managedRetainedGrowthBytes} exceeded the " +
            $"{ScoreOnlyPerformanceMaximumManagedRetainedGrowthBytes}-byte score-only budget.");
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        Assert.Equal(
            normalReleaseEvidenceBefore,
            CapturePrivateState(
                fixture.Coordinator,
                "hostPublicResourceNormalReleaseEvidence"));
        fixture.AssertNoEffectCollaboratorCalls();

        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            WriteScoreOnlyPerformanceRetentionReceipt(
                evidenceRoot,
                corpusIdentity,
                managedHeapBytesBefore,
                managedHeapBytesAfter,
                nativeCapacityBefore,
                nativeCapacityAfter,
                recorderCapacityBefore,
                fixture.DebugLogWriter.Records.Capacity);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ScoreOnlyPerformanceCorpusIdentity>
        RunScoreOnlyPerformanceCorpusAsync(
            ScoreOnlyCoordinatorFixture fixture,
            string? evidenceRoot,
            int measuredCycleCount)
    {
        JsonDebugDiagnosticLogWriter? evidenceWriter = null;
        var forwardingWriterAttached = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                evidenceWriter = CreateScoreOnlyPerformanceEvidenceWriter(evidenceRoot);
                await evidenceWriter.StartAsync(CancellationToken.None);
                fixture.DebugLogWriter.AttachForwardingWriter(evidenceWriter);
                forwardingWriterAttached = true;
            }

            for (var cycle = 0; cycle < measuredCycleCount; cycle++)
            {
                await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
                AssertExactScoreOnlyPerformanceSnapshot(fixture);
            }

            var records = fixture.DebugLogWriter.Records.ToArray();
            Assert.Equal(measuredCycleCount, records.Length);
            Assert.All(records, AssertExactScoreOnlyPerformanceRecord);
            Assert.Equal(
                records.Length,
                records
                    .Select(static record => Assert.IsType<string>(record.Properties["runId"]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            AssertExactScoreOnlyPerformanceOrder(records);

            var first = records[0];
            var last = records[^1];
            var identity = new ScoreOnlyPerformanceCorpusIdentity(
                Assert.IsType<string>(first.Properties["producerInstanceId"]),
                Assert.IsType<long>(first.Properties["cycleSequence"]),
                Assert.IsType<long>(last.Properties["cycleSequence"]),
                Assert.IsType<long>(first.Properties["qpcFrequency"]));
            if (evidenceWriter is not null)
            {
                await SealScoreOnlyPerformanceEvidenceAsync(
                    evidenceRoot!,
                    evidenceWriter,
                    records);
            }
            return identity;
        }
        finally
        {
            try
            {
                if (forwardingWriterAttached && evidenceWriter is not null)
                {
                    fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
                }
            }
            finally
            {
                try
                {
                    if (evidenceWriter is not null)
                    {
                        using var shutdownCancellation =
                            new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await evidenceWriter.StopAsync(shutdownCancellation.Token);
                    }
                }
                finally
                {
                    evidenceWriter?.Dispose();
                    fixture.DebugLogWriter.Records.Clear();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CaptureManagedHeapBytesAfterFullGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    private static object ReadRequiredPrivateReference(object owner, string fieldName)
        => owner.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(owner)
            ?? throw new InvalidDataException(
                $"The required private field '{fieldName}' is unavailable.");

    private static void AssertNativeCapacityPositive(NativeSmartCoordinatorCapacity capacity)
    {
        Assert.InRange(capacity.InputRowCapacity, 1U, uint.MaxValue);
        Assert.InRange(capacity.ActionCapacity, 1U, uint.MaxValue);
        Assert.InRange(capacity.FeedbackCapacity, 1U, uint.MaxValue);
        Assert.InRange(capacity.SnapshotRowCapacity, 1U, uint.MaxValue);
    }

    private static void WriteScoreOnlyPerformanceRetentionReceipt(
        string requestedRoot,
        ScoreOnlyPerformanceCorpusIdentity corpus,
        long managedHeapBytesBefore,
        long managedHeapBytesAfter,
        NativeSmartCoordinatorCapacity capacityBefore,
        NativeSmartCoordinatorCapacity capacityAfter,
        int recorderCapacityBefore,
        int recorderCapacityAfter)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        Assert.InRange(managedHeapBytesBefore, 1, long.MaxValue);
        Assert.InRange(managedHeapBytesAfter, 1, long.MaxValue);
        Assert.Equal(capacityBefore, capacityAfter);
        Assert.Equal(recorderCapacityBefore, recorderCapacityAfter);
        var managedRetainedGrowthBytes = managedHeapBytesAfter - managedHeapBytesBefore;
        Assert.True(
            managedRetainedGrowthBytes <= ScoreOnlyPerformanceMaximumManagedRetainedGrowthBytes);
        var receiptImage = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-score-only-performance-retention-receipt-v1",
                measurementBoundary = ScoreOnlyPerformanceRetentionMeasurementBoundary,
                evidenceRunId = new DirectoryInfo(root).Name,
                warmupCycleCount = ScoreOnlyPerformanceWarmupCycleCount,
                measuredCycleCount = ScoreOnlyPerformanceEvidenceCycleCount,
                producerInstanceId = corpus.ProducerInstanceId,
                firstCycleSequence = corpus.FirstCycleSequence,
                lastCycleSequence = corpus.LastCycleSequence,
                qpcFrequency = corpus.QpcFrequency,
                before = new
                {
                    managedHeapBytesAfterFullGc = managedHeapBytesBefore,
                    capacity = new
                    {
                        inputRowCapacity = capacityBefore.InputRowCapacity,
                        actionCapacity = capacityBefore.ActionCapacity,
                        feedbackCapacity = capacityBefore.FeedbackCapacity,
                        snapshotRowCapacity = capacityBefore.SnapshotRowCapacity
                    }
                },
                after = new
                {
                    managedHeapBytesAfterFullGc = managedHeapBytesAfter,
                    capacity = new
                    {
                        inputRowCapacity = capacityAfter.InputRowCapacity,
                        actionCapacity = capacityAfter.ActionCapacity,
                        feedbackCapacity = capacityAfter.FeedbackCapacity,
                        snapshotRowCapacity = capacityAfter.SnapshotRowCapacity
                    }
                },
                managedRetainedGrowthBytes,
                maximumManagedRetainedGrowthBytes =
                    ScoreOnlyPerformanceMaximumManagedRetainedGrowthBytes,
                coordinatorWorkspaceStable = true,
                sessionInstanceStable = true,
                inputRowsInstanceStable = true,
                actionsInstanceStable = true,
                feedbackRowsInstanceStable = true,
                snapshotRowsInstanceStable = true,
                capacityStable = true,
                recorderCapacityStable = true,
                recorderRecordCountAfterCleanup = 0,
                forwardingWriterDetached = true,
                satisfied = true
            },
            ScoreOnlyPerformanceJsonOptions);
        if (receiptImage.Length > 65536)
        {
            throw new InvalidDataException(
                "The score-only performance retention receipt exceeded its byte bound.");
        }
        using var receiptStream = new FileStream(
            Path.Combine(root, "retention-receipt.json"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        receiptStream.Write(receiptImage);
        receiptStream.WriteByte((byte)'\n');
        receiptStream.Flush(flushToDisk: true);
    }

    private sealed record ScoreOnlyPerformanceCorpusIdentity(
        string ProducerInstanceId,
        long FirstCycleSequence,
        long LastCycleSequence,
        long QpcFrequency);

    private static void AssertExactScoreOnlyPerformanceSnapshot(
        ScoreOnlyCoordinatorFixture fixture)
    {
        var snapshot = fixture.Workspace.Snapshot;
        Assert.True(snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.Equal((uint)ScoreOnlyPerformanceProcessCount, snapshot.ProcessCount);
        Assert.Equal((uint)ScoreOnlyPerformanceProcessCount, snapshot.SoftwareCount);
        Assert.Equal(checked((uint)(2 * ScoreOnlyPerformanceProcessCount)), snapshot.SnapshotRowCount);
        Assert.Equal(0U, snapshot.ActionCount);
        var processRows = fixture.Workspace.CurrentSnapshotRows
            .ToArray()
            .Where(static row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process)
            .ToArray();
        Assert.Equal(ScoreOnlyPerformanceProcessCount, processRows.Length);
        Assert.Equal(
            ScoreOnlyPerformanceProcessCount,
            processRows
                .Select(static row => (row.ProcessId, row.ProcessStartKey))
                .Distinct()
                .Count());
        Assert.All(processRows, static row => Assert.True(row.CpuScore > 0));
    }

    private static void AssertExactScoreOnlyPerformanceRecord(
        DebugDiagnosticLogRecord record)
    {
        Assert.Equal("smart-optimization", record.Category);
        Assert.Equal("cycle-performance", record.EventName);
        Assert.Equal("completed", Assert.IsType<string>(record.Properties["outcome"]));
        Assert.Matches(
            "^[0-9a-f]{32}$",
            Assert.IsType<string>(record.Properties["producerInstanceId"]));
        Assert.InRange(
            Assert.IsType<long>(record.Properties["cycleSequence"]),
            1,
            long.MaxValue);
        Assert.IsType<DateTimeOffset>(record.Properties["cycleStartedAtUtc"]);
        var startedAtQpcTicks = Assert.IsType<long>(
            record.Properties["cycleStartedAtQpcTicks"]);
        var completedAtQpcTicks = Assert.IsType<long>(
            record.Properties["cycleCompletedAtQpcTicks"]);
        Assert.InRange(startedAtQpcTicks, 1, long.MaxValue);
        Assert.InRange(completedAtQpcTicks, startedAtQpcTicks, long.MaxValue);
        Assert.Equal(
            Stopwatch.Frequency,
            Assert.IsType<long>(record.Properties["qpcFrequency"]));
        Assert.True(Assert.IsType<bool>(record.Properties["scoreOnly"]));
        Assert.Equal("smart", Assert.IsType<string>(record.Properties["mode"]));
        Assert.InRange(Assert.IsType<double>(record.Properties["elapsedMs"]), 0, double.MaxValue);
        Assert.InRange(
            Assert.IsType<double>(record.Properties["hostProcessCpuMs"]),
            0,
            double.MaxValue);
        Assert.InRange(
            Assert.IsType<long>(record.Properties["hostProcessAllocatedBytes"]),
            0,
            long.MaxValue);

        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(
            ScoreOnlyPerformanceProcessCount,
            Assert.IsType<int>(counts["sampleTargets"]));
        Assert.Equal(
            ScoreOnlyPerformanceProcessCount,
            Assert.IsType<int>(counts["sampleProcesses"]));
        Assert.Equal(
            ScoreOnlyPerformanceProcessCount,
            Assert.IsType<int>(counts["scoredProcesses"]));
        Assert.Equal(
            ScoreOnlyPerformanceProcessCount,
            Assert.IsType<int>(counts["policyProcesses"]));
        Assert.Equal(
            2 * ScoreOnlyPerformanceProcessCount,
            Assert.IsType<int>(counts["schedulingTargets"]));
        Assert.Equal(0, Assert.IsType<int>(counts["policyChanges"]));
        Assert.Equal(0, Assert.IsType<int>(counts["pendingChanges"]));
        Assert.Equal(0, Assert.IsType<int>(counts["resourceQueueActions"]));
        Assert.Equal(0, Assert.IsType<int>(counts["changedCount"]));
        Assert.Equal(0, Assert.IsType<int>(counts["appliedTargets"]));
        Assert.Equal(0, Assert.IsType<int>(counts["appliedPlacements"]));

        var guards = Assert.IsType<Dictionary<string, object?>>(record.Properties["guards"]);
        Assert.True(Assert.IsType<bool>(guards["sampling"]));
        Assert.True(Assert.IsType<bool>(guards["scoring"]));
        Assert.True(Assert.IsType<bool>(guards["policyExecution"]));
        Assert.False(Assert.IsType<bool>(guards["hardwarePlacement"]));

        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(
            (uint)(2 * ScoreOnlyPerformanceProcessCount),
            Assert.IsType<uint>(details["nativeInputCount"]));
        Assert.Equal(
            (uint)ScoreOnlyPerformanceProcessCount,
            Assert.IsType<uint>(details["nativeSoftwareCount"]));
        Assert.Equal(0U, Assert.IsType<uint>(details["nativeInvalidFactCount"]));
        Assert.Equal(0U, Assert.IsType<uint>(details["nativeFeedbackCount"]));

        var collections = Assert.IsType<Dictionary<string, object?>>(
            record.Properties["hostGcCollections"]);
        Assert.InRange(Assert.IsType<int>(collections["gen0"]), 0, int.MaxValue);
        Assert.InRange(Assert.IsType<int>(collections["gen1"]), 0, int.MaxValue);
        Assert.InRange(Assert.IsType<int>(collections["gen2"]), 0, int.MaxValue);
        Assert.NotEmpty(Assert.IsType<Dictionary<string, object?>[]>(record.Properties["phases"]));
    }

    private static void AssertExactScoreOnlyPerformanceOrder(
        IReadOnlyList<DebugDiagnosticLogRecord> records)
    {
        var producerInstanceId = Assert.IsType<string>(
            records[0].Properties["producerInstanceId"]);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            Assert.Equal(
                producerInstanceId,
                Assert.IsType<string>(record.Properties["producerInstanceId"]));
            Assert.Equal(
                Stopwatch.Frequency,
                Assert.IsType<long>(record.Properties["qpcFrequency"]));
            if (index == 0)
            {
                continue;
            }

            var previous = records[index - 1];
            Assert.Equal(
                checked(Assert.IsType<long>(previous.Properties["cycleSequence"]) + 1),
                Assert.IsType<long>(record.Properties["cycleSequence"]));
            Assert.True(
                Assert.IsType<long>(record.Properties["cycleStartedAtQpcTicks"]) >=
                Assert.IsType<long>(previous.Properties["cycleCompletedAtQpcTicks"]));
        }
    }

    private static JsonDebugDiagnosticLogWriter CreateScoreOnlyPerformanceEvidenceWriter(
        string requestedRoot)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        var runtimePlan = CompiledRuntimePlan.Default with
        {
            Version = 1,
            Diagnostics = new CompiledDiagnosticsPlan(
                DebugModeEnabled: true,
                DebugLogEnabled: true,
                HostManagerSmartCoordinatorScoreOnlyEnabled: true,
                HostManagerSmartCoordinatorPerformanceLogEnabled: true)
        };
        return new JsonDebugDiagnosticLogWriter(
            new PerformanceEvidenceHostEnvironment(root),
            new PerformanceEvidenceRuntimePlanProvider(runtimePlan),
            NullLogger<JsonDebugDiagnosticLogWriter>.Instance);
    }

    private static async Task SealScoreOnlyPerformanceEvidenceAsync(
        string requestedRoot,
        JsonDebugDiagnosticLogWriter writer,
        IReadOnlyList<DebugDiagnosticLogRecord> records)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        if (records.Count != ScoreOnlyPerformanceEvidenceCycleCount)
        {
            throw new InvalidDataException("The performance evidence corpus has an invalid cycle count.");
        }

        var receipt = await writer.SealForEvidenceAsync(
            records.Count,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.True(receipt.Satisfied);
        Assert.Equal(records.Count, receipt.AcceptedRecordCount);
        Assert.Equal(records.Count, receipt.WrittenRecordCount);
        Assert.Equal(0, receipt.DroppedRecordCount);
        Assert.Equal(0, receipt.FailedRecordCount);
        Assert.Equal(0, receipt.RejectedRecordCount);
        Assert.InRange(receipt.LogLength, 1, ScoreOnlyPerformanceMaximumFileBytes);
        Assert.NotNull(receipt.LogSha256);
        Assert.Equal(records.Count, File.ReadLines(receipt.LogPath).Count());
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(receipt.LogPath)!,
            "debug-log.1.jsonl")));

        var receiptPath = Path.Combine(root, "writer-receipt.json");
        var receiptImage = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-score-only-performance-writer-receipt-v1",
                expectedRecordCount = records.Count,
                acceptedRecordCount = receipt.AcceptedRecordCount,
                writtenRecordCount = receipt.WrittenRecordCount,
                droppedRecordCount = receipt.DroppedRecordCount,
                failedRecordCount = receipt.FailedRecordCount,
                rejectedRecordCount = receipt.RejectedRecordCount,
                logFile = "Config/Diagnostics/DebugLogs/debug-log.jsonl",
                logLength = receipt.LogLength,
                logSha256 = receipt.LogSha256
            },
            ScoreOnlyPerformanceJsonOptions);
        using var receiptStream = new FileStream(
            receiptPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        receiptStream.Write(receiptImage);
        receiptStream.WriteByte((byte)'\n');
        receiptStream.Flush(flushToDisk: true);
    }

    private static string ValidateScoreOnlyPerformanceEvidenceRoot(string requestedRoot)
    {
        if (!Path.IsPathFullyQualified(requestedRoot))
        {
            throw new InvalidDataException("The performance evidence root must be absolute.");
        }

        var root = Path.GetFullPath(requestedRoot);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The performance evidence root must already exist and must not be a reparse point.");
        }
        return root;
    }

    private sealed class PerformanceEvidenceRuntimePlanProvider(
        CompiledRuntimePlan initialPlan) : IRuntimePlanProvider
    {
        private CompiledRuntimePlan current = initialPlan;

        public CompiledRuntimePlan Current => Volatile.Read(ref current);

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 1);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            Volatile.Write(ref current, plan);
            return new RuntimePlanPublicationResult(plan, 1, []);
        }
    }

    private static PrivateFieldState[] CapturePrivateState(object owner, string fieldName)
    {
        var value = owner.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)
            ?? throw new InvalidOperationException($"Private field '{fieldName}' is unavailable.");
        return value.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .Select(field => new PrivateFieldState(
                field.Name,
                field.GetValue(value)?.ToString()))
            .ToArray();
    }

    private sealed record PrivateFieldState(string Name, string? Value);

    private sealed class PerformanceEvidenceHostEnvironment(
        string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.ScoreOnlyPerformanceEvidence";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
