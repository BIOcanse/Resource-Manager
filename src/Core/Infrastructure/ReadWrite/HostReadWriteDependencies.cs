using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.ReadWrite.Dependencies;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.ReadWrite;

internal static class HostReadWriteDependencies
{
    internal static DependencyActions Create(HttpClient client, IProviderRuntimeProbe providers, string packageRoot,
        HardwareBridgeClient hardwareBridge, IManagementInventoryRevisionOwner inventoryRevisionOwner,
        Func<string, DependencyStatus>? nativeProbe = null, DependencyPackageInstaller? installer = null)
    {
        var resolvedPackageRoot = Path.GetFullPath(packageRoot);
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
            (NativeMetricSnapshotSourceCatalog.AmdSmn, "amd-smu-pawnio-provider"),
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
            var directory = Path.Combine(resolvedPackageRoot, "Dependencies", component);
            bindings.Add(new(source, () => QueryProvider(source, component, directory, providers, definition),
                definition is null ? null : (package, cancellationToken) => InstallSelectedPackageAsync(
                    package, component, definition, resolvedPackageRoot, directory,
                    hardwareBridge, inventoryRevisionOwner, installer, cancellationToken)));
        }
        return new(client, bindings);
    }

    private static async ValueTask<DependencyActionResult> InstallSelectedPackageAsync(
        DependencyPackage package,
        string componentId,
        OptionalDependencyDefinition definition,
        string packageRoot,
        string installDirectory,
        HardwareBridgeClient hardwareBridge,
        IManagementInventoryRevisionOwner inventoryRevisionOwner,
        DependencyPackageInstaller installer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packagePath = Path.GetFullPath(package.LocalPath);
        var cacheRoot = Path.Combine(packageRoot, "Misc", "DependencyInstallers");
        DependencyInstallPaths.RejectLinks(packageRoot);
        DependencyInstallPaths.RejectLinks(packagePath);
        DependencyInstallPaths.RejectLinks(cacheRoot);
        DependencyInstallPaths.RejectLinks(installDirectory);
        if (Directory.Exists(installDirectory)) DependencyInstallPaths.ValidateTree(installDirectory);

        using var asset = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (IsWithinDirectory(packagePath, cacheRoot))
        {
            DependencyInstallPaths.ValidateTree(cacheRoot);
            var sidecar = DependencyReleaseRecordStore.DownloadPath(packagePath);
            DependencyInstallPaths.RejectLinks(sidecar);
            var release = DependencyReleaseRecordStore.Read(sidecar)
                ?? throw new InvalidDataException("The cached package has no valid download record.");
            var actualSha256 = Convert.ToHexString(SHA256.HashData(asset));
            if (!string.Equals(release.AssetSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The cached asset SHA-256 differs from its download record.");
            asset.Position = 0;
        }

        if (Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = new ZipArchive(asset, ZipArchiveMode.Read, leaveOpen: true);
            return await InstallSelectedZipAsync(package, componentId, definition, installDirectory,
                archive, hardwareBridge, inventoryRevisionOwner, cancellationToken).ConfigureAwait(false);
        }

        return await installer.InstallAsync(package,
            new(installDirectory, definition.RequiresElevation,
                ArchiveFiles: definition.PayloadSource is { IncludeArchiveContents: false } payload
                    ? [.. payload.FileNames] : null),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DependencyActionResult> InstallSelectedZipAsync(
        DependencyPackage package,
        string componentId,
        OptionalDependencyDefinition definition,
        string installDirectory,
        ZipArchive archive,
        HardwareBridgeClient hardwareBridge,
        IManagementInventoryRevisionOwner inventoryRevisionOwner,
        CancellationToken cancellationToken)
    {
        using var change = inventoryRevisionOwner.BeginComponentChange(componentId);
        using var transaction = new DependencyPayloadTransaction(installDirectory, replaceRelease: true);
        var payload = definition.PayloadSource;
        transaction.Prepare(
            archive,
            payload?.FileNames ?? definition.InstalledProbeRelativePaths,
            [],
            payload?.IncludeArchiveContents == true,
            null,
            cancellationToken);

        await using var helperSession = componentId.Equals("hardware-bridge", StringComparison.OrdinalIgnoreCase)
            ? await hardwareBridge.SuspendForInstallationAsync(cancellationToken).ConfigureAwait(false)
            : null;
        transaction.Publish(cancellationToken);
        inventoryRevisionOwner.ObserveComponentContentChanged(componentId);
        return new DependencyActionResult(package.DependencyId, "filesExtracted", null);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static DependencyStatus QueryProvider(string source, string component, string directory,
        IProviderRuntimeProbe providers, OptionalDependencyDefinition? definition)
    {
        var result = FromProvider(source, providers.Probe(component));
        if (definition?.PayloadSource is { IncludeArchiveContents: false } payload
            && payload.FileNames.Where(name => source switch
                {
                    NativeMetricSnapshotSourceCatalog.AmdSmu => name == "RyzenSMU.bin",
                    NativeMetricSnapshotSourceCatalog.AmdSmn => name == "AMDFamily17.bin",
                    _ => true
                }).Any(name => !File.Exists(Path.Combine(directory, name))))
            return new(source, DependencyAvailability.Missing, "dependency-module-missing");
        return result;
    }

    private static DependencyStatus FromProvider(string source, ComponentProviderRuntimeProbeResult result)
        => result.State == ComponentProviderStates.Missing
            ? new(source, DependencyAvailability.Missing, "dependency-runtime-missing")
            : result.RuntimeAvailable && result.InitializeExportAvailable
                ? new(source, DependencyAvailability.Ready, null)
                : new(source, DependencyAvailability.Unknown, "dependency-runtime-unverified");
}



