using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.GpuPlacement;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<GpuGraphicsApi>))]
public enum GpuGraphicsApi
{
    D3D11 = 1,
    D3D12 = 2,
    Vulkan = 4,
    OpenGL = 8,
    D3D9 = 16
}
