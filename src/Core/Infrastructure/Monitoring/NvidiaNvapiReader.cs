using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvapiReader
{
    private const int NvapiOk = 0;
    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader windowsAdapterReader = new();
    private bool initAttempted;
    private bool initialized;
    private NativeBindings bindings = NativeBindings.Unavailable;

    public Task<IReadOnlyList<NvidiaNvapiGpuSensor>> ReadAsync(
        NvidiaNvapiReadRequest request,
        WindowsGpuAdapterInventoryRead? windowsInventory,
        CancellationToken cancellationToken)
    {
        if (!request.IncludesAny || !EnsureInitialized())
        {
            return Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]);
        }

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
        lock (gate)
        {
            if (initAttempted)
            {
                return initialized;
            }

            initAttempted = true;
            if (!TryLoadBindings(out bindings))
            {
                initialized = false;
                return false;
            }

            initialized = bindings.Initialize is not null
                && bindings.Initialize() == NvapiOk;
            return initialized;
        }
    }
}

internal sealed record NvidiaNvapiReadRequest(
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

internal sealed record NvidiaNvapiGpuSensor(
    int DisplayIndex,
    string Name,
    GpuSensorMetrics Sensors);
