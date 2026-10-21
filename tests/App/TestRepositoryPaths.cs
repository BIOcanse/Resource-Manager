using System.Runtime.CompilerServices;

namespace Resource_Manager_APP.Tests;

internal static class TestRepositoryPaths
{
    internal static string FindRoot([CallerFilePath] string sourceFilePath = "")
    {
        var explicitRoot = Environment.GetEnvironmentVariable("RM_TEST_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (Path.IsPathFullyQualified(explicitRoot) && IsRepository(explicitRoot))
                return Path.GetFullPath(explicitRoot);

            throw new DirectoryNotFoundException(
                "RM_TEST_REPOSITORY_ROOT must name the real Resource Manager repository.");
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (IsRepository(current.FullName))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Resource Manager repository root was not found from the test source path.");
    }

    private static bool IsRepository(string path)
        => File.Exists(Path.Combine(path, "src", "Core", "ResourceManager.App.csproj"));
}
