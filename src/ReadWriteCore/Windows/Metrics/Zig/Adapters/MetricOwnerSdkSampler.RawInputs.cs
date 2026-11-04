using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    private static bool HasRawInput(MetricDiscoveryRoundFacts facts, ulong source)
        => facts.GpuInventory?.SourceId == source || facts.PdhGpu?.SourceId == source || facts.Notebook?.SourceId == source;

    private void ValidateRawInputs(MetricDiscoveryRoundFacts facts)
    {
        void Check(ulong source, ulong observation, uint role, MetricOwnerSdkSource resource)
        {
            var i = 0; while (i < facts.SourceCount && facts.SourceId(i) != source) i++;
            if (i == facts.SourceCount || facts.Observation(i) != observation || (facts.SourceRoleFlags(i) & role) == 0
                || SourceBinding(source).Resource != resource) throw Contract();
        }
        // Fixed native role bits, declared by discovery_runtime/discovery_vectors:
        // inventory1, PDH2, NPU4, notebook8. No source IDs/names are inferred.
        if (facts.GpuInventory is { } inventory) Check(inventory.SourceId, inventory.Observation, 1, MetricOwnerSdkSource.GpuInventory);
        if (facts.PdhGpu is { } pdh) Check(pdh.SourceId, pdh.Observation, 2, MetricOwnerSdkSource.PdhGpu);
        if (facts.Notebook is { } notebook) Check(notebook.SourceId, notebook.Observation, 8, MetricOwnerSdkSource.NotebookFan);
    }

    private static void RequireInputCapacity(int count, uint capacity)
    {
        if ((ulong)count > capacity) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = MetricOwnerFamily.Binding, Required = checked((ulong)count), Limit = capacity });
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadGpuInventory(MetricDiscoveryRoundFacts facts, int sourceRow)
    {
        var input = facts.GpuInventory ?? throw Contract();
        if (input.SourceId != facts.SourceId(sourceRow)) throw Contract();
        var value = resources.ReadGpuInventory();
        RequireInputCapacity(value.Adapters.Count, input.Capacity);
        var status = value.Status switch
        {
            SamplingObservationStatus.Current => Complete,
            SamplingObservationStatus.Partial => 2U,
            SamplingObservationStatus.Unsupported => Unsupported,
            SamplingObservationStatus.NotRequested => 5U,
            _ => Unavailable
        };
        var i = 0;
        while (i < value.Adapters.Count)
        {
            var adapter = value.Adapters[i];
            // The original SDK adapter has no stored exact key. Reuse the raw
            // topology identity formatter, not a catalog projection/handle join.
            // Its identity string + physical-identity sort are SDK shape costs.
            input.SetRow(i++, WindowsGpuAdapterIdentity.Pack(adapter.Luid), WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter));
        }
        var observed = value.ObservedAtUtcTicks == 0 ? 0 : new DateTimeOffset(value.ObservedAtUtcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds();
        input.Complete(value.Generation, value.TopologyFingerprint, observed, checked((uint)value.Adapters.Count),
            value.ObservedCount, value.SkippedCount, value.OverflowCount, status);
        return status;
    }

    private static uint PdhStatus(PdhGpuEngineUsageRead value) => value.ProviderAvailability switch
    {
        NativePdhProviderAvailability.Available when value.ObservedAt.HasValue => Complete,
        NativePdhProviderAvailability.NotRequested => 5,
        _ when value.ResultCode == NativePdhResultCode.NoData => 5,
        _ => Unavailable
    };

    private static void CopyPdhInput(MetricDiscoveryRoundFacts facts, PdhGpuEngineUsageRead value)
    {
        var input = facts.PdhGpu;
        if (input is null) return;
        RequireInputCapacity(value.UsageByEngine.Count, input.Capacity);
        var i = 0;
        foreach (var pair in value.UsageByEngine)
        {
            // NativePdhEngineClass in Native/Pdh/NativePdhModels.cs is exactly
            // Other0/RayTracing1/Compute2, as required by MetricVectorGpuInput.
            var identity = pair.Key;
            input.SetRow(i++, identity.AdapterLuid, identity.PhysicalIndex, identity.EngineIndex, (uint)identity.EngineClass, pair.Value);
        }
        input.Complete(checked((uint)i), PdhStatus(value), value.ObservedAt?.ToUnixTimeMilliseconds() ?? 0,
            value.NativeStatus != 0 ? unchecked((uint)value.NativeStatus) : (uint)value.ResultCode, 0);
        // Original PdhGpuEngineUsageRead has no frame-valid-mask operand. Zero
        // is the declared absent mask, never an invented HasData flag.
    }

    private static void FailRawInput(MetricDiscoveryRoundFacts facts, ulong source)
    {
        var at = Now();
        if (facts.GpuInventory is { } inventory && inventory.SourceId == source)
            inventory.Complete(0, 0, at, 0, 0, 0, 0, Unavailable, SdkUnavailableReason);
        if (facts.PdhGpu is { } pdh && pdh.SourceId == source) pdh.Complete(0, Unavailable, at, SdkUnavailableReason);
        if (facts.Notebook is { } notebook && notebook.SourceId == source)
        { notebook.SetCpu(0, 0, 0); notebook.Complete(0, Unavailable, at, SdkUnavailableReason); }
    }
}
