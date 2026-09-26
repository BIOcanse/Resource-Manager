namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum NativeAppliedOwnershipOwnerState
{
    Ready = 1,
    PersistenceFaulted = 2,
    Disposing = 3,
    Disposed = 4
}

internal sealed class NativeAppliedOwnershipOwner : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly NativeAppliedOwnershipFileStore fileStore;
    private readonly NativeAppliedOwnershipPathLease pathLease;
    private readonly INativeAppliedOwnershipSession session;
    private readonly NativeAppliedOwnershipWorkspace workspace;
    private int state = (int)NativeAppliedOwnershipOwnerState.Ready;
    private Exception? persistenceFailure;

    private NativeAppliedOwnershipOwner(
        NativeAppliedOwnershipFileStore fileStore,
        NativeAppliedOwnershipPathLease pathLease,
        INativeAppliedOwnershipSession session)
    {
        this.fileStore = fileStore;
        this.pathLease = pathLease;
        this.session = session;
        workspace = new NativeAppliedOwnershipWorkspace(session.Capacity);
    }

    public NativeAppliedOwnershipOwnerState State
        => (NativeAppliedOwnershipOwnerState)Volatile.Read(ref state);

    public Exception? PersistenceFailure => persistenceFailure;

    public NativeAppliedOwnershipCapacity Capacity => session.Capacity;

    public static async Task<NativeAppliedOwnershipOwner> OpenOrCreateAsync(
        NativeAppliedOwnershipFileStore fileStore,
        INativeAppliedOwnershipSessionFactory sessionFactory,
        NativeAppliedOwnershipCreateConfiguration createConfiguration,
        NativeAppliedOwnershipOpenConfiguration openConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ValidateCompatibleConfigurations(in createConfiguration, in openConfiguration);

        var pathLease = fileStore.AcquireOwnerLease();
        try
        {
            var opened = await fileStore.TryOpenAsync(
                sessionFactory,
                openConfiguration,
                cancellationToken);
            INativeAppliedOwnershipSession session;
            var requiresInitialPersistence = false;
            switch (opened.Status)
            {
                case NativeAppliedOwnershipStatus.Ok when opened.Session is not null:
                    session = opened.Session;
                    break;
                case NativeAppliedOwnershipStatus.NoData when opened.Session is null:
                    session = sessionFactory.Create(in createConfiguration);
                    requiresInitialPersistence = true;
                    break;
                default:
                    opened.Session?.Dispose();
                    throw new InvalidDataException(
                        $"Native applied ownership open failed with {opened.Status}.");
            }

            NativeAppliedOwnershipOwner owner;
            try
            {
                owner = new NativeAppliedOwnershipOwner(fileStore, pathLease, session);
            }
            catch
            {
                session.Dispose();
                throw;
            }
            if (!requiresInitialPersistence)
            {
                return owner;
            }

            try
            {
                await fileStore.PersistAsync(session, owner.workspace, CancellationToken.None);
                return owner;
            }
            catch
            {
                await owner.DisposeAsync();
                throw;
            }
        }
        catch
        {
            pathLease.Dispose();
            throw;
        }
    }

    public Task<NativeAppliedOwnershipStatus> PromoteAsync(
        NativeAppliedOwnershipPromoteInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (INativeAppliedOwnershipSession ledger, in NativeAppliedOwnershipPromoteInput value) =>
                ledger.Promote(in value),
            cancellationToken);

    public Task<NativeAppliedOwnershipStatus> TransitionAsync(
        NativeAppliedOwnershipTransitionInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (INativeAppliedOwnershipSession ledger, in NativeAppliedOwnershipTransitionInput value) =>
                ledger.Transition(in value),
            cancellationToken);

    public async Task<NativeAppliedOwnershipTransitionPlanResult> PlanTransitionAsync(
        NativeAppliedOwnershipPrimaryIdentity primary,
        NativeAppliedOwnershipOriginalBinding transitionBinding,
        NativeAppliedOwnershipDurablePayloadReference transitionPayload,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var status = session.PlanTransition(
                in primary,
                in transitionBinding,
                in transitionPayload,
                out var transition);
            return new NativeAppliedOwnershipTransitionPlanResult(status, transition);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NativeAppliedOwnershipStatus> RemoveAsync(
        NativeAppliedOwnershipRemoveInput input,
        CancellationToken cancellationToken)
        => MutateAndPersistAsync(
            input,
            static (INativeAppliedOwnershipSession ledger, in NativeAppliedOwnershipRemoveInput value) =>
                ledger.Remove(in value),
            cancellationToken);

    public async Task<NativeAppliedOwnershipReadResult> GetAsync(
        NativeAppliedOwnershipPrimaryIdentity primary,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var status = session.Get(in primary, out var record);
            return new NativeAppliedOwnershipReadResult(status, record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NativeAppliedOwnershipSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var header = default(NativeAppliedOwnershipSnapshotHeader);
            workspace.Records.AsSpan().Clear();
            var status = session.GetSnapshot(ref header, workspace.Records);
            if (status != NativeAppliedOwnershipStatus.Ok ||
                header.EntryCount > workspace.Records.Length)
            {
                throw new InvalidOperationException(
                    $"Native applied ownership snapshot failed with {status}.");
            }

            var records = new NativeAppliedOwnershipRecord[header.EntryCount];
            Array.Copy(workspace.Records, records, records.Length);
            return new NativeAppliedOwnershipSnapshot(header, records);
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
            if (current is NativeAppliedOwnershipOwnerState.Disposing or
                NativeAppliedOwnershipOwnerState.Disposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref state,
                    (int)NativeAppliedOwnershipOwnerState.Disposing,
                    (int)current) == (int)current)
            {
                break;
            }
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                session.Dispose();
            }
            finally
            {
                pathLease.Dispose();
                Volatile.Write(ref state, (int)NativeAppliedOwnershipOwnerState.Disposed);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NativeAppliedOwnershipStatus> MutateAndPersistAsync<TInput>(
        TInput input,
        OwnershipMutation<TInput> mutation,
        CancellationToken cancellationToken)
        where TInput : unmanaged
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var status = mutation(session, in input);
            if (status != NativeAppliedOwnershipStatus.Ok)
            {
                return status;
            }

            await PersistOrFaultAsync();
            return NativeAppliedOwnershipStatus.Ok;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistOrFaultAsync()
    {
        try
        {
            await fileStore.PersistAsync(session, workspace, CancellationToken.None);
        }
        catch (Exception exception)
        {
            persistenceFailure = exception;
            _ = Interlocked.CompareExchange(
                ref state,
                (int)NativeAppliedOwnershipOwnerState.PersistenceFaulted,
                (int)NativeAppliedOwnershipOwnerState.Ready);
            throw new NativeAppliedOwnershipPersistenceException(exception);
        }
    }

    private void EnsureReady()
    {
        var current = State;
        if (current != NativeAppliedOwnershipOwnerState.Ready)
        {
            throw new InvalidOperationException(
                $"Native applied ownership owner is not ready: {current}.",
                persistenceFailure);
        }
    }

    private static void ValidateCompatibleConfigurations(
        in NativeAppliedOwnershipCreateConfiguration createConfiguration,
        in NativeAppliedOwnershipOpenConfiguration openConfiguration)
    {
        if (createConfiguration.AbiVersion != NativeAppliedOwnershipAbi.Version ||
            openConfiguration.AbiVersion != NativeAppliedOwnershipAbi.Version ||
            createConfiguration.StructSize != NativeAppliedOwnershipAbi.CreateConfigurationSize ||
            openConfiguration.StructSize != NativeAppliedOwnershipAbi.OpenConfigurationSize ||
            createConfiguration.RecordCapacity == 0 ||
            createConfiguration.RecordCapacity > openConfiguration.MaximumRecordCapacity ||
            createConfiguration.PrimaryIndexCapacity < createConfiguration.RecordCapacity ||
            createConfiguration.PrimaryIndexCapacity >
                openConfiguration.MaximumPrimaryIndexCapacity ||
            createConfiguration.PayloadIndexCapacity < createConfiguration.RecordCapacity ||
            createConfiguration.PayloadIndexCapacity >
                openConfiguration.MaximumPayloadIndexCapacity ||
            createConfiguration.MaximumResidentBytes == 0 ||
            createConfiguration.MaximumResidentBytes > openConfiguration.MaximumResidentBytes ||
            createConfiguration.MaximumImageBytes < NativeAppliedOwnershipAbi.ImageHeaderSize ||
            createConfiguration.MaximumImageBytes > openConfiguration.MaximumImageBytes ||
            createConfiguration.MaximumImageBytes > int.MaxValue ||
            openConfiguration.MaximumImageBytes > int.MaxValue)
        {
            throw new ArgumentException(
                "Applied ownership create/open configurations are not compatible.");
        }
    }

    private delegate NativeAppliedOwnershipStatus OwnershipMutation<TInput>(
        INativeAppliedOwnershipSession ledger,
        in TInput input)
        where TInput : unmanaged;
}

internal sealed class NativeAppliedOwnershipPersistenceException(Exception innerException)
    : IOException("Native applied ownership durability barrier failed.", innerException);

internal sealed record NativeAppliedOwnershipReadResult(
    NativeAppliedOwnershipStatus Status,
    NativeAppliedOwnershipRecord Record);

internal sealed record NativeAppliedOwnershipTransitionPlanResult(
    NativeAppliedOwnershipStatus Status,
    NativeAppliedOwnershipTransitionInput Transition);

internal sealed record NativeAppliedOwnershipSnapshot(
    NativeAppliedOwnershipSnapshotHeader Header,
    IReadOnlyList<NativeAppliedOwnershipRecord> Records);
