using System.Reflection;
using System.Runtime.CompilerServices;

namespace Resource_Manager_APP.Tests;

internal static class NativeUiTestExecutableLocator
{
    private const string ArtifactsPathMetadataKey = "ResourceManagerTestArtifactsPath";

    // Whether this runner can create the interface process at all (its manifest requires an elevated administrator).
    internal static bool RunnerIsElevatedAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    internal static string GetPath([CallerFilePath] string testSourcePath = "")
    {
        var assembly = typeof(NativeUiTestExecutableLocator).Assembly;
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The test assembly has no build configuration metadata.");
        var artifactsPath = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == ArtifactsPathMetadataKey)?.Value;

        if (!string.IsNullOrWhiteSpace(artifactsPath))
        {
            return Path.Combine(Path.GetFullPath(artifactsPath), "bin", "ResourceManager.NativeUi",
                configuration.ToLowerInvariant(), "ResourceManager.NativeUi.exe");
        }

        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSourcePath)!, "..", ".."));
        return Path.Combine(repositoryRoot, "src", "UI", "Core", "bin", configuration, "net10.0-windows",
            "ResourceManager.NativeUi.exe");
    }
}
