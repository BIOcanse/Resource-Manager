using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>Owns one explicit recording and its observation leases.</summary>
public sealed class TargetedRecordingService(
    TargetedRecordingStore store,
    ISoftwareRegistryView softwareRegistry,
    IFrameTimingObservationSource frames,
    IResourceBreakdownObservationSource breakdown,
    IMetricSnapshotObservationSource metrics,
    ILogger<TargetedRecordingService> logger) : IHostedService
{
    public const int DefaultMaximumDurationSeconds = 600;
    public const int MaximumDurationLimitSeconds = 3600;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly MetricSampleRequest MetricRequest = MetricSampleRequest.All;
    private static readonly string[] BaseBreakdownMetrics =
    [
        ResourceBreakdownMetricIds.CpuUsage, ResourceBreakdownMetricIds.MemoryUsage,
         ResourceBreakdownMetricIds.VirtualMemoryUsage, ResourceBreakdownMetricIds.DiskRead,
         ResourceBreakdownMetricIds.DiskWrite, ResourceBreakdownMetricIds.NetworkReceive,
         ResourceBreakdownMetricIds.NetworkSend
    ];

    private readonly SemaphoreSlim control = new(1, 1);
    private ActiveRecording? active;

    public Task StartAsync(CancellationToken cancellationToken)
        => store.RecoverInterruptedAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveRecording? current;
        await control.WaitAsync(cancellationToken);
        try
        {
            current = active;
            if (current is not null)
            {
                current.StopReason = "interrupted";
                current.Cancellation.Cancel();
            }
        }
        finally
        {
            control.Release();
        }

        if (current?.Worker is { } worker)
        {
            await worker.WaitAsync(cancellationToken);
        }
    }

    public async Task<TargetedRecordingHeader> BeginAsync(string softwareId, int maximumDurationSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            throw new ArgumentException("Software ID is required.", nameof(softwareId));
        }
        if (maximumDurationSeconds is < 1 or > MaximumDurationLimitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDurationSeconds));
        }

        await control.WaitAsync(cancellationToken);
        try
        {
            if (active is not null)
            {
                throw new InvalidOperationException("A targeted recording is already running.");
            }

            var software = (await softwareRegistry.GetSoftwareAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, softwareId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Software was not found.");
            var header = new TargetedRecordingHeader(Guid.NewGuid().ToString("N"), software.Id,
                software.Name, DateTimeOffset.UtcNow, null, maximumDurationSeconds,
                "recording", null, false);
            await store.CreateAsync(header, cancellationToken);
            var current = new ActiveRecording(header);
            active = current;
            current.Worker = RunAsync(current);
            return header;
        }
        finally
        {
            control.Release();
        }
    }

    public async Task<TargetedRecordingHeader?> FinishAsync(string id, CancellationToken cancellationToken)
    {
        ActiveRecording? current;
        await control.WaitAsync(cancellationToken);
        try
        {
            current = active;
            if (current is null || current.Header.Id != id)
            {
                return null;
            }
            current.StopReason = "user";
            current.Cancellation.Cancel();
        }
        finally
        {
            control.Release();
        }

        await current.Worker!.WaitAsync(cancellationToken);
        return (await store.ListAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
    }

    public Task<IReadOnlyList<TargetedRecordingHeader>> ListAsync(CancellationToken cancellationToken)
        => store.ListAsync(cancellationToken);

    public Task<TargetedRecordingReport?> GetAsync(string id, CancellationToken cancellationToken)
        => store.GetAsync(id, cancellationToken);

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
        => store.DeleteAsync(id, cancellationToken);

    private async Task RunAsync(ActiveRecording current)
    {
        var id = current.Header.Id;
        var cursor = current.Header.StartedAt;
        var incomplete = false;
        var seenProcess = false;
        var readyWithoutProcess = 0;
        var notRunningTicks = 0;
        var knownProcesses = new HashSet<TargetedProcessIdentity>();
        var reason = "interrupted";
        try
        {
            using var metricLease = metrics.AcquireSubscription($"targeted-recording:{id}",
                MetricRequest, SampleInterval);
            using var frameLease = frames.AcquireSubscription();
            var hardware = metrics.ReadLatest(MetricRequest);
            var gpuIndexes = hardware?.Gpus.Select(static gpu => gpu.Index).Order().ToArray() ?? [];
            var request = CreateBreakdownRequest(gpuIndexes);
            IDisposable breakdownLease = breakdown.AcquireSubscription($"targeted-recording:{id}",
                request, SampleInterval);
            try
            {
            using var timer = new PeriodicTimer(SampleInterval);
            while (await timer.WaitForNextTickAsync(current.Cancellation.Token))
            {
                var now = DateTimeOffset.UtcNow;
                hardware = metrics.ReadLatest(MetricRequest);
                var discoveredGpuIndexes = hardware?.Gpus.Select(static gpu => gpu.Index).Order().ToArray() ?? gpuIndexes;
                if (!gpuIndexes.SequenceEqual(discoveredGpuIndexes))
                {
                    var nextRequest = CreateBreakdownRequest(discoveredGpuIndexes);
                    var nextLease = breakdown.AcquireSubscription($"targeted-recording:{id}",
                        nextRequest, SampleInterval);
                    breakdownLease.Dispose();
                    breakdownLease = nextLease;
                    request = nextRequest;
                    gpuIndexes = discoveredGpuIndexes;
                }
                var through = now - TimeSpan.FromMilliseconds(250);
                if (through - cursor > TimeSpan.FromSeconds(29))
                {
                    cursor = through - TimeSpan.FromSeconds(29);
                    incomplete = true;
                }
                var batch = frames.ReadIntervals(cursor, through);
                if (batch is null || !batch.Complete)
                {
                    incomplete = true;
                }

                var resource = CaptureResource(current.Header.SoftwareId, now, request, hardware);
                if (resource.Software.Count == 0) incomplete = true;
                var identities = resource.Processes.Concat(knownProcesses).ToHashSet();
                var matchingFrames = batch?.Intervals.Where(item => identities.Any(process =>
                    process.ProcessId == item.ProcessId &&
                    (process.ProcessStartKey == 0 || process.ProcessStartKey == item.ProcessStartKey)))
                    .ToArray() ?? [];
                await store.AppendAsync(id, matchingFrames, resource, CancellationToken.None);
                if (identities.Count > 0)
                {
                    cursor = through;
                }

                if (resource.Processes.Count > 0)
                {
                    seenProcess = true;
                    readyWithoutProcess = 0;
                    knownProcesses = resource.Processes.ToHashSet();
                }
                else if (resource.Software.Count > 0)
                {
                    readyWithoutProcess++;
                }

                if (seenProcess && knownProcesses.All(static process => !IsProcessRunning(process)))
                {
                    notRunningTicks++;
                }
                else
                {
                    notRunningTicks = 0;
                }

                if ((seenProcess && notRunningTicks >= 2)
                    || (!seenProcess && readyWithoutProcess >= 3))
                {
                    reason = "process-exit";
                    break;
                }
                if (now - current.Header.StartedAt >= TimeSpan.FromSeconds(current.Header.MaximumDurationSeconds))
                {
                    reason = "max-duration";
                    break;
                }
            }

            if (current.Cancellation.IsCancellationRequested)
            {
                reason = current.StopReason;
            }
            }
            finally
            {
                try
                {
                    // Allow the ETW flush timer to deliver the final Present events before closing the lease.
                    var stoppedAt = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                    if (stoppedAt > cursor && stoppedAt - cursor <= TimeSpan.FromSeconds(29))
                    {
                        var finalBatch = frames.ReadIntervals(cursor, stoppedAt);
                        if (finalBatch is null || !finalBatch.Complete) incomplete = true;
                        var tail = finalBatch?.Intervals.Where(item => knownProcesses.Any(process =>
                            process.ProcessId == item.ProcessId &&
                            (process.ProcessStartKey == 0 || process.ProcessStartKey == item.ProcessStartKey)))
                            .ToArray() ?? [];
                        await store.AppendAsync(id, tail, null, CancellationToken.None);
                    }
                }
                finally
                {
                    breakdownLease.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (current.Cancellation.IsCancellationRequested)
        {
            reason = current.StopReason;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Targeted recording {RecordingId} failed.", id);
            reason = "capture-error";
            incomplete = true;
        }
        finally
        {
            try
            {
                await store.FinishAsync(id, reason, incomplete, CancellationToken.None);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Targeted recording {RecordingId} could not be finalized.", id);
            }
            await control.WaitAsync();
            try
            {
                if (ReferenceEquals(active, current))
                {
                    active = null;
                }
                current.Cancellation.Dispose();
            }
            finally
            {
                control.Release();
            }
        }
    }

    private static ResourceBreakdownSampleRequest CreateBreakdownRequest(IReadOnlyList<int> gpuIndexes)
    {
        var ids = new List<string>(BaseBreakdownMetrics);
        foreach (var index in gpuIndexes)
        {
            ids.Add($"gpu.{index}.usage");
            ids.Add($"gpu.{index}.vram");
        }
        return new ResourceBreakdownSampleRequest(ids, new Dictionary<string, string>(),
            ProcessSampleDetailLevel.ResourceTableFull);
    }

    private static bool IsProcessRunning(TargetedProcessIdentity identity)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            if (process.HasExited) return false;
            if (identity.ProcessStartKey == 0) return true;
            try { return process.StartTime.ToFileTimeUtc() == identity.ProcessStartKey; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private TargetedResourceSample CaptureResource(string softwareId, DateTimeOffset now,
        ResourceBreakdownSampleRequest request, HardwareMetricSnapshot? hardware)
    {
        var snapshot = breakdown.ReadLatest(request);
        var software = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var processes = new HashSet<TargetedProcessIdentity>();
        if (snapshot.Sampling.Status == ResourceBreakdownSamplingStatuses.Ready)
        {
            foreach (var bar in snapshot.Bars)
            {
                var segment = bar.Software.FirstOrDefault(item =>
                    string.Equals(item.SoftwareId, softwareId, StringComparison.OrdinalIgnoreCase));
                software[bar.MetricId] = segment?.Value;
                if (segment is null)
                {
                    continue;
                }
                foreach (var process in segment.Processes.Where(static item =>
                    item.ProcessId > 0 && item.AttributionKind == ResourceProcessAttributionKinds.Process))
                {
                    processes.Add(new TargetedProcessIdentity(process.ProcessId, process.ProcessStartKey ?? 0));
                }
            }
        }

        var system = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu.usage"] = null,
            ["cpu.temperature"] = null,
            ["cpu.frequency"] = null,
            ["memory.usage"] = null
        };
        if (hardware is not null)
        {
            system["cpu.usage"] = hardware.Cpu.IsUsageAvailable ? hardware.Cpu.UsagePercent : null;
            system["cpu.temperature"] = hardware.Cpu.Sensors.TemperatureCelsius;
            system["cpu.frequency"] = hardware.Cpu.CurrentFrequencyMhz > 0 ? hardware.Cpu.CurrentFrequencyMhz : null;
            system["memory.usage"] = hardware.Memory.IsUsageAvailable ? hardware.Memory.UsedBytes : null;
            foreach (var gpu in hardware.Gpus)
            {
                var prefix = $"gpu.{gpu.Index}.";
                system[prefix + "usage"] = gpu.IsUsageAvailable ? gpu.UsagePercent : null;
                system[prefix + "temperature"] = gpu.Sensors.TemperatureCelsius;
                system[prefix + "vram"] = gpu.TotalMemoryBytes > 0 ? gpu.UsedMemoryBytes : null;
            }
        }
        return new TargetedResourceSample(now, software, system, processes.ToArray());
    }

    private sealed class ActiveRecording(TargetedRecordingHeader header)
    {
        public TargetedRecordingHeader Header { get; } = header;
        public CancellationTokenSource Cancellation { get; } = new();
        public string StopReason { get; set; } = "user";
        public Task? Worker { get; set; }
    }
}
