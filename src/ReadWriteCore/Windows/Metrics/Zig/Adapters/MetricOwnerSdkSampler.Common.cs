using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadCpu(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var value = resources.ReadCpuCounters();
        Each(rows, row =>
        {
            if (!value.IsAvailable) { Missing(raw, row); return; }
            if (Field(facts, row) != MetricFieldCode.CpuUsage) throw Contract();
            raw.WriteCpuCounter(row, value.IdleTicks, value.KernelTicks, value.UserTicks,
                value.MonotonicTicks, value.MonotonicTicksPerSecond, value.ObservedAt.ToUnixTimeMilliseconds());
        });
        return value.IsAvailable ? Complete : Unavailable;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadMemory(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var at = Now();
        var global = Has(rows, facts, MetricFieldCode.MemoryUsed, MetricFieldCode.MemoryTotal,
            MetricFieldCode.MemoryAvailable, MetricFieldCode.MemoryPercent) ? resources.ReadMemory() : null;
        var available = rows.Fields(MetricFieldCode.NumaNodeAvailableBytes);
        var capacity = rows.Fields(MetricFieldCode.NumaNodeCapacityMiB);
        IReadOnlyDictionary<string, ulong>? availableValues = null, capacityValues = null;
        if (available.Count != 0)
        {
            using var call = available.BeginCall();
            availableValues = resources.ReadNumaAvailable(SdkScopes(call, x => x.Numa ?? throw Contract()),
                SdkDeviceKeys(call));
        }
        if (capacity.Count != 0)
        {
            using var call = capacity.BeginCall();
            capacityValues = resources.ReadNumaCapacityMiB(SdkScopes(call, x => x.Numa ?? throw Contract()),
                SdkDeviceKeys(call));
        }
        Each(rows, row =>
        {
            var code = Field(facts, row); var key = Scope(facts, row).DeviceKey;
            if (code == MetricFieldCode.NumaNodeAvailableBytes)
                Integer(raw, row, availableValues?.TryGetValue(key, out var nodeAvailable) == true ? nodeAvailable : null, at);
            else if (code == MetricFieldCode.NumaNodeCapacityMiB)
                Integer(raw, row, capacityValues?.TryGetValue(key, out var nodeCapacity) == true ? nodeCapacity : null, at);
            else if (global is null || !global.IsUsageAvailable) Missing(raw, row);
            else if (code == MetricFieldCode.MemoryPercent) Number(raw, row, global.UsagePercent, at);
            else
            {
                ulong? value = code switch { MetricFieldCode.MemoryUsed => global.UsedBytes,
                    MetricFieldCode.MemoryTotal => global.TotalBytes, MetricFieldCode.MemoryAvailable => global.AvailableBytes, _ => null };
                // Old declarations include float memory fields; exact integer declarations never round-trip through double.
                if (raw.ValueKind(row) == MetricOwnerValueKind.Float64) Number(raw, row, value, at);
                else Integer(raw, row, value, at);
            }
        });
        // A completed SDK query may legitimately contain no sensor values.
        return global is { IsUsageAvailable: false } && available.Count == 0 && capacity.Count == 0 ? Unavailable : Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadVirtualMemory(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var value = resources.ReadVirtualMemory(); var at = Now();
        Each(rows, row =>
        {
            if (!value.IsSelectable) { Missing(raw, row); return; }
            var code = Field(facts, row);
            if (code == MetricFieldCode.CommitPercent) { Number(raw, row, value.UsagePercent, at); return; }
            ulong? v = code switch
            {
                MetricFieldCode.CommitUsed => value.UsedBytes, MetricFieldCode.CommitLimit => value.TotalBytes,
                MetricFieldCode.CommitPeak => value.CommitPeakBytes, MetricFieldCode.SystemCache => value.SystemCacheBytes,
                MetricFieldCode.KernelPaged => value.KernelPagedBytes, MetricFieldCode.KernelNonpaged => value.KernelNonpagedBytes,
                MetricFieldCode.ProcessCount => value.ProcessCount, MetricFieldCode.ThreadCount => value.ThreadCount,
                MetricFieldCode.HandleCount => value.HandleCount, _ => null
            };
            if (raw.ValueKind(row) == MetricOwnerValueKind.Float64) Number(raw, row, v, at); else Integer(raw, row, v, at);
        });
        return value.IsSelectable ? Complete : Unavailable;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadFrequency(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var value = resources.ReadCpuFrequency(); var at = Now();
        Each(rows, row => Number(raw, row, Field(facts, row) switch
        { MetricFieldCode.CpuFrequency => value.CurrentMhz, MetricFieldCode.CpuFrequencyPercent => value.Percent, _ => null }, at));
        return value.Source == CpuFrequencySourceStates.Unavailable ? Unavailable : Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadPdhGpu(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var inventory = topology.GpuInventory ?? throw Contract();
        var engineRows = rows.Fields(MetricFieldCode.GpuUsage, MetricFieldCode.GpuEngineBusy);
        var sharedRows = rows.Fields(MetricFieldCode.GpuSharedMemoryUsed);
        var vector = facts.PdhGpu;
        PdhGpuEngineUsageRead? engines = null; PdhGpuAdapterSharedMemoryRead? shared = null;
        // Each domain is independent; failure must not suppress another requested domain.
        try
        {
            if (engineRows.Count != 0 || vector is not null)
            {
                using var call = engineRows.BeginCall(2);
                engines = resources.ReadGpuUsage(vector is not null ? inventory.Adapters : GpuTargets(call, inventory));
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or MetricOwnerException))
        { vector?.Complete(0, Unavailable, Now(), SdkUnavailableReason); }
        try
        {
            if (sharedRows.Count != 0)
            {
                using var call = sharedRows.BeginCall(2);
                shared = resources.ReadGpuSharedMemory(GpuTargets(call, inventory));
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or MetricOwnerException)) { }
        if (engines is not null) CopyPdhInput(facts, engines);
        Each(rows, row =>
        {
            // Native publishes/suppresses this vector trigger. It has no scalar
            // SDK operand; leave its observation untouched when the hook exists.
            if (Field(facts, row) == MetricFieldCode.GpuEngineBusy && vector is not null) return;
            var index = Scope(facts, row).DisplayIndex;
            if (Field(facts, row) == MetricFieldCode.GpuSharedMemoryUsed)
                Integer(raw, row, shared is { ProviderAvailability: NativePdhProviderAvailability.Available, ObservedAt: not null }
                    && shared.SharedUsageBytesByAdapterIndex.TryGetValue(index, out var sharedBytes) ? sharedBytes : null,
                    shared?.ObservedAt?.ToUnixTimeMilliseconds() ?? Now());
            else if (Field(facts, row) == MetricFieldCode.GpuUsage)
            {
                if (engines?.ResultCode == NativePdhResultCode.NoData)
                    raw.WriteUnavailable(row, MetricOwnerObservationStatus.Skipped, 0);
                else Number(raw, row, engines is { ProviderAvailability: NativePdhProviderAvailability.Available, ObservedAt: not null }
                    && engines.UsageByAdapterIndex.TryGetValue(index, out var engineUsage) ? engineUsage : null,
                    engines?.ObservedAt?.ToUnixTimeMilliseconds() ?? Now());
            }
            else Missing(raw, row, MissingOperandReason); // Legacy small fixture without vector inputs.
        });
        // The native raw vector/source guard requires the SAME domain status.
        // Shared-memory scalar health remains independent and is already written.
        if (vector is not null) return engines is null ? Unavailable : PdhStatus(engines);
        return engines?.ProviderAvailability == NativePdhProviderAvailability.Available
            || engines?.ResultCode == NativePdhResultCode.NoData || shared?.ProviderAvailability == NativePdhProviderAvailability.Available ? Complete : Unavailable;
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "SDK topology replaced or owner stopped")]
    private IReadOnlyList<WindowsGpuAdapter> GpuTargets(SourceRows rows, WindowsGpuAdapterInventoryRead inventory)
    {
        if (!rows.Borrow.Facts.HasSdkGrouping)
        { var indexes = SdkIndexes(rows); return SdkListView(rows, inventory.Adapters.Where(x => indexes.Contains(x.Index))); }
        return SdkScopes(rows, scope =>
        {
            if (scope.GpuAdapter is { } original) return original;
            // The normal raw inventory is in display-index order. A caller with
            // another immutable ordering supplies the original GpuAdapter ref.
            var low = 0; var high = inventory.Adapters.Count;
            while (low < high) { var middle = low + (high - low) / 2; if (inventory.Adapters[middle].Index < scope.DisplayIndex) low = middle + 1; else high = middle; }
            if (low == inventory.Adapters.Count || inventory.Adapters[low].Index != scope.DisplayIndex) throw Contract();
            return inventory.Adapters[low];
        });
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadSystemIo(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var disk = Has(rows, facts, MetricFieldCode.DiskActive, MetricFieldCode.DiskReadRate, MetricFieldCode.DiskWriteRate, MetricFieldCode.DiskQueue);
        var network = Has(rows, facts, MetricFieldCode.NetworkReceiveRate, MetricFieldCode.NetworkSendRate, MetricFieldCode.NetworkUtilization);
        var value = resources.ReadSystemIo(new PdhSystemIoReadRequest(disk, network));
        Each(rows, row =>
        {
            var code = Field(facts, row);
            var diskField = code is MetricFieldCode.DiskActive or MetricFieldCode.DiskReadRate or MetricFieldCode.DiskWriteRate or MetricFieldCode.DiskQueue;
            if ((diskField ? value.DiskAvailability : value.NetworkAvailability) != NativePdhProviderAvailability.Available)
            { Missing(raw, row); return; }
            double? v = code switch
            {
                MetricFieldCode.DiskActive when value.Disk.HasValue(PdhDiskMetricValidity.ActivePercent) => value.Disk.ActivePercent,
                MetricFieldCode.DiskReadRate when value.Disk.HasValue(PdhDiskMetricValidity.ReadBytesPerSecond) => value.Disk.ReadBytesPerSecond,
                MetricFieldCode.DiskWriteRate when value.Disk.HasValue(PdhDiskMetricValidity.WriteBytesPerSecond) => value.Disk.WriteBytesPerSecond,
                MetricFieldCode.DiskQueue when value.Disk.HasValue(PdhDiskMetricValidity.QueueLength) => value.Disk.QueueLength,
                MetricFieldCode.NetworkReceiveRate => value.Network.ReceiveBytesPerSecond,
                MetricFieldCode.NetworkSendRate => value.Network.SendBytesPerSecond,
                MetricFieldCode.NetworkUtilization => value.Network.UtilizationPercent, _ => null
            };
            Number(raw, row, v, (diskField ? value.DiskObservedAt : value.NetworkObservedAt)?.ToUnixTimeMilliseconds() ?? Now());
        });
        return (disk && value.DiskAvailability == NativePdhProviderAvailability.Available)
            || (network && value.NetworkAvailability == NativePdhProviderAvailability.Available) ? Complete : Unavailable;
    }
}
