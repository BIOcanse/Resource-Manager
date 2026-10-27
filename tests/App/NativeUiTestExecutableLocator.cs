using System.Reflection;
using System.Runtime.CompilerServices;

namespace Resource_Manager_APP.Tests;

internal static class NativeUiTestExecutableLocator
{
    private const string ArtifactsPathMetadataKey = "ResourceManagerTestArtifactsPath";

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
