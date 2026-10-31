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
            _ => ResourceManager.NativeUi.StateItems.StateRegistry.Version });
        using var document = JsonDocument.Parse(bytes);
        var blockType = process switch {
            "backend" => typeof(BackendUids),
            _ => typeof(ResourceManager.NativeUi.StateItems.BlockUids) };
        var itemType = process switch {
            "backend" => typeof(ResourceManager.App.StateItems.StateItemUids),
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
        Assert.Equal("Program", ResourceManager.NativeUi.StateItems.StateNames.Name(ResourceManager.NativeUi.StateItems.BlockUids.Program));
        Assert.Null(ResourceManager.App.StateItems.StateNames.Name(0));
        Assert.Null(ResourceManager.NativeUi.StateItems.StateNames.Comment(-1));
#else
        Assert.Null(typeof(BackendUids).Assembly.GetType("ResourceManager.App.StateItems.StateNames"));
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.StateItems.StateNames"));
#endif
    }

    [Fact]
    public void WindowProcessGeneratesOnlyItsOwnTreeAndCatalogHasNoUiEntries()
    {
        // The window process carries only the window tree; the desktop entries are the resident's (Zig) table.
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.DesktopStateItems.StateRegistry"));
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.LegacyStateItems.StateRegistry"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(RegistryPath()));
        Assert.DoesNotContain(document.RootElement.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("process").GetString() == "ui");
        Assert.Equal(new[] { ResourceManager.NativeUi.StateItems.BlockUids.MainWindow,
            ResourceManager.NativeUi.StateItems.BlockUids.LoopbackProxy,
            ResourceManager.NativeUi.StateItems.BlockUids.WebViewHost,
            ResourceManager.NativeUi.StateItems.BlockUids.Program }.Order(),
            ResourceManager.NativeUi.StateItems.StateRegistry.Blocks.Order());
    }

    [Fact]
    public void BackendRulesDeclareHostAdmissionAndOnlyAuthoritativeBusinessInputs()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RegistryPath()));
        var backend = document.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("process").GetString() == "backend").ToArray();
        var byUid = backend.ToDictionary(entry => entry.GetProperty("uid").GetInt32());
        Assert.DoesNotContain(backend, entry => entry.GetProperty("name").GetString() is "BlockDemand" or "HasExternalDemand");
        var removedResources = new[] { "KernelEtwSessionBroker", "HostManagerMetricSnapshotOwner",
            "HostManagerSoftwareIdentityOwner", "HostManagerFileQueryOwner", "HostManagerDisplayCoordinatorOwner" };
        Assert.DoesNotContain(backend.Where(entry => entry.GetProperty("kind").GetString() == "block"),
            entry => removedResources.Contains(entry.GetProperty("name").GetString()));
        var permit = byUid[BackendItems.HostStartPermitted];
        var fanCore = backend.Single(entry => entry.GetProperty("name").GetString() == "FanCoreShouldRun");
        Assert.Equal(BackendExpectedProductionTree.FanCoreFacts.Order(), fanCore.GetProperty("items").EnumerateArray()
            .Select(read => read.GetProperty("item").GetInt32()).Order());
        Assert.True(permit.GetProperty("perBlock").GetBoolean());
        Assert.Equal("BackendHostPermitFacts", permit.GetProperty("owner").GetProperty("component").GetString());
        foreach (var condition in backend.Where(entry => entry.GetProperty("kind").GetString() == "condition"))
        {
            var perBlock = condition.GetProperty("perBlock").GetBoolean();
            var reads = condition.GetProperty("items").EnumerateArray().ToArray();
            if (condition.GetProperty("uid").GetInt32() == ResourceManager.App.StateItems.ConditionUids.KernelLeakObservationShouldRun)
            {
                Assert.False(perBlock);
                Assert.Equal(BackendItems.KernelLeakObservationEnabled, Assert.Single(reads).GetProperty("item").GetInt32());
            }
            else if (!perBlock && condition.GetProperty("decides").GetInt32() == BackendUids.FanCurveTemperatureFeed)
                Assert.Equal(new[] { BackendItems.SoftwareCurveRunNeeded, BackendItems.HostedRunEnded }, reads.Select(read => read.GetProperty("item").GetInt32()));
            else
                Assert.Contains(reads, read => read.GetProperty("item").GetInt32() == BackendItems.HostStartPermitted
                    && (perBlock ? !read.TryGetProperty("block", out _)
                        : read.GetProperty("block").GetInt32() == condition.GetProperty("decides").GetInt32()));
            foreach (var read in reads)
            {
                var item = byUid[read.GetProperty("item").GetInt32()];
                Assert.Equal("bool", item.GetProperty("type").GetString());
                Assert.Equal("reference", item.GetProperty("storage").GetString());
                Assert.True(item.GetProperty("owner").TryGetProperty("component", out _));
                if (item.GetProperty("uid").GetInt32() == BackendItems.PortableAttributionCatalogAvailable)
                {
                    Assert.Equal("poll", item.GetProperty("trigger").GetString());
                    Assert.Equal("RuntimeProcessAttributionCatalogAvailability:RecoveryCheckIntervalSeconds",
                        item.GetProperty("pollInterval").GetString());
                    Assert.Equal("RuntimeProcessAttributionCatalogProvider", item.GetProperty("owner").GetProperty("component").GetString());
                }
                else
                {
                    Assert.Equal("event", item.GetProperty("trigger").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("changeEvent").GetString()));
                }
            }
        }
    }

    internal static void AssertBlocks(string process, IEnumerable<int> productionIds, params int[] inactive)
    {
        var blockType = process switch {
            "backend" => typeof(BackendUids),
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

    // Capture this helper file, so callers in nested test folders resolve the same repository input.
    private static string SourceFile([CallerFilePath] string source = "") => source;
    internal static string RegistryPath()
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../../src/Shared/StateItems/state-items.json"));
}
