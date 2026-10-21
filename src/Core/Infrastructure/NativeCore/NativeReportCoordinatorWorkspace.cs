using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Domain.RuntimeSpecialization;
using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeReportCoordinatorWorkspace : IDisposable
{
    private readonly IComputeScratch scratch;
    private bool retiring;
    private bool disposed;

    public NativeReportCoordinatorWorkspace(
        CompiledHostManagerReportCoordinatorPlan plan,
        ulong sessionInstanceLow,
        ulong sessionInstanceHigh,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(createScratch);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager report-coordinator plan is not published.");
        }
        if (sessionInstanceLow == 0 || sessionInstanceHigh == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionInstanceLow),
                "Report-coordinator session identity components must be non-zero.");
        }

        var configuration = CreateConfiguration(
            plan,
            sessionInstanceLow,
            sessionInstanceHigh);
        Session = new NativeReportCoordinatorSession(in configuration);
        IComputeScratch? created = null;
        try
        {
            Layout = new NativeReportScratchLayout(Session.Capacity.ReportOutputCapacity,
                Session.Capacity.PersistenceOperationCapacity);
            Rules = CreateRules(plan);
            created = createScratch(Layout.Length)
                ?? throw new InvalidOperationException("The report scratch factory returned no resource.");
            if (created.Length != Layout.Length)
                throw new InvalidOperationException("The report scratch factory returned the wrong layout length.");
            scratch = created;
        }
        catch (Exception failure)
        {
            try { created?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            finally { Session.Dispose(); }
            throw;
        }
    }

    public NativeReportCoordinatorSession Session { get; }
    public NativeReportRuleInput[] Rules { get; }
    internal NativeReportScratchLayout Layout { get; }
    internal PendingReportPlan? PendingPlan { get; set; }

    internal ComputeScratchLease AcquireScratch()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        return scratch.Acquire();
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        scratch.Dispose(); // InUse/error retains both this reference and the native session.
        Session.Dispose();
        disposed = true;
    }

    internal static NativeReportCoordinatorConfiguration CreateConfiguration(
        CompiledHostManagerReportCoordinatorPlan plan,
        ulong sessionInstanceLow,
        ulong sessionInstanceHigh)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var capacity = plan.Recreate.Capacity;
        return new NativeReportCoordinatorConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<
                NativeReportCoordinatorConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            SessionInstanceLow = sessionInstanceLow,
            SessionInstanceHigh = sessionInstanceHigh,
            MaximumSourceCount = checked((uint)capacity.MaximumSourceCount),
            MaximumRuleCount = checked((uint)capacity.MaximumRuleCount),
            MaximumObservationCount = checked((uint)capacity.MaximumObservationCount),
            MaximumReportCount = checked((uint)capacity.MaximumReportCount),
            MaximumTrustCount = checked((uint)capacity.MaximumTrustCount),
            MaximumBucketCount = checked((uint)capacity.MaximumBucketCount),
            MaximumPersistenceOperationCount = checked(
                (uint)capacity.MaximumPersistenceOperationCount),
            MaximumReportOutputCount = checked((uint)capacity.MaximumReportOutputCount),
            SourceIndexCapacity = checked((uint)capacity.SourceIndexCapacity),
            RuleIndexCapacity = checked((uint)capacity.RuleIndexCapacity),
            ObservationIndexCapacity = checked((uint)capacity.ObservationIndexCapacity),
            ReportIndexCapacity = checked((uint)capacity.ReportIndexCapacity),
            TrustIndexCapacity = checked((uint)capacity.TrustIndexCapacity),
            BucketIndexCapacity = checked((uint)capacity.BucketIndexCapacity),
            BucketWidthMilliseconds = checked((ulong)plan.HotPublish.BucketWidthMilliseconds),
            Window24HoursMilliseconds = checked(
                (ulong)plan.HotPublish.Window24HoursMilliseconds),
            Window7DaysMilliseconds = checked(
                (ulong)plan.HotPublish.Window7DaysMilliseconds),
            MaximumFutureSkewMilliseconds = checked(
                (ulong)plan.HotPublish.MaximumFutureSkewMilliseconds),
            DefaultStaleAfterMilliseconds = checked(
                (ulong)plan.HotPublish.DefaultStaleAfterMilliseconds),
            DefaultRetentionMilliseconds = checked(
                (ulong)plan.HotPublish.DefaultRetentionMilliseconds),
            ResidentByteBudget = checked((ulong)plan.HotPublish.ResidentByteBudget),
            Flags = 0,
            MaximumRollingObservationCount = checked(
                (uint)capacity.MaximumRollingObservationCount),
            PlannedPersistenceIndexCapacity = checked(
                (uint)capacity.PlannedPersistenceIndexCapacity),
            MetadataCheckpointIntervalMilliseconds = checked(
                (ulong)plan.HotPublish.MetadataCheckpointIntervalMilliseconds)
        };
    }

    private static NativeReportRuleInput[] CreateRules(
        CompiledHostManagerReportCoordinatorPlan plan)
    {
        var result = new NativeReportRuleInput[plan.Recreate.Rules.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var rule = plan.Recreate.Rules[index];
            result[index] = new NativeReportRuleInput
            {
                StructSize = checked((uint)Unsafe.SizeOf<NativeReportRuleInput>()),
                Flags = rule.Rolling ? (uint)NativeReportRuleFlags.Rolling : 0,
                RuleHandle = rule.RuleHandle,
                RuleGeneration = rule.RuleGeneration,
                SourceHandle = rule.SourceHandle,
                CoverageScopeHandle = rule.CoverageScopeHandle,
                FamilyHandle = rule.FamilyHandle,
                ReportTypeHandle = rule.ReportTypeHandle,
                ResourceKindHandle = rule.ResourceKindHandle,
                PayloadHandle = rule.PayloadHandle,
                MetricSelector = rule.MetricSelector,
                Comparison = rule.Comparison,
                Priority = rule.Priority,
                Severity = rule.Severity,
                RequiredConsecutiveHits = rule.RequiredConsecutiveHits,
                RequiredConsecutiveMisses = rule.RequiredConsecutiveMisses,
                MinimumSampleDurationMilliseconds = checked(
                    (ulong)rule.MinimumSampleDurationMilliseconds),
                ActivationThreshold = rule.ActivationThreshold,
                ClearThreshold = rule.ClearThreshold,
                StaleAfterMilliseconds = checked((ulong)rule.StaleAfterMilliseconds),
                RetentionMilliseconds = checked((ulong)rule.RetentionMilliseconds),
                PredicateGroupHandle = rule.PredicateGroupHandle,
                PredicateIndex = rule.PredicateIndex,
                PredicateCount = rule.PredicateCount
            };
        }

        return result;
    }
}

internal sealed record PendingReportPlan(
    NativeReportPlanInput Input,
    SystemInterruptSnapshot InterruptSnapshot);
