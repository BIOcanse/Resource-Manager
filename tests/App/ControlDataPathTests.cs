using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlDataPathTests
{
    [Fact]
    public void ReadOnlyPathResolutionUsesLegacyWithoutCopyingOrCreatingDestination()
    {
        var package = Path.Combine(Path.GetTempPath(), "rm-readonly-control-path-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(package, "Bin", "ResourceManager");
        var oldDirectory = Path.Combine(content, "UserData", "Control");
        Directory.CreateDirectory(oldDirectory);
        var legacy = Path.Combine(oldDirectory, "access-level.txt");
        try
        {
            File.WriteAllText(legacy, "root");
            Assert.Equal(legacy, ControlDataPaths.FilePath(content, "access-level.txt",
                ResourceManager.App.Hosting.StartupCapabilities.StartupCapabilitySet.NormalReadOnly));
            Assert.False(Directory.Exists(Path.Combine(package, "UserData")));
        }
        finally
        {
            File.Delete(legacy);
            foreach (var directory in new[] { oldDirectory, Path.Combine(content, "UserData"), content, Path.Combine(package, "Bin"), package }) Directory.Delete(directory);
        }
    }

    [Fact]
    public void InstalledContentRootMigratesLegacyControlFileAndKeepsNewerRootFile()
    {
        var package = Path.Combine(Path.GetTempPath(), "rm-control-path-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(package, "Bin", "ResourceManager");
        var oldDirectory = Path.Combine(content, "UserData", "Control");
        Directory.CreateDirectory(oldDirectory);
        try
        {
            var old = Path.Combine(oldDirectory, "access-level.txt");
            File.WriteAllText(old, "root");
            var expected = Path.Combine(package, "UserData", "Control", "access-level.txt");
            Assert.Equal(expected, ControlDataPaths.FilePath(content, "access-level.txt"));
            Assert.Equal("root", File.ReadAllText(expected));
            File.WriteAllText(expected, "normal");
            Assert.Equal(expected, ControlDataPaths.FilePath(content, "access-level.txt"));
            Assert.Equal("normal", File.ReadAllText(expected));
            Assert.Equal("root", File.ReadAllText(old));
        }
        finally
        {
            var files = new[] { Path.Combine(oldDirectory, "access-level.txt"), Path.Combine(package, "UserData", "Control", "access-level.txt") };
            foreach (var file in files) if (File.Exists(file)) File.Delete(file);
            foreach (var directory in new[] { oldDirectory, Path.Combine(content, "UserData"), content, Path.Combine(package, "Bin"), Path.Combine(package, "UserData", "Control"), Path.Combine(package, "UserData"), package })
                Directory.Delete(directory);
        }
    }
}
