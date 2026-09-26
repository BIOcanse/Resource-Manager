using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

public sealed class ExternalGpuResidentObservation(
    IMetricSnapshotObservationSource hardware, ISchedulingProcessFactObservationSource processes)
{
    internal IReadOnlyList<ulong> ReadAdapterKeys()
        => hardware.ReadLatest(MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([
            SamplingDatasetIds.SystemCpuUsage, SamplingDatasetIds.SystemMemoryUsage,
            SamplingDatasetIds.SystemVirtualMemoryUsage]))?.GpuInventory?.Adapters
            .Select(adapter => adapter.AdapterKey).Where(key => key != 0).Distinct().ToArray() ?? [];

    internal ExternalResidentSample? Read(GpuPlacementProcessInstance identity)
    {
        var inventory = hardware.ReadLatest(MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([
            SamplingDatasetIds.SystemCpuUsage, SamplingDatasetIds.SystemMemoryUsage,
            SamplingDatasetIds.SystemVirtualMemoryUsage]))?.GpuInventory;
        if (inventory is null) return null;
        var snapshot = processes.ReadLatest(new(SchedulingProcessMetricMask.GpuDedicatedMemory
            | SchedulingProcessMetricMask.GpuUsage, null, inventory));
        if (snapshot is null || !snapshot.DatasetObservations.TryGetValue(
                SchedulingProcessMetricMask.GpuDedicatedMemory, out var memory)
            || memory.Status != SamplingObservationStatus.Current
            || !snapshot.DatasetObservations.TryGetValue(SchedulingProcessMetricMask.GpuUsage, out var usage)
            || usage.Status != SamplingObservationStatus.Current) return null;
        var process = snapshot.Processes.FirstOrDefault(row => row.ProcessId == identity.ProcessId
            && row.ProcessStartKey == identity.ProcessStartKey);
        // UMA allocations and active engines can each identify an adapter used by this process.
        var rows = process is not null
            ? process.Gpus.Where(row => row.PrivateMemoryBytes is >= 0 && row.SharedMemoryBytes is >= 0
                    && double.IsFinite(row.PrivateMemoryBytes.Value) && double.IsFinite(row.SharedMemoryBytes.Value)
                || row.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                    && double.IsFinite(row.UsagePercent) && row.UsagePercent is >= 0 and <= 100).ToArray()
            : [];
        return new(Math.Min(memory.ObservedAtUtcTicks, usage.ObservedAtUtcTicks),
            rows.Select(row => new ExternalAdapterResident(row.AdapterKey, row.PrivateMemoryBytes ?? 0,
                row.SharedMemoryBytes ?? 0, row.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                    ? row.UsagePercent : null)).ToArray(), Math.Min(memory.LastAttemptAtUtcTicks, usage.LastAttemptAtUtcTicks));
    }

    internal Task<ExternalResidentSample?> WaitForNextAsync(GpuPlacementProcessInstance identity,
        long afterUtcTicks, ulong deadline, CancellationToken cancellationToken)
        => WaitForNextAsync(() => Read(identity), afterUtcTicks, deadline, cancellationToken);

    internal static async Task<ExternalResidentSample?> WaitForNextAsync(Func<ExternalResidentSample?> read,
        long afterUtcTicks, ulong deadline, CancellationToken cancellationToken)
    {
        while (checked((ulong)Environment.TickCount64) < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = read();
            // Inspect existing publications only. An empty next value also settles this attempt.
            if (sample?.LastAttemptAtUtcTicks > afterUtcTicks) return sample;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    internal static bool ConfirmsTarget(ExternalResidentSample before, ExternalResidentSample? after, ulong target)
        => ClassifyTransfer(before, after, target) == ExternalResidentTransfer.Full;

    internal static ExternalResidentTransfer ClassifyTransfer(ExternalResidentSample before, ExternalResidentSample? after, ulong target)
    {
        var previous = before.Adapters.FirstOrDefault(row => row.AdapterKey == target)?.ResidentBytes ?? 0;
        if (before.Adapters.Count == 0 || after?.Adapters.Any(row => row.AdapterKey == target
                && row.ResidentBytes > previous) != true) return ExternalResidentTransfer.Unconfirmed;

        var retained = false;
        foreach (var row in after.Adapters.Where(row => row.AdapterKey != target))
        {
            var original = before.Adapters.FirstOrDefault(item => item.AdapterKey == row.AdapterKey);
            if (original is null && (row.ResidentBytes > 0 || row.UsagePercent is > 0)
                || original is not null && row.ResidentBytes > original.ResidentBytes)
                return ExternalResidentTransfer.Unconfirmed;
            retained |= row.ResidentBytes > 0 || row.UsagePercent is > 0;
        }
        if (!retained) return ExternalResidentTransfer.Full;
        var reduced = before.Adapters.Where(row => row.AdapterKey != target).Any(row =>
            row.ResidentBytes > (after.Adapters.FirstOrDefault(item => item.AdapterKey == row.AdapterKey)?.ResidentBytes ?? 0));
        return reduced ? ExternalResidentTransfer.Partial : ExternalResidentTransfer.Unconfirmed;
    }
}

internal enum ExternalResidentTransfer { Unconfirmed, Partial, Full }

internal sealed record ExternalResidentSample(long ObservedAtUtcTicks, IReadOnlyList<ExternalAdapterResident> Adapters,
    long LastAttemptAtUtcTicks);
internal sealed record ExternalAdapterResident(ulong AdapterKey, double PrivateBytes, double SharedBytes, double? UsagePercent)
{
    public double ResidentBytes => PrivateBytes + SharedBytes;
}
