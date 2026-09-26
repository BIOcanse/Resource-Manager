using System.Diagnostics;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal enum HostManagerDisplayCoordinatorReadState
{
    Warming,
    Ready,
    Failed
}

internal sealed record HostManagerDisplayCoordinatorReadModel(
    HostManagerDisplayCoordinatorReadState State,
    IReadOnlyList<DeviceTopologyResolvedDisplayPath> DisplayPaths,
    ulong ContentGeneration,
    ulong StateRevision);

internal sealed class NativeDisplayCoordinatorWorkspace : IDisposable
{
    private readonly object gate = new();
    private readonly NativeDisplayCoordinatorSession session;
    private readonly NativeDisplayCoordinatorPersistenceStore? persistenceStore;
    private readonly CompiledHostManagerDisplayCoordinatorPlan plan;
    private NativeDisplayCoordinatorPayloadCatalog payloads = new();
    private readonly ulong[] sourceGenerations = new ulong[NativeDisplayCoordinatorAbi.SourceCount];
    private HostManagerDisplayCoordinatorReadModel readModel = new(
        HostManagerDisplayCoordinatorReadState.Warming,
        [],
        0,
        0);
    private ulong nextRefreshEpoch;
    private ulong nextPersistenceEpoch;
    private ulong lastMonotonicMilliseconds;
    private SemanticState? committedState;
    private bool disposed;

    internal NativeDisplayCoordinatorWorkspace(
        CompiledHostManagerDisplayCoordinatorPlan plan,
        string? persistencePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new ArgumentException(
                "The display-coordinator plan must be published.",
                nameof(plan));
        }
        this.plan = plan;
        persistenceStore = string.IsNullOrWhiteSpace(persistencePath)
            ? null
            : new NativeDisplayCoordinatorPersistenceStore(persistencePath);
        var configuration = CreateConfiguration(plan);
        session = new NativeDisplayCoordinatorSession(in configuration);
        try
        {
            ImportPersistence();
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal HostManagerDisplayCoordinatorReadModel Refresh(
        IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (gate)
        {
            ThrowIfDisposed();
            var capturedAt = DateTimeOffset.UtcNow;
            var refreshEpoch = Next(ref nextRefreshEpoch, "display refresh epoch");
            var monotonicMilliseconds = AdvanceMonotonicMilliseconds(
                lastMonotonicMilliseconds,
                MonotonicMilliseconds());
            var requiredMask = plan.Recreate.RequiredSourceMask;
            var requestedMask = requiredMask | plan.Recreate.OptionalSourceMask;
            var begin = new NativeDisplayRefreshInput
            {
                AbiVersion = NativeDisplayCoordinatorAbi.Version,
                StructSize = SizeOf<NativeDisplayRefreshInput>(),
                ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
                RefreshEpoch = refreshEpoch,
                CapturedUtcMilliseconds = capturedAt.ToUnixTimeMilliseconds(),
                MonotonicMilliseconds = monotonicMilliseconds,
                RequiredSourceMask = requiredMask,
                RequestedSourceMask = requestedMask,
                ValidMask = (ulong)NativeDisplayRefreshValidity.Required
            };
            RequireOk(session.BeginRefresh(in begin), "begin refresh");
            lastMonotonicMilliseconds = monotonicMilliseconds;

            var nextPayloads = payloads.Clone();
            var finalized = false;
            try
            {
                var raw = WindowsDisplayPathTopologyReader.ReadSnapshot();
                IReadOnlyList<NativeDisplaySourceBatch> batches;
                if (raw.Error is null)
                {
                    batches = NativeDisplayCoordinatorObservationProjector.Create(
                        raw.Paths,
                        profiles,
                        nextPayloads,
                        capturedAt);
                }
                else
                {
                    batches = CreateUnavailableBatches();
                }
                foreach (var batch in batches)
                {
                    Submit(batch, refreshEpoch, capturedAt);
                }

                var finalize = new NativeDisplayFinalizeInput
                {
                    AbiVersion = NativeDisplayCoordinatorAbi.Version,
                    StructSize = SizeOf<NativeDisplayFinalizeInput>(),
                    ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
                    RefreshEpoch = refreshEpoch,
                    ExpectedSourceMask = requestedMask,
                    ValidMask = (ulong)NativeDisplayRefreshValidity.RefreshEpoch
                };
                RequireOk(session.FinalizeRefresh(in finalize), "finalize refresh");
                finalized = true;
                var snapshot = Snapshot();
                var semanticState = SemanticState.From(snapshot);
                if (semanticState == committedState)
                {
                    return readModel;
                }

                if (snapshot.ContentGeneration != readModel.ContentGeneration)
                {
                    payloads = nextPayloads;
                }
                readModel = ProjectReadModel(snapshot);
                Persist(snapshot);
                committedState = semanticState;
                return readModel;
            }
            catch
            {
                if (!finalized)
                {
                    var abort = new NativeDisplayAbortInput
                    {
                        AbiVersion = NativeDisplayCoordinatorAbi.Version,
                        StructSize = SizeOf<NativeDisplayAbortInput>(),
                        ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
                        RefreshEpoch = refreshEpoch,
                        ValidMask = (ulong)NativeDisplayRefreshValidity.RefreshEpoch
                    };
                    _ = session.AbortRefresh(in abort);
                }
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            session.Dispose();
        }
    }

    private void ImportPersistence()
    {
        if (persistenceStore is null)
        {
            return;
        }

        var image = persistenceStore.Load(plan.Recreate.ConfigurationGeneration);
        if (image is null)
        {
            return;
        }
        var importedPayloads = new NativeDisplayCoordinatorPayloadCatalog();
        importedPayloads.Import(image.Payloads);
        var header = image.Header;
        var input = new NativeDisplayPersistenceInput
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = SizeOf<NativeDisplayPersistenceInput>(),
            ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
            OperationEpoch = header.OperationEpoch,
            ValidMask = (ulong)NativeDisplayPersistenceValidity.Required
        };
        RequireOk(
            session.ImportPersistence(
                in input,
                in header,
                image.Facts,
                image.Texts,
                image.TextBytes),
            "import persistence");
        unsafe
        {
            for (var index = 0; index < sourceGenerations.Length; index++)
            {
                sourceGenerations[index] = header.SourceGenerations[index];
            }
        }
        payloads = importedPayloads;
        nextRefreshEpoch = header.LastRefreshEpoch;
        nextPersistenceEpoch = header.OperationEpoch;
        lastMonotonicMilliseconds = header.LastMonotonicMilliseconds;
        var snapshot = Snapshot();
        readModel = ProjectReadModel(snapshot);
        committedState = SemanticState.From(snapshot);
    }

    private void Persist(NativeDisplaySnapshotOutput snapshot)
    {
        if (persistenceStore is null || snapshot.ContentGeneration == 0)
        {
            return;
        }
        var facts = new NativeDisplayFact[checked((int)session.Capacity.MaximumObservationCount)];
        var texts = new NativeDisplayTextInput[checked((int)session.Capacity.MaximumTextBindingCount)];
        var bytes = new byte[checked((int)session.Capacity.MaximumTextByteCount)];
        var operationEpoch = Next(ref nextPersistenceEpoch, "display persistence epoch");
        var input = new NativeDisplayPersistenceInput
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = SizeOf<NativeDisplayPersistenceInput>(),
            ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
            OperationEpoch = operationEpoch,
            ValidMask = (ulong)NativeDisplayPersistenceValidity.Required
        };
        RequireOk(
            session.ExportPersistence(in input, out var header, facts, texts, bytes),
            "export persistence");
        persistenceStore.Save(
            plan.Recreate.ConfigurationGeneration,
            new NativeDisplayCoordinatorPersistenceImage(
                header,
                facts[..checked((int)header.FactCount)],
                texts[..checked((int)header.TextCount)],
                bytes[..checked((int)header.TextByteCount)],
                payloads.Export()));
    }

    private HostManagerDisplayCoordinatorReadModel ProjectReadModel(
        NativeDisplaySnapshotOutput snapshot)
    {
        var read = new NativeDisplayReadInput
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = SizeOf<NativeDisplayReadInput>(),
            ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
            StateRevision = snapshot.StateRevision,
            ContentGeneration = snapshot.ContentGeneration,
            ValidMask = (ulong)NativeDisplayReadValidity.Required
        };
        var nodes = new NativeDisplayNodeOutput[checked((int)snapshot.NodeCount)];
        RequireOk(session.ReadNodes(in read, nodes), "read nodes");
        var phase = (NativeDisplayCoordinatorPhase)snapshot.Phase;
        return new HostManagerDisplayCoordinatorReadModel(
            phase switch
            {
                NativeDisplayCoordinatorPhase.Ready => HostManagerDisplayCoordinatorReadState.Ready,
                NativeDisplayCoordinatorPhase.Warming => HostManagerDisplayCoordinatorReadState.Warming,
                _ => HostManagerDisplayCoordinatorReadState.Failed
            },
            NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(nodes, payloads),
            snapshot.ContentGeneration,
            snapshot.StateRevision);
    }

    private void Submit(
        NativeDisplaySourceBatch batch,
        ulong refreshEpoch,
        DateTimeOffset capturedAt)
    {
        var sourceIndex = checked((int)batch.Source - 1);
        var sourceGeneration = Next(
            ref sourceGenerations[sourceIndex],
            $"{batch.Source} source generation");
        var header = new NativeDisplaySourceBatchHeader
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = SizeOf<NativeDisplaySourceBatchHeader>(),
            ConfigurationGeneration = plan.Recreate.ConfigurationGeneration,
            RefreshEpoch = refreshEpoch,
            SourceGeneration = sourceGeneration,
            CapturedUtcMilliseconds = capturedAt.ToUnixTimeMilliseconds(),
            SourceId = (uint)batch.Source,
            Status = (uint)batch.Status,
            FactCount = checked((uint)batch.Facts.Length),
            TextBindingCount = checked((uint)batch.Texts.Length),
            TextByteCount = checked((uint)batch.TextBytes.Length),
            ValidMask = (ulong)NativeDisplayBatchValidity.Required
        };
        RequireOk(
            session.SubmitSource(
                in header,
                batch.Facts,
                batch.Texts,
                batch.TextBytes),
            $"submit {batch.Source}");
    }

    private NativeDisplaySnapshotOutput Snapshot()
    {
        RequireOk(session.Snapshot(out var result), "read snapshot");
        if (result.AbiVersion != NativeDisplayCoordinatorAbi.Version
            || result.StructSize != SizeOf<NativeDisplaySnapshotOutput>()
            || result.ConfigurationGeneration != plan.Recreate.ConfigurationGeneration)
        {
            throw new InvalidOperationException(
                "The native display-coordinator snapshot is non-canonical.");
        }
        return result;
    }

    private static IReadOnlyList<NativeDisplaySourceBatch> CreateUnavailableBatches()
        =>
        [
            new(
                NativeDisplaySource.DisplayConfig,
                NativeDisplaySourceStatus.Unavailable,
                [],
                [],
                []),
            new(NativeDisplaySource.Dxgi, NativeDisplaySourceStatus.Unavailable, [], [], []),
            new(NativeDisplaySource.Edid, NativeDisplaySourceStatus.Unavailable, [], [], []),
            new(
                NativeDisplaySource.SetupApiMonitor,
                NativeDisplaySourceStatus.Unsupported,
                [],
                [],
                []),
            new(
                NativeDisplaySource.OemConnectorProfile,
                NativeDisplaySourceStatus.Unavailable,
                [],
                [],
                [])
        ];

    private static unsafe NativeDisplayCoordinatorConfiguration CreateConfiguration(
        CompiledHostManagerDisplayCoordinatorPlan plan)
    {
        var recreate = plan.Recreate;
        var capacity = recreate.Capacity;
        return new NativeDisplayCoordinatorConfiguration
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = SizeOf<NativeDisplayCoordinatorConfiguration>(),
            Generation = recreate.ConfigurationGeneration,
            MaximumSourceCount = capacity.MaximumSourceCount,
            MaximumObservationCount = capacity.MaximumObservationCount,
            MaximumNodeCount = capacity.MaximumNodeCount,
            MaximumEdgeCount = capacity.MaximumEdgeCount,
            MaximumCapabilityCount = capacity.MaximumCapabilityCount,
            MaximumDiffEntryCount = capacity.MaximumDiffEntryCount,
            MaximumTextBindingCount = capacity.MaximumTextBindingCount,
            MaximumTextByteCount = capacity.MaximumTextByteCount,
            MaximumUnresolvedCount = capacity.MaximumUnresolvedCount,
            MaximumSourceBatchCount = capacity.MaximumSourceBatchCount,
            IdentityIndexCapacity = capacity.IdentityIndexCapacity,
            TextIndexCapacity = capacity.TextIndexCapacity,
            ResidentByteBudget = capacity.ResidentByteBudget,
            RequiredSourceMask = recreate.RequiredSourceMask,
            OptionalSourceMask = recreate.OptionalSourceMask,
            IdentitySourcePriorityOrder = recreate.IdentitySourcePriorityOrder,
            FriendlyNameSourcePriorityOrder = recreate.FriendlyNameSourcePriorityOrder,
            CapabilitySourcePriorityOrder = recreate.CapabilitySourcePriorityOrder,
            ProjectionSourcePriorityOrder = recreate.ProjectionSourcePriorityOrder,
            IdentityContractVersion = recreate.IdentityContractVersion,
            CapabilityContractVersion = recreate.CapabilityContractVersion,
            MaximumFutureSkewMilliseconds = recreate.MaximumFutureSkewMilliseconds
        };
    }

    private static ulong Next(ref ulong value, string name)
    {
        if (value == ulong.MaxValue)
        {
            throw new InvalidOperationException($"{name} space is exhausted.");
        }
        return ++value;
    }

    private static ulong MonotonicMilliseconds()
        => checked((ulong)(Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency));

    internal static ulong AdvanceMonotonicMilliseconds(ulong highWaterMark, ulong observed)
    {
        if (observed > highWaterMark)
        {
            return observed;
        }
        if (highWaterMark == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The display monotonic clock space is exhausted.");
        }
        return highWaterMark + 1;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static void RequireOk(NativeDisplayCoordinatorStatus status, string operation)
    {
        if (status != NativeDisplayCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native display-coordinator {operation} failed with {status}.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private readonly record struct SemanticState(
        ulong ContentGeneration,
        uint Phase,
        uint Flags,
        ulong CurrentSourceMask,
        ulong RetainedSourceMask,
        ulong FailedSourceMask,
        ulong UnsupportedSourceMask)
    {
        internal static SemanticState From(NativeDisplaySnapshotOutput snapshot)
            => new(
                snapshot.ContentGeneration,
                snapshot.Phase,
                snapshot.Flags,
                snapshot.CurrentSourceMask,
                snapshot.RetainedSourceMask,
                snapshot.FailedSourceMask,
                snapshot.UnsupportedSourceMask);
    }
}
