using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control;

internal static class ControlDataPaths
{
    internal static string FilePath(string contentRoot, string name)
    {
        var destination = Path.Combine(PackagePathResolver.ResolvePackageRoot(contentRoot), "UserData", "Control", name);
        var legacy = Path.Combine(contentRoot, "UserData", "Control", name);
        if (!string.Equals(destination, legacy, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(destination) && File.Exists(legacy))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try { File.Copy(legacy, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        return destination;
    }
}
