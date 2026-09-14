using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerProcessEffectValidationScopeState : byte
{
    ProductionUnscoped = 1,
    Active = 2,
    ExpiredBlocked = 3,
    FaultedBlocked = 4
}

internal enum HostManagerProcessEffectValidationFamily : byte
{
    NonAdaptedMemoryTransaction = 1,
    AutomaticMemoryCleanup = 2,
    NativeProcessPolicyTransaction = 3
}

internal enum HostManagerProcessEffectValidationDecision : byte
{
    AllowedScoped = 1,
    DeniedNotAllowlisted = 2,
    DeniedIdentityMismatch = 3,
    DeniedNotJobMember = 4,
    DeniedMembershipUnknown = 5,
    DeniedExpired = 6,
    DeniedScopeFault = 7,
    DeniedSnapshotMismatch = 8,
    AllowedPriorHandoffRecovery = 9
}

internal sealed class HostManagerProcessEffectValidationCycleSnapshot
{
    internal static HostManagerProcessEffectValidationCycleSnapshot ProductionUnscoped { get; }
        = new(
            HostManagerProcessEffectValidationScopeState.ProductionUnscoped,
            Guid.Empty,
            0,
            null,
            DateTimeOffset.MaxValue,
            null,
            null,
            automaticMemoryCleanupAllowed: false,
            nonAdaptedMemoryTransactionAllowed: false);

    private readonly HostManagerProcessEffectValidationScopeState publishedState;
    private readonly TimeProvider? timeProvider;

    internal HostManagerProcessEffectValidationCycleSnapshot(
        HostManagerProcessEffectValidationScopeState publishedState,
        Guid scopeId,
        long generation,
        string? jobName,
        DateTimeOffset expiresAt,
        IReadOnlySet<HostManagerComputeProcessIdentity>? allowedProcesses,
        TimeProvider? timeProvider,
        bool automaticMemoryCleanupAllowed = false,
        bool nonAdaptedMemoryTransactionAllowed = false)
    {
        this.publishedState = publishedState;
        ScopeId = scopeId;
        Generation = generation;
        JobName = jobName;
        ExpiresAt = expiresAt;
        AllowedProcesses = allowedProcesses;
        AutomaticMemoryCleanupAllowed = automaticMemoryCleanupAllowed;
        NonAdaptedMemoryTransactionAllowed = nonAdaptedMemoryTransactionAllowed;
        this.timeProvider = timeProvider;
    }

    internal HostManagerProcessEffectValidationScopeState State
        => publishedState == HostManagerProcessEffectValidationScopeState.Active
            && timeProvider!.GetUtcNow() >= ExpiresAt
                ? HostManagerProcessEffectValidationScopeState.ExpiredBlocked
                : publishedState;

    internal Guid ScopeId { get; }
    internal long Generation { get; }
    internal string? JobName { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal IReadOnlySet<HostManagerComputeProcessIdentity>? AllowedProcesses { get; }
    internal bool AutomaticMemoryCleanupAllowed { get; }
    internal bool NonAdaptedMemoryTransactionAllowed { get; }

    internal bool IsProductionUnscoped
        => publishedState ==
            HostManagerProcessEffectValidationScopeState.ProductionUnscoped;

    internal bool Allows(HostManagerComputeProcessIdentity identity)
        => IsProductionUnscoped
            || State == HostManagerProcessEffectValidationScopeState.Active
                && AllowedProcesses is not null
                && AllowedProcesses.Contains(identity);
}

internal sealed class HostManagerProcessEffectValidationAdmissionPermit
{
    internal static HostManagerProcessEffectValidationAdmissionPermit ProductionUnscoped { get; }
        = new();

    private int handoffStage;

    private HostManagerProcessEffectValidationAdmissionPermit()
    {
        IsProductionUnscoped = true;
    }

    internal HostManagerProcessEffectValidationAdmissionPermit(
        HostManagerProcessEffectValidationScopeAuthority owner,
        Guid runtimeIncarnation,
        Guid scopeId,
        long generation,
        Guid admissionId,
        long firstAuditSequence,
        int auditCount,
        Guid parentAdmissionId = default)
    {
        Owner = owner;
        RuntimeIncarnation = runtimeIncarnation;
        ScopeId = scopeId;
        Generation = generation;
        AdmissionId = admissionId;
        FirstAuditSequence = firstAuditSequence;
        AuditCount = auditCount;
        ParentAdmissionId = parentAdmissionId;
    }

    internal HostManagerProcessEffectValidationScopeAuthority? Owner { get; }
    internal Guid RuntimeIncarnation { get; }
    internal Guid ScopeId { get; }
    internal long Generation { get; }
    internal Guid AdmissionId { get; }
    internal long FirstAuditSequence { get; }
    internal int AuditCount { get; }
    internal Guid ParentAdmissionId { get; }
    internal bool IsRecovery => ParentAdmissionId != Guid.Empty;
    internal bool IsProductionUnscoped { get; }

    internal bool TryClaimHandoffDeclaration()
        => IsProductionUnscoped
            || Interlocked.CompareExchange(ref handoffStage, 1, 0) == 0;

    internal bool TryClaimHandoffCommit()
        => IsProductionUnscoped
            || Interlocked.CompareExchange(ref handoffStage, 2, 1) == 1;
}

internal enum WindowsJobMembershipStatus : byte
{
    ExactMember = 1,
    IdentityMismatch = 2,
    NotMember = 3,
    Unknown = 4
}

internal readonly record struct WindowsJobMembershipResult(
    WindowsJobMembershipStatus Status,
    int SystemError);

internal interface IWindowsJobMembershipProbe
{
    WindowsJobMembershipResult Probe(
        string jobName,
        HostManagerComputeProcessIdentity identity);
}

internal interface IHostManagerProcessEffectValidationScopeFileCommitter
{
    void Commit(string temporaryPath, string canonicalPath, bool replaceExisting);

    void DeleteExact(string canonicalPath);
}

internal sealed class WindowsHostManagerProcessEffectValidationScopeFileCommitter
    : IHostManagerProcessEffectValidationScopeFileCommitter
{
    internal static WindowsHostManagerProcessEffectValidationScopeFileCommitter Instance { get; }
        = new();

    private WindowsHostManagerProcessEffectValidationScopeFileCommitter()
    {
    }

    public void Commit(string temporaryPath, string canonicalPath, bool replaceExisting)
    {
        if (replaceExisting)
        {
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, canonicalPath);
            return;
        }

        WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, canonicalPath);
    }

    public void DeleteExact(string canonicalPath)
        => _ = WindowsNativeAtomicFileCommitter.DeleteExact(canonicalPath);
}

public sealed class HostManagerProcessEffectValidationScopeAuthority
{
    internal const int SchemaVersion = 1;
    internal const string Contract = "host-manager-process-effect-validation-scope-v1";
    internal const int MaximumAllowedProcessCount = 64;
    internal const int MaximumAuditRecordCount = 4096;
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private const int MaximumAuditFenceBytes = 4 * 1024 * 1024;
    private const int MaximumAuthorityManifestBytes = 64 * 1024;
    private const int MaximumJobNameCharacters = 256;
    private const int MaximumStageCharacters = 96;
    private const int StorageSchemaVersion = 2;
    private const string StorageContract =
        "host-manager-process-effect-validation-scope-storage-v2";
    private const int AuditFenceSchemaVersion = 6;
    private const string AuditFenceContract =
        "host-manager-process-effect-validation-admission-fence-v6";
    private const int AuthorityManifestSchemaVersion = 1;
    private const string AuthorityManifestContract =
        "host-manager-process-effect-validation-authority-manifest-v1";
    private const string AuditFencePathSuffix = ".audit-fence.json";
    private const string PreviousRuntimeIncarnationFault =
        "effect-scope-load-failed:previous-runtime-incarnation";
    private const string OpeningCleanupPendingFault =
        "effect-scope-open-cleanup-pending";
    private const string ClosingPendingFault = "effect-scope-close-pending";
    private const string RequiredJobNamePrefix =
        "Global\\ResourceManager-NonAdaptedOptimizationLab-";
    private static readonly TimeSpan MinimumLease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string statePath;
    private readonly string auditFencePath;
    private readonly string authorityManifestPath;
    private readonly TimeProvider timeProvider;
    private readonly IWindowsJobMembershipProbe membershipProbe;
    private readonly IHostManagerProcessEffectValidationScopeFileCommitter committer;
    private readonly Guid runtimeIncarnation;
    private HostManagerProcessEffectValidationScopeDocument? current;
    private HostManagerProcessEffectValidationScopeDocument? closedReceipt;
    private Guid pendingAdmissionId;
    private bool recoveryLedgerAvailable;
    private bool loadedPendingReconciliation;
    private bool scopedAutomaticMemoryCleanupAllowed;
    private bool scopedNonAdaptedMemoryTransactionAllowed;
    private string? fault;
    private HostManagerProcessEffectValidationCycleSnapshot publishedSnapshot =
        HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped;

    internal HostManagerProcessEffectValidationScopeAuthority(
        IHostEnvironment environment,
        TimeProvider timeProvider,
        IWindowsJobMembershipProbe membershipProbe)
        : this(
            ResolveProductionPaths(environment),
            timeProvider,
            membershipProbe,
            WindowsHostManagerProcessEffectValidationScopeFileCommitter.Instance)
    {
    }

    private HostManagerProcessEffectValidationScopeAuthority(
        (string StatePath, string AuthorityManifestPath) paths,
        TimeProvider timeProvider,
        IWindowsJobMembershipProbe membershipProbe,
        IHostManagerProcessEffectValidationScopeFileCommitter committer)
        : this(
            paths.StatePath,
            timeProvider,
            membershipProbe,
            committer,
            authorityManifestPath: paths.AuthorityManifestPath)
    {
    }

    internal HostManagerProcessEffectValidationScopeAuthority(
        string statePath,
        TimeProvider timeProvider,
        IWindowsJobMembershipProbe membershipProbe,
        IHostManagerProcessEffectValidationScopeFileCommitter committer,
        Guid runtimeIncarnation = default,
        string? authorityManifestPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(membershipProbe);
        ArgumentNullException.ThrowIfNull(committer);
        if (!Path.IsPathFullyQualified(statePath))
        {
            throw new ArgumentException(
                "The validation effect-scope path must be absolute.",
                nameof(statePath));
        }

        this.statePath = Path.GetFullPath(statePath);
        auditFencePath = $"{this.statePath}{AuditFencePathSuffix}";
        this.authorityManifestPath = Path.GetFullPath(
            authorityManifestPath
                ?? ResolveDefaultAuthorityManifestPath(this.statePath));
        ValidateAuthorityManifestLocation(
            this.statePath,
            this.authorityManifestPath);
        this.timeProvider = timeProvider;
        this.membershipProbe = membershipProbe;
        this.committer = committer;
        this.runtimeIncarnation = runtimeIncarnation == Guid.Empty
            ? Guid.NewGuid()
            : runtimeIncarnation;
        LoadInitialState();
    }

    internal HostManagerProcessEffectValidationCycleSnapshot Capture()
        => Volatile.Read(ref publishedSnapshot);

    internal HostManagerProcessEffectValidationScopeStatus Open(
        HostManagerProcessEffectValidationScopeOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (string.Equals(
                    fault,
                    OpeningCleanupPendingFault,
                    StringComparison.Ordinal)
                && current is not null)
            {
                _ = TryResolveOpenPersistenceLocked(current);
            }
            if (fault is not null)
            {
                throw new InvalidOperationException(
                    "The validation effect scope is faulted and remains fail-closed.");
            }
            if (current is not null)
            {
                throw new InvalidOperationException(
                    "A validation effect scope is already active.");
            }
            try
            {
                ValidateReadyStoreBoundaryLocked();
                if (closedReceipt is null
                    && (PathExistsExact(statePath)
                        || PathExistsExact(auditFencePath)))
                {
                    throw new InvalidDataException(
                        "Validation effect-scope storage appeared after the authority initialized empty.");
                }
            }
            catch (Exception exception) when (IsStorageBoundaryException(exception))
            {
                LatchStorageFaultLocked("open-preflight", exception);
                throw new InvalidOperationException(
                    "The validation effect-scope storage preflight failed closed.",
                    exception);
            }

            var now = timeProvider.GetUtcNow();
            if (request.RunNonce == Guid.Empty
                || !IsValidationJobName(request.JobName)
                || string.IsNullOrWhiteSpace(request.ReleaseToken)
                || request.ReleaseToken.Length is < 32 or > 512
                || request.ExpiresAt <= now + MinimumLease
                || request.ExpiresAt > now + MaximumLease
                || request.AllowedProcesses is null
                || request.AllowedProcesses.Count is < 1 or > MaximumAllowedProcessCount)
            {
                throw new ArgumentException(
                    "The validation effect-scope request is outside its bounded contract.",
                    nameof(request));
            }

            var identities = new List<HostManagerProcessEffectValidationScopeIdentityDocument>(
                request.AllowedProcesses.Count);
            var unique = new HashSet<HostManagerComputeProcessIdentity>();
            foreach (var process in request.AllowedProcesses)
            {
                if (process.ProcessId <= 0 || process.ProcessStartTimeFileTimeUtc <= 0)
                {
                    throw new ArgumentException(
                        "The validation effect scope contains an invalid process identity.",
                        nameof(request));
                }
                var identity = new HostManagerComputeProcessIdentity(
                    process.ProcessId,
                    checked((ulong)process.ProcessStartTimeFileTimeUtc));
                if (!unique.Add(identity))
                {
                    throw new ArgumentException(
                        "The validation effect scope contains duplicate process identities.",
                        nameof(request));
                }
                var membership = membershipProbe.Probe(request.JobName, identity);
                if (membership.Status != WindowsJobMembershipStatus.ExactMember)
                {
                    throw new InvalidOperationException(
                        $"Validation target PID {identity.ProcessId} is not an exact member of the dedicated Job ({membership.Status}, {membership.SystemError}).");
                }
                identities.Add(new(identity.ProcessId, identity.ProcessStartKey));
            }
            identities.Sort(static (left, right) =>
            {
                var processOrder = left.ProcessId.CompareTo(right.ProcessId);
                return processOrder != 0
                    ? processOrder
                    : left.ProcessStartKey.CompareTo(right.ProcessStartKey);
            });

            if (closedReceipt is not null)
            {
                RetireClosedReceiptLocked();
            }

            var scopeId = Guid.NewGuid();
            var generation = CreateNonzeroGeneration(scopeId, request.RunNonce);
            var document = new HostManagerProcessEffectValidationScopeDocument(
                StorageSchemaVersion,
                StorageContract,
                scopeId,
                request.RunNonce,
                runtimeIncarnation,
                request.JobName,
                ComputeSha256(request.JobName),
                request.ExpiresAt.UtcTicks,
                ComputeSha256(request.ReleaseToken),
                generation,
                identities,
                [],
                string.Empty);
            document = document with { ChecksumSha256 = ComputeChecksum(document) };
            current = document;
            scopedAutomaticMemoryCleanupAllowed = request.AllowAutomaticMemoryCleanup;
            scopedNonAdaptedMemoryTransactionAllowed =
                request.AllowNonAdaptedMemoryTransaction;
            try
            {
                PersistAuditFence(
                    CreateOpeningAuditFence(document),
                    replaceExisting: false);
                PersistScope(document, replaceExisting: false);
                PersistAuditFence(
                    CreateCleanAuditFence(document, []),
                    replaceExisting: true);
                recoveryLedgerAvailable = true;
            }
            catch (Exception exception)
            {
                try
                {
                    if (TryResolveOpenPersistenceLocked(document))
                    {
                        PublishSnapshotLocked();
                        return CreateStatus(
                            HostManagerProcessEffectValidationScopeState.Active,
                            document,
                            failure: null);
                    }
                }
                catch (Exception reconciliationException)
                {
                    fault = OpeningCleanupPendingFault;
                    recoveryLedgerAvailable = false;
                    PublishSnapshotLocked();
                    throw new InvalidOperationException(
                        "The validation effect scope open transaction could not be reconciled.",
                        new AggregateException(exception, reconciliationException));
                }
                throw new InvalidOperationException(
                    "The validation effect scope could not be durably opened.",
                    exception);
            }
            PublishSnapshotLocked();
            return CreateStatus(
                HostManagerProcessEffectValidationScopeState.Active,
                document,
                failure: null);
        }
    }

    internal HostManagerProcessEffectValidationScopeStatus GetStatus()
    {
        lock (gate)
        {
            if (fault is null
                && pendingAdmissionId == Guid.Empty
                && current is null
                && closedReceipt is not null)
            {
                return CreateClosedStatus(closedReceipt);
            }
            var state = fault is not null || pendingAdmissionId != Guid.Empty
                ? HostManagerProcessEffectValidationScopeState.FaultedBlocked
                : current is null
                    ? HostManagerProcessEffectValidationScopeState.ProductionUnscoped
                    : IsExpired(current)
                        ? HostManagerProcessEffectValidationScopeState.ExpiredBlocked
                        : HostManagerProcessEffectValidationScopeState.Active;
            return CreateStatus(
                state,
                current,
                fault ?? (pendingAdmissionId == Guid.Empty
                    ? null
                    : "effect-scope-admission-pending"));
        }
    }

    internal HostManagerProcessEffectValidationScopeStatus Close(
        HostManagerProcessEffectValidationScopeCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (current is null)
            {
                if (fault is not null || pendingAdmissionId != Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "The validation effect scope is faulted and remains fail-closed.");
                }
                var receipt = closedReceipt
                    ?? throw new InvalidOperationException(
                        "No validation effect scope is active or durably closed.");
                ValidateCloseRequest(receipt, request);
                return CreateClosedStatus(receipt);
            }
            var document = current;
            ValidateCloseRequest(document, request);

            var fence = ReadAuditFence();
            if (fence.State == HostManagerProcessEffectValidationAuditFenceState.Closed)
            {
                ValidateClosedAuditFenceBinding(document, fence);
                ActivateClosedReceiptLocked(document);
                return CreateClosedStatus(document);
            }
            if (fence.State == HostManagerProcessEffectValidationAuditFenceState.Closing)
            {
                ValidateClosingAuditFenceBinding(document, fence);
            }
            else
            {
                ValidateCleanAuditFenceBinding(document, fence);
                if (pendingAdmissionId != Guid.Empty
                    || loadedPendingReconciliation
                    || fence.CommittedHandoffs.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The validation effect scope still owns unsettled durable handoffs.");
                }
                fault = ClosingPendingFault;
                recoveryLedgerAvailable = false;
                PublishSnapshotLocked();
                PersistAuditFence(
                    CreateClosingAuditFence(document),
                    replaceExisting: true);
            }
            CompleteCloseLifecycleLocked(document);
            return CreateClosedStatus(document);
        }
    }

    private static void ValidateCloseRequest(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationScopeCloseRequest request)
    {
        if (request.ScopeId == Guid.Empty
            || request.RunNonce == Guid.Empty
            || request.ScopeId != document.ScopeId
            || request.RunNonce != document.RunNonce
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(document.ReleaseTokenSha256),
                SHA256.HashData(Encoding.UTF8.GetBytes(request.ReleaseToken ?? string.Empty))))
        {
            throw new UnauthorizedAccessException(
                "The validation effect-scope release proof is invalid.");
        }
    }

    private bool TryResolveOpenPersistenceLocked(
        HostManagerProcessEffectValidationScopeDocument expected)
    {
        var stateExists = TryReadBoundedDocument(
            statePath,
            MaximumDocumentBytes,
            "effect-scope",
            out HostManagerProcessEffectValidationScopeDocument? document);
        var fenceExists = TryReadBoundedDocument(
            auditFencePath,
            MaximumAuditFenceBytes,
            "effect-scope admission-fence",
            out HostManagerProcessEffectValidationAuditFenceDocument? fence);
        if (!stateExists && !fenceExists)
        {
            WindowsProcessEffectValidationScopeStorage
                .ValidateExistingDirectoryIfPresent(statePath);
            ResetLifecycleStateLocked();
            return false;
        }
        if (stateExists)
        {
            ValidateDocument(document
                ?? throw new InvalidDataException(
                    "The opening scope document is missing."));
            if (!string.Equals(
                    document.ChecksumSha256,
                    expected.ChecksumSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The opening scope document changed during persistence recovery.");
            }
        }
        if (!fenceExists || fence is null)
        {
            throw new InvalidDataException(
                "The opening scope fence is missing during persistence recovery.");
        }
        ValidateAuditFenceDocument(fence);
        if (fence.State == HostManagerProcessEffectValidationAuditFenceState.Clean)
        {
            if (!stateExists || document is null)
            {
                throw new InvalidDataException(
                    "A committed opening fence has no scope document.");
            }
            ValidateAuditFenceScopeBinding(document, fence);
            ValidateCleanAuditFenceBinding(document, fence);
            current = document;
            pendingAdmissionId = Guid.Empty;
            loadedPendingReconciliation = false;
            recoveryLedgerAvailable = true;
            fault = null;
            return true;
        }
        ValidateOpeningAuditFenceBinding(expected, fence);
        if (stateExists && document is not null)
        {
            ValidateAuditFenceScopeBinding(document, fence);
        }
        fault = OpeningCleanupPendingFault;
        recoveryLedgerAvailable = false;
        PublishSnapshotLocked();
        CompleteLifecycleDeletionLocked();
        return false;
    }

    private void CompleteLifecycleDeletionLocked()
    {
        // An opening fence authorizes cleanup of an open transaction that never
        // became externally usable.
        committer.DeleteExact(statePath);
        committer.DeleteExact(auditFencePath);
        ResetLifecycleStateLocked();
    }

    private void CompleteCloseLifecycleLocked(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        PersistAuditFence(
            CreateClosedAuditFence(document),
            replaceExisting: true);
        ActivateClosedReceiptLocked(document);
    }

    private void ActivateClosedReceiptLocked(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        current = null;
        scopedAutomaticMemoryCleanupAllowed = false;
        scopedNonAdaptedMemoryTransactionAllowed = false;
        closedReceipt = document;
        pendingAdmissionId = Guid.Empty;
        loadedPendingReconciliation = false;
        recoveryLedgerAvailable = false;
        fault = null;
        PublishSnapshotLocked();
    }

    private void RetireClosedReceiptLocked()
    {
        var receipt = closedReceipt
            ?? throw new InvalidOperationException(
                "The validation effect-scope close receipt is missing.");
        var stateExists = TryReadBoundedDocument(
            statePath,
            MaximumDocumentBytes,
            "closed effect-scope receipt",
            out HostManagerProcessEffectValidationScopeDocument? persisted);
        var fenceExists = TryReadBoundedDocument(
            auditFencePath,
            MaximumAuditFenceBytes,
            "closed effect-scope receipt fence",
            out HostManagerProcessEffectValidationAuditFenceDocument? fence);
        if (stateExists)
        {
            ValidateDocument(persisted
                ?? throw new InvalidDataException(
                    "The closed effect-scope receipt document is missing."));
            if (!string.Equals(
                    persisted.ChecksumSha256,
                    receipt.ChecksumSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The closed effect-scope receipt changed before retirement.");
            }
        }
        if (!fenceExists || fence is null)
        {
            throw new InvalidDataException(
                stateExists
                    ? "The closed effect-scope receipt fence is missing."
                    : "The durable closed effect-scope receipt disappeared before retirement.");
        }
        ValidateAuditFenceDocument(fence);
        if (fence.State == HostManagerProcessEffectValidationAuditFenceState.Closed)
        {
            ValidateClosedAuditFenceBinding(receipt, fence);
            var retiring = CreateRetiringClosedAuditFence(receipt);
            try
            {
                PersistAuditFence(retiring, replaceExisting: true);
            }
            catch (Exception commitException) when (
                IsStorageBoundaryException(commitException))
            {
                bool retirementCommitted;
                try
                {
                    retirementCommitted =
                        TryResolveClosedReceiptRetirementPersistenceLocked(receipt);
                }
                catch (Exception confirmationException)
                {
                    LatchStorageFaultLocked(
                        "closed-receipt-retirement-reconcile",
                        confirmationException);
                    throw new InvalidOperationException(
                        "The retiring closed effect-scope receipt commit could not be reconciled.",
                        new AggregateException(
                            commitException,
                            confirmationException));
                }
                if (!retirementCommitted)
                {
                    throw new InvalidOperationException(
                        "The retiring closed effect-scope receipt transition was not durably committed.",
                        commitException);
                }
            }
        }
        else
        {
            ValidateRetiringClosedAuditFenceBinding(receipt, fence);
        }
        committer.DeleteExact(statePath);
        committer.DeleteExact(auditFencePath);
        closedReceipt = null;
        PublishSnapshotLocked();
    }

    private bool TryResolveClosedReceiptRetirementPersistenceLocked(
        HostManagerProcessEffectValidationScopeDocument receipt)
    {
        var stateExists = TryReadBoundedDocument(
            statePath,
            MaximumDocumentBytes,
            "closed effect-scope retirement receipt",
            out HostManagerProcessEffectValidationScopeDocument? persisted);
        var fenceExists = TryReadBoundedDocument(
            auditFencePath,
            MaximumAuditFenceBytes,
            "closed effect-scope retirement fence",
            out HostManagerProcessEffectValidationAuditFenceDocument? fence);
        if (!stateExists || persisted is null)
        {
            throw new InvalidDataException(
                "The closed effect-scope receipt disappeared during retirement reconciliation.");
        }
        ValidateDocument(persisted);
        if (!string.Equals(
                persisted.ChecksumSha256,
                receipt.ChecksumSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The closed effect-scope receipt changed during retirement reconciliation.");
        }
        if (!fenceExists || fence is null)
        {
            throw new InvalidDataException(
                "The closed effect-scope retirement fence is missing during reconciliation.");
        }
        ValidateAuditFenceDocument(fence);
        switch (fence.State)
        {
            case HostManagerProcessEffectValidationAuditFenceState.Closed:
                ValidateClosedAuditFenceBinding(receipt, fence);
                return false;
            case HostManagerProcessEffectValidationAuditFenceState.RetiringClosed:
                ValidateRetiringClosedAuditFenceBinding(receipt, fence);
                return true;
            default:
                throw new InvalidDataException(
                    "The closed effect-scope retirement fence has an unexpected state during reconciliation.");
        }
    }

    private void LatchStorageFaultLocked(string operation, Exception exception)
    {
        fault = $"effect-scope-{operation}-failed:{exception.GetType().Name}";
        recoveryLedgerAvailable = false;
        PublishSnapshotLocked();
    }

    private static bool IsStorageBoundaryException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException;

    private void ResetLifecycleStateLocked()
    {
        current = null;
        scopedAutomaticMemoryCleanupAllowed = false;
        scopedNonAdaptedMemoryTransactionAllowed = false;
        closedReceipt = null;
        pendingAdmissionId = Guid.Empty;
        loadedPendingReconciliation = false;
        recoveryLedgerAvailable = false;
        fault = null;
        PublishSnapshotLocked();
    }

    internal bool TryBeginAdmission(
        in HostManagerProcessEffectValidationCycleSnapshot snapshot,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        HostManagerComputeProcessIdentity identity,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
    {
        if (snapshot.IsProductionUnscoped)
        {
            return TryBeginProductionUnscopedAdmission(snapshot, out permit);
        }
        return TryBeginAdmissionCore(snapshot, family, stage, [identity], out permit);
    }

    internal bool TryBeginBatchAdmission(
        in HostManagerProcessEffectValidationCycleSnapshot snapshot,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        IReadOnlyList<HostManagerComputeProcessIdentity> identities,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
    {
        if (snapshot.IsProductionUnscoped)
        {
            return TryBeginProductionUnscopedAdmission(snapshot, out permit);
        }
        return TryBeginAdmissionCore(snapshot, family, stage, identities, out permit);
    }

    private bool TryBeginProductionUnscopedAdmission(
        HostManagerProcessEffectValidationCycleSnapshot snapshot,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
    {
        lock (gate)
        {
            if (!ReferenceEquals(snapshot, publishedSnapshot)
                || current is not null
                || fault is not null
                || pendingAdmissionId != Guid.Empty)
            {
                permit = null;
                return false;
            }
            permit = HostManagerProcessEffectValidationAdmissionPermit.ProductionUnscoped;
            return true;
        }
    }

    internal bool TryDeclareNativeTransactionJournal(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        in NativeTransactionJournalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(permit);
        if (permit.IsProductionUnscoped)
        {
            return true;
        }
        if (!TryCreateNativeHandoff(identity, out var handoff, out var process))
        {
            MarkAdmissionUnsettled(permit, "native-journal-identity-invalid");
            return false;
        }
        return TryDeclareDurableHandoff(permit, handoff, [process]);
    }

    internal bool TryCommitNativeTransactionJournal(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        in NativeTransactionJournalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(permit);
        if (permit.IsProductionUnscoped)
        {
            return true;
        }
        if (!TryCreateNativeHandoff(identity, out var handoff, out var process))
        {
            MarkAdmissionUnsettled(permit, "native-journal-identity-invalid");
            return false;
        }
        return TryCommitDurableHandoff(permit, handoff, [process]);
    }

    internal bool TryDeclareMemoryCleanupAttemptBatch(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerMemoryCleanupAttemptBatch batch,
        ulong attemptGeneration)
    {
        ArgumentNullException.ThrowIfNull(permit);
        if (permit.IsProductionUnscoped)
        {
            return true;
        }
        if (!TryCreateMemoryCleanupHandoff(
                batch,
                attemptGeneration,
                out var handoff,
                out var processes))
        {
            MarkAdmissionUnsettled(permit, "memory-cleanup-batch-identity-invalid");
            return false;
        }
        return TryDeclareDurableHandoff(
            permit,
            handoff,
            processes);
    }

    internal bool TryCommitMemoryCleanupAttemptBatch(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerMemoryCleanupAttemptBatch batch,
        ulong attemptGeneration)
    {
        ArgumentNullException.ThrowIfNull(permit);
        if (permit.IsProductionUnscoped)
        {
            return true;
        }
        if (!TryCreateMemoryCleanupHandoff(
                batch,
                attemptGeneration,
                out var handoff,
                out var processes))
        {
            MarkAdmissionUnsettled(permit, "memory-cleanup-batch-identity-invalid");
            return false;
        }
        return TryCommitDurableHandoff(permit, handoff, processes);
    }

    internal bool TryAuthorizeNativeRecovery(
        HostManagerProcessEffectValidationFamily family,
        in NativeTransactionJournalIdentity identity)
    {
        if (!TryCreateNativeHandoff(identity, out var handoff, out var process))
        {
            return false;
        }
        lock (gate)
        {
            if (current is null)
            {
                return fault is null;
            }
            if (!CanUseRecoveryAuthorityLocked()
                || !recoveryLedgerAvailable
                || pendingAdmissionId != Guid.Empty)
            {
                return false;
            }
            try
            {
                var fence = ReadAuditFence();
                ValidateCleanAuditFenceBinding(current, fence);
                return TryFindCommittedHandoff(
                    current,
                    fence,
                    family,
                    handoff,
                    process,
                    out _);
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-recovery-authorization-failed:{exception.GetType().Name}";
                recoveryLedgerAvailable = false;
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    internal bool TryBeginRecoveryAdmission(
        HostManagerProcessEffectValidationFamily family,
        string stage,
        HostManagerComputeProcessIdentity identity,
        in NativeTransactionJournalIdentity sourceIdentity,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
    {
        permit = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (stage.Length > MaximumStageCharacters
            || identity.ProcessId <= 0
            || identity.ProcessStartKey == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }
        if (!TryCreateNativeHandoff(sourceIdentity, out var sourceHandoff, out var sourceProcess)
            || sourceProcess != identity)
        {
            return false;
        }

        lock (gate)
        {
            if (current is null)
            {
                if (fault is null)
                {
                    permit = HostManagerProcessEffectValidationAdmissionPermit.ProductionUnscoped;
                    return true;
                }
                return false;
            }
            if (!CanUseRecoveryAuthorityLocked()
                || !recoveryLedgerAvailable
                || pendingAdmissionId != Guid.Empty)
            {
                return false;
            }

            try
            {
                var cleanFence = ReadAuditFence();
                ValidateCleanAuditFenceBinding(current, cleanFence);
                if (!TryFindCommittedHandoff(
                        current,
                        cleanFence,
                        family,
                        sourceHandoff,
                        identity,
                        out var source)
                    || current.AuditRecords.Count >= MaximumAuditRecordCount)
                {
                    return false;
                }

                var admissionId = Guid.NewGuid();
                var audit = CreateAuditRecord(
                    current,
                    admissionId,
                    checked((long)current.AuditRecords.Count + 1L),
                    family,
                    stage,
                    identity,
                    HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery,
                    0);
                PersistAuditFence(
                    CreateIntentAuditFence(
                        current,
                        cleanFence.CommittedHandoffs,
                        admissionId,
                        source.AdmissionId,
                        [audit]),
                    replaceExisting: true);
                var next = CreateNextAuditDocument(current, [audit]);
                PersistScope(next, replaceExisting: true);
                current = next;
                PersistAuditFence(
                    CreateAuditCommittedFence(
                        next,
                        cleanFence.CommittedHandoffs,
                        admissionId,
                        source.AdmissionId,
                        [audit]),
                    replaceExisting: true);
                pendingAdmissionId = admissionId;
                PublishSnapshotLocked();
                permit = new HostManagerProcessEffectValidationAdmissionPermit(
                    this,
                    runtimeIncarnation,
                    next.ScopeId,
                    next.Generation,
                    admissionId,
                    audit.Sequence,
                    1,
                    source.AdmissionId);
                return true;
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-recovery-admission-failed:{exception.GetType().Name}";
                recoveryLedgerAvailable = false;
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    internal bool ReconcileDeclaredNativeHandoff(
        IReadOnlyList<NativeTransactionJournalIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var handoffs = identities
            .Select(static identity => HostManagerProcessEffectValidationDurableHandoffDocument
                .CreateNativeTransaction(identity))
            .ToArray();
        return ReconcileDeclaredHandoff(
            HostManagerProcessEffectValidationDurableHandoffKind.NativeTransactionJournal,
            handoffs);
    }

    internal bool ReconcileDeclaredMemoryCleanupHandoff(
        IReadOnlyList<HostManagerMemoryCleanupAttemptBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var handoffs = new HostManagerProcessEffectValidationDurableHandoffDocument[
            batches.Count];
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            if (!TryNormalizeMemoryCleanupProcesses(batch.Processes, out var processes)
                || batch.BatchId == Guid.Empty
                || batch.AttemptGeneration == 0)
            {
                handoffs[index] = HostManagerProcessEffectValidationDurableHandoffDocument
                    .CreateMemoryCleanupBatch(Guid.Empty, 0, []);
                continue;
            }
            handoffs[index] = HostManagerProcessEffectValidationDurableHandoffDocument
                .CreateMemoryCleanupBatch(
                    batch.BatchId,
                    batch.AttemptGeneration,
                    processes);
        }
        return ReconcileDeclaredHandoff(
            HostManagerProcessEffectValidationDurableHandoffKind.MemoryCleanupAttemptBatch,
            handoffs);
    }

    internal void MarkAdmissionUnsettled(
        HostManagerProcessEffectValidationAdmissionPermit? permit,
        string reason)
    {
        if (permit is null || permit.IsProductionUnscoped)
        {
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            if (ReferenceEquals(permit.Owner, this)
                && permit.RuntimeIncarnation == runtimeIncarnation
                && permit.ScopeId == current?.ScopeId
                && permit.Generation == current.Generation
                && permit.AdmissionId == pendingAdmissionId)
            {
                if (fault is null
                    || string.Equals(
                        fault,
                        PreviousRuntimeIncarnationFault,
                        StringComparison.Ordinal))
                {
                    fault = $"effect-scope-admission-unsettled:{reason}";
                }
                PublishSnapshotLocked();
            }
        }
    }

    private bool TryBeginAdmissionCore(
        in HostManagerProcessEffectValidationCycleSnapshot snapshot,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        IReadOnlyList<HostManagerComputeProcessIdentity> identities,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
    {
        permit = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(identities);
        if (stage.Length > MaximumStageCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }
        if (identities.Count is < 1 or > MaximumAllowedProcessCount)
        {
            throw new ArgumentOutOfRangeException(nameof(identities));
        }
        var unique = new HashSet<HostManagerComputeProcessIdentity>();
        foreach (var identity in identities)
        {
            if (identity.ProcessId <= 0
                || identity.ProcessStartKey == 0
                || !unique.Add(identity))
            {
                throw new ArgumentException(
                    "A validation admission must contain unique valid process identities.",
                    nameof(identities));
            }
        }

        lock (gate)
        {
            if (fault is not null
                || current is null
                || pendingAdmissionId != Guid.Empty)
            {
                return false;
            }

            try
            {
                var cleanFence = ReadAuditFence();
                ValidateCleanAuditFenceBinding(current, cleanFence);
                if (current.AuditRecords.Count > MaximumAuditRecordCount - identities.Count)
                {
                    throw new InvalidOperationException(
                        "The validation effect-scope audit ledger has no admission capacity.");
                }

                var admissionId = Guid.NewGuid();
                var auditRecords = new HostManagerProcessEffectValidationScopeAuditDocument[
                    identities.Count];
                var allAllowed = true;
                for (var index = 0; index < identities.Count; index++)
                {
                    var identity = identities[index];
                    var (decision, systemError) = EvaluateDecision(
                        snapshot,
                        current,
                        identity);
                    allAllowed &= decision ==
                        HostManagerProcessEffectValidationDecision.AllowedScoped;
                    auditRecords[index] = CreateAuditRecord(
                        current,
                        admissionId,
                        checked((long)current.AuditRecords.Count + index + 1L),
                        family,
                        stage,
                        identity,
                        decision,
                        systemError);
                }

                // The intent is durable before the scope audit changes. The scope's
                // runtime incarnation blocks restart even if this first commit fails.
                PersistAuditFence(
                    CreateIntentAuditFence(
                        current,
                        cleanFence.CommittedHandoffs,
                        admissionId,
                        Guid.Empty,
                        auditRecords),
                    replaceExisting: true);
                var next = CreateNextAuditDocument(current, auditRecords);
                PersistScope(next, replaceExisting: true);
                current = next;
                PersistAuditFence(
                    CreateAuditCommittedFence(
                        next,
                        cleanFence.CommittedHandoffs,
                        admissionId,
                        Guid.Empty,
                        auditRecords),
                    replaceExisting: true);

                if (!allAllowed)
                {
                    PersistAuditFence(
                        CreateCleanAuditFence(next, cleanFence.CommittedHandoffs),
                        replaceExisting: true);
                    return false;
                }

                pendingAdmissionId = admissionId;
                PublishSnapshotLocked();
                permit = new HostManagerProcessEffectValidationAdmissionPermit(
                    this,
                    runtimeIncarnation,
                    next.ScopeId,
                    next.Generation,
                    admissionId,
                    auditRecords[0].Sequence,
                    auditRecords.Length);
                return true;
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-audit-persist-failed:{exception.GetType().Name}";
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    private bool TryDeclareDurableHandoff(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerProcessEffectValidationDurableHandoffDocument handoff,
        IReadOnlyList<HostManagerComputeProcessIdentity> expectedProcesses)
    {
        if (!permit.TryClaimHandoffDeclaration())
        {
            MarkAdmissionUnsettled(permit, "permit-declaration-already-consumed");
            return false;
        }

        lock (gate)
        {
            if ((fault is not null
                    && (!permit.IsRecovery
                        || !string.Equals(
                            fault,
                            PreviousRuntimeIncarnationFault,
                            StringComparison.Ordinal)))
                || current is null
                || !recoveryLedgerAvailable
                || !ReferenceEquals(permit.Owner, this)
                || permit.RuntimeIncarnation != runtimeIncarnation
                || permit.ScopeId != current.ScopeId
                || permit.Generation != current.Generation
                || permit.AdmissionId != pendingAdmissionId)
            {
                fault ??= "effect-scope-admission-handoff-invalid";
                PublishSnapshotLocked();
                return false;
            }

            try
            {
                var fence = ReadAuditFence();
                ValidateAdmissionAuditFenceBinding(
                    current,
                    fence,
                    HostManagerProcessEffectValidationAuditFenceState.AuditCommitted,
                    permit.AdmissionId,
                    permit.FirstAuditSequence,
                    permit.AuditCount,
                    permit.ParentAdmissionId,
                    expectedProcesses);
                var existingTargets = fence.CommittedHandoffs
                    .Where(item => item.DurableHandoff == handoff)
                    .ToArray();
                if (existingTargets.Length != 0)
                {
                    throw new InvalidDataException(
                        "The validation admission durable target was already committed.");
                }
                PersistAuditFence(
                    CreateHandoffDeclaredFence(fence, handoff),
                    replaceExisting: true);
                return true;
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-admission-handoff-declare-failed:{exception.GetType().Name}";
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    private bool TryCommitDurableHandoff(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerProcessEffectValidationDurableHandoffDocument handoff,
        IReadOnlyList<HostManagerComputeProcessIdentity> expectedProcesses)
    {
        if (!permit.TryClaimHandoffCommit())
        {
            MarkAdmissionUnsettled(permit, "permit-commit-before-declaration-or-reused");
            return false;
        }

        lock (gate)
        {
            if ((fault is not null
                    && (!permit.IsRecovery
                        || !string.Equals(
                            fault,
                            PreviousRuntimeIncarnationFault,
                            StringComparison.Ordinal)))
                || current is null
                || !recoveryLedgerAvailable
                || !ReferenceEquals(permit.Owner, this)
                || permit.RuntimeIncarnation != runtimeIncarnation
                || permit.ScopeId != current.ScopeId
                || permit.Generation != current.Generation
                || permit.AdmissionId != pendingAdmissionId)
            {
                fault ??= "effect-scope-admission-handoff-commit-invalid";
                PublishSnapshotLocked();
                return false;
            }

            try
            {
                var fence = ReadAuditFence();
                ValidateAdmissionAuditFenceBinding(
                    current,
                    fence,
                    HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared,
                    permit.AdmissionId,
                    permit.FirstAuditSequence,
                    permit.AuditCount,
                    permit.ParentAdmissionId,
                    expectedProcesses);
                if (fence.DurableHandoff != handoff)
                {
                    throw new InvalidDataException(
                        "The prepared durable target does not match its prior declaration.");
                }
                FinalizeCommittedHandoffLocked(fence);
                return true;
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-admission-handoff-commit-failed:{exception.GetType().Name}";
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    private bool ReconcileDeclaredHandoff(
        HostManagerProcessEffectValidationDurableHandoffKind kind,
        IReadOnlyList<HostManagerProcessEffectValidationDurableHandoffDocument> authoritativeHandoffs)
    {
        lock (gate)
        {
            if (current is null)
            {
                return fault is null;
            }
            if (!recoveryLedgerAvailable)
            {
                return false;
            }

            try
            {
                var fence = ReadAuditFence();
                var authoritative = authoritativeHandoffs.ToArray();
                foreach (var handoff in authoritative)
                {
                    ValidateDurableHandoff(handoff);
                    if (handoff.Kind != kind)
                    {
                        throw new InvalidDataException(
                            "The authoritative validation handoff set contains a foreign kind.");
                    }
                }
                if (authoritative.Distinct().Count() != authoritative.Length)
                {
                    throw new InvalidDataException(
                        "The authoritative validation handoff set contains duplicates.");
                }
                if (loadedPendingReconciliation)
                {
                    if (fence.State !=
                            HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared
                        || fence.DurableHandoff is not { } declared)
                    {
                        throw new InvalidDataException(
                            "The pending validation handoff reconciliation state is invalid.");
                    }
                    if (declared.Kind != kind)
                    {
                        return true;
                    }
                    ValidateAdmissionAuditFenceBinding(
                        current,
                        fence,
                        HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared,
                        fence.AdmissionId,
                        fence.FirstAuditSequence,
                        fence.AdmissionAudits.Count,
                        fence.ParentAdmissionId,
                        fence.AdmissionAudits.Select(static audit =>
                            new HostManagerComputeProcessIdentity(
                                audit.ProcessId,
                                audit.ProcessStartKey)).ToArray());

                    var exactCount = authoritative.Count(item => item == declared);
                    if (exactCount > 1
                        || exactCount == 0 && authoritative.Any(item =>
                            HasSameHandoffPrimaryIdentity(item, declared)))
                    {
                        throw new InvalidDataException(
                            "The declared validation handoff has an ambiguous authoritative target.");
                    }

                    if (exactCount == 0)
                    {
                        PersistAuditFence(
                            CreateCleanAuditFence(current, fence.CommittedHandoffs),
                            replaceExisting: true);
                        pendingAdmissionId = Guid.Empty;
                    }
                    else
                    {
                        FinalizeCommittedHandoffLocked(fence);
                    }
                    loadedPendingReconciliation = false;
                    fault = current.OwnerRuntimeIncarnation == runtimeIncarnation
                        ? null
                        : PreviousRuntimeIncarnationFault;
                    fence = ReadAuditFence();
                }

                if (pendingAdmissionId != Guid.Empty)
                {
                    return false;
                }
                ValidateCleanAuditFenceBinding(current, fence);
                var ledger = fence.CommittedHandoffs;
                var keep = new bool[ledger.Count];
                var byAdmission = new Dictionary<Guid, int>(ledger.Count);
                for (var index = 0; index < ledger.Count; index++)
                {
                    var item = ledger[index];
                    byAdmission.Add(item.AdmissionId, index);
                    if (item.DurableHandoff.Kind != kind)
                    {
                        keep[index] = true;
                        continue;
                    }

                    var exactCount = authoritative.Count(candidate =>
                        candidate == item.DurableHandoff);
                    if (exactCount > 1
                        || exactCount == 0 && authoritative.Any(candidate =>
                            HasSameHandoffPrimaryIdentity(
                                candidate,
                                item.DurableHandoff)))
                    {
                        throw new InvalidDataException(
                            "The committed validation handoff has an ambiguous authoritative target.");
                    }
                    keep[index] = exactCount == 1;
                }

                for (var index = ledger.Count - 1; index >= 0; index--)
                {
                    if (!keep[index]
                        || ledger[index].ParentAdmissionId == Guid.Empty)
                    {
                        continue;
                    }
                    if (!byAdmission.TryGetValue(
                            ledger[index].ParentAdmissionId,
                            out var parentIndex))
                    {
                        throw new InvalidDataException(
                            "The committed validation handoff parent is missing.");
                    }
                    keep[parentIndex] = true;
                }

                if (keep.Any(static item => !item))
                {
                    var retained = ledger.Where((_, index) => keep[index]).ToArray();
                    PersistAuditFence(
                        CreateCleanAuditFence(current, retained),
                        replaceExisting: true);
                }
                PublishSnapshotLocked();
                return true;
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-handoff-reconcile-failed:{exception.GetType().Name}";
                recoveryLedgerAvailable = false;
                PublishSnapshotLocked();
                return false;
            }
        }
    }

    private void FinalizeCommittedHandoffLocked(
        HostManagerProcessEffectValidationAuditFenceDocument declared)
    {
        if (current is null || declared.DurableHandoff is not { } handoff)
        {
            throw new InvalidOperationException(
                "The validation handoff cannot be committed without a durable declaration.");
        }
        var prepared = declared.State ==
            HostManagerProcessEffectValidationAuditFenceState.JournalPrepared
                ? declared
                : CreateJournalPreparedFence(declared);
        if (declared.State !=
            HostManagerProcessEffectValidationAuditFenceState.JournalPrepared)
        {
            PersistAuditFence(prepared, replaceExisting: true);
        }
        var ledger = new List<HostManagerProcessEffectValidationCommittedHandoffDocument>(
            prepared.CommittedHandoffs.Count + 1);
        ledger.AddRange(prepared.CommittedHandoffs);
        if (prepared.CommittedHandoffs.Any(item => item.DurableHandoff == handoff))
        {
            throw new InvalidDataException(
                "The validation handoff target was already committed.");
        }
        ledger.Add(new HostManagerProcessEffectValidationCommittedHandoffDocument(
            prepared.AdmissionId,
            prepared.FirstAuditSequence,
            prepared.AdmissionAudits.Count,
            prepared.ParentAdmissionId,
            handoff));
        PersistAuditFence(
            CreateCleanAuditFence(current, ledger),
            replaceExisting: true);
        pendingAdmissionId = Guid.Empty;
        recoveryLedgerAvailable = true;
        PublishSnapshotLocked();
    }

    private static bool TryCreateNativeHandoff(
        in NativeTransactionJournalIdentity identity,
        out HostManagerProcessEffectValidationDurableHandoffDocument handoff,
        out HostManagerComputeProcessIdentity process)
    {
        handoff = default;
        process = default;
        if (identity.ProcessId == 0
            || identity.ProcessId > int.MaxValue
            || identity.ProcessStartKey == 0
            || identity.ActionId == 0
            || identity.HostSessionIncarnation == 0)
        {
            return false;
        }
        handoff = HostManagerProcessEffectValidationDurableHandoffDocument
            .CreateNativeTransaction(identity);
        process = new(
            checked((int)identity.ProcessId),
            identity.ProcessStartKey);
        return true;
    }

    private static bool TryCreateMemoryCleanupHandoff(
        HostManagerMemoryCleanupAttemptBatch batch,
        ulong attemptGeneration,
        out HostManagerProcessEffectValidationDurableHandoffDocument handoff,
        out IReadOnlyList<HostManagerComputeProcessIdentity> processes)
    {
        handoff = default;
        processes = [];
        if (batch.BatchId == Guid.Empty
            || attemptGeneration == 0
            || batch.AttemptGeneration != attemptGeneration
            || !TryNormalizeMemoryCleanupProcesses(batch.Processes, out var orderedProcesses))
        {
            return false;
        }
        handoff = HostManagerProcessEffectValidationDurableHandoffDocument
            .CreateMemoryCleanupBatch(
                batch.BatchId,
                attemptGeneration,
                orderedProcesses);
        processes = orderedProcesses;
        return true;
    }

    private static bool TryNormalizeMemoryCleanupProcesses(
        IReadOnlySet<HostManagerComputeProcessIdentity>? source,
        out HostManagerComputeProcessIdentity[] processes)
    {
        processes = [];
        if (source is null
            || source.Count is < 1 or > MaximumAllowedProcessCount)
        {
            return false;
        }
        var ordered = source.OrderBy(static item => item.ProcessId)
            .ThenBy(static item => item.ProcessStartKey)
            .ToArray();
        if (ordered.Any(static item => item.ProcessId <= 0 || item.ProcessStartKey == 0)
            || ordered.Distinct().Count() != ordered.Length)
        {
            return false;
        }
        processes = ordered;
        return true;
    }

    private static bool HasSameHandoffPrimaryIdentity(
        HostManagerProcessEffectValidationDurableHandoffDocument left,
        HostManagerProcessEffectValidationDurableHandoffDocument right)
        => left.Kind == right.Kind && (left.Kind ==
            HostManagerProcessEffectValidationDurableHandoffKind.NativeTransactionJournal
                ? left.NativeActionId == right.NativeActionId
                    && left.NativeHostSessionIncarnation == right.NativeHostSessionIncarnation
                    && left.NativeProcessId == right.NativeProcessId
                    && left.NativeProcessStartKey == right.NativeProcessStartKey
                : left.MemoryCleanupBatchId == right.MemoryCleanupBatchId);

    private static bool TryFindCommittedHandoff(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        HostManagerProcessEffectValidationFamily family,
        HostManagerProcessEffectValidationDurableHandoffDocument handoff,
        HostManagerComputeProcessIdentity process,
        out HostManagerProcessEffectValidationCommittedHandoffDocument committed)
    {
        foreach (var candidate in fence.CommittedHandoffs)
        {
            if (candidate.DurableHandoff != handoff
                || candidate.AuditCount != 1
                || candidate.FirstAuditSequence <= 0
                || candidate.FirstAuditSequence > document.AuditRecords.Count)
            {
                continue;
            }
            var audit = document.AuditRecords[
                checked((int)candidate.FirstAuditSequence - 1)];
            if (audit.AdmissionId == candidate.AdmissionId
                && audit.Family == family
                && audit.ProcessId == process.ProcessId
                && audit.ProcessStartKey == process.ProcessStartKey
                && audit.Decision is HostManagerProcessEffectValidationDecision.AllowedScoped
                    or HostManagerProcessEffectValidationDecision
                        .AllowedPriorHandoffRecovery)
            {
                committed = candidate;
                return true;
            }
        }
        committed = default;
        return false;
    }

    private (HostManagerProcessEffectValidationDecision Decision, int SystemError)
        EvaluateDecision(
        in HostManagerProcessEffectValidationCycleSnapshot snapshot,
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerComputeProcessIdentity identity)
    {
        if (snapshot.ScopeId != document.ScopeId
            || snapshot.Generation != document.Generation)
        {
            return (HostManagerProcessEffectValidationDecision.DeniedSnapshotMismatch, 0);
        }
        if (IsExpired(document))
        {
            return (HostManagerProcessEffectValidationDecision.DeniedExpired, 0);
        }
        if (snapshot.AllowedProcesses is null
            || !snapshot.AllowedProcesses.Contains(identity))
        {
            return (HostManagerProcessEffectValidationDecision.DeniedNotAllowlisted, 0);
        }

        var membership = membershipProbe.Probe(document.JobName, identity);
        var decision = membership.Status switch
        {
            WindowsJobMembershipStatus.ExactMember =>
                HostManagerProcessEffectValidationDecision.AllowedScoped,
            WindowsJobMembershipStatus.IdentityMismatch =>
                HostManagerProcessEffectValidationDecision.DeniedIdentityMismatch,
            WindowsJobMembershipStatus.NotMember =>
                HostManagerProcessEffectValidationDecision.DeniedNotJobMember,
            _ => HostManagerProcessEffectValidationDecision.DeniedMembershipUnknown
        };
        return (decision, membership.SystemError);
    }

    private HostManagerProcessEffectValidationScopeAuditDocument CreateAuditRecord(
        HostManagerProcessEffectValidationScopeDocument document,
        Guid admissionId,
        long sequence,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        HostManagerComputeProcessIdentity identity,
        HostManagerProcessEffectValidationDecision decision,
        int systemError)
    {
        if (document.AuditRecords.Count >= MaximumAuditRecordCount)
        {
            throw new InvalidOperationException(
                "The validation effect-scope audit ledger is full.");
        }
        return new(
            sequence,
            timeProvider.GetUtcNow().UtcTicks,
            admissionId,
            family,
            stage,
            identity.ProcessId,
            identity.ProcessStartKey,
            decision,
            systemError);
    }

    private static HostManagerProcessEffectValidationScopeDocument CreateNextAuditDocument(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> auditRecords)
    {
        var records = new List<HostManagerProcessEffectValidationScopeAuditDocument>(
            document.AuditRecords.Count + auditRecords.Count);
        records.AddRange(document.AuditRecords);
        records.AddRange(auditRecords);
        var next = document with
        {
            AuditRecords = records,
            ChecksumSha256 = string.Empty
        };
        next = next with { ChecksumSha256 = ComputeChecksum(next) };
        return next;
    }

    private HostManagerProcessEffectValidationScopeStatus CreateStatus(
        HostManagerProcessEffectValidationScopeState state,
        HostManagerProcessEffectValidationScopeDocument? document,
        string? failure)
        => new(
            SchemaVersion,
            Contract,
            MapState(state),
            document?.ScopeId,
            document?.RunNonce,
            document is null
                ? null
                : new DateTimeOffset(document.ExpiresAtUtcTicks, TimeSpan.Zero),
            document?.JobNameSha256,
            document?.Generation ?? 0,
            document?.AllowedProcesses.Count ?? 0,
            document?.ChecksumSha256,
            failure,
            document?.AuditRecords.Select(static record =>
                new HostManagerProcessEffectValidationScopeAuditRecord(
                    record.Sequence,
                    new DateTimeOffset(record.RecordedAtUtcTicks, TimeSpan.Zero),
                    MapFamily(record.Family),
                    record.Stage,
                    record.ProcessId,
                    checked((long)record.ProcessStartKey),
                    MapDecision(record.Decision),
                    record.SystemError)).ToArray()
                ?? []);

    private HostManagerProcessEffectValidationScopeStatus CreateClosedStatus(
        HostManagerProcessEffectValidationScopeDocument document)
        => CreateStatus(
            HostManagerProcessEffectValidationScopeState.Active,
            document,
            failure: null) with
        {
            State = "closed"
        };

    private void InitializeOrValidateStoreBoundaryLocked()
    {
        var manifestExists = TryReadBoundedDocument(
            authorityManifestPath,
            MaximumAuthorityManifestBytes,
            "effect-scope authority manifest",
            out HostManagerProcessEffectValidationAuthorityManifest? manifest);
        if (!manifestExists || manifest is null)
        {
            if (WindowsProcessEffectValidationScopeStorage.ProbeDirectoryExistsExact(
                    authorityManifestPath)
                || WindowsProcessEffectValidationScopeStorage.ProbeDirectoryExistsExact(
                    statePath)
                || PathExistsExact(statePath)
                || PathExistsExact(auditFencePath))
            {
                throw new InvalidDataException(
                    "Validation effect-scope storage exists without its permanent authority manifest.");
            }

            var storeId = Guid.NewGuid();
            var initializing = CreateAuthorityManifest(
                storeId,
                HostManagerProcessEffectValidationAuthorityManifestState.Initializing);
            PersistAuthorityManifest(initializing, replaceExisting: false);
            using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
                       statePath,
                       create: true))
            {
            }
            PersistAuthorityManifest(
                CreateAuthorityManifest(
                    storeId,
                    HostManagerProcessEffectValidationAuthorityManifestState.Ready),
                replaceExisting: true);
            return;
        }

        ValidateAuthorityManifest(manifest);
        switch (manifest.State)
        {
            case HostManagerProcessEffectValidationAuthorityManifestState.Initializing:
                if (PathExistsExact(statePath) || PathExistsExact(auditFencePath))
                {
                    throw new InvalidDataException(
                        "An initializing validation effect-scope store contains lifecycle documents.");
                }
                using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
                           statePath,
                           create: true))
                {
                }
                PersistAuthorityManifest(
                    CreateAuthorityManifest(
                        manifest.StoreId,
                        HostManagerProcessEffectValidationAuthorityManifestState.Ready),
                    replaceExisting: true);
                break;
            case HostManagerProcessEffectValidationAuthorityManifestState.Ready:
                using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
                           statePath,
                           create: false))
                {
                }
                break;
            default:
                throw new InvalidDataException(
                    "The validation effect-scope authority manifest has an unknown state.");
        }
    }

    private void ValidateReadyStoreBoundaryLocked()
    {
        if (!TryReadBoundedDocument(
                authorityManifestPath,
                MaximumAuthorityManifestBytes,
                "effect-scope authority manifest",
                out HostManagerProcessEffectValidationAuthorityManifest? manifest)
            || manifest is null)
        {
            throw new InvalidDataException(
                "The validation effect-scope authority manifest is missing.");
        }
        ValidateAuthorityManifest(manifest);
        if (manifest.State !=
            HostManagerProcessEffectValidationAuthorityManifestState.Ready)
        {
            throw new InvalidDataException(
                "The validation effect-scope authority manifest is not ready.");
        }
        using var _ = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
            statePath,
            create: false);
    }

    private HostManagerProcessEffectValidationAuthorityManifest CreateAuthorityManifest(
        Guid storeId,
        HostManagerProcessEffectValidationAuthorityManifestState state)
    {
        var manifest = new HostManagerProcessEffectValidationAuthorityManifest(
            AuthorityManifestSchemaVersion,
            AuthorityManifestContract,
            storeId,
            ComputeStatePathIdentitySha256(statePath),
            state,
            string.Empty);
        return manifest with
        {
            ChecksumSha256 = ComputeAuthorityManifestChecksum(manifest)
        };
    }

    private void PersistAuthorityManifest(
        HostManagerProcessEffectValidationAuthorityManifest manifest,
        bool replaceExisting)
    {
        ValidateAuthorityManifest(manifest);
        PersistDocument(
            manifest,
            authorityManifestPath,
            MaximumAuthorityManifestBytes,
            replaceExisting,
            createDirectory: true);
    }

    private void ValidateAuthorityManifest(
        HostManagerProcessEffectValidationAuthorityManifest manifest)
    {
        if (manifest.SchemaVersion != AuthorityManifestSchemaVersion
            || !string.Equals(
                manifest.Contract,
                AuthorityManifestContract,
                StringComparison.Ordinal)
            || manifest.StoreId == Guid.Empty
            || !string.Equals(
                manifest.StatePathSha256,
                ComputeStatePathIdentitySha256(statePath),
                StringComparison.Ordinal)
            || manifest.State is <
                    HostManagerProcessEffectValidationAuthorityManifestState.Initializing
                or > HostManagerProcessEffectValidationAuthorityManifestState.Ready
            || !IsSha256(manifest.ChecksumSha256)
            || !string.Equals(
                manifest.ChecksumSha256,
                ComputeAuthorityManifestChecksum(manifest),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope authority manifest is invalid.");
        }
    }

    private void LoadInitialState()
    {
        lock (gate)
        {
            try
            {
                InitializeOrValidateStoreBoundaryLocked();
                var stateExists = TryReadBoundedDocument(
                    statePath,
                    MaximumDocumentBytes,
                    "effect-scope",
                    out HostManagerProcessEffectValidationScopeDocument? document);
                var auditFenceExists = TryReadBoundedDocument(
                    auditFencePath,
                    MaximumAuditFenceBytes,
                    "effect-scope admission-fence",
                    out HostManagerProcessEffectValidationAuditFenceDocument? auditFence);
                if (!stateExists && !auditFenceExists)
                {
                    WindowsProcessEffectValidationScopeStorage
                        .ValidateExistingDirectoryIfPresent(statePath);
                    return;
                }
                if (!stateExists || document is null)
                {
                    if (auditFenceExists && auditFence is not null)
                    {
                        ValidateAuditFenceDocument(auditFence);
                        ValidateOrphanLifecycleAuditFence(auditFence);
                        committer.DeleteExact(auditFencePath);
                        return;
                    }
                    throw new InvalidDataException(
                        "The validation effect-scope audit fence is orphaned.");
                }
                ValidateDocument(document);
                current = document;
                if (!auditFenceExists || auditFence is null)
                {
                    throw new InvalidDataException(
                        "The validation effect-scope audit fence is missing.");
                }
                ValidateAuditFenceDocument(auditFence);
                ValidateAuditFenceScopeBinding(document, auditFence);
                recoveryLedgerAvailable = true;
                switch (auditFence.State)
                {
                    case HostManagerProcessEffectValidationAuditFenceState.Clean:
                        ValidateCleanAuditFenceBinding(document, auditFence);
                        break;
                    case HostManagerProcessEffectValidationAuditFenceState.Intent:
                    case HostManagerProcessEffectValidationAuditFenceState.AuditCommitted:
                        ValidatePreHandoffAuditFenceBinding(document, auditFence);
                        PersistAuditFence(
                            CreateCleanAuditFence(
                                document,
                                auditFence.CommittedHandoffs),
                            replaceExisting: true);
                        break;
                    case HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared:
                        ValidateLoadedEffectAdmissionFenceBinding(document, auditFence);
                        pendingAdmissionId = auditFence.AdmissionId;
                        loadedPendingReconciliation = true;
                        break;
                    case HostManagerProcessEffectValidationAuditFenceState.JournalPrepared:
                        ValidateLoadedEffectAdmissionFenceBinding(document, auditFence);
                        pendingAdmissionId = auditFence.AdmissionId;
                        FinalizeCommittedHandoffLocked(auditFence);
                        break;
                    case HostManagerProcessEffectValidationAuditFenceState.Opening:
                        ValidateOpeningAuditFenceBinding(document, auditFence);
                        fault = OpeningCleanupPendingFault;
                        recoveryLedgerAvailable = false;
                        CompleteLifecycleDeletionLocked();
                        return;
                    case HostManagerProcessEffectValidationAuditFenceState.Closing:
                        ValidateClosingAuditFenceBinding(document, auditFence);
                        fault = ClosingPendingFault;
                        recoveryLedgerAvailable = false;
                        CompleteCloseLifecycleLocked(document);
                        return;
                    case HostManagerProcessEffectValidationAuditFenceState.Closed:
                        ValidateClosedAuditFenceBinding(document, auditFence);
                        ActivateClosedReceiptLocked(document);
                        return;
                    case HostManagerProcessEffectValidationAuditFenceState.RetiringClosed:
                        ValidateRetiringClosedAuditFenceBinding(document, auditFence);
                        committer.DeleteExact(statePath);
                        committer.DeleteExact(auditFencePath);
                        ResetLifecycleStateLocked();
                        return;
                    default:
                        throw new InvalidDataException(
                            "The validation effect-scope audit fence has an unknown state.");
                }
                if (document.OwnerRuntimeIncarnation != runtimeIncarnation)
                {
                    fault = PreviousRuntimeIncarnationFault;
                }
                else if (pendingAdmissionId != Guid.Empty)
                {
                    fault = "effect-scope-handoff-reconciliation-required";
                }
            }
            catch (Exception exception)
            {
                fault = $"effect-scope-load-failed:{exception.GetType().Name}";
                recoveryLedgerAvailable = false;
            }
            finally
            {
                PublishSnapshotLocked();
            }
        }
    }

    private void PublishSnapshotLocked()
    {
        if (current is null)
        {
            Volatile.Write(
                ref publishedSnapshot,
                fault is null && pendingAdmissionId == Guid.Empty
                    ? HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped
                    : new HostManagerProcessEffectValidationCycleSnapshot(
                        HostManagerProcessEffectValidationScopeState.FaultedBlocked,
                        Guid.Empty,
                        0,
                        null,
                        DateTimeOffset.MinValue,
                        null,
                        null));
            return;
        }

        var state = fault is not null || pendingAdmissionId != Guid.Empty
            ? HostManagerProcessEffectValidationScopeState.FaultedBlocked
            : HostManagerProcessEffectValidationScopeState.Active;
        Volatile.Write(
            ref publishedSnapshot,
            new HostManagerProcessEffectValidationCycleSnapshot(
                state,
                current.ScopeId,
                current.Generation,
                current.JobName,
                new DateTimeOffset(current.ExpiresAtUtcTicks, TimeSpan.Zero),
                current.AllowedProcesses
                    .Select(static item => item.ToIdentity())
                    .ToFrozenSet(),
                state == HostManagerProcessEffectValidationScopeState.Active
                    ? timeProvider
                    : null,
                state == HostManagerProcessEffectValidationScopeState.Active
                    && scopedAutomaticMemoryCleanupAllowed,
                state == HostManagerProcessEffectValidationScopeState.Active
                    && scopedNonAdaptedMemoryTransactionAllowed));
    }

    private bool CanUseRecoveryAuthorityLocked()
        => fault is null
            || string.Equals(
                fault,
                PreviousRuntimeIncarnationFault,
                StringComparison.Ordinal);

    private HostManagerProcessEffectValidationScopeDocument ReadScopeDocument()
    {
        var document = ReadBoundedDocument<HostManagerProcessEffectValidationScopeDocument>(
            statePath,
            MaximumDocumentBytes,
            "effect-scope");
        ValidateDocument(document);
        return document;
    }

    private HostManagerProcessEffectValidationAuditFenceDocument ReadAuditFence()
    {
        var document = ReadBoundedDocument<HostManagerProcessEffectValidationAuditFenceDocument>(
            auditFencePath,
            MaximumAuditFenceBytes,
            "effect-scope audit-fence");
        ValidateAuditFenceDocument(document);
        return document;
    }

    private static T ReadBoundedDocument<T>(
        string path,
        int maximumBytes,
        string documentName)
    {
        using var directory = WindowsProcessEffectValidationScopeStorage
            .AcquireDirectory(path, create: false);
        using var stream = WindowsProcessEffectValidationScopeStorage.OpenReadExact(
            directory,
            path);
        var length = stream.Length;
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The validation {documentName} document is outside its storage boundary.");
        }
        var image = new byte[checked((int)length)];
        stream.ReadExactly(image);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The validation {documentName} document changed while it was read.");
        }
        return JsonSerializer.Deserialize<T>(image, JsonOptions)
            ?? throw new InvalidDataException(
                $"The validation {documentName} document is empty.");
    }

    private static bool TryReadBoundedDocument<T>(
        string path,
        int maximumBytes,
        string documentName,
        out T? document)
    {
        try
        {
            if (!WindowsProcessEffectValidationScopeStorage.ProbeFileExistsExact(path))
            {
                document = default;
                return false;
            }
            document = ReadBoundedDocument<T>(path, maximumBytes, documentName);
            return true;
        }
        catch (FileNotFoundException)
        {
            document = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            document = default;
            return false;
        }
    }

    private static bool PathExistsExact(string path)
        => WindowsProcessEffectValidationScopeStorage.ProbeFileExistsExact(path);

    private void PersistScope(
        HostManagerProcessEffectValidationScopeDocument document,
        bool replaceExisting)
    {
        ValidateDocument(document);
        PersistDocument(
            document,
            statePath,
            MaximumDocumentBytes,
            replaceExisting,
            createDirectory: false);
    }

    private void PersistAuditFence(
        HostManagerProcessEffectValidationAuditFenceDocument document,
        bool replaceExisting)
    {
        ValidateAuditFenceDocument(document);
        PersistDocument(
            document,
            auditFencePath,
            MaximumAuditFenceBytes,
            replaceExisting,
            createDirectory: false);
    }

    private void PersistDocument<T>(
        T document,
        string canonicalPath,
        int maximumBytes,
        bool replaceExisting,
        bool createDirectory)
    {
        using var directory = WindowsProcessEffectValidationScopeStorage
            .AcquireDirectory(canonicalPath, createDirectory);
        var temporaryPath = $"{canonicalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var image = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (image.Length is <= 0 || image.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    "The validation durable document exceeds its byte budget.");
            }
            using (new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough
                }))
            {
            }
            WindowsProcessEffectValidationScopeStorage.ApplySecureFileAcl(
                directory,
                temporaryPath);
            using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough
                }))
            {
                stream.Write(image);
                stream.Flush(flushToDisk: true);
            }
            if (replaceExisting)
            {
                WindowsProcessEffectValidationScopeStorage.VerifyCommittedFile(
                    directory,
                    canonicalPath);
            }
            committer.Commit(temporaryPath, canonicalPath, replaceExisting);
            WindowsProcessEffectValidationScopeStorage.VerifyCommittedFile(
                directory,
                canonicalPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument CreateCleanAuditFence(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument>
            committedHandoffs)
    {
        var fence = new HostManagerProcessEffectValidationAuditFenceDocument(
            AuditFenceSchemaVersion,
            AuditFenceContract,
            document.ScopeId,
            document.RunNonce,
            document.OwnerRuntimeIncarnation,
            document.Generation,
            HostManagerProcessEffectValidationAuditFenceState.Clean,
            document.AuditRecords.Count,
            document.ChecksumSha256,
            Guid.Empty,
            Guid.Empty,
            0,
            [],
            null,
            committedHandoffs,
            string.Empty);
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateOpeningAuditFence(HostManagerProcessEffectValidationScopeDocument document)
    {
        var fence = CreateCleanAuditFence(document, []) with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.Opening,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateClosingAuditFence(HostManagerProcessEffectValidationScopeDocument document)
    {
        var fence = CreateCleanAuditFence(document, []) with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.Closing,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateClosedAuditFence(HostManagerProcessEffectValidationScopeDocument document)
    {
        var fence = CreateCleanAuditFence(document, []) with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.Closed,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateRetiringClosedAuditFence(
            HostManagerProcessEffectValidationScopeDocument document)
    {
        var fence = CreateCleanAuditFence(document, []) with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.RetiringClosed,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument CreateIntentAuditFence(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument>
            committedHandoffs,
        Guid admissionId,
        Guid parentAdmissionId,
        IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> auditRecords)
    {
        var fence = new HostManagerProcessEffectValidationAuditFenceDocument(
            AuditFenceSchemaVersion,
            AuditFenceContract,
            document.ScopeId,
            document.RunNonce,
            document.OwnerRuntimeIncarnation,
            document.Generation,
            HostManagerProcessEffectValidationAuditFenceState.Intent,
            document.AuditRecords.Count,
            document.ChecksumSha256,
            admissionId,
            parentAdmissionId,
            auditRecords[0].Sequence,
            auditRecords,
            null,
            committedHandoffs,
            string.Empty);
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateAuditCommittedFence(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument>
            committedHandoffs,
        Guid admissionId,
        Guid parentAdmissionId,
        IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> auditRecords)
    {
        var fence = new HostManagerProcessEffectValidationAuditFenceDocument(
            AuditFenceSchemaVersion,
            AuditFenceContract,
            document.ScopeId,
            document.RunNonce,
            document.OwnerRuntimeIncarnation,
            document.Generation,
            HostManagerProcessEffectValidationAuditFenceState.AuditCommitted,
            document.AuditRecords.Count,
            document.ChecksumSha256,
            admissionId,
            parentAdmissionId,
            auditRecords[0].Sequence,
            auditRecords,
            null,
            committedHandoffs,
            string.Empty);
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateHandoffDeclaredFence(
        HostManagerProcessEffectValidationAuditFenceDocument auditCommitted,
        HostManagerProcessEffectValidationDurableHandoffDocument handoff)
    {
        var fence = auditCommitted with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared,
            DurableHandoff = handoff,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument
        CreateJournalPreparedFence(
        HostManagerProcessEffectValidationAuditFenceDocument handoffDeclared)
    {
        var fence = handoffDeclared with
        {
            State = HostManagerProcessEffectValidationAuditFenceState.JournalPrepared,
            ChecksumSha256 = string.Empty
        };
        return fence with { ChecksumSha256 = ComputeAuditFenceChecksum(fence) };
    }

    private static void ValidateCleanAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Clean,
            requireEmptyHandoffs: false);

    private static void ValidateClosingAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Closing,
            requireEmptyHandoffs: true);

    private static void ValidateClosedAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Closed,
            requireEmptyHandoffs: true);

    private static void ValidateRetiringClosedAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.RetiringClosed,
            requireEmptyHandoffs: true);

    private static void ValidateOpeningAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Opening,
            requireEmptyHandoffs: true);

    private static void ValidateSettledAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        HostManagerProcessEffectValidationAuditFenceState expectedState,
        bool requireEmptyHandoffs)
    {
        if (fence.State != expectedState
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.AdmissionId != Guid.Empty
            || fence.ParentAdmissionId != Guid.Empty
            || fence.FirstAuditSequence != 0
            || fence.AdmissionAudits.Count != 0
            || fence.DurableHandoff is not null
            || fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.Generation != document.Generation
            || fence.CommittedAuditRecordCount != document.AuditRecords.Count
            || requireEmptyHandoffs && fence.CommittedHandoffs.Count != 0
            || !string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope audit fence does not match the committed scope document.");
        }
    }

    private static void ValidateOrphanLifecycleAuditFence(
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.State is not HostManagerProcessEffectValidationAuditFenceState.Opening
                and not HostManagerProcessEffectValidationAuditFenceState.RetiringClosed
            || fence.AdmissionId != Guid.Empty
            || fence.ParentAdmissionId != Guid.Empty
            || fence.FirstAuditSequence != 0
            || fence.AdmissionAudits.Count != 0
            || fence.DurableHandoff is not null
            || fence.CommittedHandoffs.Count != 0)
        {
            throw new InvalidDataException(
                "Only a settled lifecycle fence may survive its scope document.");
        }
    }

    private static void ValidateAdmissionAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        HostManagerProcessEffectValidationAuditFenceState expectedState,
        Guid admissionId,
        long firstAuditSequence,
        int auditCount,
        Guid parentAdmissionId,
        IReadOnlyList<HostManagerComputeProcessIdentity> expectedProcesses)
    {
        if (fence.State != expectedState
            || fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.Generation != document.Generation
            || fence.CommittedAuditRecordCount != document.AuditRecords.Count
            || !string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal)
            || fence.AdmissionId != admissionId
            || fence.ParentAdmissionId != parentAdmissionId
            || fence.FirstAuditSequence != firstAuditSequence
            || fence.AdmissionAudits.Count != auditCount
            || expectedProcesses.Count != auditCount)
        {
            throw new InvalidDataException(
                "The validation admission fence does not match its permit or scope document.");
        }

        var firstIndex = checked(document.AuditRecords.Count - auditCount);
        var expectedDecision = parentAdmissionId == Guid.Empty
            ? HostManagerProcessEffectValidationDecision.AllowedScoped
            : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
        var actualProcesses = new HashSet<HostManagerComputeProcessIdentity>();
        for (var index = 0; index < auditCount; index++)
        {
            var audit = fence.AdmissionAudits[index];
            if (audit != document.AuditRecords[firstIndex + index]
                || audit.AdmissionId != admissionId
                || audit.Decision != expectedDecision
                || !actualProcesses.Add(new(
                    audit.ProcessId,
                    audit.ProcessStartKey)))
            {
                throw new InvalidDataException(
                    "The validation admission fence audit tail is not exact.");
            }
        }
        if (!actualProcesses.SetEquals(expectedProcesses))
        {
            throw new InvalidDataException(
                "The validation admission handoff process set does not match its audit.");
        }
    }

    private static void ValidateAuditFenceScopeBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.Generation != document.Generation)
        {
            throw new InvalidDataException(
                "The validation audit fence belongs to another scope.");
        }
        ValidateCommittedHandoffLedger(document, fence.CommittedHandoffs);
    }

    private static void ValidatePreHandoffAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.State == HostManagerProcessEffectValidationAuditFenceState.AuditCommitted)
        {
            ValidatePendingAuditTail(document, fence, requireCommittedDocument: true);
            return;
        }
        if (fence.State != HostManagerProcessEffectValidationAuditFenceState.Intent)
        {
            throw new InvalidDataException(
                "The validation audit fence is not a pre-handoff state.");
        }

        var beforeWrite = fence.CommittedAuditRecordCount == document.AuditRecords.Count
            && string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal);
        if (beforeWrite)
        {
            return;
        }
        if (document.AuditRecords.Count !=
                fence.CommittedAuditRecordCount + fence.AdmissionAudits.Count)
        {
            throw new InvalidDataException(
                "The intent fence cannot be reconciled with the scope audit ledger.");
        }
        ValidatePendingAuditTail(document, fence, requireCommittedDocument: false);
    }

    private static void ValidateLoadedEffectAdmissionFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        var processes = fence.AdmissionAudits.Select(static audit =>
            new HostManagerComputeProcessIdentity(
                audit.ProcessId,
                audit.ProcessStartKey)).ToArray();
        ValidateAdmissionAuditFenceBinding(
            document,
            fence,
            fence.State,
            fence.AdmissionId,
            fence.FirstAuditSequence,
            fence.AdmissionAudits.Count,
            fence.ParentAdmissionId,
            processes);
    }

    private static void ValidatePendingAuditTail(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        bool requireCommittedDocument)
    {
        if (requireCommittedDocument
            && (fence.CommittedAuditRecordCount != document.AuditRecords.Count
                || !string.Equals(
                    fence.CommittedScopeDocumentSha256,
                    document.ChecksumSha256,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The committed admission fence does not bind the scope document.");
        }
        var firstIndex = checked(document.AuditRecords.Count - fence.AdmissionAudits.Count);
        if (firstIndex < 0)
        {
            throw new InvalidDataException(
                "The pending admission audit range exceeds the scope ledger.");
        }
        for (var index = 0; index < fence.AdmissionAudits.Count; index++)
        {
            if (fence.AdmissionAudits[index] != document.AuditRecords[firstIndex + index])
            {
                throw new InvalidDataException(
                    "The pending admission audit range is not the committed ledger tail.");
            }
        }
    }

    private static void ValidateAuditFenceDocument(
        HostManagerProcessEffectValidationAuditFenceDocument document)
    {
        if (document.SchemaVersion != AuditFenceSchemaVersion
            || !string.Equals(document.Contract, AuditFenceContract, StringComparison.Ordinal)
            || document.ScopeId == Guid.Empty
            || document.RunNonce == Guid.Empty
            || document.OwnerRuntimeIncarnation == Guid.Empty
            || document.Generation <= 0
            || document.State is < HostManagerProcessEffectValidationAuditFenceState.Clean
                or > HostManagerProcessEffectValidationAuditFenceState.RetiringClosed
            || document.CommittedAuditRecordCount is < 0 or > MaximumAuditRecordCount
            || !IsSha256(document.CommittedScopeDocumentSha256)
            || document.AdmissionAudits is null
            || document.CommittedHandoffs is null
            || document.CommittedHandoffs.Count > MaximumAuditRecordCount
            || !IsSha256(document.ChecksumSha256)
            || !string.Equals(
                document.ChecksumSha256,
                ComputeAuditFenceChecksum(document),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope audit fence failed its hash-closed contract.");
        }

        if (document.State is HostManagerProcessEffectValidationAuditFenceState.Clean
            or HostManagerProcessEffectValidationAuditFenceState.Opening
            or HostManagerProcessEffectValidationAuditFenceState.Closing
            or HostManagerProcessEffectValidationAuditFenceState.Closed
            or HostManagerProcessEffectValidationAuditFenceState.RetiringClosed)
        {
            if (document.AdmissionId != Guid.Empty
                || document.ParentAdmissionId != Guid.Empty
                || document.FirstAuditSequence != 0
                || document.AdmissionAudits.Count != 0
                || document.DurableHandoff is not null
                || (document.State is HostManagerProcessEffectValidationAuditFenceState.Opening
                        or HostManagerProcessEffectValidationAuditFenceState.Closing
                        or HostManagerProcessEffectValidationAuditFenceState.Closed
                        or HostManagerProcessEffectValidationAuditFenceState.RetiringClosed)
                    && document.CommittedHandoffs.Count != 0)
            {
                throw new InvalidDataException(
                    "A settled validation effect-scope audit fence contains pending work.");
            }
            return;
        }

        if (document.AdmissionId == Guid.Empty
            || document.AdmissionAudits.Count is < 1 or > MaximumAllowedProcessCount
            || document.FirstAuditSequence <= 0
            || document.State == HostManagerProcessEffectValidationAuditFenceState.Intent
                && document.CommittedAuditRecordCount >
                    MaximumAuditRecordCount - document.AdmissionAudits.Count
            || document.State != HostManagerProcessEffectValidationAuditFenceState.Intent
                && document.CommittedAuditRecordCount < document.AdmissionAudits.Count)
        {
            throw new InvalidDataException(
                "A non-clean validation admission fence is incomplete.");
        }
        var expectedFirst = document.State ==
            HostManagerProcessEffectValidationAuditFenceState.Intent
            ? checked(document.CommittedAuditRecordCount + 1L)
            : checked(document.CommittedAuditRecordCount -
                document.AdmissionAudits.Count + 1L);
        if (document.FirstAuditSequence != expectedFirst)
        {
            throw new InvalidDataException(
                "The validation admission fence audit range is not contiguous.");
        }
        for (var index = 0; index < document.AdmissionAudits.Count; index++)
        {
            var audit = document.AdmissionAudits[index];
            ValidateAuditRecord(audit, checked(expectedFirst + index));
            if (audit.AdmissionId != document.AdmissionId)
            {
                throw new InvalidDataException(
                    "The validation admission fence contains a foreign audit record.");
            }
        }

        if (document.ParentAdmissionId != Guid.Empty
            && !document.CommittedHandoffs.Any(item =>
                item.AdmissionId == document.ParentAdmissionId))
        {
            throw new InvalidDataException(
                "A recovery admission does not name a prior committed handoff.");
        }

        if (document.State is HostManagerProcessEffectValidationAuditFenceState
                .HandoffDeclared
            or HostManagerProcessEffectValidationAuditFenceState.JournalPrepared)
        {
            ValidateDurableHandoff(document.DurableHandoff);
            var expectedDecision = document.ParentAdmissionId == Guid.Empty
                ? HostManagerProcessEffectValidationDecision.AllowedScoped
                : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
            if (document.AdmissionAudits.Any(audit => audit.Decision != expectedDecision))
            {
                throw new InvalidDataException(
                    "A declared handoff contains a non-effect admission decision.");
            }
        }
        else if (document.DurableHandoff is not null)
        {
            throw new InvalidDataException(
                "A validation admission fence exposes a handoff before declaration.");
        }


        var committedAdmissionIds = new HashSet<Guid>();
        var committedTargets = new HashSet<
            HostManagerProcessEffectValidationDurableHandoffDocument>();
        foreach (var committed in document.CommittedHandoffs)
        {
            ValidateDurableHandoff(committed.DurableHandoff);
            if (committed.AdmissionId == Guid.Empty
                || committed.FirstAuditSequence <= 0
                || committed.AuditCount is < 1 or > MaximumAllowedProcessCount
                || !committedAdmissionIds.Add(committed.AdmissionId)
                || !committedTargets.Add(committed.DurableHandoff)
                || committed.ParentAdmissionId != Guid.Empty
                    && !committedAdmissionIds.Contains(committed.ParentAdmissionId))
            {
                throw new InvalidDataException(
                    "The committed validation handoff ledger is not monotonic and unique.");
            }
        }
    }

    private static void ValidateCommittedHandoffLedger(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument> ledger)
    {
        var priorAdmissions = new HashSet<Guid>();
        foreach (var committed in ledger)
        {
            var firstIndex = checked((int)committed.FirstAuditSequence - 1);
            if (firstIndex < 0
                || committed.AuditCount is < 1 or > MaximumAllowedProcessCount
                || firstIndex > document.AuditRecords.Count - committed.AuditCount)
            {
                throw new InvalidDataException(
                    "A committed validation handoff names an invalid audit range.");
            }
            var expectedDecision = committed.ParentAdmissionId == Guid.Empty
                ? HostManagerProcessEffectValidationDecision.AllowedScoped
                : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
            var processes = new HashSet<HostManagerComputeProcessIdentity>();
            HostManagerProcessEffectValidationFamily? family = null;
            for (var offset = 0; offset < committed.AuditCount; offset++)
            {
                var audit = document.AuditRecords[firstIndex + offset];
                family ??= audit.Family;
                if (audit.AdmissionId != committed.AdmissionId
                    || audit.Sequence != committed.FirstAuditSequence + offset
                    || audit.Decision != expectedDecision
                    || audit.Family != family
                    || !processes.Add(new(audit.ProcessId, audit.ProcessStartKey)))
                {
                    throw new InvalidDataException(
                        "A committed validation handoff is not bound to one exact audit range.");
                }
            }
            if (committed.ParentAdmissionId != Guid.Empty
                && !priorAdmissions.Contains(committed.ParentAdmissionId))
            {
                throw new InvalidDataException(
                    "A committed recovery handoff does not follow its parent admission.");
            }
            if (committed.DurableHandoff.Kind ==
                    HostManagerProcessEffectValidationDurableHandoffKind.NativeTransactionJournal
                && (committed.AuditCount != 1
                    || !processes.Contains(new(
                        checked((int)committed.DurableHandoff.NativeProcessId),
                        committed.DurableHandoff.NativeProcessStartKey))))
            {
                throw new InvalidDataException(
                    "A native validation handoff is not bound to its exact process audit.");
            }
            if (committed.DurableHandoff.Kind ==
                    HostManagerProcessEffectValidationDurableHandoffKind.MemoryCleanupAttemptBatch
                && (committed.AuditCount !=
                        committed.DurableHandoff.MemoryCleanupProcessCount
                    || !string.Equals(
                        HostManagerProcessEffectValidationDurableHandoffDocument
                            .ComputeMemoryCleanupProcessSetSha256(processes),
                        committed.DurableHandoff.MemoryCleanupProcessSetSha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "A cleanup validation handoff is not bound to its exact process audit set.");
            }
            priorAdmissions.Add(committed.AdmissionId);
        }
    }

    private static void ValidateDurableHandoff(
        HostManagerProcessEffectValidationDurableHandoffDocument? handoff)
    {
        if (handoff is not { } value
            || value.Kind is < HostManagerProcessEffectValidationDurableHandoffKind
                    .NativeTransactionJournal
                or > HostManagerProcessEffectValidationDurableHandoffKind
                    .MemoryCleanupAttemptBatch)
        {
            throw new InvalidDataException(
                "The validation admission durable handoff is invalid.");
        }

        if (value.Kind == HostManagerProcessEffectValidationDurableHandoffKind
                .NativeTransactionJournal)
        {
            if (value.MemoryCleanupBatchId != Guid.Empty
                || value.MemoryCleanupAttemptGeneration != 0
                || value.MemoryCleanupProcessCount != 0
                || !string.IsNullOrEmpty(value.MemoryCleanupProcessSetSha256)
                || value.NativeProcessId == 0
                || value.NativeProcessId > int.MaxValue
                || value.NativeProcessStartKey == 0
                || value.NativeActionId == 0
                || value.NativeHostSessionIncarnation == 0)
            {
                throw new InvalidDataException(
                    "The validation admission native-journal handoff is incomplete.");
            }
            return;
        }

        if (value.MemoryCleanupBatchId == Guid.Empty
            || value.MemoryCleanupAttemptGeneration == 0
            || value.MemoryCleanupProcessCount is < 1 or > MaximumAllowedProcessCount
            || value.MemoryCleanupProcessSetSha256 is null
            || value.MemoryCleanupProcessSetSha256.Length != 64
            || value.MemoryCleanupProcessSetSha256.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            || value.NativeConfigurationGeneration != 0
            || value.NativePlanEpoch != 0
            || value.NativeActionId != 0
            || value.NativeHostSessionIncarnation != 0
            || value.NativeTargetId != 0
            || value.NativeSoftwareId != 0
            || value.NativeProcessStartKey != 0
            || value.NativeProcessId != 0)
        {
            throw new InvalidDataException(
                "The validation admission cleanup-batch handoff is incomplete.");
        }
    }

    private static void ValidateDocument(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        if (document.SchemaVersion != StorageSchemaVersion
            || !string.Equals(document.Contract, StorageContract, StringComparison.Ordinal)
            || document.ScopeId == Guid.Empty
            || document.RunNonce == Guid.Empty
            || document.OwnerRuntimeIncarnation == Guid.Empty
            || !IsValidationJobName(document.JobName)
            || !IsSha256(document.JobNameSha256)
            || !string.Equals(
                document.JobNameSha256,
                ComputeSha256(document.JobName),
                StringComparison.Ordinal)
            || document.ExpiresAtUtcTicks <= 0
            || !IsSha256(document.ReleaseTokenSha256)
            || document.Generation <= 0
            || document.AllowedProcesses is null
            || document.AllowedProcesses.Count is < 1 or > MaximumAllowedProcessCount
            || document.AuditRecords is null
            || document.AuditRecords.Count > MaximumAuditRecordCount
            || !IsSha256(document.ChecksumSha256)
            || !string.Equals(
                document.ChecksumSha256,
                ComputeChecksum(document),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope document failed its hash-closed contract.");
        }

        var identities = new HashSet<HostManagerComputeProcessIdentity>();
        HostManagerComputeProcessIdentity? previous = null;
        foreach (var item in document.AllowedProcesses)
        {
            var identity = item.ToIdentity();
            if (identity.ProcessId <= 0
                || identity.ProcessStartKey == 0
                || !identities.Add(identity)
                || previous is { } prior
                    && (identity.ProcessId < prior.ProcessId
                        || identity.ProcessId == prior.ProcessId
                            && identity.ProcessStartKey <= prior.ProcessStartKey))
            {
                throw new InvalidDataException(
                    "The validation effect-scope process set is not canonical.");
            }
            previous = identity;
        }
        for (var index = 0; index < document.AuditRecords.Count; index++)
        {
            ValidateAuditRecord(document.AuditRecords[index], index + 1L);
        }
    }

    private static void ValidateAuditRecord(
        HostManagerProcessEffectValidationScopeAuditDocument record,
        long expectedSequence)
    {
        if (record.Sequence != expectedSequence
            || record.RecordedAtUtcTicks <= 0
            || record.AdmissionId == Guid.Empty
            || record.Family is < HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction
                or > HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction
            || string.IsNullOrWhiteSpace(record.Stage)
            || record.Stage.Length > MaximumStageCharacters
            || record.ProcessId <= 0
            || record.ProcessStartKey == 0
            || record.Decision is < HostManagerProcessEffectValidationDecision.AllowedScoped
                or > HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery
            || record.SystemError < 0)
        {
            throw new InvalidDataException(
                "The validation effect-scope audit sequence is invalid.");
        }
    }

    private bool IsExpired(HostManagerProcessEffectValidationScopeDocument document)
        => timeProvider.GetUtcNow().UtcTicks >= document.ExpiresAtUtcTicks;

    private static string ComputeChecksum(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.SchemaVersion);
            writer.Write(document.Contract);
            writer.Write(document.ScopeId.ToByteArray());
            writer.Write(document.RunNonce.ToByteArray());
            writer.Write(document.OwnerRuntimeIncarnation.ToByteArray());
            writer.Write(document.JobName);
            writer.Write(document.JobNameSha256);
            writer.Write(document.ExpiresAtUtcTicks);
            writer.Write(document.ReleaseTokenSha256);
            writer.Write(document.Generation);
            writer.Write(document.AllowedProcesses.Count);
            foreach (var process in document.AllowedProcesses)
            {
                writer.Write(process.ProcessId);
                writer.Write(process.ProcessStartKey);
            }
            writer.Write(document.AuditRecords.Count);
            foreach (var record in document.AuditRecords)
            {
                writer.Write(record.Sequence);
                writer.Write(record.RecordedAtUtcTicks);
                writer.Write(record.AdmissionId.ToByteArray());
                writer.Write((byte)record.Family);
                writer.Write(record.Stage);
                writer.Write(record.ProcessId);
                writer.Write(record.ProcessStartKey);
                writer.Write((byte)record.Decision);
                writer.Write(record.SystemError);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string ComputeAuthorityManifestChecksum(
        HostManagerProcessEffectValidationAuthorityManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(manifest.SchemaVersion);
            writer.Write(manifest.Contract);
            writer.Write(manifest.StoreId.ToByteArray());
            writer.Write(manifest.StatePathSha256);
            writer.Write((byte)manifest.State);
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string ComputeAuditFenceChecksum(
        HostManagerProcessEffectValidationAuditFenceDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.SchemaVersion);
            writer.Write(document.Contract);
            writer.Write(document.ScopeId.ToByteArray());
            writer.Write(document.RunNonce.ToByteArray());
            writer.Write(document.OwnerRuntimeIncarnation.ToByteArray());
            writer.Write(document.Generation);
            writer.Write((byte)document.State);
            writer.Write(document.CommittedAuditRecordCount);
            writer.Write(document.CommittedScopeDocumentSha256);
            writer.Write(document.AdmissionId.ToByteArray());
            writer.Write(document.ParentAdmissionId.ToByteArray());
            writer.Write(document.FirstAuditSequence);
            writer.Write(document.AdmissionAudits.Count);
            foreach (var audit in document.AdmissionAudits)
            {
                writer.Write(audit.Sequence);
                writer.Write(audit.RecordedAtUtcTicks);
                writer.Write(audit.AdmissionId.ToByteArray());
                writer.Write((byte)audit.Family);
                writer.Write(audit.Stage);
                writer.Write(audit.ProcessId);
                writer.Write(audit.ProcessStartKey);
                writer.Write((byte)audit.Decision);
                writer.Write(audit.SystemError);
            }
            writer.Write(document.DurableHandoff is not null);
            if (document.DurableHandoff is { } handoff)
            {
                writer.Write((byte)handoff.Kind);
                writer.Write(handoff.MemoryCleanupBatchId.ToByteArray());
                writer.Write(handoff.MemoryCleanupAttemptGeneration);
                writer.Write(handoff.MemoryCleanupProcessCount);
                writer.Write(handoff.MemoryCleanupProcessSetSha256);
                writer.Write(handoff.NativeConfigurationGeneration);
                writer.Write(handoff.NativePlanEpoch);
                writer.Write(handoff.NativeActionId);
                writer.Write(handoff.NativeHostSessionIncarnation);
                writer.Write(handoff.NativeTargetId);
                writer.Write(handoff.NativeSoftwareId);
                writer.Write(handoff.NativeProcessStartKey);
                writer.Write(handoff.NativeProcessId);
            }
            writer.Write(document.CommittedHandoffs.Count);
            foreach (var committed in document.CommittedHandoffs)
            {
                writer.Write(committed.AdmissionId.ToByteArray());
                writer.Write(committed.FirstAuditSequence);
                writer.Write(committed.AuditCount);
                writer.Write(committed.ParentAdmissionId.ToByteArray());
                var committedHandoff = committed.DurableHandoff;
                writer.Write((byte)committedHandoff.Kind);
                writer.Write(committedHandoff.MemoryCleanupBatchId.ToByteArray());
                writer.Write(committedHandoff.MemoryCleanupAttemptGeneration);
                writer.Write(committedHandoff.MemoryCleanupProcessCount);
                writer.Write(committedHandoff.MemoryCleanupProcessSetSha256);
                writer.Write(committedHandoff.NativeConfigurationGeneration);
                writer.Write(committedHandoff.NativePlanEpoch);
                writer.Write(committedHandoff.NativeActionId);
                writer.Write(committedHandoff.NativeHostSessionIncarnation);
                writer.Write(committedHandoff.NativeTargetId);
                writer.Write(committedHandoff.NativeSoftwareId);
                writer.Write(committedHandoff.NativeProcessStartKey);
                writer.Write(committedHandoff.NativeProcessId);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static long CreateNonzeroGeneration(Guid scopeId, Guid runNonce)
    {
        Span<byte> material = stackalloc byte[32];
        scopeId.TryWriteBytes(material[..16]);
        runNonce.TryWriteBytes(material[16..]);
        var value = BitConverter.ToInt64(SHA256.HashData(material), 0) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ComputeStatePathIdentitySha256(string path)
        => ComputeSha256(
            Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToUpperInvariant());

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsValidationJobName(string? value)
    {
        if (value is null
            || value.Length > MaximumJobNameCharacters
            || !value.StartsWith(RequiredJobNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        return Guid.TryParseExact(
            value.AsSpan(RequiredJobNamePrefix.Length),
            "N",
            out _);
    }

    private static string MapState(HostManagerProcessEffectValidationScopeState state)
        => state switch
        {
            HostManagerProcessEffectValidationScopeState.ProductionUnscoped =>
                "productionUnscoped",
            HostManagerProcessEffectValidationScopeState.Active => "active",
            HostManagerProcessEffectValidationScopeState.ExpiredBlocked =>
                "expiredBlocked",
            HostManagerProcessEffectValidationScopeState.FaultedBlocked =>
                "faultedBlocked",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static string MapFamily(HostManagerProcessEffectValidationFamily family)
        => family switch
        {
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction =>
                "nonAdaptedMemoryTransaction",
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup =>
                "automaticMemoryCleanup",
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction =>
                "nativeProcessPolicyTransaction",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

    private static string MapDecision(HostManagerProcessEffectValidationDecision decision)
        => decision switch
        {
            HostManagerProcessEffectValidationDecision.AllowedScoped => "allowedScoped",
            HostManagerProcessEffectValidationDecision.DeniedNotAllowlisted =>
                "deniedNotAllowlisted",
            HostManagerProcessEffectValidationDecision.DeniedIdentityMismatch =>
                "deniedIdentityMismatch",
            HostManagerProcessEffectValidationDecision.DeniedNotJobMember =>
                "deniedNotJobMember",
            HostManagerProcessEffectValidationDecision.DeniedMembershipUnknown =>
                "deniedMembershipUnknown",
            HostManagerProcessEffectValidationDecision.DeniedExpired => "deniedExpired",
            HostManagerProcessEffectValidationDecision.DeniedScopeFault =>
                "deniedScopeFault",
            HostManagerProcessEffectValidationDecision.DeniedSnapshotMismatch =>
                "deniedSnapshotMismatch",
            HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery =>
                "allowedPriorHandoffRecovery",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null)
        };

    private static (string StatePath, string AuthorityManifestPath)
        ResolveProductionPaths(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var stableRoot = HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
            environment.ContentRootPath,
            allowUnregisteredFallback: environment.IsDevelopment());
        var statePath = Path.Combine(
            stableRoot,
            "UserData",
            "HostManager",
            "Validation",
            "generation-00000001",
            "process-effect-scope.json");
        var authorityManifestPath = Path.Combine(
            stableRoot,
            ".resource-manager-validation-authority",
            "process-effect-scope-generation-00000001.json");
        return (statePath, authorityManifestPath);
    }

    private static string ResolveDefaultAuthorityManifestPath(string statePath)
    {
        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath))
            ?? throw new InvalidOperationException(
                "The validation effect-scope path has no storage directory.");
        var anchorRoot = Directory.GetParent(stateDirectory)?.FullName
            ?? throw new InvalidOperationException(
                "The validation effect-scope storage directory has no authority root.");
        var pathIdentity = ComputeStatePathIdentitySha256(statePath);
        return Path.Combine(
            anchorRoot,
            ".resource-manager-validation-authority",
            $"process-effect-scope-{pathIdentity[..16]}.json");
    }

    private static void ValidateAuthorityManifestLocation(
        string statePath,
        string authorityManifestPath)
    {
        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath))
            ?? throw new InvalidOperationException(
                "The validation effect-scope path has no storage directory.");
        var relative = Path.GetRelativePath(
            stateDirectory,
            Path.GetFullPath(authorityManifestPath));
        var outsideStateDirectory = Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
        if (!outsideStateDirectory)
        {
            throw new ArgumentException(
                "The validation effect-scope authority manifest must be outside the deletable state directory.",
                nameof(authorityManifestPath));
        }
    }
}

internal enum HostManagerProcessEffectValidationAuthorityManifestState : byte
{
    Initializing = 1,
    Ready = 2
}

internal sealed record HostManagerProcessEffectValidationAuthorityManifest(
    int SchemaVersion,
    string Contract,
    Guid StoreId,
    string StatePathSha256,
    HostManagerProcessEffectValidationAuthorityManifestState State,
    string ChecksumSha256);

internal sealed record HostManagerProcessEffectValidationScopeDocument(
    int SchemaVersion,
    string Contract,
    Guid ScopeId,
    Guid RunNonce,
    Guid OwnerRuntimeIncarnation,
    string JobName,
    string JobNameSha256,
    long ExpiresAtUtcTicks,
    string ReleaseTokenSha256,
    long Generation,
    IReadOnlyList<HostManagerProcessEffectValidationScopeIdentityDocument> AllowedProcesses,
    IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> AuditRecords,
    string ChecksumSha256);

internal readonly record struct HostManagerProcessEffectValidationScopeIdentityDocument(
    int ProcessId,
    ulong ProcessStartKey)
{
    internal HostManagerComputeProcessIdentity ToIdentity()
        => new(ProcessId, ProcessStartKey);
}

internal readonly record struct HostManagerProcessEffectValidationScopeAuditDocument(
    long Sequence,
    long RecordedAtUtcTicks,
    Guid AdmissionId,
    HostManagerProcessEffectValidationFamily Family,
    string Stage,
    int ProcessId,
    ulong ProcessStartKey,
    HostManagerProcessEffectValidationDecision Decision,
    int SystemError);

internal enum HostManagerProcessEffectValidationAuditFenceState : byte
{
    Clean = 1,
    Intent = 2,
    AuditCommitted = 3,
    HandoffDeclared = 4,
    JournalPrepared = 5,
    Opening = 6,
    Closing = 7,
    Closed = 8,
    RetiringClosed = 9
}

internal enum HostManagerProcessEffectValidationDurableHandoffKind : byte
{
    NativeTransactionJournal = 1,
    MemoryCleanupAttemptBatch = 2
}

internal readonly record struct HostManagerProcessEffectValidationDurableHandoffDocument(
    HostManagerProcessEffectValidationDurableHandoffKind Kind,
    Guid MemoryCleanupBatchId,
    ulong MemoryCleanupAttemptGeneration,
    int MemoryCleanupProcessCount,
    string MemoryCleanupProcessSetSha256,
    ulong NativeConfigurationGeneration,
    ulong NativePlanEpoch,
    ulong NativeActionId,
    ulong NativeHostSessionIncarnation,
    ulong NativeTargetId,
    ulong NativeSoftwareId,
    ulong NativeProcessStartKey,
    uint NativeProcessId)
{
    internal static HostManagerProcessEffectValidationDurableHandoffDocument
        CreateNativeTransaction(in NativeTransactionJournalIdentity identity)
        => new(
            HostManagerProcessEffectValidationDurableHandoffKind.NativeTransactionJournal,
            Guid.Empty,
            0,
            0,
            string.Empty,
            identity.ConfigurationGeneration,
            identity.PlanEpoch,
            identity.ActionId,
            identity.HostSessionIncarnation,
            identity.TargetId,
            identity.SoftwareId,
            identity.ProcessStartKey,
            identity.ProcessId);

    internal static HostManagerProcessEffectValidationDurableHandoffDocument
        CreateMemoryCleanupBatch(
            Guid batchId,
            ulong attemptGeneration,
            IReadOnlyCollection<HostManagerComputeProcessIdentity> processes)
        => new(
            HostManagerProcessEffectValidationDurableHandoffKind.MemoryCleanupAttemptBatch,
            batchId,
            attemptGeneration,
            processes.Count,
            ComputeMemoryCleanupProcessSetSha256(processes),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

    internal static string ComputeMemoryCleanupProcessSetSha256(
        IEnumerable<HostManagerComputeProcessIdentity> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var ordered = processes.OrderBy(static item => item.ProcessId)
            .ThenBy(static item => item.ProcessStartKey)
            .ToArray();
        using var stream = new MemoryStream(capacity: checked(4 + ordered.Length * 12));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ordered.Length);
            foreach (var process in ordered)
            {
                writer.Write(process.ProcessId);
                writer.Write(process.ProcessStartKey);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

internal readonly record struct HostManagerProcessEffectValidationCommittedHandoffDocument(
    Guid AdmissionId,
    long FirstAuditSequence,
    int AuditCount,
    Guid ParentAdmissionId,
    HostManagerProcessEffectValidationDurableHandoffDocument DurableHandoff);

internal sealed record HostManagerProcessEffectValidationAuditFenceDocument(
    int SchemaVersion,
    string Contract,
    Guid ScopeId,
    Guid RunNonce,
    Guid OwnerRuntimeIncarnation,
    long Generation,
    HostManagerProcessEffectValidationAuditFenceState State,
    long CommittedAuditRecordCount,
    string CommittedScopeDocumentSha256,
    Guid AdmissionId,
    Guid ParentAdmissionId,
    long FirstAuditSequence,
    IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> AdmissionAudits,
    HostManagerProcessEffectValidationDurableHandoffDocument? DurableHandoff,
    IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument> CommittedHandoffs,
    string ChecksumSha256);
