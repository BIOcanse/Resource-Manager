using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeLibraryResolverTests
{
    [Fact]
    public void NativeCoreResolverUsesThePublishedBaseDirectoryImage()
    {
        var expected = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "ResourceManager.NativeCore.dll"));

        Assert.Equal(expected, NativeCoreLibraryResolver.GetCanonicalPath());
        Assert.Equal("ResourceManager.NativeCore", NativeCoreLibraryResolver.LibraryName);
    }
}
