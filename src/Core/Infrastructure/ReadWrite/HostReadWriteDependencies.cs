using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.ReadWrite.Dependencies;

namespace ResourceManager.App.Infrastructure.ReadWrite;

internal static class HostReadWriteDependencies
{
    internal static DependencyActions Create(HttpClient client, IProviderRuntimeProbe providers, string packageRoot,
        Func<string, DependencyStatus>? nativeProbe = null, DependencyPackageInstaller? installer = null)
    {
        nativeProbe ??= ReadWriteNativeDependencyProbe.Query;
        installer ??= new DependencyPackageInstaller();
        var bindings = new List<DependencyBinding>
        {
            new(NativeMetricSnapshotSourceCatalog.NvidiaNvml, () => nativeProbe(NativeMetricSnapshotSourceCatalog.NvidiaNvml), null),
            new(NativeMetricSnapshotSourceCatalog.IntelGcl, () => nativeProbe(NativeMetricSnapshotSourceCatalog.IntelGcl), null),
            new("control.intel.igcl", () => nativeProbe(NativeMetricSnapshotSourceCatalog.IntelGcl) with { Id = "control.intel.igcl" }, null),
            // The canonical table declares a dependency ID, not its read source ID.
            new("intel-graphics-driver", () => nativeProbe(NativeMetricSnapshotSourceCatalog.IntelGcl)
                with { Id = "intel-graphics-driver" }, null)
        };
        // Source bindings are explicit. A different vendor library is never
        // evidence that the dependency of this read/write operation is present.
        (string Source, string Component)[] components =
        [
            (NativeMetricSnapshotSourceCatalog.NvidiaNvapi, "nvidia-nvapi-provider"),
            (NativeMetricSnapshotSourceCatalog.AmdAdlx, "amd-adlx-provider"),
            (NativeMetricSnapshotSourceCatalog.AmdSmu, "amd-smu-pawnio-provider"),
            (NativeMetricSnapshotSourceCatalog.IntelMsr, "intel-msr-pawnio-provider"),
            ("control.intel.msr", "intel-msr-pawnio-provider"),
            ("control.amd.hardware-bridge", "hardware-bridge"),
            ("control.amd.adlx", "amd-adlx-provider"),
            ("control.amd.igpu.hardware-bridge", "hardware-bridge"),
            ("intel-msr-pawnio-provider", "intel-msr-pawnio-provider"),
            ("hardware-bridge", "hardware-bridge"),
            ("amd-adlx-provider", "amd-adlx-provider"),
            (NativeMetricSnapshotSourceCatalog.HardwareMonitorWmi, "librehardwaremonitor-provider"),
            (NativeMetricSnapshotSourceCatalog.NotebookOemFan, NotebookOemFanSensorReader.ComponentId)
        ];
        foreach (var (source, component) in components)
        {
            var definition = OptionalDependencyCatalog.Find(component);
            var directory = Path.Combine(packageRoot, "Dependencies", component);
            bindings.Add(new(source, () => QueryProvider(source, component, directory, providers, definition),
                definition is null ? null : (package, cancellationToken) => installer.InstallAsync(package,
                    new(directory, definition.RequiresElevation, ArchiveFiles: definition.PayloadSource is { IncludeArchiveContents: false } payload
                        ? [.. payload.FileNames] : null), cancellationToken)));
        }
        return new(client, bindings);
    }

    private static DependencyStatus QueryProvider(string source, string component, string directory,
        IProviderRuntimeProbe providers, OptionalDependencyDefinition? definition)
    {
        var result = FromProvider(source, providers.Probe(component));
        if (definition?.PayloadSource is { IncludeArchiveContents: false } payload
            && payload.FileNames.Any(name => !File.Exists(Path.Combine(directory, name))))
            return new(source, DependencyAvailability.Missing, "dependency-module-missing");
        return result;
    }

    private static DependencyStatus FromProvider(string source, ComponentProviderRuntimeProbeResult result)
        => result.State == "Missing" ? new(source, DependencyAvailability.Missing, "dependency-runtime-missing")
            : result.RuntimeAvailable && result.InitializeExportAvailable
                ? new(source, DependencyAvailability.Ready, null)
                : new(source, DependencyAvailability.Unknown, "dependency-runtime-unverified");
}



