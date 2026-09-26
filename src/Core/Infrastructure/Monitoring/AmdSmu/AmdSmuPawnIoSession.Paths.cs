using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession
{
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
}
