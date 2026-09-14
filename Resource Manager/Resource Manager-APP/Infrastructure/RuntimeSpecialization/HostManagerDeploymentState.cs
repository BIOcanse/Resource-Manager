using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerDeploymentState : IHostManagerDeploymentState
{
    private readonly object sync = new();
    private readonly ModuleState sharedResources = new(HostManagerModuleKind.SharedResources);
    private readonly ModuleState resourceScheduler = new(HostManagerModuleKind.ResourceScheduler);
    private readonly ModuleState adapterPrivateResourceLedger = new(HostManagerModuleKind.AdapterPrivateResourceLedger);
    private readonly ModuleState smartCoordinator = new(HostManagerModuleKind.SmartCoordinator);
    private readonly ModuleState memoryCleanup = new(HostManagerModuleKind.MemoryCleanup);
    private readonly ModuleState placementCoordinator = new(HostManagerModuleKind.PlacementCoordinator);
    private readonly ModuleState appliedOwnership = new(HostManagerModuleKind.AppliedOwnership);
    private readonly ModuleState transactionJournal = new(HostManagerModuleKind.TransactionJournal);
    private readonly ModuleState samplingSubscription = new(HostManagerModuleKind.SamplingSubscription);
    private readonly ModuleState portableSoftwareRegistry = new(HostManagerModuleKind.PortableSoftwareRegistry);
    private readonly ModuleState softwareIdentityCatalog = new(HostManagerModuleKind.SoftwareIdentityCatalog);
    private readonly ModuleState softwareIdentityResolution = new(HostManagerModuleKind.SoftwareIdentityResolution);
    private readonly ModuleState reportCoordinator = new(HostManagerModuleKind.ReportCoordinator);
    private readonly ModuleState fileQuery = new(HostManagerModuleKind.FileQuery);
    private readonly ModuleState publicServiceCoordinator = new(
        HostManagerModuleKind.PublicServiceCoordinator);
    private readonly ModuleState displayCoordinator = new(
        HostManagerModuleKind.DisplayCoordinator);
    private readonly ModuleState operationCoordinator = new(
        HostManagerModuleKind.OperationCoordinator);
    private readonly ModuleState metricSnapshot = new(
        HostManagerModuleKind.MetricSnapshot);
    private readonly ModuleState processPolicyExecutor = new(HostManagerModuleKind.ProcessPolicyExecutor);
    private readonly ModuleState pdhCollector = new(HostManagerModuleKind.PdhCollector);
    private CompiledRuntimePlan currentRuntimePlan = CompiledRuntimePlan.Default;
    private ulong publicationSequence;
    private ulong nextAttemptId;

    internal CompiledRuntimePlan CurrentRuntimePlan => Volatile.Read(ref currentRuntimePlan);

    internal HostManagerRuntimePlanPublication CaptureRuntimePlanPublication()
    {
        lock (sync)
        {
            return new HostManagerRuntimePlanPublication(
                currentRuntimePlan,
                publicationSequence);
        }
    }

    public HostManagerDeploymentSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return CreateSnapshot(DateTimeOffset.UtcNow);
            }
        }
    }

    public HostManagerDeploymentDiagnosticsSnapshot CaptureDiagnostics()
    {
        lock (sync)
        {
            var capturedAtUtc = DateTimeOffset.UtcNow;
            var runtimePlan = currentRuntimePlan;
            var plan = runtimePlan.HostManager;
            return new HostManagerDeploymentDiagnosticsSnapshot(
                capturedAtUtc,
                publicationSequence,
                runtimePlan.Version,
                plan.SchemaVersion,
                plan.ProfileRevision,
                plan.ProfileName,
                plan.ProfileSource,
                plan.ProfileSha256,
                plan.PlanSha256,
                plan.BuildSha256,
                plan.RecreateSha256,
                plan.HotPublishSha256,
                plan.PlanEpoch,
                plan.BindingProvenance,
                plan.BuildSpecialize.NativeBinaries,
                CreateSnapshot(capturedAtUtc));
        }
    }

    internal void PublishRuntimePlan(CompiledRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version <= 0)
        {
            throw new InvalidOperationException("A published runtime plan version must be greater than zero.");
        }

        plan.HostManager.RequirePublished();
        lock (sync)
        {
            if (plan.Version <= currentRuntimePlan.Version)
            {
                throw new InvalidOperationException(
                    $"Runtime plan version {plan.Version} must be greater than the published version {currentRuntimePlan.Version}.");
            }
            if (publicationSequence == ulong.MaxValue)
            {
                throw new InvalidOperationException("The Host Manager publication sequence is exhausted.");
            }

            ValidateAppliedOwnershipGeneration(
                currentRuntimePlan.HostManager,
                plan.HostManager);
            var nextPublicationSequence = publicationSequence + 1;
            ObserveDesired(plan.HostManager, nextPublicationSequence, DateTimeOffset.UtcNow);
            publicationSequence = nextPublicationSequence;
            Volatile.Write(ref currentRuntimePlan, plan);
        }
    }

    internal HostManagerDeploymentAttemptToken BeginAttempt(
        HostManagerModuleKind module,
        CompiledHostManagerPlan plan,
        HostManagerDeploymentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.RequirePublished();
        lock (sync)
        {
            if (nextAttemptId == ulong.MaxValue)
            {
                throw new InvalidOperationException("The Host Manager deployment attempt identity is exhausted.");
            }

            var attemptId = nextAttemptId + 1;
            var current = Get(module);
            var token = current.BeginAttempt(
                attemptId,
                plan,
                publicationSequence,
                operation,
                GetRecreateDigest(module, plan),
                GetHotDigest(module, plan),
                DateTimeOffset.UtcNow);
            nextAttemptId = attemptId;
            return token;
        }
    }

    internal HostManagerDeploymentAttemptSettlement CompleteAttemptSucceeded(
        HostManagerDeploymentAttemptToken token)
    {
        lock (sync)
        {
            return TryGet(token.Module, out var module)
                ? module.CompleteSucceeded(
                    token,
                    publicationSequence,
                    DateTimeOffset.UtcNow)
                : HostManagerDeploymentAttemptSettlement.Stale;
        }
    }

    internal HostManagerDeploymentAttemptSettlement CompleteAttemptFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        lock (sync)
        {
            return TryGet(token.Module, out var module)
                ? module.CompleteFailed(
                    token,
                    failureCode,
                    nativeResult,
                    DateTimeOffset.UtcNow)
                : HostManagerDeploymentAttemptSettlement.Stale;
        }
    }

    internal bool CanApplyHot(HostManagerModuleKind module, CompiledHostManagerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.RequirePublished();
        lock (sync)
        {
            var current = Get(module);
            return current.AppliedBuildSha256 == plan.BuildSha256
                && current.AppliedRecreateSha256 == GetRecreateDigest(module, plan);
        }
    }

    private void ObserveDesired(
        CompiledHostManagerPlan plan,
        ulong nextPublicationSequence,
        DateTimeOffset observedAtUtc)
    {
        sharedResources.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.SharedResources.RecreateSha256,
            plan.DeploymentDigests.SharedResources.HotPublishSha256,
            observedAtUtc);
        resourceScheduler.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.ResourceScheduler.RecreateSha256,
            plan.DeploymentDigests.ResourceScheduler.HotPublishSha256,
            observedAtUtc);
        adapterPrivateResourceLedger.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
            plan.DeploymentDigests.AdapterPrivateResourceLedger.HotPublishSha256,
            observedAtUtc);
        smartCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.SmartCoordinator.RecreateSha256,
            plan.DeploymentDigests.SmartCoordinator.HotPublishSha256,
            observedAtUtc);
        memoryCleanup.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.MemoryCleanup.RecreateSha256,
            plan.DeploymentDigests.MemoryCleanup.HotPublishSha256,
            observedAtUtc);
        placementCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.PlacementCoordinator.RecreateSha256,
            plan.DeploymentDigests.PlacementCoordinator.HotPublishSha256,
            observedAtUtc);
        appliedOwnership.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.AppliedOwnership.RecreateSha256,
            plan.DeploymentDigests.AppliedOwnership.HotPublishSha256,
            observedAtUtc);
        transactionJournal.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.TransactionJournal.RecreateSha256,
            plan.DeploymentDigests.TransactionJournal.HotPublishSha256,
            observedAtUtc);
        samplingSubscription.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.SamplingSubscription.RecreateSha256,
            plan.DeploymentDigests.SamplingSubscription.HotPublishSha256,
            observedAtUtc);
        portableSoftwareRegistry.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.PortableSoftwareRegistry.RecreateSha256,
            plan.DeploymentDigests.PortableSoftwareRegistry.HotPublishSha256,
            observedAtUtc);
        softwareIdentityCatalog.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.SoftwareIdentityCatalog.RecreateSha256,
            plan.DeploymentDigests.SoftwareIdentityCatalog.HotPublishSha256,
            observedAtUtc);
        softwareIdentityResolution.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.SoftwareIdentityResolution.RecreateSha256,
            plan.DeploymentDigests.SoftwareIdentityResolution.HotPublishSha256,
            observedAtUtc);
        reportCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.ReportCoordinator.RecreateSha256,
            plan.DeploymentDigests.ReportCoordinator.HotPublishSha256,
            observedAtUtc);
        fileQuery.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.FileQuery.RecreateSha256,
            plan.DeploymentDigests.FileQuery.HotPublishSha256,
            observedAtUtc);
        publicServiceCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.PublicServiceCoordinator.RecreateSha256,
            plan.DeploymentDigests.PublicServiceCoordinator.HotPublishSha256,
            observedAtUtc);
        displayCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.DisplayCoordinator.RecreateSha256,
            plan.DeploymentDigests.DisplayCoordinator.HotPublishSha256,
            observedAtUtc);
        operationCoordinator.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.OperationCoordinator.RecreateSha256,
            plan.DeploymentDigests.OperationCoordinator.HotPublishSha256,
            observedAtUtc);
        metricSnapshot.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.MetricSnapshot.RecreateSha256,
            plan.DeploymentDigests.MetricSnapshot.HotPublishSha256,
            observedAtUtc);
        processPolicyExecutor.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.ProcessPolicyExecutor.RecreateSha256,
            plan.DeploymentDigests.ProcessPolicyExecutor.HotPublishSha256,
            observedAtUtc);
        pdhCollector.ObserveDesired(
            plan,
            nextPublicationSequence,
            plan.DeploymentDigests.PdhCollector.RecreateSha256,
            plan.DeploymentDigests.PdhCollector.HotPublishSha256,
            observedAtUtc);
    }

    private HostManagerDeploymentSnapshot CreateSnapshot(DateTimeOffset capturedAtUtc)
    {
        var plan = currentRuntimePlan;
        return new HostManagerDeploymentSnapshot(
            publicationSequence,
            plan.Version,
            plan.HostManager.PlanEpoch,
            capturedAtUtc,
            sharedResources.CreateSnapshot(),
            resourceScheduler.CreateSnapshot(),
            adapterPrivateResourceLedger.CreateSnapshot(),
            smartCoordinator.CreateSnapshot(),
            memoryCleanup.CreateSnapshot(),
            placementCoordinator.CreateSnapshot(),
            appliedOwnership.CreateSnapshot(),
            transactionJournal.CreateSnapshot(),
            samplingSubscription.CreateSnapshot(),
            portableSoftwareRegistry.CreateSnapshot(),
            softwareIdentityCatalog.CreateSnapshot(),
            softwareIdentityResolution.CreateSnapshot(),
            reportCoordinator.CreateSnapshot(),
            fileQuery.CreateSnapshot(),
            publicServiceCoordinator.CreateSnapshot(),
            displayCoordinator.CreateSnapshot(),
            operationCoordinator.CreateSnapshot(),
            metricSnapshot.CreateSnapshot(),
            processPolicyExecutor.CreateSnapshot(),
            pdhCollector.CreateSnapshot());
    }

    private ModuleState Get(HostManagerModuleKind module)
        => module switch
        {
            HostManagerModuleKind.SharedResources => sharedResources,
            HostManagerModuleKind.ResourceScheduler => resourceScheduler,
            HostManagerModuleKind.AdapterPrivateResourceLedger => adapterPrivateResourceLedger,
            HostManagerModuleKind.SmartCoordinator => smartCoordinator,
            HostManagerModuleKind.MemoryCleanup => memoryCleanup,
            HostManagerModuleKind.PlacementCoordinator => placementCoordinator,
            HostManagerModuleKind.AppliedOwnership => appliedOwnership,
            HostManagerModuleKind.TransactionJournal => transactionJournal,
            HostManagerModuleKind.SamplingSubscription => samplingSubscription,
            HostManagerModuleKind.PortableSoftwareRegistry => portableSoftwareRegistry,
            HostManagerModuleKind.SoftwareIdentityCatalog => softwareIdentityCatalog,
            HostManagerModuleKind.SoftwareIdentityResolution => softwareIdentityResolution,
            HostManagerModuleKind.ReportCoordinator => reportCoordinator,
            HostManagerModuleKind.FileQuery => fileQuery,
            HostManagerModuleKind.PublicServiceCoordinator => publicServiceCoordinator,
            HostManagerModuleKind.DisplayCoordinator => displayCoordinator,
            HostManagerModuleKind.OperationCoordinator => operationCoordinator,
            HostManagerModuleKind.MetricSnapshot => metricSnapshot,
            HostManagerModuleKind.ProcessPolicyExecutor => processPolicyExecutor,
            HostManagerModuleKind.PdhCollector => pdhCollector,
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null)
        };

    private bool TryGet(HostManagerModuleKind module, out ModuleState state)
    {
        state = module switch
        {
            HostManagerModuleKind.SharedResources => sharedResources,
            HostManagerModuleKind.ResourceScheduler => resourceScheduler,
            HostManagerModuleKind.AdapterPrivateResourceLedger => adapterPrivateResourceLedger,
            HostManagerModuleKind.SmartCoordinator => smartCoordinator,
            HostManagerModuleKind.MemoryCleanup => memoryCleanup,
            HostManagerModuleKind.PlacementCoordinator => placementCoordinator,
            HostManagerModuleKind.AppliedOwnership => appliedOwnership,
            HostManagerModuleKind.TransactionJournal => transactionJournal,
            HostManagerModuleKind.SamplingSubscription => samplingSubscription,
            HostManagerModuleKind.PortableSoftwareRegistry => portableSoftwareRegistry,
            HostManagerModuleKind.SoftwareIdentityCatalog => softwareIdentityCatalog,
            HostManagerModuleKind.SoftwareIdentityResolution => softwareIdentityResolution,
            HostManagerModuleKind.ReportCoordinator => reportCoordinator,
            HostManagerModuleKind.FileQuery => fileQuery,
            HostManagerModuleKind.PublicServiceCoordinator => publicServiceCoordinator,
            HostManagerModuleKind.DisplayCoordinator => displayCoordinator,
            HostManagerModuleKind.OperationCoordinator => operationCoordinator,
            HostManagerModuleKind.MetricSnapshot => metricSnapshot,
            HostManagerModuleKind.ProcessPolicyExecutor => processPolicyExecutor,
            HostManagerModuleKind.PdhCollector => pdhCollector,
            _ => null!
        };
        return state is not null;
    }

    private static string GetRecreateDigest(
        HostManagerModuleKind module,
        CompiledHostManagerPlan plan)
        => module switch
        {
            HostManagerModuleKind.SharedResources => plan.DeploymentDigests.SharedResources.RecreateSha256,
            HostManagerModuleKind.ResourceScheduler => plan.DeploymentDigests.ResourceScheduler.RecreateSha256,
            HostManagerModuleKind.AdapterPrivateResourceLedger => plan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
            HostManagerModuleKind.SmartCoordinator => plan.DeploymentDigests.SmartCoordinator.RecreateSha256,
            HostManagerModuleKind.MemoryCleanup => plan.DeploymentDigests.MemoryCleanup.RecreateSha256,
            HostManagerModuleKind.PlacementCoordinator => plan.DeploymentDigests.PlacementCoordinator.RecreateSha256,
            HostManagerModuleKind.AppliedOwnership => plan.DeploymentDigests.AppliedOwnership.RecreateSha256,
            HostManagerModuleKind.TransactionJournal => plan.DeploymentDigests.TransactionJournal.RecreateSha256,
            HostManagerModuleKind.SamplingSubscription => plan.DeploymentDigests.SamplingSubscription.RecreateSha256,
            HostManagerModuleKind.PortableSoftwareRegistry => plan.DeploymentDigests.PortableSoftwareRegistry.RecreateSha256,
            HostManagerModuleKind.SoftwareIdentityCatalog => plan.DeploymentDigests.SoftwareIdentityCatalog.RecreateSha256,
            HostManagerModuleKind.SoftwareIdentityResolution => plan.DeploymentDigests.SoftwareIdentityResolution.RecreateSha256,
            HostManagerModuleKind.ReportCoordinator => plan.DeploymentDigests.ReportCoordinator.RecreateSha256,
            HostManagerModuleKind.FileQuery => plan.DeploymentDigests.FileQuery.RecreateSha256,
            HostManagerModuleKind.PublicServiceCoordinator => plan.DeploymentDigests.PublicServiceCoordinator.RecreateSha256,
            HostManagerModuleKind.DisplayCoordinator => plan.DeploymentDigests.DisplayCoordinator.RecreateSha256,
            HostManagerModuleKind.OperationCoordinator => plan.DeploymentDigests.OperationCoordinator.RecreateSha256,
            HostManagerModuleKind.MetricSnapshot => plan.DeploymentDigests.MetricSnapshot.RecreateSha256,
            HostManagerModuleKind.ProcessPolicyExecutor => plan.DeploymentDigests.ProcessPolicyExecutor.RecreateSha256,
            HostManagerModuleKind.PdhCollector => plan.DeploymentDigests.PdhCollector.RecreateSha256,
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null)
        };

    private static string GetHotDigest(
        HostManagerModuleKind module,
        CompiledHostManagerPlan plan)
        => module switch
        {
            HostManagerModuleKind.SharedResources => plan.DeploymentDigests.SharedResources.HotPublishSha256,
            HostManagerModuleKind.ResourceScheduler => plan.DeploymentDigests.ResourceScheduler.HotPublishSha256,
            HostManagerModuleKind.AdapterPrivateResourceLedger => plan.DeploymentDigests.AdapterPrivateResourceLedger.HotPublishSha256,
            HostManagerModuleKind.SmartCoordinator => plan.DeploymentDigests.SmartCoordinator.HotPublishSha256,
            HostManagerModuleKind.MemoryCleanup => plan.DeploymentDigests.MemoryCleanup.HotPublishSha256,
            HostManagerModuleKind.PlacementCoordinator => plan.DeploymentDigests.PlacementCoordinator.HotPublishSha256,
            HostManagerModuleKind.AppliedOwnership => plan.DeploymentDigests.AppliedOwnership.HotPublishSha256,
            HostManagerModuleKind.TransactionJournal => plan.DeploymentDigests.TransactionJournal.HotPublishSha256,
            HostManagerModuleKind.SamplingSubscription => plan.DeploymentDigests.SamplingSubscription.HotPublishSha256,
            HostManagerModuleKind.PortableSoftwareRegistry => plan.DeploymentDigests.PortableSoftwareRegistry.HotPublishSha256,
            HostManagerModuleKind.SoftwareIdentityCatalog => plan.DeploymentDigests.SoftwareIdentityCatalog.HotPublishSha256,
            HostManagerModuleKind.SoftwareIdentityResolution => plan.DeploymentDigests.SoftwareIdentityResolution.HotPublishSha256,
            HostManagerModuleKind.ReportCoordinator => plan.DeploymentDigests.ReportCoordinator.HotPublishSha256,
            HostManagerModuleKind.FileQuery => plan.DeploymentDigests.FileQuery.HotPublishSha256,
            HostManagerModuleKind.PublicServiceCoordinator => plan.DeploymentDigests.PublicServiceCoordinator.HotPublishSha256,
            HostManagerModuleKind.DisplayCoordinator => plan.DeploymentDigests.DisplayCoordinator.HotPublishSha256,
            HostManagerModuleKind.OperationCoordinator => plan.DeploymentDigests.OperationCoordinator.HotPublishSha256,
            HostManagerModuleKind.MetricSnapshot => plan.DeploymentDigests.MetricSnapshot.HotPublishSha256,
            HostManagerModuleKind.ProcessPolicyExecutor => plan.DeploymentDigests.ProcessPolicyExecutor.HotPublishSha256,
            HostManagerModuleKind.PdhCollector => plan.DeploymentDigests.PdhCollector.HotPublishSha256,
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null)
        };

    private static void ValidateAppliedOwnershipGeneration(
        CompiledHostManagerPlan current,
        CompiledHostManagerPlan next)
    {
        if (!current.IsPublished)
        {
            return;
        }

        var currentGeneration = current.HotPublish.AppliedOwnership.ConfigurationGeneration;
        var nextGeneration = next.HotPublish.AppliedOwnership.ConfigurationGeneration;
        if (nextGeneration < currentGeneration)
        {
            throw new InvalidOperationException(
                "Applied ownership configuration generation cannot move backwards.");
        }

        if (nextGeneration == currentGeneration
            && (current.DeploymentDigests.AppliedOwnership.RecreateSha256
                != next.DeploymentDigests.AppliedOwnership.RecreateSha256
                || current.DeploymentDigests.AppliedOwnership.HotPublishSha256
                != next.DeploymentDigests.AppliedOwnership.HotPublishSha256))
        {
            throw new InvalidOperationException(
                "Applied ownership configuration cannot drift within one generation.");
        }
    }

    private sealed class ModuleState(HostManagerModuleKind module)
    {
        private HostManagerDeploymentAttemptSnapshot? activeAttempt;

        public ulong DesiredPublicationSequence { get; private set; }
        public ulong DesiredPlanEpoch { get; private set; }
        public string DesiredBuildSha256 { get; private set; } = string.Empty;
        public string DesiredRecreateSha256 { get; private set; } = string.Empty;
        public string DesiredHotPublishSha256 { get; private set; } = string.Empty;
        public ulong AppliedPublicationSequence { get; private set; }
        public ulong AppliedPlanEpoch { get; private set; }
        public string AppliedBuildSha256 { get; private set; } = string.Empty;
        public string AppliedRecreateSha256 { get; private set; } = string.Empty;
        public string AppliedHotPublishSha256 { get; private set; } = string.Empty;
        public DateTimeOffset? AppliedAtUtc { get; private set; }
        public ulong PendingSincePlanEpoch { get; private set; }
        public ulong LastSettledAttemptId { get; private set; }
        public uint StaleCompletionCount { get; private set; }
        public HostManagerModuleFailureSnapshot? LastFailure { get; private set; }

        public void ObserveDesired(
            CompiledHostManagerPlan plan,
            ulong nextPublicationSequence,
            string recreateDigest,
            string hotDigest,
            DateTimeOffset observedAtUtc)
        {
            var digestChanged = DesiredBuildSha256 != plan.BuildSha256
                || DesiredRecreateSha256 != recreateDigest
                || DesiredHotPublishSha256 != hotDigest;
            DesiredPublicationSequence = nextPublicationSequence;
            DesiredPlanEpoch = plan.PlanEpoch;
            DesiredBuildSha256 = plan.BuildSha256;
            DesiredRecreateSha256 = recreateDigest;
            DesiredHotPublishSha256 = hotDigest;
            if (digestChanged && PendingSincePlanEpoch == 0)
            {
                PendingSincePlanEpoch = plan.PlanEpoch;
            }

            SupersedeFailureIfNeeded(observedAtUtc);
            RefreshPendingEpoch();
        }

        public HostManagerDeploymentAttemptToken BeginAttempt(
            ulong attemptId,
            CompiledHostManagerPlan plan,
            ulong currentPublicationSequence,
            HostManagerDeploymentOperation operation,
            string recreateDigest,
            string hotDigest,
            DateTimeOffset startedAtUtc)
        {
            if (activeAttempt is not null)
            {
                throw new InvalidOperationException(
                    $"Host Manager module {module} already has active deployment attempt {activeAttempt.AttemptId}.");
            }
            if (currentPublicationSequence == 0
                || DesiredPublicationSequence != currentPublicationSequence
                || DesiredPlanEpoch != plan.PlanEpoch
                || DesiredBuildSha256 != plan.BuildSha256
                || DesiredRecreateSha256 != recreateDigest
                || DesiredHotPublishSha256 != hotDigest)
            {
                throw new InvalidOperationException(
                    $"Host Manager module {module} cannot begin an attempt for a plan that is not the exact current desired publication.");
            }

            ValidateOperation(operation, plan.BuildSha256, recreateDigest);
            var lifecycle = operation switch
            {
                HostManagerDeploymentOperation.InitialCreate => HostManagerPendingLifecycle.InitialCreate,
                HostManagerDeploymentOperation.HotPublish => HostManagerPendingLifecycle.HotPublish,
                HostManagerDeploymentOperation.HostRecreate or
                    HostManagerDeploymentOperation.HostRecreateAndHotPublish => HostManagerPendingLifecycle.HostRecreate,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
            activeAttempt = new HostManagerDeploymentAttemptSnapshot(
                attemptId,
                currentPublicationSequence,
                module,
                operation,
                lifecycle,
                plan.PlanEpoch,
                plan.BuildSha256,
                recreateDigest,
                hotDigest,
                startedAtUtc);
            return new HostManagerDeploymentAttemptToken(attemptId, currentPublicationSequence, module);
        }

        public HostManagerDeploymentAttemptSettlement CompleteSucceeded(
            HostManagerDeploymentAttemptToken token,
            ulong currentPublicationSequence,
            DateTimeOffset completedAtUtc)
        {
            if (!TryGetAttempt(token, out var attempt))
            {
                RecordStaleCompletion();
                return HostManagerDeploymentAttemptSettlement.Stale;
            }
            if (attempt.PublicationSequence != currentPublicationSequence
                || !MatchesDesired(attempt))
            {
                RecordStaleCompletion();
                CommitAttempt(attempt);
                return HostManagerDeploymentAttemptSettlement.Stale;
            }

            ValidateSettledOperation(attempt.Operation);
            var nextFailure = CreateRecoveredFailure(attempt, completedAtUtc);

            switch (attempt.Operation)
            {
                case HostManagerDeploymentOperation.InitialCreate:
                case HostManagerDeploymentOperation.HostRecreateAndHotPublish:
                    AppliedPublicationSequence = attempt.PublicationSequence;
                    AppliedPlanEpoch = attempt.PlanEpoch;
                    AppliedBuildSha256 = attempt.BuildSha256;
                    AppliedRecreateSha256 = attempt.RecreateSha256;
                    AppliedHotPublishSha256 = attempt.HotPublishSha256;
                    AppliedAtUtc = completedAtUtc;
                    break;
                case HostManagerDeploymentOperation.HostRecreate:
                    AppliedPublicationSequence = attempt.PublicationSequence;
                    AppliedPlanEpoch = attempt.PlanEpoch;
                    AppliedBuildSha256 = attempt.BuildSha256;
                    AppliedRecreateSha256 = attempt.RecreateSha256;
                    AppliedHotPublishSha256 = string.Empty;
                    AppliedAtUtc = null;
                    break;
                case HostManagerDeploymentOperation.HotPublish:
                    AppliedPublicationSequence = attempt.PublicationSequence;
                    AppliedPlanEpoch = attempt.PlanEpoch;
                    AppliedHotPublishSha256 = attempt.HotPublishSha256;
                    AppliedAtUtc = completedAtUtc;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attempt.Operation), attempt.Operation, null);
            }

            LastSettledAttemptId = attempt.AttemptId;
            LastFailure = nextFailure;
            RefreshPendingEpoch();
            CommitAttempt(attempt);
            return HostManagerDeploymentAttemptSettlement.Applied;
        }

        public HostManagerDeploymentAttemptSettlement CompleteFailed(
            HostManagerDeploymentAttemptToken token,
            string failureCode,
            HostManagerNativeResultSnapshot? nativeResult,
            DateTimeOffset completedAtUtc)
        {
            if (!TryGetAttempt(token, out var attempt))
            {
                RecordStaleCompletion();
                return HostManagerDeploymentAttemptSettlement.Stale;
            }

            var isCurrentDesired = MatchesDesired(attempt);
            HostManagerModuleFailureSnapshot nextFailure;
            if (LastFailure is not null
                && LastFailure.Active
                && LastFailure.StableCode == failureCode
                && LastFailure.NativeResult == nativeResult
                && LastFailure.AttemptedOperation == attempt.Operation
                && MatchesIdentity(LastFailure, attempt))
            {
                nextFailure = LastFailure with
                {
                    AttemptId = attempt.AttemptId,
                    PublicationSequence = attempt.PublicationSequence,
                    LastOccurredAtUtc = completedAtUtc,
                    OccurrenceCount = LastFailure.OccurrenceCount == uint.MaxValue
                        ? uint.MaxValue
                        : LastFailure.OccurrenceCount + 1
                };
            }
            else
            {
                nextFailure = new HostManagerModuleFailureSnapshot(
                    isCurrentDesired,
                    attempt.AttemptId,
                    attempt.PublicationSequence,
                    failureCode,
                    nativeResult,
                    attempt.Operation,
                    attempt.Lifecycle,
                    attempt.PlanEpoch,
                    attempt.BuildSha256,
                    attempt.RecreateSha256,
                    attempt.HotPublishSha256,
                    completedAtUtc,
                    completedAtUtc,
                    1,
                    isCurrentDesired
                        ? HostManagerFailureResolution.None
                        : HostManagerFailureResolution.Superseded,
                    isCurrentDesired ? null : completedAtUtc);
            }

            LastSettledAttemptId = attempt.AttemptId;
            LastFailure = nextFailure;
            CommitAttempt(attempt);
            return HostManagerDeploymentAttemptSettlement.Failed;
        }

        public HostManagerModuleDeploymentSnapshot CreateSnapshot()
            => new(
                module,
                ResolveStatus(),
                ResolvePendingLifecycle(),
                ResolveHealth(),
                DesiredPublicationSequence,
                DesiredPlanEpoch,
                DesiredBuildSha256,
                DesiredRecreateSha256,
                DesiredHotPublishSha256,
                AppliedPublicationSequence,
                AppliedPlanEpoch,
                AppliedBuildSha256,
                AppliedRecreateSha256,
                AppliedHotPublishSha256,
                AppliedAtUtc,
                PendingSincePlanEpoch,
                activeAttempt,
                LastSettledAttemptId,
                StaleCompletionCount,
                LastFailure);

        private void ValidateOperation(
            HostManagerDeploymentOperation operation,
            string buildDigest,
            string recreateDigest)
        {
            switch (operation)
            {
                case HostManagerDeploymentOperation.InitialCreate when AppliedPlanEpoch == 0:
                    return;
                case HostManagerDeploymentOperation.HotPublish
                    when AppliedPlanEpoch != 0
                        && AppliedBuildSha256 == buildDigest
                        && AppliedRecreateSha256 == recreateDigest:
                    return;
                case HostManagerDeploymentOperation.HostRecreate:
                case HostManagerDeploymentOperation.HostRecreateAndHotPublish:
                    if (AppliedPlanEpoch != 0
                        && AppliedBuildSha256 == buildDigest)
                    {
                        return;
                    }
                    break;
            }

            throw new InvalidOperationException(
                $"Host Manager module {module} cannot begin {operation} from deployment lifecycle {ResolvePendingLifecycle()}.");
        }

        private bool TryGetAttempt(
            HostManagerDeploymentAttemptToken token,
            out HostManagerDeploymentAttemptSnapshot attempt)
        {
            if (activeAttempt is not null
                && activeAttempt.AttemptId == token.AttemptId
                && activeAttempt.PublicationSequence == token.PublicationSequence
                && activeAttempt.Module == token.Module)
            {
                attempt = activeAttempt;
                return true;
            }

            attempt = null!;
            return false;
        }

        private void CommitAttempt(HostManagerDeploymentAttemptSnapshot attempt)
        {
            if (!ReferenceEquals(activeAttempt, attempt))
            {
                throw new InvalidOperationException(
                    $"Host Manager module {module} attempt changed before exact settlement commit.");
            }

            activeAttempt = null;
        }

        private void RecordStaleCompletion()
        {
            if (StaleCompletionCount != uint.MaxValue)
            {
                StaleCompletionCount++;
            }
        }

        private void SupersedeFailureIfNeeded(DateTimeOffset resolvedAtUtc)
        {
            if (LastFailure is not { Active: true } failure
                || MatchesDesired(failure))
            {
                return;
            }

            LastFailure = failure with
            {
                Active = false,
                Resolution = HostManagerFailureResolution.Superseded,
                ResolvedAtUtc = resolvedAtUtc
            };
        }

        private HostManagerModuleFailureSnapshot? CreateRecoveredFailure(
            HostManagerDeploymentAttemptSnapshot attempt,
            DateTimeOffset resolvedAtUtc)
        {
            if (LastFailure is not { Active: true } failure
                || !MatchesIdentity(failure, attempt))
            {
                return LastFailure;
            }

            return failure with
            {
                Active = false,
                Resolution = HostManagerFailureResolution.Recovered,
                ResolvedAtUtc = resolvedAtUtc
            };
        }

        private static void ValidateSettledOperation(HostManagerDeploymentOperation operation)
        {
            _ = operation switch
            {
                HostManagerDeploymentOperation.InitialCreate or
                    HostManagerDeploymentOperation.HotPublish or
                    HostManagerDeploymentOperation.HostRecreate or
                    HostManagerDeploymentOperation.HostRecreateAndHotPublish => true,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }

        private bool MatchesDesired(HostManagerDeploymentAttemptSnapshot attempt)
            => attempt.PublicationSequence == DesiredPublicationSequence
                && attempt.PlanEpoch == DesiredPlanEpoch
                && attempt.BuildSha256 == DesiredBuildSha256
                && attempt.RecreateSha256 == DesiredRecreateSha256
                && attempt.HotPublishSha256 == DesiredHotPublishSha256;

        private bool MatchesDesired(HostManagerModuleFailureSnapshot failure)
            => failure.PublicationSequence == DesiredPublicationSequence
                && failure.AttemptedPlanEpoch == DesiredPlanEpoch
                && failure.AttemptedBuildSha256 == DesiredBuildSha256
                && failure.AttemptedRecreateSha256 == DesiredRecreateSha256
                && failure.AttemptedHotPublishSha256 == DesiredHotPublishSha256;

        private static bool MatchesIdentity(
            HostManagerModuleFailureSnapshot failure,
            HostManagerDeploymentAttemptSnapshot attempt)
            => failure.PublicationSequence == attempt.PublicationSequence
                && failure.AttemptedPlanEpoch == attempt.PlanEpoch
                && failure.AttemptedBuildSha256 == attempt.BuildSha256
                && failure.AttemptedRecreateSha256 == attempt.RecreateSha256
                && failure.AttemptedHotPublishSha256 == attempt.HotPublishSha256;

        private HostManagerDeploymentStatus ResolveStatus()
        {
            if (DesiredPlanEpoch == 0)
            {
                return HostManagerDeploymentStatus.Unpublished;
            }
            if (AppliedPlanEpoch == 0)
            {
                return HostManagerDeploymentStatus.Initializing;
            }
            if (ResolvePendingLifecycle() != HostManagerPendingLifecycle.None)
            {
                return HostManagerDeploymentStatus.Pending;
            }
            return HostManagerDeploymentStatus.InSync;
        }

        private HostManagerPendingLifecycle ResolvePendingLifecycle()
        {
            if (DesiredPlanEpoch == 0)
            {
                return HostManagerPendingLifecycle.None;
            }
            if (AppliedPlanEpoch == 0)
            {
                return HostManagerPendingLifecycle.InitialCreate;
            }
            if (DesiredBuildSha256 != AppliedBuildSha256)
            {
                return HostManagerPendingLifecycle.ProcessRestart;
            }
            if (DesiredRecreateSha256 != AppliedRecreateSha256)
            {
                return HostManagerPendingLifecycle.HostRecreate;
            }
            if (DesiredHotPublishSha256 != AppliedHotPublishSha256)
            {
                return HostManagerPendingLifecycle.HotPublish;
            }
            return HostManagerPendingLifecycle.None;
        }

        private HostManagerDeploymentHealth ResolveHealth()
            => LastFailure is { Active: true }
                ? HostManagerDeploymentHealth.Failed
                : AppliedPlanEpoch == 0
                    ? HostManagerDeploymentHealth.Unknown
                    : HostManagerDeploymentHealth.Healthy;

        private void RefreshPendingEpoch()
        {
            if (ResolveStatus() == HostManagerDeploymentStatus.InSync)
            {
                PendingSincePlanEpoch = 0;
            }
            else if (DesiredPlanEpoch > 0 && PendingSincePlanEpoch == 0)
            {
                PendingSincePlanEpoch = DesiredPlanEpoch;
            }
        }
    }
}

internal sealed record HostManagerRuntimePlanPublication(
    CompiledRuntimePlan Plan,
    ulong PublicationSequence);
