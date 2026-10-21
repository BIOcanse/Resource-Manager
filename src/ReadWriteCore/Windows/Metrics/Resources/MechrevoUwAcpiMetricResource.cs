using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Vendors.Mechrevo;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;
public sealed class MechrevoUwAcpiMetricResource : IDisposable
{    private const string ProviderName = "Mechrevo UWACPI";
    private readonly object gate = new();
    private readonly Func<string?> resolveLibraryPath;
    private readonly Func<string, MechrevoUwAcpiReader> createReader;
    private MechrevoUwAcpiReader? reader;
    private readonly Func<MechrevoUwAcpiReadResult, string, string>? describe;
    private readonly string missingMessage;
    private bool initialized;
    private bool disposed;

public MechrevoUwAcpiMetricResource(Func<string?> resolveLibraryPath,
        Func<string, MechrevoUwAcpiReader> createReader, Func<MechrevoUwAcpiReadResult, string, string>? describe = null,
        string missingMessage = "Official UWACPI library path is unavailable.")
    {
        this.resolveLibraryPath = resolveLibraryPath ?? throw new ArgumentNullException(nameof(resolveLibraryPath));
        this.createReader = createReader ?? throw new ArgumentNullException(nameof(createReader));
        this.describe = describe;
        this.missingMessage = missingMessage;
    }

    // A demand lifetime ends here; the next explicit read resolves its path and creates a fresh SDK reader.
    public void ReleaseSession()
    {
        lock (gate)
        {
            if (disposed) return;
            reader?.Dispose();
            reader = null;
            initialized = false;
        }
    }
    public MechrevoUwAcpiSensorSnapshot Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized)
            {
                var dllPath = resolveLibraryPath();
                reader = dllPath is null ? null : createReader(dllPath);
                initialized = true;
            }
            if (reader is null)
            {
                return new(new HardwareSensorProviderState(ProviderName, "Missing",
                    missingMessage), null, null, null);
            }

            var result = reader.Read();
            var state = result.Status == MechrevoUwAcpiReadStatus.Active ? "Active" : "Unavailable";
            var message = describe?.Invoke(result, reader.LibraryPath) ?? result.Status.ToString();
            var gpuSensor = result.GpuFanSpeedRpm is null && result.GpuFanSpeedPercent is null
                ? null
                : new PlatformGpuSensor(0, "NVIDIA AMD dGPU fan",
                    "Mechrevo UWACPI / dGPU fan / official ACPI EC read-only provider",
                    null, null, result.GpuFanSpeedRpm, result.GpuFanSpeedPercent, null, null);
            return new(new HardwareSensorProviderState(ProviderName, state, message),
                result.CpuFanSpeedRpm, result.CpuFanSpeedPercent, gpuSensor);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            reader?.Dispose();
            reader = null;
        }
    }

}

