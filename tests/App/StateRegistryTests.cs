using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class StateRegistryTests
{
    [Theory]
    [InlineData("backend")]
    [InlineData("desktop")]
    [InlineData("window")]
    public void GeneratedVersionAndConstantsMatchRepositoryBytes(string process)
    {
        var bytes = File.ReadAllBytes(RegistryPath());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        var version = Convert.ToHexStringLower(hash.GetHashAndReset());
        Assert.Equal(version, process switch {
            "backend" => ResourceManager.App.StateItems.StateRegistry.Version,
            "desktop" => ResourceManager.NativeUi.DesktopStateItems.StateRegistry.Version,
            _ => ResourceManager.NativeUi.StateItems.StateRegistry.Version });
        using var document = JsonDocument.Parse(bytes);
        var blockType = process switch {
            "backend" => typeof(BackendUids),
            "desktop" => typeof(ResourceManager.NativeUi.DesktopStateItems.BlockUids),
            _ => typeof(ResourceManager.NativeUi.StateItems.BlockUids) };
        var itemType = process switch {
            "backend" => typeof(ResourceManager.App.StateItems.StateItemUids),
            "desktop" => typeof(ResourceManager.NativeUi.DesktopStateItems.StateItemUids),
            _ => typeof(ResourceManager.NativeUi.StateItems.StateItemUids) };
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
        Assert.Equal("Program", ResourceManager.NativeUi.LegacyStateItems.StateNames.Name(UiUids.Program));
        Assert.Equal("Program", ResourceManager.NativeUi.StateItems.StateNames.Name(ResourceManager.NativeUi.StateItems.BlockUids.Program));
        Assert.Null(ResourceManager.App.StateItems.StateNames.Name(0));
        Assert.Null(ResourceManager.NativeUi.StateItems.StateNames.Comment(-1));
#else
        Assert.Null(typeof(BackendUids).Assembly.GetType("ResourceManager.App.StateItems.StateNames"));
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.StateItems.StateNames"));
#endif
    }

    [Fact]
    public void LegacyBridgePreservesTheOriginalTreeAndCatalogHasNoUiEntries()
    {
        Assert.Equal(new[] { 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50 },
            ResourceManager.NativeUi.LegacyStateItems.StateRegistry.Blocks.Order());
        Assert.Equal(new[] { 58, 59, 60, 61 },
            ResourceManager.NativeUi.LegacyStateItems.StateRegistry.Items.Select(item => item.Uid).Order());
        using var document = JsonDocument.Parse(File.ReadAllBytes(RegistryPath()));
        Assert.DoesNotContain(document.RootElement.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("process").GetString() == "ui");
        Assert.Equal(new[] { ResourceManager.NativeUi.StateItems.BlockUids.MainWindow,
            ResourceManager.NativeUi.StateItems.BlockUids.LoopbackProxy,
            ResourceManager.NativeUi.StateItems.BlockUids.WebViewHost,
            ResourceManager.NativeUi.StateItems.BlockUids.Program }.Order(),
            ResourceManager.NativeUi.StateItems.StateRegistry.Blocks.Order());
    }

    internal static void AssertBlocks(string process, IEnumerable<int> productionIds, params int[] inactive)
    {
        var blockType = process switch {
            "backend" => typeof(BackendUids),
            "desktop" => typeof(ResourceManager.NativeUi.DesktopStateItems.BlockUids),
            "window" => typeof(ResourceManager.NativeUi.StateItems.BlockUids),
            "ui" => typeof(UiUids),
            _ => throw new ArgumentOutOfRangeException(nameof(process)) };
        var generated = blockType
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
