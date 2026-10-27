using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

public sealed class HostMemoryProtectionSource(IMetricSnapshotPushSource metrics,
    IMetricSnapshotObservationSource currentMetrics, IResourceBreakdownPushSource resources,
    ISchedulingProcessFactObservationSource foundations) : IHostMemoryProtectionSource
{
    internal const string PrivateCommitField = "process_all_private_commit_actual";
    // These existing addresses are the three memory_all_commit_* fields in MetricFieldRegistry.
    private static readonly MetricSampleRequest CommitRequest = MetricSampleRequest.ForIds(
        ["virtualMemory.usage", "virtualMemory.total", "virtualMemory.percent"]);
    private static readonly ResourceBreakdownSampleRequest ProcessRequest = new(
        [PrivateCommitField], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
        { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] };

    public HostMemoryCommit? ReadCurrentCommit() => ReadCommit(currentMetrics.ReadLatest(CommitRequest));

    public async IAsyncEnumerable<HostMemoryReading> SubscribeAsync(string holder, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deliveries = Channel.CreateBounded<HostMemoryReading>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var gate = new object();
        HostMemoryCommit? commit = null;
        IReadOnlyList<HostMemoryProcess>? processes = null;
        var machine = PumpMachineAsync();
        var process = PumpProcessesAsync();
        var completion = CompleteAsync();
        try
        {
            await foreach (var value in deliveries.Reader.ReadAllAsync(cancellationToken)) yield return value;
        }
        finally
        {
            cancellation.Cancel();
            try { await completion.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        async Task PumpMachineAsync()
        {
            try
            {
                await foreach (var snapshot in metrics.SubscribeAsync(holder + ":commit", CommitRequest, interval, cancellation.Token))
                    lock (gate)
                    {
                        commit = ReadCommit(snapshot);
                        deliveries.Writer.TryWrite(new(DateTimeOffset.UtcNow, commit, processes));
                    }
            }
            catch { cancellation.Cancel(); throw; }
        }
        async Task PumpProcessesAsync()
        {
            try
            {
                await foreach (var snapshot in resources.SubscribeAsync(holder + ":processes", ProcessRequest, interval, cancellation.Token))
                    lock (gate)
                    {
                        processes = ReadProcesses(snapshot);
                        deliveries.Writer.TryWrite(new(DateTimeOffset.UtcNow, commit, processes));
                    }
            }
            catch { cancellation.Cancel(); throw; }
        }
        async Task CompleteAsync()
        {
            try { await Task.WhenAll(machine, process).ConfigureAwait(false); deliveries.Writer.TryComplete(); }
            catch (Exception error) { deliveries.Writer.TryComplete(error); }
        }
    }

    private IReadOnlyList<HostMemoryProcess>? ReadProcesses(ResourceBreakdownSnapshot snapshot)
    {
        var facts = foundations.ReadLatest(new(SchedulingProcessMetricMask.None,
            new SchedulingGpuInventorySnapshot(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
        if (facts?.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _) != true) return null;
        var field = ProcessMetricFieldRegistry.Find(PrivateCommitField)!;
        var available = snapshot.ProcessFields?.Sources.Any(item => item.SourceId == field.SourceId && item.CapturedAt is not null) == true;
        return facts.InventoryProcesses.Select(item =>
        {
            var key = item.ProcessStartKey.ToString(CultureInfo.InvariantCulture);
            var value = available ? snapshot.ProcessFields?.FindProcess(item.ProcessId, key)?.Values
                .FirstOrDefault(value => value.Field == PrivateCommitField) : null;
            var number = value?.IntegerValue?.ToBigInteger();
            ulong? bytes = number is { } integer && integer >= 0 && integer <= ulong.MaxValue ? (ulong)integer : null;
            return new HostMemoryProcess(item.ProcessId, key, item.ProcessName, bytes)
            {
                IsResourceManager = item.ProcessId == Environment.ProcessId
                    || ResourceManagerSelfDescriptor.ProcessNames.Contains(item.ProcessName, StringComparer.OrdinalIgnoreCase)
                    || facts.AttributionProcesses.Any(attribution => attribution.ProcessId == item.ProcessId
                        && attribution.ProcessStartKey == item.ProcessStartKey && attribution.SoftwareId == RuntimeAttributionIds.ResourceManagerSelf)
            };
        }).ToArray();
    }

    private static HostMemoryCommit? ReadCommit(HardwareMetricSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        var used = snapshot.Items.Values.FirstOrDefault(item => item.FieldCode == MetricFieldCode.CommitUsed);
        var limit = snapshot.Items.Values.FirstOrDefault(item => item.FieldCode == MetricFieldCode.CommitLimit);
        var percent = snapshot.Items.Values.FirstOrDefault(item => item.FieldCode == MetricFieldCode.CommitPercent);
        if (used?.Status != SamplingObservationStatus.Current || limit?.Status != SamplingObservationStatus.Current
            || percent?.Status != SamplingObservationStatus.Current) return null;
        var usedInteger = used.IntegerValue?.ToBigInteger();
        var limitInteger = limit.IntegerValue?.ToBigInteger();
        if (usedInteger is not { } bytes || limitInteger is not { } total || bytes < 0 || bytes > ulong.MaxValue
            || total <= 0 || total > ulong.MaxValue || percent.NumericValue is not { } ratio || !double.IsFinite(ratio)) return null;
        return new((ulong)bytes, (ulong)total, ratio);
    }
}
