using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal static class GpuStartupProviderArtifacts
{
    public const string VulkanLayerFileName = "ResourceManager.VulkanPlacementLayer.dll";
    public const string VulkanManifestFileName = "ResourceManager.VulkanPlacementLayer.json";

    public static IReadOnlyList<string> MissingFiles(string baseDirectory, IReadOnlyList<string> providers)
    {
        var required = new List<string> { WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName };
        foreach (var provider in providers)
        {
            if (provider == GpuPlacementProviderIds.D3dDeviceCreateShim)
            {
                required.Add(Path.Combine("GpuPlacementShim", WindowsGpuPlacementInjector.StartupBootstrapFileName));
                required.Add(Path.Combine("GpuPlacementShim", WindowsGpuPlacementInjector.RuntimeProviderFileName));
            }
            else if (provider == GpuPlacementProviderIds.VulkanExplicitLayer)
            {
                required.Add(Path.Combine("GpuPlacementShim", VulkanLayerFileName));
                required.Add(Path.Combine("GpuPlacementShim", VulkanManifestFileName));
            }
            else
            {
                throw new ArgumentException("Unsupported startup provider.", nameof(providers));
            }
        }
        return required.Where(path => !File.Exists(Path.Combine(baseDirectory, path))).ToArray();
    }
}
