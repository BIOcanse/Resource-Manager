using System.Management;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>Product package/runtime paths, topology acquisition and resolved reader policy.</summary>
internal static class AmdSmuHardwareBinding
{
    private const string ComponentId = "amd-smu-pawnio-provider";
    private const string ModuleFileName = "RyzenSMU.bin";
    internal static AmdSmuCpuSensorReader CreateReader(string contentRoot)
        => new(() => AmdSmuPawnIoSession.Open(CreateOpenOptions(contentRoot)), ReadPhysicalCoreCount(),
            TimeSpan.FromSeconds(10), TimeProvider.System);
    internal static AmdSmuPawnIoOpenOptions CreateOpenOptions(string contentRoot)
        => new(LocateModulePath(contentRoot) ?? Path.Combine(PackagePathResolver.ResolvePackageRoot(contentRoot),
                "Dependencies", ComponentId, ModuleFileName), CandidatePawnIoLibraryPaths().ToArray(), true, TimeSpan.FromSeconds(2));

    private static string? LocateModulePath(string contentRootPath)
    {
        return CandidateModulePaths(contentRootPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> CandidateModulePaths(string contentRootPath)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(contentRootPath);
        yield return Path.Combine(packageRoot, "Dependencies", ComponentId, ModuleFileName);
        yield return Path.Combine(PackagePathResolver.ResolvePackageRoot(AppContext.BaseDirectory), "Dependencies", ComponentId, ModuleFileName);
    }

    private static IEnumerable<string> CandidatePawnIoLibraryPaths()
    {
        var runtime = PawnIoRuntimeProbe.Probe();
        if (!string.IsNullOrWhiteSpace(runtime.RuntimePath))
        {
            if (Directory.Exists(runtime.RuntimePath))
            {
                yield return Path.Combine(runtime.RuntimePath, "PawnIOLib.dll");
            }
            else if (File.Exists(runtime.RuntimePath))
            {
                yield return runtime.RuntimePath;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "PawnIO", "PawnIOLib.dll");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "PawnIO", "PawnIOLib.dll");
        }
    }

    internal static int ReadPhysicalCoreCount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select NumberOfCores from Win32_Processor");
            var total = 0;
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                using var ownedItem = item;
                if (item["NumberOfCores"] is uint cores)
                {
                    total += (int)cores;
                }
            }

            if (total > 0)
            {
                return total;
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Math.Max(1, Environment.ProcessorCount / 2);
    }
}
