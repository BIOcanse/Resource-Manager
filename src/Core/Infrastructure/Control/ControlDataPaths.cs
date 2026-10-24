using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Infrastructure.Control;

internal static class ControlDataPaths
{
    internal static string FilePath(string contentRoot, string name, StartupCapabilitySet? capabilities = null)
    {
        var destination = Path.Combine(PackagePathResolver.ResolvePackageRoot(contentRoot), "UserData", "Control", name);
        var legacy = Path.Combine(contentRoot, "UserData", "Control", name);
        if (capabilities is not null && !capabilities.Allows(StartupCapability.MutablePersistence))
            return File.Exists(destination) ? destination : legacy;
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
