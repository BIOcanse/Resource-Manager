using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlAccessLevelTests
{
    [Fact]
    public void OnlyNormalAndRootAreAvailable()
    {
        Assert.Equal(new[] { "normal", "root" }, ControlAccessLevels.All);
        Assert.True(ControlAccessLevels.Allows("normal", "normal"));
        Assert.False(ControlAccessLevels.Allows("normal", "root"));
        Assert.True(ControlAccessLevels.Allows("root", "normal"));
    }

    [Theory]
    [InlineData(null, "normal")]
    [InlineData("safe", "normal")]
    [InlineData("normal", "normal")]
    [InlineData("root", "root")]
    public async Task SavedLevelNormalizesAndRoundTrips(string? saved, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-control-level-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "UserData", "Control");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "access-level.txt");
        try
        {
            if (saved is not null) File.WriteAllText(path, saved);
            var environment = new Environment(root);
            var level = new JsonControlAccessLevel(environment);
            Assert.Equal(expected, level.Current);
            await level.SetAsync("root", default);
            Assert.Equal("root", new JsonControlAccessLevel(environment).Current);
            await level.SetAsync("normal", default);
            Assert.Equal("normal", new JsonControlAccessLevel(environment).Current);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
            Directory.Delete(Path.Combine(root, "UserData"));
            Directory.Delete(root);
        }
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ControlTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
