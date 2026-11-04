namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuGraphicsApiRoutes
{
    // Candidates select the actual API-observation bridge; imports alone do not identify an application API.
    public static GpuGraphicsApi CollectModuleCandidates(IEnumerable<string> moduleNames)
    {
        GpuGraphicsApi candidates = 0;
        foreach (var name in moduleNames)
            candidates |= name.ToLowerInvariant() switch
            {
                "d3d11.dll" => GpuGraphicsApi.D3D11,
                "d3d12.dll" => GpuGraphicsApi.D3D12,
                "vulkan-1.dll" => GpuGraphicsApi.Vulkan,
                "opengl32.dll" => GpuGraphicsApi.OpenGL,
                "d3d9.dll" => GpuGraphicsApi.D3D9,
                _ => 0
            };
        return candidates;
    }

    public static string? StartupProvider(GpuGraphicsApi? api) => api switch
    {
        GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)
            => GpuPlacementProviderIds.D3dDeviceCreateShim,
        GpuGraphicsApi.Vulkan => GpuPlacementProviderIds.VulkanExplicitLayer,
        _ => null
    };

    public static string? RuntimeProvider(GpuGraphicsApi? api) => api switch
    {
        GpuGraphicsApi.Vulkan => GpuPlacementProviderIds.VulkanExplicitLayer,
        GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)
            => GpuPlacementProviderIds.D3dDeviceCreateShim,
        _ => null
    };

    public static bool IsIdentified(GpuGraphicsApi? api) => api is
        GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)
        or GpuGraphicsApi.Vulkan or GpuGraphicsApi.OpenGL or GpuGraphicsApi.D3D9;

    public static string DisplayName(GpuGraphicsApi? api)
        => api is null ? "" : api.Value.ToString().Replace(", ", "/", StringComparison.Ordinal);
}
