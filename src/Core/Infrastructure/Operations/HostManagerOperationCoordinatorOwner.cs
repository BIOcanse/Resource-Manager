using ResourceManager.App.Infrastructure.Adaptation;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner :
    IHostedService,
    IHostManagerOperationCommandService,
    IHostManagerOperationQueryService,
    IHostManagerOperationProgressSink,
    IDisposable,
    IAsyncDisposable
{
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly SemaphoreSlim plannerWake = new(0, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerOperationCoordinatorRuntime deployment;
    private readonly HostManagerOperationEffectRouter effectRouter;
    private HostManagerOperationAttemptArbiter attemptArbiter = new();
    private readonly string hostManagerDataRoot;
    private readonly ILogger<HostManagerOperationCoordinatorOwner> logger;
    private readonly TimeProvider timeProvider;
    private NativeOperationCoordinatorWorkspace? workspace;
    private NativeOperationCoordinatorWorkspace? pendingWorkspaceClose;
    private readonly Func<ulong, IComputeScratch> createScratch;
    private HostManagerOperationCanonicalStore? store;
    private HostManagerOperationPayloadCatalog payloads = new();
    private HostManagerOperationEffectReceiptTable receipts = new();
    private CompiledHostManagerOperationCoordinatorPlan? appliedPlan;
    private Channel<HostManagerOperationActionTicket>? actionChannel;
    private CancellationTokenSource? workerCancellation;
    private Task? plannerWorker;
    private Task[] actionWorkers = [];
    private CommittedState committedState = CommittedState.Empty;
    private byte[] lastEnvelopeSha256 =
        HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest();
    private ulong publicationRevision;
    private ulong committedStateRevision;
    private bool ready;
    private bool persistenceFaulted;
    private string? faultStage;
    private string? faultMessage;
    private int pendingQuiescentRecreate;
    private string? lastPlanApplicationError;
    private bool started;
    private bool retiring;
    private bool disposed;

    public HostManagerOperationCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerOperationCoordinatorRuntime deployment,
        HostManagerOperationEffectRouter effectRouter,
        IHostEnvironment environment,
        ILogger<HostManagerOperationCoordinatorOwner> logger,
        ResourceManagerSelfLocalResourceManager selfLocalResources)
        : this(planProvider, deployment, effectRouter, environment, logger,
            (selfLocalResources ?? throw new ArgumentNullException(nameof(selfLocalResources))).CreateScratch)
    {
    }

    internal HostManagerOperationCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerOperationCoordinatorRuntime deployment,
        HostManagerOperationEffectRouter effectRouter,
        IHostEnvironment environment,
        ILogger<HostManagerOperationCoordinatorOwner> logger,
        Func<ulong, IComputeScratch> createScratch,
        TimeProvider? timeProvider = null)
    {
        this.createScratch = createScratch ?? throw new ArgumentNullException(nameof(createScratch));
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.effectRouter = effectRouter;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        hostManagerDataRoot = Path.Combine(
            ResourceManager.App.Infrastructure.Optimization.HostManagerDurableStorePaths.ResolveRuntimeDataRoot(
                PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath)), "HostManager");
    }

    public HostManagerOperationSnapshot? Get(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var state = Volatile.Read(ref committedState);
        return state.ById.GetValueOrDefault(operationId);
    }

    public IReadOnlyList<HostManagerOperationSnapshot> GetRecent()
        => Volatile.Read(ref committedState).Recent;

    public HostManagerOperationsPublishedState GetPublishedState()
    {
        var state = Volatile.Read(ref committedState);
        return new HostManagerOperationsPublishedState(
            state.CapturedAt,
            state.PublicationRevision,
            state.ConfigurationGeneration,
            state.Health,
            state.Recent);
    }

    public async IAsyncEnumerable<HostManagerOperationsPublishedState> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        using var publication = publicationSignal.Subscribe();
        yield return GetPublishedState();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return GetPublishedState();
        }
    }

    public HostManagerOperationCoordinatorHealth GetHealth()
        => Volatile.Read(ref committedState).Health;

    internal bool HasPendingQuiescentRecreate
        => Volatile.Read(ref pendingQuiescentRecreate) != 0;

    internal string? LastPlanApplicationError
        => Volatile.Read(ref lastPlanApplicationError);

    private void OnPublished(CompiledRuntimePlan _publishedPlan)
    {
        _ = ApplyPublishedPlanAsync();
    }

    private async Task ApplyPublishedPlanAsync()
    {
        try
        {
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanAsync(
                    publication.Plan.HostManager.RequirePublished(),
                    throwOnFailure: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Volatile.Write(ref lastPlanApplicationError, error.Message);
            logger.LogError(
                error,
                "Host Manager operation-coordinator plan application failed.");
        }
    }

    private async Task ApplyPendingQuiescentPlanAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref pendingQuiescentRecreate) == 0)
        {
            return;
        }

        using var publication = planProvider.AcquirePublicationLease();
        await ApplyPlanAsync(
                publication.Plan.HostManager.RequirePublished(),
                throwOnFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void PublishLocked(NativeOperationCanonicalImage image)
    {
        var next = BuildCommittedState(
            image.Records,
            payloads,
            appliedPlan?.HotPublish.ConfigurationGeneration ?? 0,
            publicationRevision,
            NextCommittedStateRevisionLocked(),
            timeProvider.GetUtcNow());
        Volatile.Write(ref committedState, next);
        publicationSignal.Publish();
    }

    private void PublishHealthOnlyLocked()
    {
        var current = Volatile.Read(ref committedState);
        var capturedAt = timeProvider.GetUtcNow();
        if (capturedAt < current.CapturedAt)
        {
            capturedAt = current.CapturedAt;
        }
        Volatile.Write(
            ref committedState,
            current with
            {
                CapturedAt = capturedAt,
                PublicationRevision = NextCommittedStateRevisionLocked(),
                Health = CreateHealth(
                    current.Health.PublicationRevision,
                    current.ConfigurationGeneration)
            });
        publicationSignal.Publish();
    }

    private CommittedState BuildCommittedState(
        IReadOnlyList<NativeOperationOutput> operations,
        HostManagerOperationPayloadCatalog sourcePayloads,
        ulong configurationGeneration,
        ulong canonicalPublicationRevision,
        ulong stateRevision,
        DateTimeOffset capturedAt)
    {
        var snapshots = operations
            .Select(operation => ProjectSnapshot(
                operation,
                sourcePayloads,
                configurationGeneration))
            .OrderByDescending(static snapshot => snapshot.UpdatedAt)
            .ToArray();
        if (snapshots.Length != 0 && capturedAt < snapshots[0].UpdatedAt)
        {
            capturedAt = snapshots[0].UpdatedAt;
        }
        return new CommittedState(
            capturedAt,
            stateRevision,
            configurationGeneration,
            snapshots.ToFrozenDictionary(
                static snapshot => snapshot.Id,
                StringComparer.Ordinal),
            snapshots,
            CreateHealth(
                canonicalPublicationRevision,
                configurationGeneration));
    }

    private ulong NextCommittedStateRevisionLocked()
    {
        committedStateRevision = checked(committedStateRevision + 1);
        return committedStateRevision;
    }

    private HostManagerOperationSnapshot ProjectSnapshot(
        NativeOperationOutput operation,
        HostManagerOperationPayloadCatalog sourcePayloads,
        ulong configurationGeneration)
    {
        var kind = ReadText(sourcePayloads, operation.KindHandle);
        var domain = ReadOptionalText(
            sourcePayloads,
            operation.DomainId,
            (operation.Flags & (ulong)NativeOperationFlags.DomainValid) != 0);
        var subject = (operation.Flags & (ulong)NativeOperationFlags.SubjectValid) != 0
            ? HostManagerOperationRequestCodec.DecodeSubject(sourcePayloads.Require(operation.SubjectHandle).Payload)
            : null;
        var result = ReadOptionalText(
            sourcePayloads,
            operation.ResultHandle,
            (operation.Flags & (ulong)NativeOperationFlags.ResultValid) != 0);
        var error = ReadOptionalText(
            sourcePayloads,
            operation.ErrorHandle,
            (operation.Flags & (ulong)NativeOperationFlags.ErrorValid) != 0);
        HostManagerOperationProgress? progress = null;
        if ((operation.Flags & (ulong)NativeOperationFlags.ProgressValid) != 0)
        {
            progress = new HostManagerOperationProgress(
                operation.ProgressSequence,
                (operation.ProgressValidMask
                    & (ulong)NativeOperationProgressValidity.PercentMilli) != 0
                    ? operation.PercentMilli / 1000D
                    : null,
                (operation.ProgressValidMask
                    & (ulong)NativeOperationProgressValidity.BytesDone) != 0
                    ? operation.BytesDone
                    : null,
                (operation.ProgressValidMask
                    & (ulong)NativeOperationProgressValidity.BytesTotal) != 0
                    ? operation.BytesTotal
                    : null,
                (operation.ProgressValidMask
                    & (ulong)NativeOperationProgressValidity.SpeedBytesPerSecond) != 0
                    ? operation.SpeedBytesPerSecond
                    : null,
                (operation.ProgressValidMask & (ulong)NativeOperationProgressValidity.Stage) != 0
                    ? HostManagerOperationRequestCodec.DecodeStage(sourcePayloads.Require(operation.StageFactHandle).Payload)
                    : null);
        }
        var state = MapState((NativeOperationState)operation.State);
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            operation.UpdatedUtcMilliseconds);
        return new HostManagerOperationSnapshot(
            FormatHandle(operation.OperationId),
            kind,
            domain,
            subject,
            state,
            configurationGeneration,
            operation.StateRevision,
            operation.AttemptNumber,
            operation.MaximumAttempts,
            DateTimeOffset.FromUnixTimeMilliseconds(
                operation.CreatedUtcMilliseconds),
            updatedAt,
            HostManagerOperationStates.IsTerminal(state) ? updatedAt : null,
            (operation.Flags & (ulong)NativeOperationFlags.CancelRequested) != 0,
            progress,
            result,
            error);
    }

    private static string ReadText(
        HostManagerOperationPayloadCatalog sourcePayloads,
        NativeOperationHandle128 handle)
        => HostManagerOperationRequestCodec.DecodeText(
            sourcePayloads.Require(handle).Payload);

    private static string? ReadOptionalText(
        HostManagerOperationPayloadCatalog sourcePayloads,
        NativeOperationHandle128 handle,
        bool valid)
    {
        if (!valid)
        {
            if (!handle.IsZero)
            {
                throw new InvalidDataException(
                    "An operation payload handle is populated without validity.");
            }
            return null;
        }
        return ReadText(sourcePayloads, handle);
    }

    private HostManagerOperationCoordinatorHealth CreateHealth(
        ulong canonicalPublicationRevision,
        ulong configurationGeneration)
        => new(
            ready,
            persistenceFaulted,
            faultStage,
            faultMessage,
            canonicalPublicationRevision,
            configurationGeneration);

    private static string MapState(NativeOperationState state)
        => state switch
        {
            NativeOperationState.Queued => HostManagerOperationStates.Queued,
            NativeOperationState.StartPending =>
                HostManagerOperationStates.StartPending,
            NativeOperationState.Running => HostManagerOperationStates.Running,
            NativeOperationState.CancelPending =>
                HostManagerOperationStates.CancelPending,
            NativeOperationState.RetryWait =>
                HostManagerOperationStates.RetryWait,
            NativeOperationState.RecoveryPending =>
                HostManagerOperationStates.RecoveryPending,
            NativeOperationState.Succeeded => HostManagerOperationStates.Succeeded,
            NativeOperationState.Failed => HostManagerOperationStates.Failed,
            NativeOperationState.Canceled => HostManagerOperationStates.Canceled,
            NativeOperationState.StateUncertain =>
                HostManagerOperationStates.StateUncertain,
            _ => throw new InvalidDataException(
                "The native operation state is unknown.")
        };

    private static string FormatHandle(NativeOperationHandle128 handle)
        => string.Create(
            32,
            handle,
            static (span, value) =>
            {
                _ = value.High.TryFormat(
                    span[..16],
                    out _,
                    "x16",
                    CultureInfo.InvariantCulture);
                _ = value.Low.TryFormat(
                    span[16..],
                    out _,
                    "x16",
                    CultureInfo.InvariantCulture);
            });

    private static bool TryParseHandle(
        string value,
        out NativeOperationHandle128 handle)
    {
        handle = default;
        return value.Length == 32
            && ulong.TryParse(
                value.AsSpan(0, 16),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var high)
            && ulong.TryParse(
                value.AsSpan(16, 16),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var low)
            && !(handle = new NativeOperationHandle128(high, low)).IsZero;
    }

    private void SignalPlanner()
    {
        try
        {
            _ = plannerWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private void RequireMutableLocked()
    {
        ThrowIfRetiringLocked();
        if (!ready || workspace is null || store is null || appliedPlan is null)
        {
            throw new InvalidOperationException(
                "The operation coordinator owner is not ready.");
        }
        if (persistenceFaulted)
        {
            throw new InvalidOperationException(
                "The operation coordinator owner is permanently faulted; restart is required.");
        }
    }

    private void ThrowIfRetiringLocked()
    {
        ThrowIfDisposed();
        if (retiring)
        {
            throw new InvalidOperationException(
                "The operation coordinator owner is retiring.");
        }
    }

    private bool TryRequireMutableLocked()
    {
        if (retiring || disposed)
        {
            return false;
        }
        RequireMutableLocked();
        return true;
    }

    private sealed record CommittedState(
        DateTimeOffset CapturedAt,
        ulong PublicationRevision,
        ulong ConfigurationGeneration,
        FrozenDictionary<string, HostManagerOperationSnapshot> ById,
        IReadOnlyList<HostManagerOperationSnapshot> Recent,
        HostManagerOperationCoordinatorHealth Health)
    {
        internal static CommittedState Empty { get; } = new(
            DateTimeOffset.UnixEpoch,
            0,
            0,
            FrozenDictionary<string, HostManagerOperationSnapshot>.Empty,
            [],
            new(false, false, null, null, 0, 0));
    }
}
