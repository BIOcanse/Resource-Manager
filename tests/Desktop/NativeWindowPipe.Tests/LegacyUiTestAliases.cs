global using ResourceManager.Shared.BusinessBlocks;
global using ResourceManager.Shared.BusinessBlocks.State;
global using UiUids = ResourceManager.NativeUi.LegacyStateItems.BlockUids;
global using UiItems = ResourceManager.NativeUi.LegacyStateItems.StateItemUids;

namespace Resource_Manager_APP.Tests;

// The focused UI suite shares the App assertions without loading the backend test assembly.
public sealed partial class StateRegistryTests
{
    internal static void AssertBlocks(string process, IEnumerable<int> productionIds, params int[] inactive)
    {
        Assert.Equal("ui", process);
        var generated = ResourceManager.NativeUi.LegacyStateItems.StateRegistry.Blocks;
        var production = productionIds.ToArray();
        Assert.Equal(generated.Count, generated.Distinct().Count());
        Assert.Equal(production.Length, production.Distinct().Count());
        Assert.Equal(generated.Except(inactive).Order(), production.Order());
    }
}

// Linked WebView integration tests use this workset-owned fixture instead of
// the backend assembly's general temporary-directory helper.
internal sealed class TrustedAccessTemporaryDirectory : IDisposable
{
    internal TrustedAccessTemporaryDirectory()
    {
        var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
            ?? throw new InvalidOperationException("A registered validation scratch root is required.");
        Root = Path.Combine(scratch, "native-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string Root { get; }
    public void Dispose() { } // The workset's maintenance entry owns bounded cleanup.
}
