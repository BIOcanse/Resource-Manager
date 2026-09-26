using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const string LongSoakEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_LONG_SOAK_EVIDENCE_ROOT";
    private const string LongSoakPlanEnvironmentVariable =
        "RESOURCE_MANAGER_LONG_SOAK_PLAN";
    private const string LongSoakPlanContract =
        "non-adapted-score-only-long-soak-plan-v1";
    private const string LongSoakSampleContract =
        "non-adapted-score-only-long-soak-sample-v2";
    private const string LongSoakShardIndexContract =
        "non-adapted-score-only-long-soak-shard-index-v1";
    private const string LongSoakAssessmentContract =
        "non-adapted-score-only-long-soak-assessment-v1";
    private const string LongSoakLoopReceiptContract =
        "non-adapted-score-only-long-soak-loop-receipt-v2";
    private const string LongSoakMeasurementBoundary =
        "production-scheduled-cycle-profiler-followed-by-test-only-early-and-late-trend-window-start-blocking-compacting-full-gc-finalizer-drain-second-full-gc-and-synchronous-bounded-shard-flush";
    private const int LongSoakMaximumProcesses = 256;
    private const int LongSoakMaximumLineBytes = 16 * 1024;
    private const long LongSoakMaximumTotalSampleBytes = 4L * 1024 * 1024;
    private const int LongSoakRecordsPerShard = 128;
    private const double LongSoakMaximumUtcQpcDriftMilliseconds = 5_000D;

    private static readonly JsonSerializerOptions LongSoakJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions LongSoakCompactJsonOptions =
        new(LongSoakJsonOptions)
        {
            WriteIndented = false
        };

    private static LongSoakEvidencePlan LoadLongSoakEvidencePlan(
        string evidenceRoot,
        string planPath)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
        var expectedPath = Path.Combine(root, "long-soak-plan.json");
        var fullPath = Path.GetFullPath(planPath);
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The long-soak plan must be the evidence-root long-soak-plan.json file.");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException(
                "The long-soak plan is missing, redirected, empty, or oversized.");
        }

        var plan = JsonSerializer.Deserialize<LongSoakEvidencePlan>(
            File.ReadAllBytes(fullPath),
            LongSoakJsonOptions)
            ?? throw new InvalidDataException("The long-soak plan is invalid.");
        ValidateLongSoakEvidencePlan(plan, new DirectoryInfo(root).Name);
        return plan;
    }

    private static LongSoakEvidencePlan CreateDefaultLongSoakEvidencePlan(
        string evidenceRunId = "in-process-contract-smoke")
    {
        var profile = ResolveLongSoakProfile("contract-smoke");
        return new LongSoakEvidencePlan(
            1,
            LongSoakPlanContract,
            evidenceRunId,
            profile.Id,
            AllowLongRunning: false,
            RequestedDurationQualification: false,
            profile.ActiveDurationMilliseconds,
            profile.NormalIntervalMilliseconds,
            profile.MinimumCycleCount,
            profile.MaximumCycleCount,
            profile.WarmupCycleCount,
            profile.MaximumShardCount,
            profile.TrendWindowSize,
            profile.MaximumSuspendedDurationMilliseconds,
            profile.MinimumOpportunityGapMilliseconds,
            profile.MaximumOpportunityGapMilliseconds,
            profile.MaximumOpportunityLatenessMilliseconds,
            profile.Budgets);
    }

    private static void ValidateLongSoakEvidencePlan(
        LongSoakEvidencePlan plan,
        string? expectedRunId)
    {
        if (plan.SchemaVersion != 1
            || !string.Equals(plan.Contract, LongSoakPlanContract, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(plan.EvidenceRunId)
            || plan.EvidenceRunId.Length > 160
            || plan.EvidenceRunId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || (expectedRunId is not null
                && !string.Equals(plan.EvidenceRunId, expectedRunId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The long-soak plan identity is invalid.");
        }

        var expected = ResolveLongSoakProfile(plan.Profile);
        if (plan.ActiveDurationMilliseconds != expected.ActiveDurationMilliseconds
            || plan.NormalIntervalMilliseconds != expected.NormalIntervalMilliseconds
            || plan.MinimumCycleCount != expected.MinimumCycleCount
            || plan.MaximumCycleCount != expected.MaximumCycleCount
            || plan.WarmupCycleCount != expected.WarmupCycleCount
            || plan.MaximumShardCount != expected.MaximumShardCount
            || plan.TrendWindowSize != expected.TrendWindowSize
            || plan.MaximumSuspendedDurationMilliseconds
                != expected.MaximumSuspendedDurationMilliseconds
            || plan.MinimumOpportunityGapMilliseconds
                != expected.MinimumOpportunityGapMilliseconds
            || plan.MaximumOpportunityGapMilliseconds
                != expected.MaximumOpportunityGapMilliseconds
            || plan.MaximumOpportunityLatenessMilliseconds
                != expected.MaximumOpportunityLatenessMilliseconds
            || plan.Budgets != expected.Budgets
            || plan.RequestedDurationQualification != expected.IsFormal)
        {
            throw new InvalidDataException(
                "The long-soak plan does not match its fixed versioned profile.");
        }

        if (expected.IsFormal != plan.AllowLongRunning)
        {
            throw new InvalidDataException(
                expected.IsFormal
                    ? "A formal long-soak profile requires explicit long-running admission."
                    : "The contract-smoke profile cannot acquire long-running admission.");
        }
    }

    private static LongSoakProfileDefinition ResolveLongSoakProfile(string profile)
    {
        var budgets = new LongSoakBudgetPlan(
            ElapsedP99Milliseconds: 50D,
            ProcessCpuP99Milliseconds: 62.5D,
            AllocationP99Bytes: 10_000_000,
            MaximumGcCollectionsPerCycle: 2,
            ManagedHeapGrowthBytes: 8L * 1024 * 1024,
            PrivateBytesGrowthBytes: 32L * 1024 * 1024,
            WorkingSetGrowthBytes: 64L * 1024 * 1024,
            HandleGrowth: 16,
            ThreadGrowth: 4);
        return profile switch
        {
            "contract-smoke" => new(
                profile,
                IsFormal: false,
                ActiveDurationMilliseconds: 4_000,
                NormalIntervalMilliseconds: 250,
                MinimumCycleCount: 15,
                MaximumCycleCount: 20,
                WarmupCycleCount: 4,
                MaximumShardCount: 1,
                TrendWindowSize: 4,
                MaximumSuspendedDurationMilliseconds: 2_000,
                MinimumOpportunityGapMilliseconds: 100,
                MaximumOpportunityGapMilliseconds: 1_000,
                MaximumOpportunityLatenessMilliseconds: 1_000,
                budgets),
            "pressure-30m" => new(
                profile,
                IsFormal: true,
                ActiveDurationMilliseconds: 30 * 60 * 1_000,
                NormalIntervalMilliseconds: 60_000,
                MinimumCycleCount: 30,
                MaximumCycleCount: 34,
                WarmupCycleCount: 4,
                MaximumShardCount: 1,
                TrendWindowSize: 8,
                MaximumSuspendedDurationMilliseconds: 5_000,
                MinimumOpportunityGapMilliseconds: 30_000,
                MaximumOpportunityGapMilliseconds: 90_000,
                MaximumOpportunityLatenessMilliseconds: 30_000,
                budgets),
            "mixed-2h" => new(
                profile,
                IsFormal: true,
                ActiveDurationMilliseconds: 2 * 60 * 60 * 1_000,
                NormalIntervalMilliseconds: 60_000,
                MinimumCycleCount: 120,
                MaximumCycleCount: 124,
                WarmupCycleCount: 4,
                MaximumShardCount: 1,
                TrendWindowSize: 16,
                MaximumSuspendedDurationMilliseconds: 5_000,
                MinimumOpportunityGapMilliseconds: 30_000,
                MaximumOpportunityGapMilliseconds: 90_000,
                MaximumOpportunityLatenessMilliseconds: 30_000,
                budgets),
            "idle-8h" => new(
                profile,
                IsFormal: true,
                ActiveDurationMilliseconds: 8 * 60 * 60 * 1_000,
                NormalIntervalMilliseconds: 60_000,
                MinimumCycleCount: 480,
                MaximumCycleCount: 484,
                WarmupCycleCount: 4,
                MaximumShardCount: 4,
                TrendWindowSize: 16,
                MaximumSuspendedDurationMilliseconds: 5_000,
                MinimumOpportunityGapMilliseconds: 30_000,
                MaximumOpportunityGapMilliseconds: 90_000,
                MaximumOpportunityLatenessMilliseconds: 30_000,
                budgets),
            _ => throw new InvalidDataException(
                $"Unsupported long-soak profile '{profile}'.")
        };
    }

    private static LongSoakShape ResolveLongSoakShape(string profile, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return profile switch
        {
            "contract-smoke" => ((sequence - 1) % 4) switch
            {
                0 => new("smoke-1", "base", 1),
                1 => new("smoke-16", "base", 16),
                2 => new("smoke-64", "base", 64),
                _ => new("smoke-drain", "empty", 0)
            },
            "pressure-30m" => new("pressure-256", "base", 256),
            "mixed-2h" when sequence <= 15 => new("mixed-1", "base", 1),
            "mixed-2h" when sequence <= 30 => new("mixed-16", "base", 16),
            "mixed-2h" when sequence <= 45 => new("mixed-grow-64", "base", 64),
            "mixed-2h" when sequence <= 60 => new("mixed-256", "base", 256),
            "mixed-2h" when sequence <= 75 => new("mixed-shrink-64", "base", 64),
            "mixed-2h" when sequence <= 90 => new("mixed-replacement-64", "replacement", 64),
            "mixed-2h" when sequence <= 105 => new("mixed-pid-reuse-64", "pid-reuse", 64),
            "mixed-2h" => new("mixed-drain", "empty", 0),
            "idle-8h" => new("idle-empty", "empty", 0),
            _ => throw new InvalidDataException(
                $"Unsupported long-soak profile '{profile}'.")
        };
    }

    private static LongSoakClockReading CaptureLongSoakClock()
    {
        QueryUnbiasedInterruptTimePrecise(out var activeTime100Nanoseconds);
        return new LongSoakClockReading(
            activeTime100Nanoseconds,
            Stopwatch.GetTimestamp(),
            DateTimeOffset.UtcNow.UtcDateTime.Ticks);
    }

    private static LongSoakClockAssessment AssessLongSoakClock(
        LongSoakProfileDefinition profile,
        LongSoakClockReading started,
        LongSoakClockReading completed)
    {
        if (completed.ActiveTime100Nanoseconds < started.ActiveTime100Nanoseconds
            || completed.QpcTicks < started.QpcTicks
            || completed.UtcTicks < started.UtcTicks)
        {
            throw new InvalidDataException("A long-soak authority clock moved backwards.");
        }

        var activeMilliseconds =
            (completed.ActiveTime100Nanoseconds - started.ActiveTime100Nanoseconds) / 10_000D;
        var qpcMilliseconds =
            (completed.QpcTicks - started.QpcTicks) * 1_000D / Stopwatch.Frequency;
        var utcMilliseconds = (completed.UtcTicks - started.UtcTicks) / 10_000D;
        var qpcSuspendMilliseconds = Math.Max(0D, qpcMilliseconds - activeMilliseconds);
        var utcActiveDifferenceMilliseconds = Math.Abs(utcMilliseconds - activeMilliseconds);
        var utcQpcDifferenceMilliseconds = Math.Abs(utcMilliseconds - qpcMilliseconds);
        if (!double.IsFinite(activeMilliseconds)
            || !double.IsFinite(qpcMilliseconds)
            || !double.IsFinite(utcMilliseconds)
            || qpcMilliseconds + 25D < activeMilliseconds
            || activeMilliseconds + 0.5D < profile.ActiveDurationMilliseconds
            || qpcSuspendMilliseconds > profile.MaximumSuspendedDurationMilliseconds
            || utcActiveDifferenceMilliseconds > profile.MaximumSuspendedDurationMilliseconds
            || utcQpcDifferenceMilliseconds > LongSoakMaximumUtcQpcDriftMilliseconds)
        {
            throw new InvalidDataException(
                "The long-soak active-duration or suspend-budget clock contract failed.");
        }

        return new LongSoakClockAssessment(
            started,
            completed,
            Stopwatch.Frequency,
            activeMilliseconds,
            qpcMilliseconds,
            utcMilliseconds,
            qpcSuspendMilliseconds,
            utcActiveDifferenceMilliseconds,
            utcQpcDifferenceMilliseconds);
    }

    [DllImport("KernelBase.dll", ExactSpelling = true)]
    private static extern void QueryUnbiasedInterruptTimePrecise(
        out ulong unbiasedTime100Nanoseconds);

    private sealed record LongSoakEvidencePlan(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        string Profile,
        bool AllowLongRunning,
        bool RequestedDurationQualification,
        int ActiveDurationMilliseconds,
        int NormalIntervalMilliseconds,
        int MinimumCycleCount,
        int MaximumCycleCount,
        int WarmupCycleCount,
        int MaximumShardCount,
        int TrendWindowSize,
        int MaximumSuspendedDurationMilliseconds,
        int MinimumOpportunityGapMilliseconds,
        int MaximumOpportunityGapMilliseconds,
        int MaximumOpportunityLatenessMilliseconds,
        LongSoakBudgetPlan Budgets);

    private sealed record LongSoakProfileDefinition(
        string Id,
        bool IsFormal,
        int ActiveDurationMilliseconds,
        int NormalIntervalMilliseconds,
        int MinimumCycleCount,
        int MaximumCycleCount,
        int WarmupCycleCount,
        int MaximumShardCount,
        int TrendWindowSize,
        int MaximumSuspendedDurationMilliseconds,
        int MinimumOpportunityGapMilliseconds,
        int MaximumOpportunityGapMilliseconds,
        int MaximumOpportunityLatenessMilliseconds,
        LongSoakBudgetPlan Budgets);

    private sealed record LongSoakBudgetPlan(
        double ElapsedP99Milliseconds,
        double ProcessCpuP99Milliseconds,
        long AllocationP99Bytes,
        int MaximumGcCollectionsPerCycle,
        long ManagedHeapGrowthBytes,
        long PrivateBytesGrowthBytes,
        long WorkingSetGrowthBytes,
        int HandleGrowth,
        int ThreadGrowth);

    private readonly record struct LongSoakShape(
        string Id,
        string IdentitySet,
        int ProcessCount);

    private readonly record struct LongSoakClockReading(
        ulong ActiveTime100Nanoseconds,
        long QpcTicks,
        long UtcTicks);

    private sealed record LongSoakClockAssessment(
        LongSoakClockReading Started,
        LongSoakClockReading Completed,
        long QpcFrequency,
        double ActiveElapsedMilliseconds,
        double QpcElapsedMilliseconds,
        double UtcElapsedMilliseconds,
        double SuspendedDurationMilliseconds,
        double UtcActiveDifferenceMilliseconds,
        double UtcQpcDifferenceMilliseconds);

    private sealed record LongSoakCompactSample(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        string Profile,
        int Sequence,
        string ProducerInstanceId,
        string OpportunityLedgerId,
        string WriterInstanceId,
        string TestHostInstanceId,
        int TestHostProcessId,
        long TestHostStartUtcTicks,
        string BootIdentitySha256,
        string Trigger,
        string EpochId,
        int ProcessCount,
        ulong PlannedActiveTime100Nanoseconds,
        ulong OpportunityActiveTime100Nanoseconds,
        long OpportunityQpcTicks,
        long OpportunityUtcTicks,
        double OpportunityLatenessMilliseconds,
        long CycleStartedAtQpcTicks,
        long CycleCompletedAtQpcTicks,
        long QpcFrequency,
        ulong ActiveTime100Nanoseconds,
        long SampledAtQpcTicks,
        long SampledAtUtcTicks,
        double ElapsedMilliseconds,
        double ProcessCpuMilliseconds,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int PolicyChanges,
        int PendingChanges,
        int ResourceQueueActions,
        int ChangedCount,
        int AppliedTargets,
        int AppliedPlacements,
        bool EffectsAllZero,
        string ZeroEffectVectorSha256,
        uint NativeActionCount,
        uint NativeProcessCount,
        uint NativeSoftwareCount,
        string ResourceStabilization,
        long ManagedHeapBytes,
        long GcHeapBytes,
        long FragmentedBytes,
        long PrivateBytes,
        long WorkingSetBytes,
        int HandleCount,
        int ThreadCount);

    private sealed record LongSoakShardIdentity(
        string Path,
        int RecordCount,
        long Bytes,
        string Sha256,
        int FirstSequence,
        int LastSequence,
        string PreviousShardSha256,
        ulong FirstActiveTime100Nanoseconds,
        ulong LastActiveTime100Nanoseconds,
        long FirstQpcTicks,
        long LastQpcTicks,
        long FirstUtcTicks,
        long LastUtcTicks,
        string ProducerInstanceId,
        string OpportunityLedgerId,
        string WriterInstanceId,
        string TestHostInstanceId,
        int TestHostProcessId,
        long TestHostStartUtcTicks,
        string BootIdentitySha256);

    private sealed record LongSoakShardIndex(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        string Profile,
        int RecordsPerShard,
        int RecordCount,
        long TotalBytes,
        LongSoakShardIdentity[] Shards,
        bool Sealed);

    private sealed record LongSoakResourceTrend(
        int WindowSize,
        long ManagedHeapEarlyP50,
        long ManagedHeapLateP50,
        long ManagedHeapGrowth,
        long PrivateBytesEarlyP50,
        long PrivateBytesLateP50,
        long PrivateBytesGrowth,
        long WorkingSetEarlyP50,
        long WorkingSetLateP50,
        long WorkingSetGrowth,
        double HandleEarlyP50,
        double HandleLateP50,
        double HandleGrowth,
        double ThreadEarlyP50,
        double ThreadLateP50,
        double ThreadGrowth);

    private sealed record LongSoakAssessment(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        string Profile,
        int CycleCount,
        int WarmupCycleCount,
        int MeasuredCycleCount,
        int FirstSequence,
        int LastSequence,
        int ScheduledOpportunities,
        int CycleEntries,
        int ProviderPairs,
        int MaterializedRecords,
        int WriterAdmissions,
        int CommittedSamples,
        double MinimumOpportunityGapMilliseconds,
        double MaximumOpportunityGapMilliseconds,
        double MaximumOpportunityLatenessMilliseconds,
        double FinalUnobservedTailMilliseconds,
        double ElapsedP99Milliseconds,
        double ProcessCpuP99Milliseconds,
        long AllocationP99Bytes,
        int MaximumGcCollectionsPerCycle,
        LongSoakResourceTrend Trend,
        bool Passed);

    private sealed record LongSoakRuntimeIdentity(
        string MachineName,
        int TestHostProcessId,
        long TestHostStartUtcTicks,
        long SystemUptimeAtStartMilliseconds,
        long BootEstimateUtcTicks,
        string BootIdentitySha256,
        string TestHostInstanceId,
        string OpportunityLedgerId,
        string WriterInstanceId);

    private sealed record LongSoakOpportunity(
        int Sequence,
        LongSoakClockReading Clock,
        ulong PlannedActiveTime100Nanoseconds,
        double LatenessMilliseconds);
}
