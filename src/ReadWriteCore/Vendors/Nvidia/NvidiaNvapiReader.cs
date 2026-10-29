using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed partial class NvidiaNvapiReader : IDisposable
{
    private readonly Func<string, string, string?>? describeProviderMessage;
    public NvidiaNvapiReader(Func<string, string, string?>? describeProviderMessage = null) => this.describeProviderMessage = describeProviderMessage;
    private const int NvapiOk = 0;
    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader windowsAdapterReader = new();
    private bool initAttempted;
    private bool initialized;
    private NativeBindings bindings = NativeBindings.Unavailable;
    private INvidiaNvapiDrsApi? drsApi;
    private readonly Func<INvidiaNvapiDrsApi>? createDrsApi;
    private nint pendingSession;
    private bool disposed;

    internal NvidiaNvapiReader(INvidiaNvapiDrsApi api)
        : this(() => api) => ArgumentNullException.ThrowIfNull(api);

    internal NvidiaNvapiReader(Func<INvidiaNvapiDrsApi> createDrsApi)
        => this.createDrsApi = createDrsApi ?? throw new ArgumentNullException(nameof(createDrsApi));

    public Task<IReadOnlyList<NvidiaNvapiGpuSensor>> ReadAsync(
        NvidiaNvapiReadRequest request,
        WindowsGpuAdapterInventoryRead? windowsInventory,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IncludesAny || !EnsureInitialized())
                return Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]);

            if (bindings.EnumPhysicalGpus is null)
            {
                return Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]);
            }

            var handles = new IntPtr[NvapiMaxPhysicalGpus];
            if (bindings.EnumPhysicalGpus(handles, out var count) != NvapiOk || count == 0)
            {
                return Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]);
            }

            windowsInventory ??= windowsAdapterReader.ReadInventory();
            var byDisplayIndex = new Dictionary<int, NvidiaNvapiGpuSensor>();
            var ambiguousDisplayIndexes = new HashSet<int>();

            for (var index = 0; index < count && index < NvapiMaxPhysicalGpus; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = handles[index];
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                var sensor = ReadDevice(
                    handle,
                    request,
                    windowsInventory);
                if (sensor is null
                    || ambiguousDisplayIndexes.Contains(sensor.DisplayIndex))
                {
                    continue;
                }
                if (!byDisplayIndex.TryAdd(sensor.DisplayIndex, sensor))
                {
                    byDisplayIndex.Remove(sensor.DisplayIndex);
                    ambiguousDisplayIndexes.Add(sensor.DisplayIndex);
                }
            }

            return Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>(
                byDisplayIndex.Values
                    .OrderBy(static sensor => sensor.DisplayIndex)
                    .ToArray());
        }
    }

    private bool EnsureInitialized()
    {
        // Called only with gate held, including original GPU reads and DRS reads.
        if (initAttempted) return initialized && TryCleanupPendingSession();
        initAttempted = true;
        if (drsApi is null)
        {
            if (createDrsApi is not null) drsApi = createDrsApi();
            else
            {
                if (!TryLoadBindings(out bindings)) return false;
                drsApi = new NativeDrsApi(bindings);
            }
        }
        try
        {
            initialized = drsApi.Initialize() == NvapiOk;
            return initialized;
        }
        finally
        {
            if (!initialized)
            {
                drsApi.FreeLibrary();
                drsApi = null;
                bindings = NativeBindings.Unavailable;
            }
        }
    }

    private bool TryCleanupPendingSession()
    {
        if (pendingSession == 0) return true;
        try
        {
            if (drsApi is null || drsApi.DestroySession(pendingSession) != NvapiOk) return false;
            pendingSession = 0;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Stops all reads. On native cleanup failure throws and retains the outstanding
    /// session/initialization and module; the owner must retain this reader and may retry Dispose.
    /// No background retry and no release of another reader's initialization reference.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ReleaseSessionCore();
        }
    }

    /// <summary>Returns this reader's native state without retiring it. Failed cleanup stays owned for explicit retry.</summary>
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
        if (!TryCleanupPendingSession())
            throw new InvalidOperationException("NVAPI DRS session cleanup is still pending; retain and retry this reader.");
        // No successful owned session was released: retain the original initialization failure cache.
        if (drsApi is null) return;
        if (initialized)
        {
            if (drsApi.Unload() != NvapiOk)
                throw new InvalidOperationException("NVAPI unload failed; initialization and module are retained for cleanup.");
            initialized = false;
        }
        drsApi.FreeLibrary();
        drsApi = null;
        bindings = NativeBindings.Unavailable;
        initAttempted = false;
    }

}

public sealed record NvidiaNvapiReadRequest(
    IReadOnlySet<int>? DisplayIndexes,
    bool IncludeFanRpm,
    bool IncludeVoltage,
    bool IncludeCurrent)
{
    public static NvidiaNvapiReadRequest All { get; } = new(
        null,
        IncludeFanRpm: true,
        IncludeVoltage: true,
        IncludeCurrent: true);

    public bool IncludesAny => IncludeFanRpm || IncludeVoltage || IncludeCurrent;

    public bool IncludesDisplayIndex(int displayIndex)
    {
        return DisplayIndexes is null || DisplayIndexes.Contains(displayIndex);
    }
}

public sealed record NvidiaNvapiGpuSensor(
    int DisplayIndex,
    string Name,
    GpuSensorMetrics Sensors);
