using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuGraphicsApiDetector
{
    GpuGraphicsApi? Detect(int processId, string executablePath);
}
