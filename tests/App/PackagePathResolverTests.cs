using ResourceManager.App.Infrastructure.Paths;

namespace Resource_Manager_APP.Tests;

public sealed class PackagePathResolverTests
{
    [Fact]
    public void ExplicitPackageRootOverridesContentRootInference()
    {
        var contentRoot = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "resource-manager-package-path-tests",
            "Resource Manager-APP"));
        var overrideRoot = Path.Combine(
            Path.GetTempPath(),
            "resource-manager-package-path-override");

        var resolved = PackagePathResolver.ResolvePackageRoot(
            contentRoot,
            overrideRoot);

        Assert.Equal(Path.GetFullPath(overrideRoot), resolved);
    }

    [Fact]
    public void EmptyOverridePreservesPackageLayoutInference()
    {
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            "resource-manager-package-path-tests");
        var contentRoot = new DirectoryInfo(Path.Combine(
            packageRoot,
            "Resource Manager-APP"));

        var resolved = PackagePathResolver.ResolvePackageRoot(
            contentRoot,
            "   ");

        Assert.Equal(Path.GetFullPath(packageRoot), resolved);
    }
}
