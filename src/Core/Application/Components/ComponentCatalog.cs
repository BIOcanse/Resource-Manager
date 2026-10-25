using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Components;

public static class ComponentCatalog
{
    public static IReadOnlyList<ComponentDefinition> Definitions { get; } =
    [
        FromDependency("shared-webview2-runtime", "Microsoft", ComponentProviderKinds.Runtime, ["webview2-runtime"]),
        FromDependency("amd-smu-pawnio-provider", "namazso / AMD SMU", ComponentProviderKinds.Cpu,
            ["smu-pm-table", "stapm", "power", "voltage", "current", "temperature", "per-core"]),
        FromDependency("intel-msr-pawnio-provider", "namazso / Intel", ComponentProviderKinds.Cpu,
            ["pl1", "pl2", "time-window", "voltage-offset"]),
        FromDependency("hardware-bridge", "Resource Manager", ComponentProviderKinds.Cpu, ["power", "voltage"]),
        FromDependency("hardware-sensors-pawnio-provider", "namazso", ComponentProviderKinds.HardwareMonitor,
            ["fan", "temperature", "voltage"]),
        Bundled("librehardwaremonitor-provider", "LibreHardwareMonitor",
            "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", "https://www.mozilla.org/MPL/2.0/",
            "librehardwaremonitor-provider", ComponentProviderKinds.HardwareMonitor,
            ["hardware-monitor-core", "memory-temperature", "fan"]),
        Bundled("notebook-oem-fan-provider", "Resource Manager", "", null,
            "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem,
            ["notebook-oem-fan.cpu-rpm", "notebook-oem-fan.cpu-percent", "notebook-oem-fan.gpu-rpm",
                "notebook-oem-fan.gpu-percent", "notebook-oem-fan.vendor-adapters"]),
        Bundled("nvidia-nvml-provider", "NVIDIA",
            "https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html", null,
            "nvidia-nvml", ComponentProviderKinds.Gpu,
            ["nvidia-gpu-clocks", "nvidia-gpu-memory", "nvidia-gpu-power", "nvidia-gpu-temperature"]),
        Bundled("nvidia-nvapi-provider", "NVIDIA", "https://docs.nvidia.com/nvapi/modules.html", null,
            "nvidia-nvapi", ComponentProviderKinds.Gpu,
            ["nvidia-gpu-cooler", "nvidia-gpu-voltage", "nvidia-gpu-current"]),
        Bundled("amd-adlx-provider", "AMD", "https://github.com/GPUOpen-LibrariesAndSDKs/ADLX", null,
            "amd-adlx", ComponentProviderKinds.Gpu,
            ["amd-gpu-clocks", "amd-gpu-power", "amd-gpu-board-power", "amd-gpu-temperature",
                "amd-gpu-hotspot-temperature", "amd-gpu-intake-temperature", "amd-gpu-fan", "amd-gpu-voltage"])
    ];

    public static ComponentDefinition? Find(string id) =>
        Definitions.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool IsOptionalDependency(string id) => OptionalDependencyCatalog.Find(id) is not null;

    private static ComponentDefinition FromDependency(
        string id, string vendor, string providerKind, IReadOnlyList<string> capabilities)
    {
        var dependency = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Missing optional dependency definition: {id}");
        return new(id, vendor, dependency.SourcePageUrl, dependency.ExternalTermsUrl,
            SoftwareManagementRoles.Dependency, false, dependency.RequiresExternalTermsAcknowledgement,
            dependency.RequiresElevation,
            capabilities.Select(capability => new ComponentCapability($"{id}.{capability}", id, providerKind)).ToArray());
    }

    private static ComponentDefinition Bundled(
        string id, string vendor, string sourcePageUrl, string? externalTermsUrl,
        string providerId, string providerKind, IReadOnlyList<string> capabilities) =>
        new(id, vendor, sourcePageUrl, externalTermsUrl, SoftwareManagementRoles.Dependency,
            true, false, false,
            capabilities.Select(capability => new ComponentCapability(capability, providerId, providerKind)).ToArray());
}
