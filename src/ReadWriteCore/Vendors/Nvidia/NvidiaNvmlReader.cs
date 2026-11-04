using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed partial class NvidiaNvmlReader : IDisposable
{
    private const int NvmlSuccess = 0;
    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader windowsAdapterReader = new();
    private readonly Func<int> initialize;
    private readonly Func<int> shutdown;
    private bool initialized;
    private bool disposed;

    private readonly Func<string, string, string?>? describeProviderMessage;
    public NvidiaNvmlReader(Func<string, string, string?>? describeProviderMessage = null)
        : this(NativeMethods.nvmlInit, NativeMethods.nvmlShutdown, describeProviderMessage) { }
    internal NvidiaNvmlReader(Func<int> initialize) : this(initialize, NativeMethods.nvmlShutdown, null) { }
    internal NvidiaNvmlReader(Func<int> initialize, Func<int> shutdown)
        : this(initialize, shutdown, null) { }
    private NvidiaNvmlReader(Func<int> initialize, Func<int> shutdown, Func<string, string, string?>? describeProviderMessage)
        => (this.initialize, this.shutdown, this.describeProviderMessage) = (initialize, shutdown, describeProviderMessage);

    [ResourceManager.ReadWrite.RuntimeReadTransport]

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(NvidiaNvmlReadRequest.All, null, cancellationToken);
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        NvidiaNvmlReadRequest request,
        WindowsGpuAdapterInventoryRead? windowsInventory,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EnsureInitialized())
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);

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

    internal bool EnsureInitialized()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return initialized;
            }

            try
            {
                initialized = initialize() == NvmlSuccess;
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

    /// <summary>Returns this reader's initialization reference. A failed shutdown remains owned for explicit retry.</summary>
    public void ReleaseSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ReleaseSessionCore();
        }
    }

    private void ReleaseSessionCore()
    {
        if (!initialized) return;
        var result = shutdown();
        if (result != NvmlSuccess)
            throw new InvalidOperationException($"NVML shutdown failed ({result}); initialization remains owned for cleanup.");
        initialized = false;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ReleaseSessionCore();
        }
    }
}
