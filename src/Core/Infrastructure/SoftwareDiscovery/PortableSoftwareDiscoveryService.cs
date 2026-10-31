using BlockUids = ResourceManager.App.StateItems.BlockUids;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

public sealed class PortableSoftwareDiscoveryService : IPortableSoftwareDiscovery, IHostedService, IDisposable
{
    private readonly IKernelEtwSessionBroker kernelEtwSessionBroker;
    private readonly IRuntimeProcessAttributionCatalogProvider attributionCatalogProvider;
    private readonly ISoftwareIdentityCatalog softwareIdentityCatalog;
    private readonly IPortableSoftwareRegistry portableSoftwareRegistry;
    private readonly ILogger<PortableSoftwareDiscoveryService> logger;
    private readonly Func<PortableProcessCandidate[]> captureCurrentProcesses;
    private readonly Func<PortableProcessCandidate, RuntimeProcessIdentity?> readIdentity;
    private readonly IBusinessBlockDiagnostics? blockDiagnostics;
    private readonly object failureLogGate = new();
    private readonly Dictionary<string, long> failureLogTimes = new();
    private Channel<PortableProcessCandidate> processStarts = CreateProcessStarts();
    private static Channel<PortableProcessCandidate> CreateProcessStarts() => Channel.CreateBounded<PortableProcessCandidate>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private IKernelEtwSubscription? processSubscription;
    private readonly object nativeBindingGate = new();
    private NativeBinding? nativeBinding;
    private Channel<bool> publications = CreatePublications();
    private static Channel<bool> CreatePublications() => Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? workerCts;
    private Task? workerTask;
    internal Task? ExecutionTask => workerTask;
    private Exception? handledCatalogFailure;
    private Exception? workerCleanupFailure;
    internal bool CatalogFailureHandled => Volatile.Read(ref handledCatalogFailure) is { } failure
        && Volatile.Read(ref workerCleanupFailure) is null
        && workerTask is { IsFaulted: true } task
        && task.Exception!.InnerExceptions.Any(error => ReferenceEquals(error, failure));
    private int disposed;
    private KernelEtwSessionSnapshot? inventorySnapshot;
    // (process id, start key) of every process in the last inventory handed to the worker.
    private HashSet<(int ProcessId, long StartKey)> inventoryProcesses = [];

    public PortableSoftwareDiscoveryService(
        IKernelEtwSessionBroker kernelEtwSessionBroker,
        IRuntimeProcessAttributionCatalogProvider attributionCatalogProvider,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        IPortableSoftwareRegistry portableSoftwareRegistry,
        ILogger<PortableSoftwareDiscoveryService> logger,
        IBusinessBlockDiagnostics? blockDiagnostics = null)
        : this(kernelEtwSessionBroker, attributionCatalogProvider, softwareIdentityCatalog,
            portableSoftwareRegistry, logger, CaptureCurrentProcesses, blockDiagnostics: blockDiagnostics)
    {
    }

    internal PortableSoftwareDiscoveryService(
        IKernelEtwSessionBroker kernelEtwSessionBroker,
        IRuntimeProcessAttributionCatalogProvider attributionCatalogProvider,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        IPortableSoftwareRegistry portableSoftwareRegistry,
        ILogger<PortableSoftwareDiscoveryService> logger,
        Func<PortableProcessCandidate[]> captureCurrentProcesses,
        Func<PortableProcessCandidate, RuntimeProcessIdentity?>? readIdentity = null,
        IBusinessBlockDiagnostics? blockDiagnostics = null)
    {
        this.blockDiagnostics = blockDiagnostics;
        this.kernelEtwSessionBroker = kernelEtwSessionBroker;
        this.attributionCatalogProvider = attributionCatalogProvider;
        this.softwareIdentityCatalog = softwareIdentityCatalog;
        this.portableSoftwareRegistry = portableSoftwareRegistry;
        this.logger = logger;
        this.captureCurrentProcesses = captureCurrentProcesses;
        this.readIdentity = readIdentity ?? new WindowsPortableProcessIdentityReader().TryRead;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (workerTask is { IsCompleted: false })
            throw new InvalidOperationException("The preceding portable discovery run has not drained.");
        workerCts?.Dispose();
        Volatile.Write(ref handledCatalogFailure, null);
        Volatile.Write(ref workerCleanupFailure, null);
        var runStarts = processStarts = CreateProcessStarts();
        var runPublications = publications = CreatePublications();
        var runCts = workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = runCts.Token;
        try
        {
            processSubscription = kernelEtwSessionBroker.Acquire(new KernelEtwSubscriptionRequest(
                "portable-software-discovery",
                KernelTraceEventParser.Keywords.Process,
                (kernel, _) => BindNativeSource(kernel, runPublications, workerToken))
                { NativeOutputs = KernelEtwNativeOutputs.Notifications });
            inventorySnapshot = kernelEtwSessionBroker.GetSnapshot();
            EnqueueCurrentProcesses(runStarts);
            workerTask = Task.Run(() => RunWorkerAsync(runStarts, runPublications, runCts), CancellationToken.None);
        }
        catch
        {
            workerCts.Cancel();
            ReleaseNativeBinding();
            try { Interlocked.Exchange(ref processSubscription, null)?.Dispose(); }
            finally { workerCts.Dispose(); workerCts = null; }
            throw;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        processStarts.Writer.TryComplete();
        publications.Writer.TryComplete();
        var cts = workerCts;
        cts?.Cancel();
        ReleaseNativeBinding();
        try
        {
            Interlocked.Exchange(ref processSubscription, null)?.Dispose();
        }
        finally
        {
            // A cleared CTS means this run was already joined. Keep its task
            // for diagnostics without replaying a reported cleanup failure.
            if (cts is not null && workerTask is { } task)
            {
                try { await task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (task.IsCompleted) { }
                // The run result remains on ExecutionTask for its supervisor.
                // Joining the worker is cleanup, not a second report of that result.
                catch (Exception) when (task.IsFaulted) { }
                finally
                {
                    if (task.IsCompleted && ReferenceEquals(workerCts, cts))
                    {
                        workerCts = null;
                        cts?.Dispose();
                    }
                }
            }
        }
        if (cts is not null && Volatile.Read(ref workerCleanupFailure) is { } cleanupFailure)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    public async Task ScanRunningProcessesAsync(CancellationToken cancellationToken)
    {
        using var catalog = await attributionCatalogProvider.AcquireCatalogAsync(cancellationToken);
        var candidates = captureCurrentProcesses().Where(static candidate => candidate.ProcessId > 4);
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            async (candidate, token) =>
                await ObserveCandidateAsync(candidate, catalog.Catalog.Pipeline, token).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task RunWorkerAsync(Channel<PortableProcessCandidate> runStarts, Channel<bool> runPublications,
        CancellationTokenSource runCts)
    {
        Exception? runFailure = null;
        var cancellationToken = runCts.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var nextCheck = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var nextCandidate = runStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var nextPublication = runPublications.Reader.WaitToReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAny(nextCheck, nextCandidate, nextPublication);
                if (nextPublication.IsCompleted)
                {
                    if (!await nextPublication) break;
                    while (runPublications.Reader.TryRead(out _)) { }
                    var binding = Volatile.Read(ref nativeBinding);
                    if (binding is { Source.IsActive: true, Source.NativeSession: { } session })
                    {
                        try
                        {
                            KernelEtwNativeNotifications.Read(session, ref binding.Cursor,
                                (view, skipped) => ObserveNativePublication(binding.Source, view, skipped));
                        }
                        catch (ObjectDisposedException) when (!binding.Source.IsActive) { }
                    }
                    nextPublication = runPublications.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }
                if (nextCheck.IsCompleted)
                {
                    if (!await nextCheck) break;
                    RefreshInventoryAfterLoss(kernelEtwSessionBroker.GetSnapshot(), runStarts, cancellationToken);
                    nextCheck = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
                if (!nextCandidate.IsCompleted) continue;
                if (!await nextCandidate) break;
                if (!runStarts.Reader.TryRead(out var candidate))
                {
                    nextCandidate = runStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    continue;
                }
                nextCandidate = runStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
                using var catalog = await AcquireWorkerCatalogAsync(cancellationToken);
                await ObserveCandidateAsync(candidate, catalog.Catalog.Pipeline, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            runFailure = ex;
            blockDiagnostics?.ReportFailure(BlockUids.PortableSoftwareDiscoveryService, ex, "portable-discovery-loop");
            logger.LogError(ex, "Portable software process discovery worker stopped unexpectedly.");
            throw;
        }
        finally
        {
            // Process events are used only while this worker can consume them.
            // A failed or cancelled run releases its own hold even before StopAsync.
            try
            {
                try { runCts.Cancel(); }
                finally
                {
                    try { ReleaseNativeBinding(); }
                    finally { Interlocked.Exchange(ref processSubscription, null)?.Dispose(); }
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref workerCleanupFailure, error);
                // A release failure has its own cleanup channel. It must not
                // replace the original business failure observed by the parent.
                if (runFailure is null) throw;
            }
        }
    }

    private async Task<RuntimeProcessAttributionCatalogLease> AcquireWorkerCatalogAsync(CancellationToken cancellationToken)
    {
        try { return await attributionCatalogProvider.AcquireCatalogAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Only the resource that failed is told about its failure. A broker,
            // candidate or registry error cannot declare the catalog unavailable.
            if (attributionCatalogProvider.ReportFailure(error)) Volatile.Write(ref handledCatalogFailure, error);
            throw;
        }
    }

    private async Task ObserveCandidateAsync(PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline pipeline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ProcessId <= 4 || candidate.HasExited) return;
        try
        {
            bool identityRead;
            PortableSoftwareObservation? observation;
            // A synchronous measurement must end on the thread that started it, so it covers only the identity
            // read and matching. The registry write is awaited after it and may resume on another thread.
            using (blockDiagnostics?.MeasureSegment(BlockUids.PortableSoftwareDiscoveryService))
                identityRead = TryCreateObservation(candidate, pipeline, out observation);
            if (!identityRead)
            {
                if (!candidate.HasExited) LogCandidateFailure(candidate, new IOException("Process identity could not be read."));
                return;
            }
            if (observation is not null)
                await portableSoftwareRegistry.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            LogCandidateFailure(candidate, error);
        }
        catch (Exception error) when (RuntimeProcessAttributionFailure.IsCandidateFailure(error))
        {
            LogCandidateFailure(candidate, error);
        }
    }

    private void LogCandidateFailure(PortableProcessCandidate candidate, Exception error)
    {
        var kind = RuntimeProcessAttributionFailure.Kind(error);
        var now = Environment.TickCount64;
        lock (failureLogGate)
        {
            if (failureLogTimes.TryGetValue(kind, out var previous) && now - previous < 60_000) return;
            failureLogTimes[kind] = now;
        }
        logger.LogWarning(error,
            "Portable software candidate PID {ProcessId} was not observed: {FailureKind}. Further failures of this kind are logged at most once per minute.",
            candidate.ProcessId, kind);
    }

    // False when the process identity could not be read; otherwise the observation to record, if any.
    private bool TryCreateObservation(
        PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline attributionPipeline,
        out PortableSoftwareObservation? observation)
    {
        observation = null;
        var identity = readIdentity(candidate);
        if (identity is null)
        {
            return false;
        }

        var identityMatch = softwareIdentityCatalog.MatchPortableProcess(identity);
        if (identityMatch is null)
        {
            return true;
        }

        var attribution = attributionPipeline.Match(identity);
        if (!attribution.Id.Equals(
                $"catalog:{identityMatch.Entry.Id}",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        observation = PortableSoftwareObservationFactory.TryCreate(
            identity,
            attribution,
            identityMatch.Confidence == PortableSoftwareIdentityConfidence.Confirmed);
        return true;
    }

    private void EnqueueCurrentProcesses(Channel<PortableProcessCandidate> runStarts, bool onlyNew = false)
    {
        var current = captureCurrentProcesses();
        var processes = new HashSet<(int ProcessId, long StartKey)>(current.Length);
        foreach (var candidate in current)
        {
            if (candidate.ProcessId <= 4 || candidate.HasExited) continue;
            var key = (candidate.ProcessId, candidate.StartKey ?? 0);
            processes.Add(key);
            if (!onlyNew || !inventoryProcesses.Contains(key)) runStarts.Writer.TryWrite(candidate);
        }
        inventoryProcesses = processes;
    }

    internal void RefreshInventoryAfterLoss(KernelEtwSessionSnapshot snapshot, bool force = false)
        => RefreshInventoryAfterLoss(snapshot, processStarts, workerCts?.Token ?? CancellationToken.None, force);

    private void RefreshInventoryAfterLoss(KernelEtwSessionSnapshot snapshot,
        Channel<PortableProcessCandidate> runStarts, CancellationToken cancellationToken, bool force = false)
    {
        if (!snapshot.IsRunning || Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested
            || runStarts.Reader.Completion.IsCompleted) return;
        var lossChanged = inventorySnapshot is not { IsRunning: true } previous
            || previous.Generation != snapshot.Generation || previous.EventsLost != snapshot.EventsLost;
        if (!force && !lossChanged) return;
        // A skipped notification publication without a new loss or session can only have hidden process starts,
        // so only processes missing from the last inventory are new. Re-reading every process image on each skip
        // was about 1000 file opens a second in the tray state (candidate 0.3.7-beta.20261030.5).
        EnqueueCurrentProcesses(runStarts, onlyNew: !lossChanged);
        inventorySnapshot = snapshot;
    }

    private static readonly ResourceManager.ReadWrite.Windows.Processes.WindowsProcessBasicMetricReader IdentityReader = new();

    private static PortableProcessCandidate[] CaptureCurrentProcesses()
    {
        return ProcessCollectionOwner.Use(Process.GetProcesses(), processes =>
            processes
                .Where(static process => process.Id > 4)
                .Select(static process => new PortableProcessCandidate(process.Id,
                    StartKey: IdentityReader.ReadCurrentCreationFileTime(process.Id)))
                .ToArray());
    }

    private void BindNativeSource(KernelEtwEventSource source, Channel<bool> runPublications,
        CancellationToken cancellationToken)
    {
        NativeBinding? previous;
        lock (nativeBindingGate)
        {
            if (cancellationToken.IsCancellationRequested) return;
            source.ConsumerRejectionCounter("process-start", "stopped");
            source.ConsumerRejectionCounter("process-start", "system-process");
            source.ConsumerRejectionCounter("process-start", "closed-queue");
            var binding = new NativeBinding(source, source.ObservePublications(
                () => runPublications.Writer.TryWrite(true)), cancellationToken);
            previous = Interlocked.Exchange(ref nativeBinding, binding);
        }
        previous?.Dispose();
        runPublications.Writer.TryWrite(true);
    }

    private void ReleaseNativeBinding()
    {
        NativeBinding? binding;
        lock (nativeBindingGate) binding = Interlocked.Exchange(ref nativeBinding, null);
        binding?.Dispose();
    }

    // Called on the existing worker with one leased immutable native batch.
    internal void ObserveNativePublication(KernelEtwEventSource source, KernelEtwNativeNotifications.View view, bool skipped)
    {
        var binding = Volatile.Read(ref nativeBinding);
        if (binding is null || !ReferenceEquals(binding.Source, source)) return;
        var stopped = !source.IsActive || binding.CancellationToken.IsCancellationRequested;
        if (stopped)
        {
            foreach (ref readonly var row in KernelEtwNativeNotifications.Records(view))
                if (row.Kind == 7) RejectProcessStart(binding, "stopped");
            return;
        }
        if (skipped || binding.EventsLost is { } lost && lost != view.EventsLost)
            RefreshInventoryAfterLoss(kernelEtwSessionBroker.GetSnapshot(), processStarts,
                binding.CancellationToken, force: skipped);
        binding.EventsLost = view.EventsLost;
        foreach (ref readonly var row in KernelEtwNativeNotifications.Records(view))
        {
            if (row.Kind != 7) continue;
            if (!source.IsActive || binding.CancellationToken.IsCancellationRequested)
            {
                RejectProcessStart(binding, "stopped");
                continue;
            }
            binding.Counter.Receive();
            if (row.ProcessId <= 4 || row.ProcessId > int.MaxValue)
            {
                binding.Counter.Drop();
                source.RecordConsumerRejection("process-start", "system-process");
                continue;
            }
            var path = KernelEtwNativeReaders.Utf16(row.Path, row.PathLength);
            if (!processStarts.Writer.TryWrite(new PortableProcessCandidate((int)row.ProcessId,
                row.ParentProcessId is > 0 and <= int.MaxValue ? (int)row.ParentProcessId : null,
                path is null ? null : Path.GetFileNameWithoutExtension(path),
                path is not null && Path.IsPathFullyQualified(path) ? path : null,
                StartedNoLaterThan: row.TimeFileTime)))
            {
                binding.Counter.Drop();
                source.RecordConsumerRejection("process-start", "closed-queue");
            }
        }
    }

    private static void RejectProcessStart(NativeBinding binding, string reason)
    {
        binding.Counter.Receive();
        binding.Counter.Drop();
        binding.Source.RecordConsumerRejection("process-start", reason);
    }

    private sealed class NativeBinding(KernelEtwEventSource source, IDisposable observer,
        CancellationToken cancellationToken) : IDisposable
    {
        internal KernelEtwEventSource Source { get; } = source;
        internal EtwEventDropCounter Counter { get; } = source.ConsumerCounter("process-start");
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal KernelEtwNativeNotifications.Cursor Cursor;
        internal ulong? EventsLost;
        public void Dispose() => observer.Dispose();
    }
}
