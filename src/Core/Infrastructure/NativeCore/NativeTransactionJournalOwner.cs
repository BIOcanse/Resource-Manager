using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum NativeTransactionJournalOwnerState
{
    Ready = 1,
    PersistenceFaulted = 2,
    Disposing = 3,
    Disposed = 4
}

internal sealed class NativeTransactionJournalOwner : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly NativeTransactionJournalFileStore fileStore;
    private readonly NativeTransactionJournalPathLease pathLease;
    private readonly NativeTransactionJournalSession session;
    private readonly NativeTransactionJournalWorkspace workspace;
    private int state = (int)NativeTransactionJournalOwnerState.Ready;
    private Exception? persistenceFailure;

    private NativeTransactionJournalOwner(
        NativeTransactionJournalFileStore fileStore,
        NativeTransactionJournalPathLease pathLease,
        NativeTransactionJournalSession session,
        Func<ulong, IComputeScratch> createScratch)
    {
        this.fileStore = fileStore;
        this.pathLease = pathLease;
        this.session = session;
        workspace = new NativeTransactionJournalWorkspace(session.Capacity, createScratch, session);
    }

    public NativeTransactionJournalOwnerState State
        => (NativeTransactionJournalOwnerState)Volatile.Read(ref state);

    public Exception? PersistenceFailure => persistenceFailure;

    public NativeTransactionJournalCapacity Capacity => session.Capacity;

    public static async Task<NativeTransactionJournalOwner> OpenOrCreateAsync(
        NativeTransactionJournalFileStore fileStore,
        NativeTransactionJournalCreateConfiguration createConfiguration,
        NativeTransactionJournalOpenConfiguration openConfiguration,
        Func<ulong, IComputeScratch> createScratch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(createScratch);
        ValidateCompatibleConfigurations(in createConfiguration, in openConfiguration);

        var pathLease = fileStore.AcquireOwnerLease();
        try
        {
            var opened = await fileStore.TryOpenAsync(openConfiguration, cancellationToken);
            NativeTransactionJournalSession session;
            var requiresInitialPersistence = false;
            switch (opened.Status)
            {
                case NativeTransactionJournalStatus.Ok when opened.Session is not null:
                    session = opened.Session;
                    break;
                case NativeTransactionJournalStatus.NoData when opened.Session is null:
                    session = new NativeTransactionJournalSession(in createConfiguration);
                    requiresInitialPersistence = true;
                    break;
                default:
                    opened.Session?.Dispose();
                    throw new InvalidDataException(
                        $"Native transaction journal open failed with {opened.Status}.");
            }

            try
            {
                if (requiresInitialPersistence)
                {
                    // This image has one startup consumer and no reusable identity.
                    var initialImage = new byte[NativeTransactionJournalWorkspace.ImageLengthForRows(0)];
                    await fileStore.PersistAsync(session, initialImage, CancellationToken.None);
                }
                return new NativeTransactionJournalOwner(fileStore, pathLease, session, createScratch);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch
        {
            pathLease.Dispose();
            throw;
        }
    }

    public Task<NativeTransactionJournalStatus> PrepareAsync(
        NativeTransactionJournalPrepareInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (NativeTransactionJournalSession journal, in NativeTransactionJournalPrepareInput value) =>
                journal.Prepare(in value),
            cancellationToken, maximumAdditions: 1);

    public async Task<NativeTransactionJournalStatus> PrepareBatchAsync(
        NativeTransactionJournalPrepareBatchInput batch,
        ReadOnlyMemory<NativeTransactionJournalPrepareInput> inputs,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            workspace.Ensure(session, checked((uint)inputs.Length));
            using var operation = workspace.Acquire();
            var status = session.PrepareBatch(in batch, inputs.Span);
            if (status == NativeTransactionJournalStatus.Ok)
            {
                await PersistOrFaultAsync(operation.Image);
                operation.Complete();
            }
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NativeTransactionJournalStatus> MutateAsync(
        NativeTransactionJournalMutationInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (NativeTransactionJournalSession journal, in NativeTransactionJournalMutationInput value) =>
                journal.Mutate(in value),
            cancellationToken);

    public Task<NativeTransactionJournalStatus> StageFeedbackAsync(
        NativeTransactionJournalStageFeedbackInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (NativeTransactionJournalSession journal, in NativeTransactionJournalStageFeedbackInput value) =>
                journal.StageFeedback(in value),
            cancellationToken);

    public Task<NativeTransactionJournalStatus> ApplyRecoveryEvidenceAsync(
        NativeTransactionJournalRecoveryEvidenceInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (NativeTransactionJournalSession journal, in NativeTransactionJournalRecoveryEvidenceInput value) =>
                journal.ApplyRecoveryEvidence(in value),
            cancellationToken);

    public Task<NativeTransactionJournalStatus> AcknowledgeAsync(
        NativeTransactionJournalAckInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (NativeTransactionJournalSession journal, in NativeTransactionJournalAckInput value) =>
                journal.Acknowledge(in value),
            cancellationToken);

    public async Task<NativeTransactionJournalReadResult> GetAsync(
        NativeTransactionJournalIdentity identity,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var status = session.Get(in identity, out var record);
            return new NativeTransactionJournalReadResult(status, record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NativeTransactionJournalSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            workspace.Ensure(session);
            using var operation = workspace.Acquire();
            var snapshot = operation.ReadSnapshot(session);
            operation.Complete();
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        while (true)
        {
            var current = State;
            if (current == NativeTransactionJournalOwnerState.Disposed)
            {
                return;
            }
            if (current == NativeTransactionJournalOwnerState.Disposing) break;

            if (Interlocked.CompareExchange(
                    ref state,
                    (int)NativeTransactionJournalOwnerState.Disposing,
                    (int)current) == (int)current)
            {
                break;
            }
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (State == NativeTransactionJournalOwnerState.Disposed) return;
            workspace.Dispose();
            session.Dispose();
            pathLease.Dispose();
            Volatile.Write(ref state, (int)NativeTransactionJournalOwnerState.Disposed);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NativeTransactionJournalStatus> MutateAndPersistAsync<TInput>(
        TInput input,
        JournalMutation<TInput> mutation,
        CancellationToken cancellationToken,
        uint maximumAdditions = 0)
        where TInput : unmanaged
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            workspace.Ensure(session, maximumAdditions);
            using var operation = workspace.Acquire();
            var status = mutation(session, in input);
            if (status != NativeTransactionJournalStatus.Ok)
            {
                return status;
            }

            await PersistOrFaultAsync(operation.Image);
            operation.Complete();
            return NativeTransactionJournalStatus.Ok;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistOrFaultAsync(Memory<byte> image)
    {
        try
        {
            await fileStore.PersistAsync(session, image, CancellationToken.None);
        }
        catch (Exception exception)
        {
            persistenceFailure = exception;
            _ = Interlocked.CompareExchange(
                ref state,
                (int)NativeTransactionJournalOwnerState.PersistenceFaulted,
                (int)NativeTransactionJournalOwnerState.Ready);
            throw new NativeTransactionJournalPersistenceException(exception);
        }
    }

    private void EnsureReady()
    {
        var current = State;
        if (current != NativeTransactionJournalOwnerState.Ready)
        {
            throw new InvalidOperationException(
                $"Native transaction journal owner is not ready: {current}.",
                persistenceFailure);
        }
    }

    private static void ValidateCompatibleConfigurations(
        in NativeTransactionJournalCreateConfiguration createConfiguration,
        in NativeTransactionJournalOpenConfiguration openConfiguration)
    {
        if (createConfiguration.AbiVersion != NativeTransactionJournalAbi.Version ||
            openConfiguration.AbiVersion != NativeTransactionJournalAbi.Version ||
            createConfiguration.StructSize != NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalCreateConfiguration>() ||
            openConfiguration.StructSize != NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalOpenConfiguration>() ||
            createConfiguration.RecordCapacity == 0 ||
            createConfiguration.RecordCapacity > openConfiguration.MaximumRecordCapacity ||
            createConfiguration.MaximumResidentBytes == 0 ||
            createConfiguration.MaximumResidentBytes > openConfiguration.MaximumResidentBytes)
        {
            throw new ArgumentException(
                "Transaction journal create/open configurations are not compatible.");
        }
    }

    private delegate NativeTransactionJournalStatus JournalMutation<TInput>(
        NativeTransactionJournalSession journal,
        in TInput input)
        where TInput : unmanaged;
}

internal sealed class NativeTransactionJournalPersistenceException(Exception innerException)
    : IOException("Native transaction journal durability barrier failed.", innerException);

internal sealed record NativeTransactionJournalReadResult(
    NativeTransactionJournalStatus Status,
    NativeTransactionJournalRecord Record);

internal sealed record NativeTransactionJournalSnapshot(
    NativeTransactionJournalSnapshotHeader Header,
    IReadOnlyList<NativeTransactionJournalRecord> Records);
