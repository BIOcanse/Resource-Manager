using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed class StateRegistryTests
{
    [Theory]
    [InlineData("backend")]
    [InlineData("ui")]
    public void GeneratedVersionAndConstantsMatchRepositoryBytes(string process)
    {
        var bytes = File.ReadAllBytes(RegistryPath());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        var version = Convert.ToHexStringLower(hash.GetHashAndReset());
        Assert.Equal(version, process == "backend" ? ResourceManager.App.StateItems.StateRegistry.Version
            : ResourceManager.NativeUi.StateItems.StateRegistry.Version);
        using var document = JsonDocument.Parse(bytes);
        var blockType = process == "backend" ? typeof(BackendUids) : typeof(UiUids);
        var itemType = process == "backend" ? typeof(ResourceManager.App.StateItems.StateItemUids)
            : typeof(ResourceManager.NativeUi.StateItems.StateItemUids);
        foreach (var kind in new[] { "block", "item" })
        {
            var registered = document.RootElement.GetProperty("entries").EnumerateArray()
                .Where(entry => entry.GetProperty("process").GetString() == process && entry.GetProperty("kind").GetString() == kind)
                .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry.GetProperty("uid").GetInt32());
            var generated = (kind == "block" ? blockType : itemType).GetFields(BindingFlags.Public | BindingFlags.Static)
                .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);
            Assert.Equal(registered.OrderBy(pair => pair.Key), generated.OrderBy(pair => pair.Key));
        }
    }

    [Fact]
    public void GeneratedNamesFollowTheBuildSwitchInBothProcesses()
    {
#if RM_STATE_NAMES
        Assert.Equal("Program", ResourceManager.App.StateItems.StateNames.Name(BackendUids.Program));
        Assert.Equal("Program", ResourceManager.NativeUi.StateItems.StateNames.Name(UiUids.Program));
        Assert.Null(ResourceManager.App.StateItems.StateNames.Name(0));
        Assert.Null(ResourceManager.NativeUi.StateItems.StateNames.Comment(-1));
#else
        Assert.Null(typeof(BackendUids).Assembly.GetType("ResourceManager.App.StateItems.StateNames"));
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.StateItems.StateNames"));
#endif
    }

    internal static void AssertBlocks(string process, IEnumerable<int> productionIds, params int[] inactive)
    {
        var generated = (process == "backend" ? typeof(BackendUids) : typeof(UiUids))
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (int)field.GetRawConstantValue()!).ToArray();
        var production = productionIds.ToArray();
        Assert.Equal(generated.Length, generated.Distinct().Count());
        Assert.Equal(production.Length, production.Distinct().Count());
        Assert.Equal(generated.Except(inactive).Order(), production.Order());
    }

    private static string RegistryPath([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../src/Shared/StateItems/state-items.json"));
}
