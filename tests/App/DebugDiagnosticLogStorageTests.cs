using ResourceManager.App.Infrastructure.Diagnostics;

namespace Resource_Manager_APP.Tests;

public sealed class DebugDiagnosticLogStorageTests
{
    [Fact]
    public void ReadTail_ReturnsOnlyNewestRequestedLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resource-manager-debug-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, ["one", "two", "three", "four"]);

            Assert.Equal(["two", "three", "four"], DebugDiagnosticLogStorage.ReadTail(path, 3));
            Assert.Equal(["four"], DebugDiagnosticLogStorage.ReadTail(path, 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTail_ReturnsEmptyWhenLogDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resource-manager-debug-{Guid.NewGuid():N}.jsonl");

        Assert.Empty(DebugDiagnosticLogStorage.ReadTail(path, 100));
    }
}
