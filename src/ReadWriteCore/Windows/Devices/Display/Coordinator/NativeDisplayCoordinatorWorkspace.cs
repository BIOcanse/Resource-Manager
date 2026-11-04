using System.Diagnostics;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public enum HostManagerDisplayCoordinatorReadState
{
    Warming,
    Ready,
    Failed
}

public sealed record HostManagerDisplayCoordinatorReadModel(
    HostManagerDisplayCoordinatorReadState State,
    IReadOnlyList<DeviceTopologyResolvedDisplayPath> DisplayPaths,
    ulong ContentGeneration,
    ulong StateRevision);

public sealed class NativeDisplayCoordinatorWorkspace : IDisposable
{
    private readonly object gate = new();
    private readonly INativeDisplayCoordinatorSession session;
    private readonly NativeDisplayCoordinatorPersistenceCallbacks? persistenceStore;
    private readonly NativeDisplayCoordinatorConfiguration configuration;
    private readonly Func<DeviceTopologyDisplayPathSnapshot> capture;
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

    /// <summary>Caller supplies its explicit published configuration and optional original persistence operations.
    /// This owner has no worker; the host retains configuration replacement and retirement leases.</summary>
    public NativeDisplayCoordinatorWorkspace(in NativeDisplayCoordinatorConfiguration configuration,
        NativeDisplayCoordinatorPersistenceCallbacks? persistence = null)
        : this(configuration, new NativeDisplayCoordinatorSession(in configuration),
            WindowsDisplayPathTopologyReader.ReadSnapshot, persistence) { }

    internal NativeDisplayCoordinatorWorkspace(in NativeDisplayCoordinatorConfiguration configuration,
        NativeDisplayCoordinatorPersistenceCallbacks? persistence,
        Func<ulong, ResourceManager.ReadWrite.Native.INativeDisplayNormalizationScratch> createScratch)
        : this(configuration, new NativeDisplayCoordinatorSession(in configuration, createScratch),
            WindowsDisplayPathTopologyReader.ReadSnapshot, persistence) { }

    internal NativeDisplayCoordinatorWorkspace(NativeDisplayCoordinatorConfiguration configuration,
        INativeDisplayCoordinatorSession session, Func<DeviceTopologyDisplayPathSnapshot> capture,
        NativeDisplayCoordinatorPersistenceCallbacks? persistence = null)
    {
        this.configuration = configuration;
        this.session = session;
        this.capture = capture;
        persistenceStore = persistence;
        try { ImportPersistence(); }
        catch { session.Dispose(); throw; }
    }

    public NativeDisplayCoordinatorReadView ReadCurrent()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return new(readModel, Snapshot());
        }
    }

    /// <summary>Projects this owner's committed monitor paths. Caller selects directory identities;
    /// time is the native last successful capture, cleared unless the original phase is Ready.</summary>
    public DeviceInformationSnapshot ReadFields(Func<DeviceTopologyDisplayPath, string> deviceIdentity)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentity);
        lock (gate)
        {
            ThrowIfDisposed();
            var snapshot = Snapshot();
            var observedAt = readModel.State == HostManagerDisplayCoordinatorReadState.Ready
                && snapshot.LastSuccessUtcMilliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(snapshot.LastSuccessUtcMilliseconds) : (DateTimeOffset?)null;
            return WindowsInformationFieldProjection.Displays(readModel.DisplayPaths.Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path.MonitorDevicePath))
                .Select(path => WindowsDisplayPathTopologyReader.ToInformation(path, deviceIdentity(path))), observedAt);
        }
    }

    public HostManagerDisplayCoordinatorReadModel Refresh(
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
            var requiredMask = configuration.RequiredSourceMask;
            var requestedMask = requiredMask | configuration.OptionalSourceMask;
            var begin = new NativeDisplayRefreshInput
            {
                AbiVersion = NativeDisplayCoordinatorAbi.Version,
                StructSize = SizeOf<NativeDisplayRefreshInput>(),
                ConfigurationGeneration = configuration.Generation,
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
                var raw = capture();
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
                    ConfigurationGeneration = configuration.Generation,
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
                        ConfigurationGeneration = configuration.Generation,
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
            session.Dispose();
            disposed = true;
        }
    }

    private void ImportPersistence()
    {
        if (persistenceStore is null)
        {
            return;
        }

        var image = persistenceStore.Load(configuration.Generation);
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
            ConfigurationGeneration = configuration.Generation,
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
            ConfigurationGeneration = configuration.Generation,
            OperationEpoch = operationEpoch,
            ValidMask = (ulong)NativeDisplayPersistenceValidity.Required
        };
        RequireOk(
            session.ExportPersistence(in input, out var header, facts, texts, bytes),
            "export persistence");
        persistenceStore.Save(
            configuration.Generation,
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
            ConfigurationGeneration = configuration.Generation,
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
            ConfigurationGeneration = configuration.Generation,
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
            || result.ConfigurationGeneration != configuration.Generation)
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
