using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

public sealed class PortableSoftwareDiscoveryService(
    IKernelEtwSessionBroker kernelEtwSessionBroker,
    IRuntimeProcessAttributionCatalogProvider attributionCatalogProvider,
    ISoftwareIdentityCatalog softwareIdentityCatalog,
    IPortableSoftwareRegistry portableSoftwareRegistry,
    ILogger<PortableSoftwareDiscoveryService> logger) : IPortableSoftwareDiscovery, IHostedService, IDisposable
{
    private readonly WindowsPortableProcessIdentityReader identityReader = new();
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workerTask = Task.Run(() => RunWorkerAsync(workerCts.Token), CancellationToken.None);
        processSubscription = kernelEtwSessionBroker.Subscribe(new KernelEtwSubscriptionRequest(
            "portable-software-discovery",
            KernelTraceEventParser.Keywords.Process,
            (kernel, _) => kernel.ProcessStartGroup += data => processStarts.Writer.TryWrite(
                new PortableProcessCandidate(
                    data.ProcessID,
                    data.ParentID > 0 ? data.ParentID : null,
                    data.ProcessName,
                    SelectEventPath(data.ImageFileName, data.KernelImageFileName)))));
        EnqueueCurrentProcesses();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        processSubscription?.Dispose();
        processSubscription = null;
        processStarts.Writer.TryComplete();
        workerCts?.Cancel();
        if (workerTask is { } task)
        {
            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task ScanRunningProcessesAsync(CancellationToken cancellationToken)
    {
        var catalog = await attributionCatalogProvider.GetCatalogAsync(cancellationToken);
        var candidates = CaptureCurrentProcesses();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            (candidate, _) =>
            {
                Observe(candidate, catalog.Pipeline);
                return ValueTask.CompletedTask;
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref processSubscription, null)?.Dispose();
        var cts = Interlocked.Exchange(ref workerCts, null);
        try
        {
            cts?.Cancel();
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var candidate in processStarts.Reader.ReadAllAsync(cancellationToken))
            {
                var catalog = await attributionCatalogProvider.GetCatalogAsync(cancellationToken);
                if (Observe(candidate, catalog.Pipeline))
                {
                    continue;
                }

                await Task.Delay(150, cancellationToken);
                Observe(candidate, catalog.Pipeline);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Portable software process discovery worker stopped unexpectedly.");
        }
    }

    private bool Observe(
        PortableProcessCandidate candidate,
        RuntimeProcessAttributionPipeline attributionPipeline)
    {
        var identity = identityReader.TryRead(candidate);
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
        foreach (var candidate in CaptureCurrentProcesses())
        {
            processStarts.Writer.TryWrite(candidate);
        }
    }

    private static PortableProcessCandidate[] CaptureCurrentProcesses()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes
                .Select(static process => new PortableProcessCandidate(process.Id))
                .ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string? SelectEventPath(params string?[] candidates)
    {
        return candidates
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .FirstOrDefault(Path.IsPathFullyQualified);
    }
}
