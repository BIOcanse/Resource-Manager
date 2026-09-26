using ResourceManager.Adapter;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

public enum AdapterResourceSchedulingDimension : byte
{
    Cpu = 0,
    Gpu = 1
}

public static class AdapterResourceSchedulingDimensionResolver
{
    public static AdapterResourceSchedulingDimension Resolve(TieredResourceEntry resource)
    {
        if (resource.Tier == AdapterResourceTier.Vram)
        {
            return AdapterResourceSchedulingDimension.Gpu;
        }

        return resource.Tier == AdapterResourceTier.PhysicalMemory
            && resource.ResourceKind is AdapterResourceKind.Texture
                or AdapterResourceKind.RenderBuffer
                or AdapterResourceKind.RenderSurface
            ? AdapterResourceSchedulingDimension.Gpu
            : AdapterResourceSchedulingDimension.Cpu;
    }
}
