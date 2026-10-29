using BlockUids = ResourceManager.App.StateItems.BlockUids;
using System.Diagnostics;
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
    private CancellationTokenSource? workerCts;
    private Task? workerTask;
    private int disposed;
    private KernelEtwSessionSnapshot? inventorySnapshot;

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
        var runStarts = processStarts = CreateProcessStarts();
        workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = workerCts.Token;
        try
        {
            processSubscription = kernelEtwSessionBroker.Subscribe(new KernelEtwSubscriptionRequest(
                "portable-software-discovery",
                KernelTraceEventParser.Keywords.Process,
                (kernel, _) =>
                {
                    var counter = kernel.ConsumerCounter("process-start");
                    kernel.ProcessStartGroup += data =>
                    {
                        counter.Receive();
                        var rejection = !kernel.IsActive || workerToken.IsCancellationRequested ? "stopped"
                            : data.ProcessID <= 4 ? "system-process" : null;
                        if (rejection is not null)
                        {
                            counter.Drop();
                            kernel.RecordConsumerRejection("process-start", rejection);
                            return;
                        }
                        if (!runStarts.Writer.TryWrite(new PortableProcessCandidate(
                            data.ProcessID,
                            data.ParentID > 0 ? data.ParentID : null,
                            data.ProcessName,
                            SelectEventPath(data.ImageFileName, data.KernelImageFileName),
                            StartedNoLaterThan: data.TimeStamp.ToFileTimeUtc())))
                        {
                            counter.Drop();
                            kernel.RecordConsumerRejection("process-start", "closed-queue");
                        }
                    };
                }));
            inventorySnapshot = kernelEtwSessionBroker.GetSnapshot();
            EnqueueCurrentProcesses(runStarts);
            workerTask = Task.Run(() => RunWorkerAsync(runStarts, workerToken), CancellationToken.None);
        }
        catch
        {
            workerCts.Cancel();
            try { Interlocked.Exchange(ref processSubscription, null)?.Dispose(); }
            finally { workerCts.Dispose(); workerCts = null; }
            throw;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        processStarts.Writer.TryComplete();
        var cts = workerCts;
        cts?.Cancel();
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
            {
                using var segment = blockDiagnostics?.MeasureSegment(BlockUids.PortableSoftwareDiscoveryService);
                await ObserveCandidateAsync(candidate, catalog.Catalog.Pipeline, token).ConfigureAwait(false);
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task RunWorkerAsync(Channel<PortableProcessCandidate> runStarts, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var nextCheck = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var nextCandidate = runStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAny(nextCheck, nextCandidate);
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
                using var catalog = await attributionCatalogProvider.AcquireCatalogAsync(cancellationToken);
                using var segment = blockDiagnostics?.MeasureSegment(BlockUids.PortableSoftwareDiscoveryService);
                await ObserveCandidateAsync(candidate, catalog.Catalog.Pipeline, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            blockDiagnostics?.ReportFailure(BlockUids.PortableSoftwareDiscoveryService, ex, "portable-discovery-loop");
            logger.LogError(ex, "Portable software process discovery worker stopped unexpectedly; discovery is unavailable for this service lifetime.");
        }
        finally
        {
            // Process events are used only while this worker can consume them.
            // A failed or cancelled run releases its own hold even before StopAsync.
            Interlocked.Exchange(ref processSubscription, null)?.Dispose();
        }
    }

    private async Task ObserveCandidateAsync(PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline pipeline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ProcessId <= 4 || candidate.HasExited) return;
        try
        {
            if (!await ObserveAsync(candidate, pipeline, cancellationToken).ConfigureAwait(false) && !candidate.HasExited)
                LogCandidateFailure(candidate, new IOException("Process identity could not be read."));
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

    private async Task<bool> ObserveAsync(
        PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline attributionPipeline,
        CancellationToken cancellationToken)
    {
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

        var observation = PortableSoftwareObservationFactory.TryCreate(
            identity,
            attribution,
            identityMatch.Confidence == PortableSoftwareIdentityConfidence.Confirmed);
        if (observation is not null)
        {
            await portableSoftwareRegistry.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private void EnqueueCurrentProcesses(Channel<PortableProcessCandidate> runStarts)
    {
        foreach (var candidate in captureCurrentProcesses())
        {
            if (candidate.ProcessId > 4 && !candidate.HasExited) runStarts.Writer.TryWrite(candidate);
        }
    }

    internal void RefreshInventoryAfterLoss(KernelEtwSessionSnapshot snapshot)
        => RefreshInventoryAfterLoss(snapshot, processStarts, workerCts?.Token ?? CancellationToken.None);

    private void RefreshInventoryAfterLoss(KernelEtwSessionSnapshot snapshot,
        Channel<PortableProcessCandidate> runStarts, CancellationToken cancellationToken)
    {
        if (!snapshot.IsRunning || Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested
            || runStarts.Reader.Completion.IsCompleted) return;
        if (inventorySnapshot is { IsRunning: true } previous
            && previous.Generation == snapshot.Generation && previous.EventsLost == snapshot.EventsLost) return;
        EnqueueCurrentProcesses(runStarts);
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

    private static string? SelectEventPath(params string?[] candidates)
    {
        return candidates
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .FirstOrDefault(Path.IsPathFullyQualified);
    }
}
