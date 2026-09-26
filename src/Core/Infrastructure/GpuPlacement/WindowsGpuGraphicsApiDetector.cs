using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class WindowsGpuGraphicsApiDetector : IGpuGraphicsApiDetector
{
    private readonly Func<int, string, IReadOnlyList<string>?> readModules;

    public WindowsGpuGraphicsApiDetector() : this(ReadModules) { }

    internal WindowsGpuGraphicsApiDetector(Func<int, string, IReadOnlyList<string>?> readModules)
        => this.readModules = readModules;

    public GpuGraphicsApi? Detect(int processId, string executablePath)
    {
        if (processId <= 4 || string.IsNullOrWhiteSpace(executablePath)) return null;
        var modules = readModules(processId, executablePath);
        if (modules is null) return null;
        var names = modules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Our shim imports both D3D runtimes; those imports are not application detection.
        if (names.Contains(WindowsGpuPlacementInjector.RuntimeProviderFileName)
            || names.Contains(GpuStartupProviderArtifacts.VulkanLayerFileName)) return null;

        var detected = CollectCandidates(names);
        return GpuGraphicsApiRoutes.IsIdentified(detected) ? detected : null;
    }

    internal static GpuGraphicsApi CollectCandidates(IEnumerable<string> moduleNames)
    {
        GpuGraphicsApi candidates = 0;
        foreach (var name in moduleNames)
        {
            candidates |= name.ToLowerInvariant() switch
            {
                "d3d11.dll" => GpuGraphicsApi.D3D11,
                "d3d12.dll" => GpuGraphicsApi.D3D12,
                "vulkan-1.dll" => GpuGraphicsApi.Vulkan,
                "opengl32.dll" => GpuGraphicsApi.OpenGL,
                "d3d9.dll" => GpuGraphicsApi.D3D9,
                _ => 0
            };
        }
        return candidates;
    }

    private static IReadOnlyList<string>? ReadModules(int processId, string executablePath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) || !Path.GetFullPath(path).Equals(
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)) return null;
            return process.Modules.Cast<ProcessModule>().Select(static module => module.ModuleName).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or Win32Exception or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
