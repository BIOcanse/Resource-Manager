using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Dependencies;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Explicit dependency query checks the same DLL and required exports
/// used by the reader. It does not initialize a device API or sample hardware.</summary>
internal static class ReadWriteNativeDependencyProbe
{
    internal static DependencyStatus Query(string source) => source switch
    {
        NativeMetricSnapshotSourceCatalog.NvidiaNvml => QueryLibrary(source, "nvml.dll",
            ["nvmlInit_v2", "nvmlDeviceGetCount_v2", "nvmlDeviceGetHandleByIndex_v2"], DllImportSearchPath.SafeDirectories),
        NativeMetricSnapshotSourceCatalog.IntelGcl => !Environment.Is64BitProcess
            ? new(source, DependencyAvailability.Unsupported, "dependency-requires-64-bit-process")
            : QueryLibrary(source, "ControlLib.dll",
                ["ctlInit", "ctlClose", "ctlEnumerateDevices", "ctlGetDeviceProperties", "ctlPowerTelemetryGet"], DllImportSearchPath.System32),
        _ => throw new ArgumentException("Unknown native dependency.", nameof(source))
    };

    internal static DependencyStatus QueryLibrary(string source, string library, IReadOnlyList<string> exports,
        DllImportSearchPath searchPath)
    {
        if (!OperatingSystem.IsWindows()) return new(source, DependencyAvailability.Unsupported, "dependency-requires-windows");
        if (!NativeLibrary.TryLoad(library, typeof(ReadWriteNativeDependencyProbe).Assembly, searchPath, out var handle))
            return new(source, DependencyAvailability.Missing, "dependency-runtime-missing");
        try
        {
            return exports.All(name => NativeLibrary.TryGetExport(handle, name, out _))
                ? new(source, DependencyAvailability.Ready, null)
                : new(source, DependencyAvailability.Unknown, "dependency-exports-missing");
        }
        finally { NativeLibrary.Free(handle); }
    }
}
