using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvmlReader
{
    private const int NvmlSuccess = 0;
    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader windowsAdapterReader = new();
    private bool initAttempted;
    private bool initialized;

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(NvidiaNvmlReadRequest.All, null, cancellationToken);
    }

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        NvidiaNvmlReadRequest request,
        WindowsGpuAdapterInventoryRead? windowsInventory,
        CancellationToken cancellationToken)
    {
        if (!EnsureInitialized())
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (NativeMethods.nvmlDeviceGetCount(out var count) != NvmlSuccess || count == 0)
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            windowsInventory ??= windowsAdapterReader.ReadInventory();
            var byDisplayIndex = new Dictionary<int, GpuMetrics>();
            var ambiguousDisplayIndexes = new HashSet<int>();

            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (NativeMethods.nvmlDeviceGetHandleByIndex(index, out var device) != NvmlSuccess)
                {
                    continue;
                }

                var gpu = ReadDevice(
                    device,
                    windowsInventory,
                    request);
                if (gpu is null
                    || ambiguousDisplayIndexes.Contains(gpu.Index))
                {
                    continue;
                }
                if (!byDisplayIndex.TryAdd(gpu.Index, gpu))
                {
                    byDisplayIndex.Remove(gpu.Index);
                    ambiguousDisplayIndexes.Add(gpu.Index);
                }
            }

            return Task.FromResult<IReadOnlyList<GpuMetrics>>(
                byDisplayIndex.Values
                    .OrderBy(static gpu => gpu.Index)
                    .ToArray());
        }
    }

    private bool EnsureInitialized()
    {
        lock (gate)
        {
            if (initAttempted)
            {
                return initialized;
            }

            initAttempted = true;

            try
            {
                initialized = NativeMethods.nvmlInit() == NvmlSuccess;
            }
            catch (DllNotFoundException)
            {
                initialized = false;
            }
            catch (EntryPointNotFoundException)
            {
                initialized = false;
            }

            return initialized;
        }
    }
}
