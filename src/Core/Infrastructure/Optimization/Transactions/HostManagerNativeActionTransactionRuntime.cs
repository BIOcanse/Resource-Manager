using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

public sealed class HostManagerNativeActionTransactionRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HostManagerTransactionJournalDeploymentRuntime journalDeployment;
    private readonly HostManagerAppliedOwnershipRuntime appliedOwnership;
    private readonly HostManagerAuthorityRetirementManager retirements;
    private HostManagerTransactionJournalRuntimePlan? appliedJournalPlan;
    private HostManagerTransactionJournalRuntime? journal;
    private bool disposed;

    internal HostManagerNativeActionTransactionRuntime(
        HostManagerTransactionJournalDeploymentRuntime journalDeployment,
        HostManagerAppliedOwnershipRuntime appliedOwnership,
        HostManagerAuthorityRetirementManager retirements)
    {
        this.journalDeployment = journalDeployment;
        this.appliedOwnership = appliedOwnership;
        this.retirements = retirements;
    }

    internal HostManagerAppliedOwnershipRuntime AppliedOwnership => appliedOwnership;

    internal HostManagerTransactionJournalRuntimePlan CurrentJournalPlan
        => Volatile.Read(ref appliedJournalPlan)
            ?? throw new InvalidOperationException(
                "The applied transaction-journal runtime plan is unavailable.");

    internal HostManagerTransactionJournalRuntimeState JournalState
        => journal?.State ?? HostManagerTransactionJournalRuntimeState.Initializing;

    internal int PendingRetiredJournalCount
        => retirements.PendingCount(HostManagerAuthorityKind.TransactionJournal);

    internal Exception? JournalRetirementFailure => retirements.LastFailure;

    internal async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await retirements.RetryAsync(cancellationToken);
            var desired = journalDeployment.CaptureDesired();
            if (journal is null)
            {
                if (appliedJournalPlan is null)
                {
                    await CreateInitialJournalAsync(desired, cancellationToken);
                }
                else
                {
                    await RecreateMissingJournalAsync(desired, cancellationToken);
                }

                await appliedOwnership.EnsureReadyAsync(
                    RequireJournalCleanForOwnershipRecreateAsync,
                    cancellationToken);
            }
            else
            {
                await appliedOwnership.EnsureReadyAsync(
                    RequireJournalCleanForOwnershipRecreateAsync,
                    cancellationToken);
                await ApplyJournalPlanAsync(desired, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal bool TryAcquireExecutionAdmission(
        out HostManagerTransactionJournalAdmission? admission)
    {
        var current = journal;
        if (current is not null
            && current.TryAcquireExecutionAdmission(out var acquired))
        {
            admission = acquired;
            return true;
        }

        admission = null;
        return false;
    }

    internal bool TryAcquireRecoveryAdmission(
        out HostManagerTransactionJournalAdmission? admission)
    {
        var current = journal;
        if (current is not null
            && current.TryAcquireRecoveryAdmission(out var acquired))
        {
            admission = acquired;
            return true;
        }

        admission = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Exception? disposeFailure = null;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (journal is not null)
            {
                try
                {
                    await journal.DisposeAsync();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
                finally
                {
                    journal = null;
                }
            }
            try
            {
                await appliedOwnership.DisposeAsync();
            }
            catch (Exception exception)
            {
                disposeFailure = disposeFailure is null
                    ? exception
                    : new AggregateException(disposeFailure, exception);
            }
            await retirements.RetryAsync();
            var retirementFailure = retirements.CreatePendingFailure(
                "One or more retired Host Manager authorities remain pending.");
            if (retirementFailure is not null)
            {
                disposeFailure = disposeFailure is null
                    ? retirementFailure
                    : new AggregateException(disposeFailure, retirementFailure);
            }
        }
        finally
        {
            gate.Release();
            gate.Dispose();
            retirements.Dispose();
        }
        if (disposeFailure is not null)
        {
            throw new InvalidOperationException(
                "The Host Manager transaction runtime did not dispose every owned runtime cleanly.",
                disposeFailure);
        }
    }

    private async Task CreateInitialJournalAsync(
        HostManagerTransactionJournalRuntimePlan desired,
        CancellationToken cancellationToken)
    {
        var attempt = journalDeployment.BeginInitialCreate(desired.HostPlan);
        HostManagerTransactionJournalRuntime? opened = null;
        var successSettlementAttempted = false;
        try
        {
            var dataRoot = ResolveHostManagerDataRoot();
            var storage = HostManagerTransactionJournalRuntime.ResolveStoragePaths(
                dataRoot,
                desired.Recreate);
            retirements.RequireAvailable(
                storage.JournalPath,
                storage.PayloadDirectory);
            opened = await HostManagerTransactionJournalRuntime.OpenAsync(
                dataRoot,
                desired.Recreate,
                desired.HotPublish,
                cancellationToken);
            successSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                journalDeployment.CompleteSucceeded(attempt),
                HostManagerModuleKind.TransactionJournal);
            journal = opened;
            appliedJournalPlan = desired;
        }
        catch (Exception exception)
        {
            var secondary = await CleanupAttemptAsync(
                opened is not null && !ReferenceEquals(opened, journal) ? opened : null,
                attempt,
                $"transaction-journal-create:{exception.GetType().Name}",
                settleFailure: !successSettlementAttempted);
            if (secondary is not null)
            {
                throw new AggregateException(exception, secondary);
            }
            throw;
        }
    }

    private async Task ApplyJournalPlanAsync(
        HostManagerTransactionJournalRuntimePlan desired,
        CancellationToken cancellationToken)
    {
        var current = journal
            ?? throw new InvalidOperationException("The transaction journal is not ready.");
        var applied = appliedJournalPlan
            ?? throw new InvalidOperationException("The applied transaction-journal plan is missing.");
        if (desired.HotPublish.ConfigurationGeneration == applied.HotPublish.ConfigurationGeneration)
        {
            if (!desired.HasSameModuleConfiguration(applied))
            {
                throw new InvalidOperationException(
                    "The transaction-journal configuration drifted without a generation change.");
            }
            appliedJournalPlan = desired;
            return;
        }
        if (desired.HotPublish.ConfigurationGeneration < applied.HotPublish.ConfigurationGeneration)
        {
            throw new InvalidOperationException(
                "The transaction-journal configuration generation moved backwards.");
        }

        if (desired.Recreate == applied.Recreate)
        {
            if (!journalDeployment.CanApplyHot(desired.HostPlan))
            {
                throw new InvalidOperationException(
                    "The Host Manager deployment state rejected transaction-journal hot publish.");
            }
            var attempt = journalDeployment.BeginHotPublish(desired.HostPlan);
            var successSettlementAttempted = false;
            try
            {
                if (!await current.TryApplyHotPublishAsync(desired.HotPublish, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The transaction journal rejected the compiled hot-publish plan.");
                }
                successSettlementAttempted = true;
                HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                    journalDeployment.CompleteSucceeded(attempt),
                    HostManagerModuleKind.TransactionJournal);
                appliedJournalPlan = desired;
            }
            catch (Exception exception)
            {
                var secondary = await CleanupAttemptAsync(
                    resource: null,
                    attempt,
                    $"transaction-journal-hot:{exception.GetType().Name}",
                    settleFailure: !successSettlementAttempted);
                if (secondary is not null)
                {
                    throw new AggregateException(exception, secondary);
                }
                throw;
            }
            return;
        }

        var recreateAttempt = journalDeployment.BeginHostRecreateAndHotPublish(desired.HostPlan);
        HostManagerTransactionJournalRuntime? replacement = null;
        HostManagerAuthorityRetirementTicket? retirementTicket = null;
        var recreateSuccessSettlementAttempted = false;
        var currentQuiesced = false;
        var replacementPublished = false;
        try
        {
            await RequireAppliedOwnershipEmptyForJournalRecreateAsync(cancellationToken);
            var dataRoot = ResolveHostManagerDataRoot();
            var storage = HostManagerTransactionJournalRuntime.ResolveStoragePaths(
                dataRoot,
                desired.Recreate);
            if (string.Equals(storage.JournalPath, current.JournalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    storage.PayloadDirectory,
                    current.PayloadDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Transaction-journal recreate requires new journal and payload paths.");
            }
            retirements.RequireAvailable(
                storage.JournalPath,
                storage.PayloadDirectory);
            replacement = await HostManagerTransactionJournalRuntime.OpenAsync(
                dataRoot,
                desired.Recreate,
                desired.HotPublish,
                cancellationToken);
            if (replacement.State != HostManagerTransactionJournalRuntimeState.ReadyForExecution)
            {
                throw new InvalidOperationException(
                    $"The replacement transaction journal opened in {replacement.State}.");
            }
            var quiesce = await current.QuiesceForRecreateAsync(cancellationToken);
            currentQuiesced = true;
            if (quiesce != HostManagerTransactionJournalShutdownResult.Clean)
            {
                throw new InvalidOperationException(
                    $"The transaction journal cannot be recreated from {quiesce}.");
            }
            retirementTicket = await retirements.PrepareTransactionJournalAsync(
                current.JournalPath,
                current.PayloadDirectory,
                cancellationToken);
            retirements.Attach(retirementTicket.Value, current);
            recreateSuccessSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                journalDeployment.CompleteSucceeded(recreateAttempt),
                HostManagerModuleKind.TransactionJournal);
            journal = replacement;
            appliedJournalPlan = desired;
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
                    HostManagerAuthorityKind.TransactionJournal);
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
            Exception? resumeFailure = null;
            if (currentQuiesced
                && !replacementPublished
                && retirementCancellationFailure is null)
            {
                try
                {
                    current.ResumeAfterAbortedRecreate();
                }
                catch (Exception resumeException)
                {
                    resumeFailure = resumeException;
                }
            }
            var secondary = await CleanupAttemptAsync(
                replacement is not null && !ReferenceEquals(replacement, journal)
                    ? replacement
                    : null,
                recreateAttempt,
                $"transaction-journal-recreate:{exception.GetType().Name}",
                settleFailure: !recreateSuccessSettlementAttempted);
            if (resumeFailure is not null)
            {
                secondary = secondary is null
                    ? resumeFailure
                    : new AggregateException(resumeFailure, secondary);
            }
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

    private async Task RecreateMissingJournalAsync(
        HostManagerTransactionJournalRuntimePlan desired,
        CancellationToken cancellationToken)
    {
        var applied = appliedJournalPlan
            ?? throw new InvalidOperationException(
                "A missing transaction journal does not have an applied plan.");
        if (desired.HotPublish.ConfigurationGeneration <=
            applied.HotPublish.ConfigurationGeneration ||
            desired.Recreate == applied.Recreate)
        {
            throw new InvalidOperationException(
                "A missing transaction journal can only resume its exact pending recreate.");
        }

        var attempt = journalDeployment.BeginHostRecreateAndHotPublish(desired.HostPlan);
        HostManagerTransactionJournalRuntime? replacement = null;
        var successSettlementAttempted = false;
        try
        {
            await RequireAppliedOwnershipEmptyForJournalRecreateAsync(cancellationToken);
            var dataRoot = ResolveHostManagerDataRoot();
            var storage = HostManagerTransactionJournalRuntime.ResolveStoragePaths(
                dataRoot,
                desired.Recreate);
            retirements.RequireAvailable(
                storage.JournalPath,
                storage.PayloadDirectory);
            replacement = await HostManagerTransactionJournalRuntime.OpenAsync(
                dataRoot,
                desired.Recreate,
                desired.HotPublish,
                cancellationToken);
            successSettlementAttempted = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                journalDeployment.CompleteSucceeded(attempt),
                HostManagerModuleKind.TransactionJournal);
            journal = replacement;
            appliedJournalPlan = desired;
        }
        catch (Exception exception)
        {
            var secondary = await CleanupAttemptAsync(
                replacement is not null && !ReferenceEquals(replacement, journal)
                    ? replacement
                    : null,
                attempt,
                $"transaction-journal-recreate:{exception.GetType().Name}",
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
            journalDeployment.CompleteFailed(attempt, failureCode, null),
            HostManagerModuleKind.TransactionJournal);

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

    private async Task RequireAppliedOwnershipEmptyForJournalRecreateAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await appliedOwnership.ReadSnapshotAsync(cancellationToken);
        if (snapshot.Header.EntryCount != 0)
        {
            throw new InvalidOperationException(
                "The transaction journal cannot be recreated while applied ownership records exist.");
        }
    }

    private async Task RequireJournalCleanForOwnershipRecreateAsync(
        CancellationToken cancellationToken)
    {
        var current = journal
            ?? throw new InvalidOperationException(
                "Applied ownership cannot be recreated before the transaction journal is ready.");
        var snapshot = await current.ReadSnapshotForLifecycleAsync(cancellationToken);
        if (snapshot.Records.Count != 0)
        {
            throw new InvalidOperationException(
                "Applied ownership cannot be recreated while transaction-journal records exist.");
        }
    }

    private string ResolveHostManagerDataRoot()
        => retirements.DataRoot;

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(HostManagerNativeActionTransactionRuntime));
        }
    }
}
