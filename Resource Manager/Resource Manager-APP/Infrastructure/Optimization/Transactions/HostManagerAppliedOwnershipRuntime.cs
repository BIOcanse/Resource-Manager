using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal sealed class HostManagerAppliedOwnershipRuntime(
    HostManagerAppliedOwnershipDeploymentRuntime deploymentRuntime,
    HostManagerAuthorityRetirementManager retirements) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HostManagerAppliedOwnershipRuntimePlan? appliedPlan;
    private NativeAppliedOwnershipOwner? owner;
    private string? ledgerPath;
    private bool disposed;

    internal int PendingRetiredOwnerCount
        => retirements.PendingCount(HostManagerAuthorityKind.AppliedOwnership);

    internal Exception? OwnerRetirementFailure => retirements.LastFailure;

    internal HostManagerAppliedOwnershipRuntimePlan CurrentPlan
        => Volatile.Read(ref appliedPlan)
            ?? throw new InvalidOperationException(
                "The applied-ownership runtime plan is unavailable.");

    public async Task EnsureReadyAsync(
        Func<CancellationToken, Task> requireJournalCleanForRecreate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requireJournalCleanForRecreate);
        var desired = deploymentRuntime.CaptureDesired();
        if (desired.AbiVersion != NativeAppliedOwnershipAbi.Version)
        {
            throw new InvalidOperationException(
                $"Applied ownership ABI {desired.AbiVersion} does not match native ABI {NativeAppliedOwnershipAbi.Version}.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (owner is null)
            {
                if (appliedPlan is null)
                {
                    await CreateInitialOwnerAsync(desired, cancellationToken);
                }
                else
                {
                    await RecreateMissingOwnerAsync(
                        desired,
                        requireJournalCleanForRecreate,
                        cancellationToken);
                }
                return;
            }

            var applied = appliedPlan
                ?? throw new InvalidOperationException("The applied-ownership runtime plan is missing.");
            if (desired.HotPublish.ConfigurationGeneration == applied.HotPublish.ConfigurationGeneration)
            {
                if (!desired.HasSameModuleConfiguration(applied))
                {
                    throw new InvalidOperationException(
                        "The applied-ownership configuration drifted without a generation change.");
                }
                appliedPlan = desired;
                return;
            }
            if (desired.HotPublish.ConfigurationGeneration < applied.HotPublish.ConfigurationGeneration)
            {
                throw new InvalidOperationException(
                    "The applied-ownership configuration generation moved backwards.");
            }

            if (desired.Recreate == applied.Recreate)
            {
                if (!deploymentRuntime.CanApplyHot(desired.HostPlan))
                {
                    throw new InvalidOperationException(
                        "The Host Manager deployment state rejected applied-ownership hot publish.");
                }

                var attempt = deploymentRuntime.BeginHotPublish(desired.HostPlan);
                var successSettlementAttempted = false;
                try
                {
                    successSettlementAttempted = true;
                    HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                        deploymentRuntime.CompleteSucceeded(attempt),
                        HostManagerModuleKind.AppliedOwnership);
                    appliedPlan = desired;
                }
                catch (Exception exception)
                {
                    var secondary = await CleanupAttemptAsync(
                        resource: null,
                        attempt,
                        $"applied-ownership-hot:{exception.GetType().Name}",
                        settleFailure: !successSettlementAttempted);
                    if (secondary is not null)
                    {
                        throw new AggregateException(exception, secondary);
                    }
                    throw;
                }
                return;
            }

            await RecreateEmptyOwnerAsync(
                desired,
                requireJournalCleanForRecreate,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NativeAppliedOwnershipSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            static (current, token) => current.ReadSnapshotAsync(token),
            cancellationToken);

    public async Task<NativeAppliedOwnershipReadResult> GetAsync(
        NativeAppliedOwnershipPrimaryIdentity primary,
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            (current, token) => current.GetAsync(primary, token),
            cancellationToken);

    public async Task<NativeAppliedOwnershipStatus> PromoteAsync(
        NativeAppliedOwnershipPromoteInput input,
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            (current, token) => current.PromoteAsync(input, token),
            cancellationToken);

    public async Task<NativeAppliedOwnershipStatus> TransitionAsync(
        NativeAppliedOwnershipTransitionInput input,
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            (current, token) => current.TransitionAsync(input, token),
            cancellationToken);

    public async Task<NativeAppliedOwnershipTransitionPlanResult> PlanTransitionAsync(
        NativeAppliedOwnershipPrimaryIdentity primary,
        NativeAppliedOwnershipOriginalBinding transitionBinding,
        NativeAppliedOwnershipDurablePayloadReference transitionPayload,
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            (current, token) => current.PlanTransitionAsync(
                primary,
                transitionBinding,
                transitionPayload,
                token),
            cancellationToken);

    public async Task<NativeAppliedOwnershipStatus> RemoveAsync(
        NativeAppliedOwnershipRemoveInput input,
        CancellationToken cancellationToken)
        => await WithOwnerAsync(
            (current, token) => current.RemoveAsync(input, token),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (owner is not null)
            {
                await owner.DisposeAsync();
                owner = null;
            }
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task<T> WithOwnerAsync<T>(
        Func<NativeAppliedOwnershipOwner, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var current = owner
                ?? throw new InvalidOperationException("Applied ownership is not ready.");
            return await action(current, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CreateInitialOwnerAsync(
        HostManagerAppliedOwnershipRuntimePlan desired,
        CancellationToken cancellationToken)
    {
        var attempt = deploymentRuntime.BeginInitialCreate(desired.HostPlan);
        NativeAppliedOwnershipOwner? opened = null;
        var successSettlementAttempted = false;
        try
        {
            var path = ResolveLedgerPath(desired.Recreate.LedgerRelativePath);
            retirements.RequireAvailable(path);
            opened = await OpenOwnerAsync(path, desired, cancellationToken);
            ValidateCapacity(opened.Capacity, desired.Recreate);
            successSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                deploymentRuntime.CompleteSucceeded(attempt),
                HostManagerModuleKind.AppliedOwnership);
            owner = opened;
            ledgerPath = path;
            appliedPlan = desired;
        }
        catch (Exception exception)
        {
            var secondary = await CleanupAttemptAsync(
                opened is not null && !ReferenceEquals(opened, owner) ? opened : null,
                attempt,
                $"applied-ownership-create:{exception.GetType().Name}",
                settleFailure: !successSettlementAttempted);
            if (secondary is not null)
            {
                throw new AggregateException(exception, secondary);
            }
            throw;
        }
    }

    private async Task RecreateEmptyOwnerAsync(
        HostManagerAppliedOwnershipRuntimePlan desired,
        Func<CancellationToken, Task> requireJournalCleanForRecreate,
        CancellationToken cancellationToken)
    {
        var current = owner
            ?? throw new InvalidOperationException("Applied ownership is not ready.");
        var snapshot = await current.ReadSnapshotAsync(cancellationToken);
        if (snapshot.Header.EntryCount != 0)
        {
            throw new InvalidOperationException(
                "Applied ownership cannot be recreated while applied records exist.");
        }
        await requireJournalCleanForRecreate(cancellationToken);

        var attempt = deploymentRuntime.BeginHostRecreateAndHotPublish(desired.HostPlan);
        var previousPath = ledgerPath
            ?? throw new InvalidOperationException("The applied-ownership ledger path is missing.");
        NativeAppliedOwnershipOwner? replacement = null;
        HostManagerAuthorityRetirementTicket? retirementTicket = null;
        var successSettlementAttempted = false;
        var replacementPublished = false;
        try
        {
            var nextPath = ResolveLedgerPath(desired.Recreate.LedgerRelativePath);
            if (string.Equals(nextPath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Applied-ownership recreate requires a new ledger path.");
            }
            retirements.RequireAvailable(nextPath);
            replacement = await OpenOwnerAsync(nextPath, desired, cancellationToken);
            ValidateCapacity(replacement.Capacity, desired.Recreate);
            retirementTicket = await retirements.PrepareAppliedOwnershipAsync(
                previousPath,
                cancellationToken);
            retirements.Attach(retirementTicket.Value, current);
            successSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                deploymentRuntime.CompleteSucceeded(attempt),
                HostManagerModuleKind.AppliedOwnership);
            owner = replacement;
            ledgerPath = nextPath;
            appliedPlan = desired;
            replacementPublished = true;
        }
        catch (Exception exception)
        {
            if (retirementTicket is null
                && exception is HostManagerAuthorityRetirementPrepareException
                {
                    Outcome: HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous
                } prepareException)
            {
                retirementTicket = new HostManagerAuthorityRetirementTicket(
                    prepareException.TicketId,
                    HostManagerAuthorityKind.AppliedOwnership);
            }

            Exception? retirementCancellationFailure = null;
            if (retirementTicket is not null && !replacementPublished)
            {
                try
                {
                    await retirements.CancelAsync(
                        retirementTicket.Value,
                        CancellationToken.None);
                }
                catch (Exception cancellationException)
                {
                    retirementCancellationFailure = cancellationException;
                }
            }
            var secondary = await CleanupAttemptAsync(
                replacement is not null && !replacementPublished
                    ? replacement
                    : null,
                attempt,
                $"applied-ownership-recreate:{exception.GetType().Name}",
                settleFailure: !successSettlementAttempted);
            if (retirementCancellationFailure is not null)
            {
                secondary = secondary is null
                    ? retirementCancellationFailure
                    : new AggregateException(retirementCancellationFailure, secondary);
            }
            if (secondary is not null)
            {
                throw new AggregateException(exception, secondary);
            }
            throw;
        }

        await retirements.RetryAsync(cancellationToken);
    }

    private async Task RecreateMissingOwnerAsync(
        HostManagerAppliedOwnershipRuntimePlan desired,
        Func<CancellationToken, Task> requireJournalCleanForRecreate,
        CancellationToken cancellationToken)
    {
        var applied = appliedPlan
            ?? throw new InvalidOperationException(
                "A missing applied-ownership owner does not have an applied plan.");
        if (desired.HotPublish.ConfigurationGeneration <=
            applied.HotPublish.ConfigurationGeneration ||
            desired.Recreate == applied.Recreate)
        {
            throw new InvalidOperationException(
                "A missing applied-ownership owner can only resume its exact pending recreate.");
        }

        var attempt = deploymentRuntime.BeginHostRecreateAndHotPublish(desired.HostPlan);
        NativeAppliedOwnershipOwner? replacement = null;
        var successSettlementAttempted = false;
        try
        {
            await requireJournalCleanForRecreate(cancellationToken);
            var nextPath = ledgerPath
                ?? ResolveLedgerPath(desired.Recreate.LedgerRelativePath);
            retirements.RequireAvailable(nextPath);
            replacement = await OpenOwnerAsync(nextPath, desired, cancellationToken);
            ValidateCapacity(replacement.Capacity, desired.Recreate);
            successSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                deploymentRuntime.CompleteSucceeded(attempt),
                HostManagerModuleKind.AppliedOwnership);
            owner = replacement;
            ledgerPath = nextPath;
            appliedPlan = desired;
        }
        catch (Exception exception)
        {
            var secondary = await CleanupAttemptAsync(
                replacement is not null && !ReferenceEquals(replacement, owner)
                    ? replacement
                    : null,
                attempt,
                $"applied-ownership-recreate:{exception.GetType().Name}",
                settleFailure: !successSettlementAttempted);
            if (secondary is not null)
            {
                throw new AggregateException(exception, secondary);
            }
            throw;
        }
    }

    private void SettleFailed(
        HostManagerDeploymentAttemptToken attempt,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentRuntime.CompleteFailed(attempt, failureCode),
            HostManagerModuleKind.AppliedOwnership);

    private async Task<Exception?> CleanupAttemptAsync(
        IAsyncDisposable? resource,
        HostManagerDeploymentAttemptToken attempt,
        string failureCode,
        bool settleFailure)
    {
        Exception? failure = null;
        if (resource is not null)
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        if (settleFailure)
        {
            try
            {
                SettleFailed(attempt, failureCode);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }
        return failure;
    }

    private static async Task<NativeAppliedOwnershipOwner> OpenOwnerAsync(
        string path,
        HostManagerAppliedOwnershipRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        var ledgerIdentity = CreateLedgerIdentity();
        var recreate = plan.Recreate;
        var create = new NativeAppliedOwnershipCreateConfiguration
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipSession.SizeOf<NativeAppliedOwnershipCreateConfiguration>(),
            LedgerInstanceLow = ledgerIdentity.Low,
            LedgerInstanceHigh = ledgerIdentity.High,
            RecordCapacity = checked((uint)recreate.RecordCapacity),
            PrimaryIndexCapacity = checked((uint)recreate.PrimaryIndexCapacity),
            PayloadIndexCapacity = checked((uint)recreate.PayloadIndexCapacity),
            Flags = 0,
            MaximumResidentBytes = checked((ulong)recreate.ResidentByteBudget),
            MaximumImageBytes = checked((ulong)recreate.ImageByteBudget)
        };
        var open = new NativeAppliedOwnershipOpenConfiguration
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipSession.SizeOf<NativeAppliedOwnershipOpenConfiguration>(),
            MaximumRecordCapacity = checked((uint)recreate.RecordCapacity),
            MaximumPrimaryIndexCapacity = checked((uint)recreate.PrimaryIndexCapacity),
            MaximumPayloadIndexCapacity = checked((uint)recreate.PayloadIndexCapacity),
            Flags = 0,
            MaximumResidentBytes = checked((ulong)recreate.ResidentByteBudget),
            MaximumImageBytes = checked((ulong)recreate.ImageByteBudget)
        };
        return await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(path),
            new NativeAppliedOwnershipSessionFactory(),
            create,
            open,
            cancellationToken);
    }

    private string ResolveLedgerPath(string relativePath)
    {
        var dataRoot = retirements.DataRoot;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\'))
        {
            throw new InvalidDataException(
                "The applied-ownership ledger path must be a canonical relative path.");
        }
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "The applied-ownership ledger path contains an invalid segment.");
        }
        var resolved = Path.GetFullPath(Path.Combine(dataRoot, Path.Combine(segments)));
        var prefix = Path.EndsInDirectorySeparator(dataRoot)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The applied-ownership ledger path escapes the Host Manager data root.");
        }
        return resolved;
    }

    private static void ValidateCapacity(
        NativeAppliedOwnershipCapacity capacity,
        Domain.RuntimeSpecialization.CompiledHostManagerAppliedOwnershipRecreatePlan plan)
    {
        if (capacity.RecordCapacity != checked((uint)plan.RecordCapacity) ||
            capacity.PrimaryIndexCapacity != checked((uint)plan.PrimaryIndexCapacity) ||
            capacity.PayloadIndexCapacity != checked((uint)plan.PayloadIndexCapacity) ||
            capacity.MaximumImageBytes != checked((ulong)plan.ImageByteBudget) ||
            capacity.ResidentBytes > checked((ulong)plan.ResidentByteBudget))
        {
            throw new InvalidDataException(
                "The applied-ownership ledger capacity does not match the compiled Host Manager plan.");
        }
    }

    private static (ulong Low, ulong High) CreateLedgerIdentity()
    {
        Span<byte> bytes = stackalloc byte[16];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
            var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            if (low != 0 || high != 0)
            {
                return (low, high);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(HostManagerAppliedOwnershipRuntime));
        }
    }
}
