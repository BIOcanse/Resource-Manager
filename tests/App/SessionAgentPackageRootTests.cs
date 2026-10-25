using ResourceManager.Shared.Runtime.Sessions;

namespace Resource_Manager_APP.Tests;

public sealed class SessionAgentPackageRootTests
{
    [Theory]
    [InlineData(@"D:\Product\Bin\ResourceManagerSessionAgent", @"D:\Product")]
    [InlineData(@"D:\Product\Current\ResourceManagerSessionAgent", @"D:\Product")]
    [InlineData(@"D:\FinalImage\ResourceManagerSessionAgent", @"D:\FinalImage")]
    [InlineData(@"D:\Validation\app-test\ResourceManagerSessionAgent", @"D:\Validation\app-test")]
    public void InstalledLegacyAndStandaloneLayoutsSelectTheMatchingGuardRoot(string directory, string root)
        => Assert.Equal(root, SessionAgentPackageRoot.Resolve(directory, explicitRoot: null));

    [Fact]
    public void ExplicitDevelopmentRootIsUsedWithoutDependingOnUserEnvironmentInheritance()
        => Assert.Equal(@"D:\Development\Runtime", SessionAgentPackageRoot.Resolve(
            @"D:\Source\SessionAgent\bin\Debug\net10.0-windows", @"D:\Development\Runtime"));
}
