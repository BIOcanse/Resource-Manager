using ResourceManager.Adapter;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.NativeScheduling;

public sealed unsafe class NativeResourceSchedulerSession : IDisposable
{
    private const byte TargetIncludeVramFlag = 1 << 0;
    private const byte TargetSoftwareModeFlag = 1 << 1;
    private const byte RevalidationSnapshotAvailableFlag = 1 << 0;

    private readonly object _sync = new();
    private readonly NativeResourceSchedulerTarget[] _targets;
    private readonly NativeResourceSchedulerPrivateResource[] _resources;
    private readonly NativeResourceSchedulerPendingAction[] _pendingInput;
    private readonly NativeResourceSchedulerPendingAction[] _journalPending;
    private readonly NativeResourceSchedulerCandidate[] _candidates;
    private readonly NativeResourceSchedulerSelection[] _selections;
    private readonly NativeResourceSchedulerTargetPlan[] _targetPlans;
    private readonly NativeResourceSchedulerResourceScratch[] _resourceScratch;
    private readonly NativeResourceSchedulerTargetScratch[] _targetScratch;
    private readonly uint[] _resourceOrder;
    private readonly NativeResourceSchedulerPendingAction[] _pendingScratch;
    private readonly void* _stateBuffer;
    private readonly ulong _stateSize;
    private NativeResourceSchedulerConfig _configuration;
    private bool _disposed;

    public NativeResourceSchedulerSession(
        int targetCapacity,
        int resourceCapacity,
        int pendingCapacity,
        int journalPendingCapacity,
        ResourceSchedulerConfig configuration,
        ulong stateGeneration)
    {
        if (targetCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCapacity));
        }

        if (resourceCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        }

        if (pendingCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingCapacity));
        }

        if (journalPendingCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(journalPendingCapacity));
        }

        if (stateGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateGeneration));
        }

        EnsureNativeContract();
        ThrowIfFailed(
            NativeResourceSchedulerInterop.rm_resource_scheduler_required_candidate_capacity(
                checked((uint)resourceCapacity),
                out var candidateCapacity),
            "required-candidate-capacity");

        _targets = new NativeResourceSchedulerTarget[targetCapacity];
        _resources = new NativeResourceSchedulerPrivateResource[resourceCapacity];
        _pendingInput = new NativeResourceSchedulerPendingAction[pendingCapacity];
        _journalPending = new NativeResourceSchedulerPendingAction[journalPendingCapacity];
        _candidates = new NativeResourceSchedulerCandidate[checked((int)candidateCapacity)];
        _selections = new NativeResourceSchedulerSelection[resourceCapacity];
        _targetPlans = new NativeResourceSchedulerTargetPlan[targetCapacity];
        _resourceScratch = new NativeResourceSchedulerResourceScratch[resourceCapacity];
        _targetScratch = new NativeResourceSchedulerTargetScratch[targetCapacity];
        _resourceOrder = new uint[resourceCapacity];
        _pendingScratch = new NativeResourceSchedulerPendingAction[
            checked(pendingCapacity + journalPendingCapacity)];
        _configuration = CompileConfiguration(configuration);

        ThrowIfFailed(
            NativeResourceSchedulerInterop.rm_resource_scheduler_state_required_size(
                checked((uint)pendingCapacity),
                out var stateSize),
            "state-required-size");
        _stateSize = stateSize;
        ResidentBytes = checked(
            stateSize
            + Bytes<NativeResourceSchedulerTarget>(_targets)
            + Bytes<NativeResourceSchedulerPrivateResource>(_resources)
            + Bytes<NativeResourceSchedulerPendingAction>(_pendingInput)
            + Bytes<NativeResourceSchedulerPendingAction>(_journalPending)
            + Bytes<NativeResourceSchedulerCandidate>(_candidates)
            + Bytes<NativeResourceSchedulerSelection>(_selections)
            + Bytes<NativeResourceSchedulerTargetPlan>(_targetPlans)
            + Bytes<NativeResourceSchedulerResourceScratch>(_resourceScratch)
            + Bytes<NativeResourceSchedulerTargetScratch>(_targetScratch)
            + Bytes<uint>(_resourceOrder)
            + Bytes<NativeResourceSchedulerPendingAction>(_pendingScratch));
        var alignment = NativeResourceSchedulerInterop.rm_resource_scheduler_state_alignment();
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new InvalidOperationException($"Native resource scheduler returned invalid state alignment {alignment}.");
        }

        _stateBuffer = NativeMemory.AlignedAlloc(checked((nuint)_stateSize), alignment);
        if (_stateBuffer is null)
        {
            throw new OutOfMemoryException($"Unable to allocate {_stateSize} bytes for native resource scheduler state.");
        }

        try
        {
            ThrowIfFailed(
                NativeResourceSchedulerInterop.rm_resource_scheduler_state_initialize(
                    _stateBuffer,
                    _stateSize,
                    checked((uint)pendingCapacity),
                    stateGeneration),
                "state-initialize");
        }
        catch
        {
            NativeMemory.AlignedFree(_stateBuffer);
            throw;
        }
    }

    public int TargetCapacity => _targets.Length;

    public int ResourceCapacity => _resources.Length;

    public int PendingCapacity => _pendingInput.Length;

    public int JournalPendingCapacity => _journalPending.Length;

    public ulong ResidentBytes { get; }

    public static ulong PublishedLayoutFingerprint => ManagedLayoutFingerprint();

    public static void ValidateConfiguration(ResourceSchedulerConfig configuration)
    {
        EnsureNativeContract();
        var compiled = CompileConfiguration(configuration);
        ThrowIfFailed(
            NativeResourceSchedulerInterop.rm_resource_scheduler_validate_config(&compiled),
            "validate-config");
    }

    public void ApplyConfiguration(ResourceSchedulerConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var compiled = CompileConfiguration(configuration);
        lock (_sync)
        {
            ThrowIfDisposed();
            _configuration = compiled;
        }
    }

    public ResourceSchedulerReservationToken ReserveSelection(
        ResourceSchedulerSelection selection,
        ulong nowMonotonicTimestamp,
        ulong deadlineTimestamp,
        ulong pendingGeneration,
        uint maximumInFlight,
        uint maximumInFlightPerTarget)
    {
        var nativeSelection = ToNative(selection);
        var token = default(NativeResourceSchedulerReservationToken);
        lock (_sync)
        {
            ThrowIfDisposed();
            var request = new NativeResourceSchedulerReservationRequest
            {
                AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
                StructSize = checked((uint)sizeof(NativeResourceSchedulerReservationRequest)),
                NowMonotonicTimestamp = nowMonotonicTimestamp,
                DeadlineTimestamp = deadlineTimestamp,
                PendingGeneration = pendingGeneration,
                ConfigurationGeneration = _configuration.Generation,
                DangerMinimumPhysicalAfterVramMoveRatio = _configuration.DangerMinimumPhysicalAfterVramMoveRatio,
                DangerMinimumVirtualAfterPhysicalMoveRatio = _configuration.DangerMinimumVirtualAfterPhysicalMoveRatio,
                MaximumInFlight = maximumInFlight,
                MaximumInFlightPerTarget = maximumInFlightPerTarget
            };
            ThrowIfFailed(
                NativeResourceSchedulerInterop.rm_resource_scheduler_reserve_selection(
                    _stateBuffer,
                    _stateSize,
                    &request,
                    &nativeSelection,
                    &token),
                "reserve-selection");
        }

        return Project(token);
    }

    public ResourceSchedulerRevalidationResult RevalidateAndBeginReservation(
        ResourceSchedulerReservationToken token,
        ResourceSchedulerSelection selection,
        ResourceSchedulerRevalidationResource resource)
    {
        var nativeToken = ToNative(token);
        var nativeSelection = ToNative(selection);
        var nativeInput = ToNative(resource);
        var nativeOutput = default(NativeResourceSchedulerRevalidationOutput);
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (NativeResourceSchedulerConfig* configurationPointer = &_configuration)
            {
                ThrowIfFailed(
                    NativeResourceSchedulerInterop.rm_resource_scheduler_revalidate_and_begin_reservation(
                        _stateBuffer,
                        _stateSize,
                        configurationPointer,
                        &nativeToken,
                        &nativeSelection,
                        &nativeInput,
                        &nativeOutput),
                    "revalidate-and-begin-reservation");
            }
        }

        return new ResourceSchedulerRevalidationResult(
            nativeOutput.Allowed != 0,
            (ResourceSchedulerRevalidationReason)nativeOutput.Reason);
    }

    public void CompleteReservation(ResourceSchedulerReservationToken token)
        => ChangeReservationState(token, complete: true);

    public void CancelReservation(ResourceSchedulerReservationToken token)
        => ChangeReservationState(token, complete: false);

    public void BindReservationToJournal(
        ResourceSchedulerReservationToken token,
        ulong transactionIdLow,
        ulong transactionIdHigh)
    {
        if ((transactionIdLow | transactionIdHigh) == 0)
        {
            throw new ArgumentException(
                "A resource-scheduler journal transaction identity must be nonzero.");
        }
        var nativeToken = ToNative(token);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeResourceSchedulerInterop.rm_resource_scheduler_bind_reservation_journal(
                    _stateBuffer,
                    _stateSize,
                    &nativeToken,
                    transactionIdLow,
                    transactionIdHigh),
                "bind-reservation-journal");
        }
    }

    public void AbandonReservation(ResourceSchedulerReservationToken token)
    {
        var nativeToken = ToNative(token);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeResourceSchedulerInterop.rm_resource_scheduler_abandon_reservation(
                    _stateBuffer,
                    _stateSize,
                    &nativeToken),
                "abandon-reservation");
        }
    }

    public ResourceSchedulerFeedbackResult ApplyFeedback(
        ResourceSchedulerReservationToken token,
        ResourceSchedulerFeedbackRequest request,
        ResourceSchedulerSelection selection,
        ResourceSchedulerCapacity capacity,
        ResourceSchedulerActionFeedback action)
    {
        var nativeRequest = new NativeResourceSchedulerFeedbackRequest
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerFeedbackRequest)),
            ExpectedRequestId = request.ExpectedRequestId,
            PolicyResultCode = request.PolicyResultCode,
            PolicyDangerFlags = request.PolicyDangerFlags,
            PolicyAccepted = request.PolicyAccepted ? (byte)1 : (byte)0,
            ActionResultCount = request.ActionResultCount
        };
        var nativeToken = ToNative(token);
        var nativeSelection = ToNative(selection);
        var nativeCapacity = ToNative(capacity);
        var nativeAction = ToNative(action);
        var output = default(NativeResourceSchedulerFeedbackOutput);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeResourceSchedulerInterop.rm_resource_scheduler_apply_feedback(
                    _stateBuffer,
                    _stateSize,
                    &nativeToken,
                    &nativeRequest,
                    &nativeSelection,
                    &nativeCapacity,
                    &nativeAction,
                    &output),
                "apply-feedback-and-complete");
        }

        return Project(output);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    ~NativeResourceSchedulerSession()
    {
        Dispose(disposing: false);
    }

    private void Dispose(bool disposing)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            NativeMemory.AlignedFree(_stateBuffer);
            _disposed = true;
        }
    }

    private void ChangeReservationState(
        ResourceSchedulerReservationToken token,
        bool complete)
    {
        var nativeToken = ToNative(token);
        lock (_sync)
        {
            ThrowIfDisposed();
            var result = complete
                ? NativeResourceSchedulerInterop.rm_resource_scheduler_complete_reservation(
                    _stateBuffer,
                    _stateSize,
                    &nativeToken)
                : NativeResourceSchedulerInterop.rm_resource_scheduler_cancel_reservation(
                    _stateBuffer,
                    _stateSize,
                    &nativeToken);
            ThrowIfFailed(result, complete ? "complete-reservation" : "cancel-reservation");
        }
    }

    public ResourceSchedulerPlanResult Plan(
        ResourceSchedulerPlanRequest request,
        IReadOnlyList<ResourceSchedulerTarget> targets,
        IReadOnlyList<ResourceSchedulerPrivateResource> resources,
        ResourceSchedulerFactBatch factBatch)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(factBatch);
        ArgumentNullException.ThrowIfNull(factBatch.JournalPending);
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureCapacity(targets.Count, resources.Count);
            if (factBatch.JournalPending.Count > _journalPending.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(factBatch),
                    "Journal pending input exceeds the configured capacity.");
            }
            CopyInputs(targets, resources, factBatch.JournalPending);
            var nativeRequest = ToNative(request);
            var nativeFactBatch = new NativeResourceSchedulerFactBatchInput
            {
                AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
                StructSize = checked((uint)sizeof(NativeResourceSchedulerFactBatchInput)),
                ProjectionEpoch = factBatch.ProjectionEpoch,
                ConfigurationGeneration = _configuration.Generation,
                TargetCount = checked((uint)targets.Count),
                PrivateResourceCount = checked((uint)resources.Count),
                JournalPendingCount = checked((uint)factBatch.JournalPending.Count),
                ActionBudget = factBatch.ActionBudget
            };
            var global = default(NativeResourceSchedulerGlobalSelection);
            var summary = default(NativeResourceSchedulerPlanSummary);

            fixed (NativeResourceSchedulerConfig* configurationPointer = &_configuration)
            fixed (NativeResourceSchedulerTarget* targetPointer = _targets)
            fixed (NativeResourceSchedulerPrivateResource* resourcePointer = _resources)
            fixed (NativeResourceSchedulerPendingAction* pendingInputPointer = _pendingInput)
            fixed (NativeResourceSchedulerPendingAction* journalPendingPointer = _journalPending)
            fixed (NativeResourceSchedulerCandidate* candidatePointer = _candidates)
            fixed (NativeResourceSchedulerSelection* selectionPointer = _selections)
            fixed (NativeResourceSchedulerTargetPlan* targetPlanPointer = _targetPlans)
            fixed (NativeResourceSchedulerResourceScratch* resourceScratchPointer = _resourceScratch)
            fixed (NativeResourceSchedulerTargetScratch* targetScratchPointer = _targetScratch)
            fixed (uint* resourceOrderPointer = _resourceOrder)
            fixed (NativeResourceSchedulerPendingAction* pendingScratchPointer = _pendingScratch)
            {
                ThrowIfFailed(
                    NativeResourceSchedulerInterop.rm_resource_scheduler_plan(
                        configurationPointer,
                        &nativeRequest,
                        targetPointer,
                        checked((uint)targets.Count),
                        resourcePointer,
                        checked((uint)resources.Count),
                        _stateBuffer,
                        _stateSize,
                        pendingInputPointer,
                        checked((uint)_pendingInput.Length),
                        journalPendingPointer,
                        checked((uint)factBatch.JournalPending.Count),
                        &nativeFactBatch,
                        candidatePointer,
                        checked((uint)_candidates.Length),
                        selectionPointer,
                        checked((uint)_selections.Length),
                        targetPlanPointer,
                        checked((uint)_targetPlans.Length),
                        resourceScratchPointer,
                        checked((uint)_resourceScratch.Length),
                        targetScratchPointer,
                        checked((uint)_targetScratch.Length),
                        resourceOrderPointer,
                        checked((uint)_resourceOrder.Length),
                        pendingScratchPointer,
                        checked((uint)_pendingScratch.Length),
                        &global,
                        &summary),
                    "plan");
            }

            return ProjectResult(
                request.EmitCandidates,
                nativeRequest.ConfigurationGeneration,
                summary,
                global);
        }
    }

    private void CopyInputs(
        IReadOnlyList<ResourceSchedulerTarget> targets,
        IReadOnlyList<ResourceSchedulerPrivateResource> resources,
        IReadOnlyList<ResourceSchedulerPendingAction> journalPending)
    {
        var index = 0;
        while (index < targets.Count)
        {
            _targets[index] = ToNative(targets[index]);
            index++;
        }

        index = 0;
        while (index < resources.Count)
        {
            _resources[index] = ToNative(resources[index]);
            index++;
        }

        index = 0;
        while (index < journalPending.Count)
        {
            _journalPending[index] = ToNative(journalPending[index]);
            index++;
        }

    }

    private ResourceSchedulerPlanResult ProjectResult(
        bool includeCandidates,
        ulong configurationGeneration,
        NativeResourceSchedulerPlanSummary summary,
        NativeResourceSchedulerGlobalSelection global)
    {
        var candidateCount = checked((int)summary.CandidateCount);
        var selectionCount = checked((int)summary.SelectionCount);
        var targetCount = checked((int)summary.TargetCount);
        if (candidateCount > _candidates.Length
            || selectionCount > _selections.Length
            || targetCount > _targetPlans.Length)
        {
            throw new InvalidDataException("The native scheduler returned counts outside the configured buffers.");
        }

        var candidates = includeCandidates
            ? new ResourceSchedulerCandidate[candidateCount]
            : [];
        var index = 0;
        while (index < candidates.Length)
        {
            candidates[index] = Project(_candidates[index]);
            index++;
        }

        var selections = new ResourceSchedulerSelection[selectionCount];
        index = 0;
        while (index < selections.Length)
        {
            selections[index] = Project(_selections[index], configurationGeneration);
            index++;
        }

        var targetPlans = new ResourceSchedulerTargetPlan[targetCount];
        index = 0;
        while (index < targetPlans.Length)
        {
            targetPlans[index] = Project(_targetPlans[index]);
            index++;
        }

        return new ResourceSchedulerPlanResult(
            Project(summary),
            Project(global),
            candidates,
            selections,
            targetPlans);
    }

    private void EnsureCapacity(int targetCount, int resourceCount)
    {
        if (targetCount > _targets.Length)
        {
            throw new InvalidOperationException(
                $"Resource scheduler target capacity {_targets.Length} is smaller than request {targetCount}; recreate the Host with a larger explicit capacity.");
        }

        if (resourceCount > _resources.Length)
        {
            throw new InvalidOperationException(
                $"Resource scheduler resource capacity {_resources.Length} is smaller than request {resourceCount}; recreate the Host with a larger explicit capacity.");
        }

    }

    private static void EnsureNativeContract()
    {
        var abiVersion = NativeResourceSchedulerInterop.rm_resource_scheduler_abi_version();
        if (abiVersion != NativeResourceSchedulerInterop.AbiVersion)
        {
            throw new InvalidOperationException(
                $"Resource scheduler ABI mismatch: native={abiVersion}, managed={NativeResourceSchedulerInterop.AbiVersion}.");
        }

        var nativeSize = NativeResourceSchedulerInterop.rm_resource_scheduler_config_size();
        var managedSize = NativeResourceSchedulerInterop.SizeOfConfig();
        if (nativeSize != managedSize)
        {
            throw new InvalidOperationException(
                $"Resource scheduler config size mismatch: native={nativeSize}, managed={managedSize}.");
        }

        var nativeRequestSize = NativeResourceSchedulerInterop.rm_resource_scheduler_plan_request_size();
        var managedRequestSize = NativeResourceSchedulerInterop.SizeOfPlanRequest();
        if (nativeRequestSize != managedRequestSize)
        {
            throw new InvalidOperationException(
                $"Resource scheduler request size mismatch: native={nativeRequestSize}, managed={managedRequestSize}.");
        }

        EnsureSize("capacity", NativeResourceSchedulerInterop.rm_resource_scheduler_capacity_size(), NativeResourceSchedulerInterop.SizeOfCapacity());
        EnsureSize("target", NativeResourceSchedulerInterop.rm_resource_scheduler_target_size(), NativeResourceSchedulerInterop.SizeOfTarget());
        EnsureSize("private-resource", NativeResourceSchedulerInterop.rm_resource_scheduler_private_resource_size(), NativeResourceSchedulerInterop.SizeOfPrivateResource());
        EnsureSize("pending-action", NativeResourceSchedulerInterop.rm_resource_scheduler_pending_action_size(), NativeResourceSchedulerInterop.SizeOfPendingAction());
        EnsureSize("candidate", NativeResourceSchedulerInterop.rm_resource_scheduler_candidate_size(), NativeResourceSchedulerInterop.SizeOfCandidate());
        EnsureSize("selection", NativeResourceSchedulerInterop.rm_resource_scheduler_selection_size(), NativeResourceSchedulerInterop.SizeOfSelection());
        EnsureSize("resource-scratch", NativeResourceSchedulerInterop.rm_resource_scheduler_resource_scratch_size(), NativeResourceSchedulerInterop.SizeOfResourceScratch());
        EnsureSize("execution-authority", NativeResourceSchedulerInterop.rm_resource_scheduler_execution_authority_size(), NativeResourceSchedulerInterop.SizeOfExecutionAuthority());

        var nativeFactBatchSize = NativeResourceSchedulerInterop.rm_resource_scheduler_fact_batch_input_size();
        var managedFactBatchSize = NativeResourceSchedulerInterop.SizeOfFactBatchInput();
        if (nativeFactBatchSize != managedFactBatchSize)
        {
            throw new InvalidOperationException(
                $"Resource scheduler fact-batch size mismatch: native={nativeFactBatchSize}, managed={managedFactBatchSize}.");
        }

        EnsureSize(
            "reservation-request",
            NativeResourceSchedulerInterop.rm_resource_scheduler_reservation_request_size(),
            NativeResourceSchedulerInterop.SizeOfReservationRequest());
        EnsureSize(
            "reservation-token",
            NativeResourceSchedulerInterop.rm_resource_scheduler_reservation_token_size(),
            NativeResourceSchedulerInterop.SizeOfReservationToken());
        EnsureSize(
            "revalidation-input",
            NativeResourceSchedulerInterop.rm_resource_scheduler_revalidation_input_size(),
            NativeResourceSchedulerInterop.SizeOfRevalidationInput());
        EnsureSize(
            "revalidation-output",
            NativeResourceSchedulerInterop.rm_resource_scheduler_revalidation_output_size(),
            NativeResourceSchedulerInterop.SizeOfRevalidationOutput());
        EnsureSize(
            "feedback-request",
            NativeResourceSchedulerInterop.rm_resource_scheduler_feedback_request_size(),
            NativeResourceSchedulerInterop.SizeOfFeedbackRequest());
        EnsureSize(
            "action-feedback",
            NativeResourceSchedulerInterop.rm_resource_scheduler_action_feedback_size(),
            NativeResourceSchedulerInterop.SizeOfActionFeedback());
        EnsureSize(
            "feedback-output",
            NativeResourceSchedulerInterop.rm_resource_scheduler_feedback_output_size(),
            NativeResourceSchedulerInterop.SizeOfFeedbackOutput());

        var nativeFingerprint = NativeResourceSchedulerInterop.rm_resource_scheduler_layout_fingerprint();
        var managedFingerprint = ManagedLayoutFingerprint();
        if (nativeFingerprint != managedFingerprint)
        {
            throw new InvalidOperationException(
                $"Resource scheduler layout fingerprint mismatch: native={nativeFingerprint:X16}, managed={managedFingerprint:X16}.");
        }
    }

    private static void EnsureSize(string name, uint nativeSize, uint managedSize)
    {
        if (nativeSize != managedSize)
        {
            throw new InvalidOperationException(
                $"Resource scheduler {name} size mismatch: native={nativeSize}, managed={managedSize}.");
        }
    }

    internal static ulong ManagedLayoutFingerprint()
    {
        ReadOnlySpan<ulong> values =
        [
            NativeResourceSchedulerInterop.SizeOfExecutionAuthority(),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.Source)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.Action)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ActionRoute)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.Flags)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ResourceSlot)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ResourceId)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.Reserved0)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.LedgerInstanceId)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.SourceSnapshotGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ResourceGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ResourceKey)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.OwnerApplicationKey)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.OwnerInstanceIdLow)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.OwnerInstanceIdHigh)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.OwnerContextGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.LeaseGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.BindingGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.CapabilityGeneration)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.SchedulingRevision)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ExecutorIdLow)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ExecutorIdHigh)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ActionAttemptIdLow)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ActionAttemptIdHigh)),
            OffsetOf<NativeResourceSchedulerExecutionAuthority>(nameof(NativeResourceSchedulerExecutionAuthority.ProjectionEpoch)),
            NativeResourceSchedulerInterop.SizeOfPrivateResource(),
            OffsetOf<NativeResourceSchedulerPrivateResource>(nameof(NativeResourceSchedulerPrivateResource.DiscardAuthority)),
            OffsetOf<NativeResourceSchedulerPrivateResource>(nameof(NativeResourceSchedulerPrivateResource.TrimAuthority)),
            OffsetOf<NativeResourceSchedulerPrivateResource>(nameof(NativeResourceSchedulerPrivateResource.MoveDownAuthority)),
            NativeResourceSchedulerInterop.SizeOfPendingAction(),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.AbiVersion)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.StructSize)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.Authority)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.JournalTransactionIdLow)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.JournalTransactionIdHigh)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.TargetKey)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.SizeBytes)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.DeadlineTimestamp)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.PendingGeneration)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.State)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.Tier)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.Flags)),
            OffsetOf<NativeResourceSchedulerPendingAction>(nameof(NativeResourceSchedulerPendingAction.Reserved0)),
            NativeResourceSchedulerInterop.SizeOfFactBatchInput(),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.AbiVersion)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.StructSize)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.ProjectionEpoch)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.ConfigurationGeneration)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.TargetCount)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.PrivateResourceCount)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.JournalPendingCount)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.ActionBudget)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.Flags)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.Reserved0)),
            OffsetOf<NativeResourceSchedulerFactBatchInput>(nameof(NativeResourceSchedulerFactBatchInput.Reserved1)),
            NativeResourceSchedulerInterop.SizeOfCandidate(),
            OffsetOf<NativeResourceSchedulerCandidate>(nameof(NativeResourceSchedulerCandidate.Authority)),
            OffsetOf<NativeResourceSchedulerCandidate>(nameof(NativeResourceSchedulerCandidate.SizeBytes)),
            NativeResourceSchedulerInterop.SizeOfSelection(),
            OffsetOf<NativeResourceSchedulerSelection>(nameof(NativeResourceSchedulerSelection.RequestId)),
            OffsetOf<NativeResourceSchedulerSelection>(nameof(NativeResourceSchedulerSelection.Authority)),
            OffsetOf<NativeResourceSchedulerSelection>(nameof(NativeResourceSchedulerSelection.ConfigurationGeneration)),
            NativeResourceSchedulerInterop.SizeOfRevalidationInput(),
            OffsetOf<NativeResourceSchedulerRevalidationInput>(nameof(NativeResourceSchedulerRevalidationInput.Authority)),
            OffsetOf<NativeResourceSchedulerRevalidationInput>(nameof(NativeResourceSchedulerRevalidationInput.TargetKey)),
            OffsetOf<NativeResourceSchedulerRevalidationInput>(nameof(NativeResourceSchedulerRevalidationInput.Reserved0)),
            NativeResourceSchedulerInterop.SizeOfActionFeedback(),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.Authority)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.RequestId)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.ReleasedBytes)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.ResidentBytes)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.ActionGateEpoch)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.DetailCode)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.Status)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.PreviousTier)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.CurrentTier)),
            OffsetOf<NativeResourceSchedulerActionFeedback>(nameof(NativeResourceSchedulerActionFeedback.Reserved0)),
            NativeResourceSchedulerInterop.SizeOfFeedbackOutput(),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.ProjectedCapacity)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.Applied)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.ResultCode)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.DangerFlags)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.CurrentTier)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.ErrorCode)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.ReservationDisposition)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.Reserved0)),
            OffsetOf<NativeResourceSchedulerFeedbackOutput>(nameof(NativeResourceSchedulerFeedbackOutput.Reserved1)),
            NativeResourceSchedulerInterop.SizeOfResourceScratch()
        ];

        var hash = 14695981039346656037UL;
        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211UL);
        }

        return hash;
    }

    private static ulong OffsetOf<T>(string fieldName)
        where T : struct
        => checked((ulong)Marshal.OffsetOf<T>(fieldName).ToInt64());

    private static NativeResourceSchedulerPlanRequest ToNative(ResourceSchedulerPlanRequest source)
    {
        return new NativeResourceSchedulerPlanRequest
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerPlanRequest)),
            RequestId = source.RequestId,
            ConfigurationGeneration = source.ConfigurationGeneration,
            NowMonotonicTimestamp = source.NowMonotonicTimestamp,
            RequestedActionMask = (byte)source.RequestedActions,
            EnabledTierMask = source.EnabledTierMask,
            Flags = source.EmitCandidates ? (byte)1 : (byte)0
        };
    }

    private static NativeResourceSchedulerTarget ToNative(ResourceSchedulerTarget source)
    {
        var flags = (byte)0;
        if (source.IncludeVram)
        {
            flags |= TargetIncludeVramFlag;
        }

        if (source.SoftwareMode)
        {
            flags |= TargetSoftwareModeFlag;
        }

        return new NativeResourceSchedulerTarget
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerTarget)),
            TargetKey = source.TargetKey,
            OwnerApplicationKey = source.OwnerApplicationKey,
            OwnerInstanceIdLow = source.OwnerInstanceIdLow,
            OwnerInstanceIdHigh = source.OwnerInstanceIdHigh,
            OwnerContextGeneration = source.OwnerContextGeneration,
            LeaseGeneration = source.LeaseGeneration,
            CapabilityGeneration = source.CapabilityGeneration,
            BaseScore = source.BaseScore,
            Capacity = ToNative(source.Capacity),
            PrivateResourceStart = source.PrivateResourceStart,
            PrivateResourceCount = source.PrivateResourceCount,
            PolicyGrade = source.PolicyGrade,
            CpuGrade = (byte)source.CpuGrade,
            GpuGrade = (byte)source.GpuGrade,
            SurfaceState = (byte)source.SurfaceState,
            TargetFlags = flags
        };
    }

    private static NativeResourceSchedulerCapacity ToNative(ResourceSchedulerCapacity source)
    {
        return new NativeResourceSchedulerCapacity
        {
            TotalVramBytes = source.TotalVramBytes,
            FreeVramBytes = source.FreeVramBytes,
            TotalPhysicalBytes = source.TotalPhysicalBytes,
            FreePhysicalBytes = source.FreePhysicalBytes,
            TotalVirtualBytes = source.TotalVirtualBytes,
            FreeVirtualBytes = source.FreeVirtualBytes,
            FallbackVramFreeRatio = source.FallbackVramFreeRatio,
            FallbackPhysicalFreeRatio = source.FallbackPhysicalFreeRatio,
            FallbackVirtualFreeRatio = source.FallbackVirtualFreeRatio,
            FreeBytesValidMask = source.FreeBytesValidMask
        };
    }

    private static NativeResourceSchedulerSelection ToNative(ResourceSchedulerSelection source)
    {
        return new NativeResourceSchedulerSelection
        {
            TargetKey = source.TargetKey,
            RequestId = source.RequestId,
            Authority = ToNative(source.Authority),
            SizeBytes = source.SizeBytes,
            EstimatedReleaseBytes = source.EstimatedReleaseBytes,
            ConfigurationGeneration = source.ConfigurationGeneration,
            FinalImportance = source.FinalImportance,
            CandidateIndex = source.CandidateIndex,
            TargetInputIndex = source.TargetInputIndex,
            Tier = (byte)source.Tier,
            ActivityScore = source.ActivityScore
        };
    }

    private static NativeResourceSchedulerReservationToken ToNative(ResourceSchedulerReservationToken source)
    {
        return new NativeResourceSchedulerReservationToken
        {
            StateGeneration = source.StateGeneration,
            SlotGeneration = source.SlotGeneration,
            PendingGeneration = source.PendingGeneration,
            SlotIndex = source.SlotIndex
        };
    }

    private static NativeResourceSchedulerRevalidationInput ToNative(ResourceSchedulerRevalidationResource source)
    {
        return new NativeResourceSchedulerRevalidationInput
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerRevalidationInput)),
            Authority = ToNative(source.Authority),
            TargetKey = source.TargetKey,
            SizeBytes = source.SizeBytes,
            PolicyGrade = source.PolicyGrade,
            Tier = (byte)source.Tier,
            Flags = source.SnapshotAvailable ? RevalidationSnapshotAvailableFlag : (byte)0,
            ResourceKind = (byte)source.ResourceKind,
            RecoveryKind = (byte)source.RecoveryKind,
            Granularity = (byte)source.Granularity,
            InapplicableActions = (byte)source.InapplicableActions,
            DemandMask = (byte)source.DemandMask,
            ActivityScore = source.ActivityScore
        };
    }

    private static NativeResourceSchedulerActionFeedback ToNative(ResourceSchedulerActionFeedback source)
    {
        return new NativeResourceSchedulerActionFeedback
        {
            Authority = ToNative(source.Authority),
            RequestId = source.RequestId,
            ReleasedBytes = source.ReleasedBytes,
            ResidentBytes = source.ResidentBytes,
            ActionGateEpoch = source.ActionGateEpoch,
            DetailCode = source.DetailCode,
            Status = (byte)source.Status,
            PreviousTier = (byte)source.PreviousTier,
            CurrentTier = (byte)source.CurrentTier
        };
    }

    private static NativeResourceSchedulerPrivateResource ToNative(ResourceSchedulerPrivateResource source)
    {
        return new NativeResourceSchedulerPrivateResource
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerPrivateResource)),
            TargetKey = source.TargetKey,
            SizeBytes = source.SizeBytes,
            DiscardAuthority = ToNative(source.DiscardAuthority),
            TrimAuthority = ToNative(source.TrimAuthority),
            MoveDownAuthority = ToNative(source.MoveDownAuthority),
            Tier = (byte)source.Tier,
            ResourceKind = (byte)source.ResourceKind,
            RecoveryKind = (byte)source.RecoveryKind,
            Granularity = (byte)source.Granularity,
            InapplicableActions = (byte)source.InapplicableActions,
            ActionRoute = (byte)source.ActionRoute,
            DemandMask = (byte)source.DemandMask,
            ActivityScore = source.ActivityScore
        };
    }

    private static NativeResourceSchedulerExecutionAuthority ToNative(
        ResourceSchedulerExecutionAuthority source)
    {
        return new NativeResourceSchedulerExecutionAuthority
        {
            Source = (byte)source.Source,
            Action = (byte)source.Action,
            ActionRoute = (byte)source.ActionRoute,
            Flags = source.Flags,
            ResourceSlot = source.ResourceSlot,
            ResourceId = source.ResourceId,
            LedgerInstanceId = source.LedgerInstanceId,
            SourceSnapshotGeneration = source.SourceSnapshotGeneration,
            ResourceGeneration = source.ResourceGeneration,
            ResourceKey = source.ResourceKey,
            OwnerApplicationKey = source.OwnerApplicationKey,
            OwnerInstanceIdLow = source.OwnerInstanceIdLow,
            OwnerInstanceIdHigh = source.OwnerInstanceIdHigh,
            OwnerContextGeneration = source.OwnerContextGeneration,
            LeaseGeneration = source.LeaseGeneration,
            BindingGeneration = source.BindingGeneration,
            CapabilityGeneration = source.CapabilityGeneration,
            SchedulingRevision = source.SchedulingRevision,
            ExecutorIdLow = source.ExecutorIdLow,
            ExecutorIdHigh = source.ExecutorIdHigh,
            ActionAttemptIdLow = source.ActionAttemptIdLow,
            ActionAttemptIdHigh = source.ActionAttemptIdHigh,
            ProjectionEpoch = source.ProjectionEpoch
        };
    }

    private static NativeResourceSchedulerPendingAction ToNative(
        ResourceSchedulerPendingAction source)
    {
        return new NativeResourceSchedulerPendingAction
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerPendingAction)),
            Authority = ToNative(source.Authority),
            JournalTransactionIdLow = source.JournalTransactionIdLow,
            JournalTransactionIdHigh = source.JournalTransactionIdHigh,
            TargetKey = source.TargetKey,
            SizeBytes = source.SizeBytes,
            DeadlineTimestamp = source.DeadlineTimestamp,
            PendingGeneration = source.PendingGeneration,
            State = (byte)source.State,
            Tier = (byte)source.Tier
        };
    }

    internal static NativeResourceSchedulerConfig CompileConfiguration(ResourceSchedulerConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireCount(source.ResourceKindMultipliers, ResourceSchedulerConfig.ResourceKindCount, nameof(source.ResourceKindMultipliers));
        RequireCount(source.SurfaceMultipliers, ResourceSchedulerConfig.SurfaceCount, nameof(source.SurfaceMultipliers));
        RequireCount(source.PolicyGradeMultipliers, ResourceSchedulerConfig.PolicyGradeCount, nameof(source.PolicyGradeMultipliers));
        RequireCount(source.PolicyGradePressure, ResourceSchedulerConfig.PolicyGradeCount, nameof(source.PolicyGradePressure));
        RequireCount(source.SchedulingGradeMultipliers, ResourceSchedulerConfig.SchedulingGradeCount, nameof(source.SchedulingGradeMultipliers));
        RequireCount(source.DiscardKindMultipliers, ResourceSchedulerConfig.ResourceKindCount, nameof(source.DiscardKindMultipliers));
        RequireCount(source.TrimKindMultipliers, ResourceSchedulerConfig.ResourceKindCount, nameof(source.TrimKindMultipliers));
        RequireCount(source.MoveDownKindMultipliers, ResourceSchedulerConfig.ResourceKindCount, nameof(source.MoveDownKindMultipliers));
        RequireCount(source.MoveUpKindMultipliers, ResourceSchedulerConfig.ResourceKindCount, nameof(source.MoveUpKindMultipliers));
        RequireCount(source.DemandMultipliers, ResourceSchedulerConfig.DemandMultiplierCount, nameof(source.DemandMultipliers));
        RequireCount(source.PressureFreeRatioThresholds, ResourceSchedulerConfig.PressureThresholdCount, nameof(source.PressureFreeRatioThresholds));
        RequireCount(source.TargetFreeRatios, ResourceSchedulerConfig.TierCount, nameof(source.TargetFreeRatios));
        RequireCount(source.DesiredFreeRatiosLevel2To4, ResourceSchedulerConfig.TierCount, nameof(source.DesiredFreeRatiosLevel2To4));
        RequireCount(source.MinimumReleaseBytes, ResourceSchedulerConfig.PressureLevelCount, nameof(source.MinimumReleaseBytes));
        RequireCount(source.MaximumReleaseShares, ResourceSchedulerConfig.PressureLevelCount, nameof(source.MaximumReleaseShares));
        RequireCount(source.BaseScoreThresholds, ResourceSchedulerConfig.PressureThresholdCount, nameof(source.BaseScoreThresholds));
        RequireCount(source.BaseReleaseMultipliers, ResourceSchedulerConfig.PressureLevelCount, nameof(source.BaseReleaseMultipliers));
        RequireCount(source.DangerSeverityWeights, ResourceSchedulerConfig.DangerFlagCount, nameof(source.DangerSeverityWeights));

        var result = new NativeResourceSchedulerConfig
        {
            AbiVersion = NativeResourceSchedulerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeResourceSchedulerConfig)),
            Generation = source.Generation,
            BytesPerMegabyte = source.BytesPerMegabyte,
            SizeImportanceMinimum = source.SizeImportanceMinimum,
            SizeImportanceMaximum = source.SizeImportanceMaximum,
            SizeLogDivisor = source.SizeLogDivisor,
            AbsentSchedulingGradeMultiplier = source.AbsentSchedulingGradeMultiplier,
            ActivityBaseMultiplier = source.ActivityBaseMultiplier,
            ActivityQuadraticScale = source.ActivityQuadraticScale,
            ActivityNormalizer = source.ActivityNormalizer,
            LargeResourceBytes = source.LargeResourceBytes,
            HighActivityScore = source.HighActivityScore,
            PhysicalToVirtualDesperatePressureLevel = source.PhysicalToVirtualDesperatePressureLevel,
            PhysicalToVirtualMinimumFreeRatio = source.PhysicalToVirtualMinimumFreeRatio,
            PhysicalToVirtualDesperateMinimumFreeRatio = source.PhysicalToVirtualDesperateMinimumFreeRatio,
            VramToPhysicalMinimumFreeRatio = source.VramToPhysicalMinimumFreeRatio,
            BaseScoreMinimum = source.BaseScoreMinimum,
            BaseScoreMaximum = source.BaseScoreMaximum,
            TrimReleaseNumerator = source.TrimReleaseNumerator,
            TrimReleaseDenominator = source.TrimReleaseDenominator,
            MaximumActionsPerTarget = source.MaximumActionsPerTarget,
            StrongPressureFreeRatio = source.StrongPressureFreeRatio,
            DangerMinimumPhysicalAfterVramMoveRatio = source.DangerMinimumPhysicalAfterVramMoveRatio,
            DangerMinimumVirtualAfterPhysicalMoveRatio = source.DangerMinimumVirtualAfterPhysicalMoveRatio
        };

        Copy(source.ResourceKindMultipliers, result.ResourceKindMultiplier);
        Copy(source.SurfaceMultipliers, result.SurfaceMultiplier);
        Copy(source.PolicyGradeMultipliers, result.PolicyGradeMultiplier);
        Copy(source.PolicyGradePressure, result.PolicyGradePressure);
        Copy(source.SchedulingGradeMultipliers, result.SchedulingGradeMultiplier);
        CopyActionRow(source.DiscardKindMultipliers, 0, result.ActionKindMultiplier);
        CopyActionRow(source.TrimKindMultipliers, 1, result.ActionKindMultiplier);
        CopyActionRow(source.MoveDownKindMultipliers, 2, result.ActionKindMultiplier);
        CopyActionRow(source.MoveUpKindMultipliers, 3, result.ActionKindMultiplier);
        Copy(source.DemandMultipliers, result.DemandMultiplier);
        Copy(source.PressureFreeRatioThresholds, result.PressureFreeRatioThreshold);
        Copy(source.TargetFreeRatios, result.TargetFreeRatio);
        Copy(source.DesiredFreeRatiosLevel2To4, result.DesiredFreeRatioLevel2To4);
        Copy(source.MinimumReleaseBytes, result.MinimumReleaseBytes);
        Copy(source.MaximumReleaseShares, result.MaximumReleaseShare);
        Copy(source.BaseScoreThresholds, result.BaseScoreThreshold);
        Copy(source.BaseReleaseMultipliers, result.BaseReleaseMultiplier);
        Copy(source.DangerSeverityWeights, result.DangerSeverityWeight);
        return result;
    }

    private static ResourceSchedulerCandidate Project(NativeResourceSchedulerCandidate source)
    {
        return new ResourceSchedulerCandidate(
            source.TargetKey,
            Project(source.Authority),
            source.SizeBytes,
            source.EstimatedReleaseBytes,
            source.OwnerImportance,
            source.FinalImportance,
            source.ResourceInputIndex,
            source.TargetInputIndex,
            source.Rank,
            source.ResourceId,
            (AdapterResourceTier)source.Tier,
            (AdapterResourceKind)source.ResourceKind,
            source.ActivityScore,
            (AdapterSoftwareSurfaceState)source.SurfaceState,
            source.PhaseOrder,
            source.Flags);
    }

    private static ResourceSchedulerSelection Project(
        NativeResourceSchedulerSelection source,
        ulong configurationGeneration)
    {
        if (source.ConfigurationGeneration != configurationGeneration)
        {
            throw new InvalidDataException(
                "The native scheduler returned a selection for a different configuration generation.");
        }

        return new ResourceSchedulerSelection(
            source.TargetKey,
            source.RequestId,
            Project(source.Authority),
            source.SizeBytes,
            source.EstimatedReleaseBytes,
            source.ConfigurationGeneration,
            source.FinalImportance,
            source.CandidateIndex,
            source.TargetInputIndex,
            (AdapterResourceTier)source.Tier,
            source.ActivityScore);
    }

    private static ResourceSchedulerExecutionAuthority Project(
        NativeResourceSchedulerExecutionAuthority source)
    {
        return new ResourceSchedulerExecutionAuthority(
            (ResourceSchedulerSource)source.Source,
            (AdapterResourceActionMask)source.Action,
            (AdapterResourceActionRoute)source.ActionRoute,
            source.Flags,
            source.ResourceSlot,
            source.ResourceId,
            source.LedgerInstanceId,
            source.SourceSnapshotGeneration,
            source.ResourceGeneration,
            source.ResourceKey,
            source.OwnerApplicationKey,
            source.OwnerInstanceIdLow,
            source.OwnerInstanceIdHigh,
            source.OwnerContextGeneration,
            source.LeaseGeneration,
            source.BindingGeneration,
            source.CapabilityGeneration,
            source.SchedulingRevision,
            source.ExecutorIdLow,
            source.ExecutorIdHigh,
            source.ActionAttemptIdLow,
            source.ActionAttemptIdHigh,
            source.ProjectionEpoch);
    }

    private static ResourceSchedulerReservationToken Project(NativeResourceSchedulerReservationToken source)
    {
        return new ResourceSchedulerReservationToken(
            source.StateGeneration,
            source.SlotGeneration,
            source.PendingGeneration,
            source.SlotIndex);
    }

    private static ResourceSchedulerFeedbackResult Project(NativeResourceSchedulerFeedbackOutput source)
    {
        var disposition = (ResourceSchedulerReservationDisposition)source.ReservationDisposition;
        if (disposition is not (
                ResourceSchedulerReservationDisposition.Retained or
                ResourceSchedulerReservationDisposition.Completed)
            || source.Reserved0 != 0
            || source.Reserved1 != 0)
        {
            throw new InvalidDataException(
                "Native resource-scheduler feedback returned an invalid reservation disposition.");
        }
        return new ResourceSchedulerFeedbackResult(
            Project(source.ProjectedCapacity),
            source.Applied != 0,
            disposition,
            source.ResultCode,
            source.DangerFlags,
            (AdapterResourceTier)source.CurrentTier,
            source.ErrorCode);
    }

    private static ResourceSchedulerCapacity Project(NativeResourceSchedulerCapacity source)
    {
        return new ResourceSchedulerCapacity(
            source.TotalVramBytes,
            source.FreeVramBytes,
            source.TotalPhysicalBytes,
            source.FreePhysicalBytes,
            source.TotalVirtualBytes,
            source.FreeVirtualBytes,
            source.FallbackVramFreeRatio,
            source.FallbackPhysicalFreeRatio,
            source.FallbackVirtualFreeRatio,
            source.FreeBytesValidMask);
    }

    private static ResourceSchedulerTargetPlan Project(NativeResourceSchedulerTargetPlan source)
    {
        return new ResourceSchedulerTargetPlan(
            source.TargetKey,
            source.OwnerApplicationKey,
            [source.ReleaseGoalBytes[0], source.ReleaseGoalBytes[1], source.ReleaseGoalBytes[2]],
            source.CandidateCount,
            source.SelectedActionCount,
            source.FirstSelectionIndex,
            source.ActionLimit,
            [source.PressureLevel[0], source.PressureLevel[1], source.PressureLevel[2]],
            source.ActivePhaseOrder,
            source.ResultCode,
            source.DangerFlags,
            source.TargetFlags);
    }

    private static ResourceSchedulerGlobalSelection Project(NativeResourceSchedulerGlobalSelection source)
    {
        return new ResourceSchedulerGlobalSelection(
            source.TargetKey,
            source.OwnerApplicationKey,
            source.CandidateIndex,
            source.SelectionIndex,
            source.TargetInputIndex,
            source.DangerSeverity,
            (ResourceSchedulerSelectionKind)source.Kind,
            source.ResultCode,
            source.DangerFlags);
    }

    private static ResourceSchedulerPlanSummary Project(NativeResourceSchedulerPlanSummary source)
    {
        return new ResourceSchedulerPlanSummary(
            source.RequestId,
            source.ConfigurationGeneration,
            source.TargetCount,
            source.ResourceCount,
            source.ActivePendingCount,
            source.PendingDuplicateCount,
            source.CandidateCapacityRequired,
            source.RawCandidateCount,
            source.CandidateCount,
            source.SelectionCount,
            source.InvalidResourceCount,
            source.RequiredNowCount,
            source.NoLegalActionCount,
            source.DemandBlockedCount,
            source.IntrinsicBlockedCount,
            source.CapacityBlockedCount,
            source.InvalidNumericCount,
            source.PendingBlockedCount,
            source.DangerTargetCount);
    }

    private static void RequireCount<T>(IReadOnlyList<T>? values, int expected, string name)
    {
        if (values is null || values.Count != expected)
        {
            throw new ArgumentException($"{name} must contain exactly {expected} items.", name);
        }
    }

    private static ulong Bytes<T>(T[] values) where T : unmanaged
        => checked((ulong)values.LongLength * (ulong)sizeof(T));

    private static void Copy(IReadOnlyList<double> source, double* destination)
    {
        var index = 0;
        while (index < source.Count)
        {
            destination[index] = source[index];
            index++;
        }
    }

    private static void Copy(IReadOnlyList<ulong> source, ulong* destination)
    {
        var index = 0;
        while (index < source.Count)
        {
            destination[index] = source[index];
            index++;
        }
    }

    private static void Copy(IReadOnlyList<byte> source, byte* destination)
    {
        var index = 0;
        while (index < source.Count)
        {
            destination[index] = source[index];
            index++;
        }
    }

    private static void CopyActionRow(IReadOnlyList<double> source, int row, double* destination)
    {
        var offset = checked(row * ResourceSchedulerConfig.ResourceKindCount);
        var index = 0;
        while (index < source.Count)
        {
            destination[offset + index] = source[index];
            index++;
        }
    }

    private static void ThrowIfFailed(NativeResourceSchedulerResultCode result, string operation)
    {
        if (result != NativeResourceSchedulerResultCode.Ok)
        {
            throw new NativeResourceSchedulerException(operation, (int)result);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
