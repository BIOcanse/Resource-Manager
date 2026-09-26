using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessInstanceCacheTests
{
    [Fact]
    public void GetOrAdd_ReusesIdentityOnlyForTheSamePidAndStartKey()
    {
        var cache = new ProcessInstanceCache<string>();
        var reads = 0;
        var firstKey = new ProcessInstanceKey(42, 1000);
        var reusedKey = new ProcessInstanceKey(42, 1000);
        var restartedKey = new ProcessInstanceKey(42, 2000);

        var first = cache.GetOrAdd(firstKey, () => $"identity-{++reads}");
        var reused = cache.GetOrAdd(reusedKey, () => $"identity-{++reads}");
        var restarted = cache.GetOrAdd(restartedKey, () => $"identity-{++reads}");

        Assert.Equal("identity-1", first);
        Assert.Equal(first, reused);
        Assert.Equal("identity-2", restarted);
        Assert.Equal(2, reads);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrAdd_DoesNotCacheMissingIdentity()
    {
        var cache = new ProcessInstanceCache<string>();
        var reads = 0;
        var key = new ProcessInstanceKey(42, 1000);

        var missing = cache.GetOrAdd(key, () =>
        {
            reads++;
            return null;
        });
        var recovered = cache.GetOrAdd(key, () => $"identity-{++reads}");

        Assert.Null(missing);
        Assert.Equal("identity-2", recovered);
        Assert.Equal(2, reads);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Prune_RemovesExitedInstancesWithoutKeepingReusedPidEntries()
    {
        var cache = new ProcessInstanceCache<string>();
        var exited = new ProcessInstanceKey(42, 1000);
        var current = new ProcessInstanceKey(42, 2000);
        cache.Set(exited, "old");
        cache.Set(current, "current");

        cache.Prune(new HashSet<ProcessInstanceKey> { current });

        Assert.False(cache.TryGet(exited, out _));
        Assert.True(cache.TryGet(current, out var value));
        Assert.Equal("current", value);
    }
}
