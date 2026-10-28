using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

public sealed partial class SqlitePersistenceTests
{
    [Fact]
    public async Task PortableRegistryRetiresRemovedPathPayloadsAfterPersistenceFeedback()
    {
        var database = CreateDatabase();
        using var registry = CreatePortableRegistry(database);
        await registry.StartAsync(default);
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var root = Path.Combine(testRoot, $"absent-{i}");
                await registry.ObserveAsync(new PortableSoftwareObservation($"catalog:tool-{i}", $"tool-{i}",
                    $"Tool {i}", SoftwareKinds.Other, Path.Combine(root, "tool.exe"), root, true, false), default);
            }
            Assert.True(registry.RetainedPayloadCount > 32);
            await registry.RefreshAsync(default); // Every nonexistent file is deleted and feedback settles.
            Assert.Empty(registry.GetSnapshot());
            Assert.Equal(0, registry.RetainedPayloadCount);
        }
        finally { await registry.StopAsync(default); }
    }
}
