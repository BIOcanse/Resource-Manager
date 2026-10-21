using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlNumberBaselineStoreTests
{
    [Fact]
    public void OriginalBaselineSurvivesLaterReadingsAndWriterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rm-baseline-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "baseline.txt");
        try
        {
            var store = new ControlNumberBaselineStore(path);
            Assert.Null(store.Capture("gpu:A", null));
            Assert.Null(store.Capture("gpu:A", double.NaN));
            Assert.False(File.Exists(path));
            Assert.Equal(8, store.Capture("gpu:A", 8));
            Assert.Equal(8, store.Capture("gpu:A", -20));
            Assert.Equal(8, new ControlNumberBaselineStore(path).Capture("gpu:A", -20));
            Assert.Equal(8, new ControlNumberBaselineStore(path).Capture("gpu:A", null));
            Assert.Null(store.Capture("gpu:B", null));
            Assert.Equal(-4, store.Capture("gpu:B", -4));
            var restarted = new ControlNumberBaselineStore(path);
            Assert.Equal(-4, restarted.Capture("gpu:B", -20));
            Assert.Equal(8, restarted.Capture("gpu:A", null));
            File.WriteAllText(path, "NaN");
            Assert.Null(new ControlNumberBaselineStore(path).Capture("gpu:A", -20));
            File.WriteAllText(path, "8"); // Previous schema cannot prove which target owned the scalar.
            Assert.Null(new ControlNumberBaselineStore(path).Capture("gpu:B", -4));
            Assert.Equal("8", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
