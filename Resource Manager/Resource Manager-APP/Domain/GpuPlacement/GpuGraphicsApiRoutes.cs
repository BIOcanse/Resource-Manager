namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuGraphicsApiRoutes
{
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
