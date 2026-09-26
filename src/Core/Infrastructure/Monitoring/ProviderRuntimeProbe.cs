using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.BrowserRuntimes;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class ProviderRuntimeProbe(
    IRuntimePlanProvider runtimePlanProvider,
    IHostEnvironment environment) : IProviderRuntimeProbe
{
    public ComponentProviderRuntimeProbeResult Probe(string componentId)
        => Probe(componentId, deepVerify: false);

    public ComponentProviderRuntimeProbeResult Probe(string componentId, bool deepVerify)
    {
        if (componentId.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase))
        {
            var discovery = BrowserRuntimeDiscovery.Discover(
                PackagePathResolver.ResolvePackageRoot(AppContext.BaseDirectory));
            var runtime = discovery.SharedRuntime;
            return new ComponentProviderRuntimeProbeResult(
                runtime is not null,
                runtime is not null,
                runtime is null ? "Missing" : "RuntimeAvailable",
                runtime is null ? "未检测到共享 WebView2 运行时。" : "共享 WebView2 运行时可用。",
                runtime?.RuntimeDirectory);
        }

        if (componentId.Equals("amd-adlx-provider", StringComparison.OrdinalIgnoreCase))
        {
            var adlx = AmdAdlxRuntimeProbe.Probe();
            return new ComponentProviderRuntimeProbeResult(
                adlx.RuntimeAvailable,
                adlx.InitializeExportAvailable,
                adlx.State,
                adlx.Message,
                adlx.LibraryPath);
        }

        if (componentId.Equals("amd-ryzen-master-monitoring-sdk", StringComparison.OrdinalIgnoreCase))
        {
            var ryzen = AmdRyzenMasterRuntimeProbe.Probe();
            return new ComponentProviderRuntimeProbeResult(
                ryzen.RuntimeAvailable,
                ryzen.CpuParametersExportAvailable,
                ryzen.State,
                ryzen.Message,
                ryzen.PlatformLibraryPath);
        }

        if (componentId.Equals("amd-smu-pawnio-provider", StringComparison.OrdinalIgnoreCase))
        {
            var pawnIo = PawnIoRuntimeProbe.Probe(environment.ContentRootPath, deepVerify);
            return new ComponentProviderRuntimeProbeResult(
                pawnIo.RuntimeAvailable,
                pawnIo.DeviceAvailable,
                pawnIo.State,
                pawnIo.Message,
                pawnIo.RuntimePath);
        }

        if (componentId.Equals("nvidia-nvapi-provider", StringComparison.OrdinalIgnoreCase))
        {
            var nvapi = HardwareSensorDependencyRuntimeProbe.ProbeNvidiaNvapi();
            return new ComponentProviderRuntimeProbeResult(
                nvapi.RuntimeAvailable,
                nvapi.PrimaryCapabilityAvailable,
                nvapi.State,
                nvapi.Message,
                nvapi.RuntimePath);
        }

        if (componentId.Equals("librehardwaremonitor-provider", StringComparison.OrdinalIgnoreCase))
        {
            var hardwareMonitor = HardwareSensorDependencyRuntimeProbe.ProbeLibreHardwareMonitor();
            return new ComponentProviderRuntimeProbeResult(
                hardwareMonitor.RuntimeAvailable,
                hardwareMonitor.PrimaryCapabilityAvailable,
                hardwareMonitor.State,
                hardwareMonitor.Message,
                hardwareMonitor.RuntimePath);
        }

        if (componentId.Equals("notebook-fancontrol-provider", StringComparison.OrdinalIgnoreCase))
        {
            var nbfc = HardwareSensorDependencyRuntimeProbe.ProbeNotebookFanControl();
            return new ComponentProviderRuntimeProbeResult(
                nbfc.RuntimeAvailable,
                nbfc.PrimaryCapabilityAvailable,
                nbfc.State,
                nbfc.Message,
                nbfc.RuntimePath);
        }

        if (componentId.Equals(NotebookOemFanSensorReader.ComponentId, StringComparison.OrdinalIgnoreCase))
        {
            var notebookOem = NotebookOemFanSensorReader.ProbeRuntime(
                runtimePlanProvider.Current.Monitoring.FirmwareProviders);
            return new ComponentProviderRuntimeProbeResult(
                notebookOem.RuntimeAvailable,
                notebookOem.PrimaryCapabilityAvailable,
                notebookOem.State,
                notebookOem.Message,
                notebookOem.RuntimePath);
        }

        return new ComponentProviderRuntimeProbeResult(
            false,
            false,
            "Missing",
            "未配置该 Provider 的运行库探测。",
            null);
    }
}
