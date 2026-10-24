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
    private readonly object failureLogGate = new();
    private readonly Dictionary<string, long> failureLogTimes = new();
    private readonly Channel<PortableProcessCandidate> processStarts = Channel.CreateBounded<PortableProcessCandidate>(
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
        ILogger<PortableSoftwareDiscoveryService> logger)
        : this(kernelEtwSessionBroker, attributionCatalogProvider, softwareIdentityCatalog,
            portableSoftwareRegistry, logger, CaptureCurrentProcesses)
    {
    }

    internal PortableSoftwareDiscoveryService(
        IKernelEtwSessionBroker kernelEtwSessionBroker,
        IRuntimeProcessAttributionCatalogProvider attributionCatalogProvider,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        IPortableSoftwareRegistry portableSoftwareRegistry,
        ILogger<PortableSoftwareDiscoveryService> logger,
        Func<PortableProcessCandidate[]> captureCurrentProcesses,
        Func<PortableProcessCandidate, RuntimeProcessIdentity?>? readIdentity = null)
    {
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
        workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = workerCts.Token;
        try
        {
            processSubscription = kernelEtwSessionBroker.Subscribe(new KernelEtwSubscriptionRequest(
                "portable-software-discovery",
                KernelTraceEventParser.Keywords.Process,
                (kernel, _) => kernel.ProcessStartGroup += data =>
                {
                    if (!kernel.IsActive || workerToken.IsCancellationRequested || data.ProcessID <= 4) return;
                    processStarts.Writer.TryWrite(new PortableProcessCandidate(
                        data.ProcessID,
                        data.ParentID > 0 ? data.ParentID : null,
                        data.ProcessName,
                        SelectEventPath(data.ImageFileName, data.KernelImageFileName),
                        StartedNoLaterThan: data.TimeStamp.ToFileTimeUtc()));
                }));
            inventorySnapshot = kernelEtwSessionBroker.GetSnapshot();
            EnqueueCurrentProcesses();
            workerTask = Task.Run(() => RunWorkerAsync(workerToken), CancellationToken.None);
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
        workerCts?.Cancel();
        try
        {
            Interlocked.Exchange(ref processSubscription, null)?.Dispose();
        }
        finally
        {
            if (workerTask is { } task)
            {
                try { await task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
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
            (candidate, _) =>
            {
                ObserveCandidate(candidate, catalog.Catalog.Pipeline, cancellationToken);
                return ValueTask.CompletedTask;
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        processStarts.Writer.TryComplete();
        var cts = Interlocked.Exchange(ref workerCts, null);
        try
        {
            cts?.Cancel();
        }
        finally
        {
            try { Interlocked.Exchange(ref processSubscription, null)?.Dispose(); }
            finally { cts?.Dispose(); }
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var nextCheck = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var nextCandidate = processStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAny(nextCheck, nextCandidate);
                if (nextCheck.IsCompleted)
                {
                    if (!await nextCheck) break;
                    RefreshInventoryAfterLoss(kernelEtwSessionBroker.GetSnapshot());
                    nextCheck = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
                if (!nextCandidate.IsCompleted) continue;
                if (!await nextCandidate) break;
                if (!processStarts.Reader.TryRead(out var candidate))
                {
                    nextCandidate = processStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    continue;
                }
                nextCandidate = processStarts.Reader.WaitToReadAsync(cancellationToken).AsTask();
                using var catalog = await attributionCatalogProvider.AcquireCatalogAsync(cancellationToken);
                ObserveCandidate(candidate, catalog.Catalog.Pipeline, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Portable software process discovery worker stopped unexpectedly; discovery is unavailable for this service lifetime.");
        }
    }

    private void ObserveCandidate(PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline pipeline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ProcessId <= 4 || candidate.HasExited) return;
        try
        {
            if (!Observe(candidate, pipeline) && !candidate.HasExited)
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

    private bool Observe(
        PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline attributionPipeline)
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
            portableSoftwareRegistry.Observe(observation);
        }

        return true;
    }

    private void EnqueueCurrentProcesses()
    {
        foreach (var candidate in captureCurrentProcesses())
        {
            if (candidate.ProcessId > 4 && !candidate.HasExited) processStarts.Writer.TryWrite(candidate);
        }
    }

    internal void RefreshInventoryAfterLoss(KernelEtwSessionSnapshot snapshot)
    {
        if (!snapshot.IsRunning || Volatile.Read(ref disposed) != 0 || workerCts?.IsCancellationRequested == true) return;
        if (inventorySnapshot is { IsRunning: true } previous
            && previous.Generation == snapshot.Generation && previous.EventsLost == snapshot.EventsLost) return;
        EnqueueCurrentProcesses();
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
