using System.Text;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

public sealed class NativePortableSoftwareRegistryPayloadCatalogTests
{
    [Fact]
    public void SameNamespaceAndExactTextReuseOneHandle()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 100);

        var first = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.ExecutablePath, "c:/portable/tool.exe");
        var second = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.ExecutablePath, "c:/portable/tool.exe");

        Assert.Equal(100UL, first);
        Assert.Equal(first, second);
        Assert.Equal(1, catalog.Count);
        var payload = catalog.ResolveRequired(NativePortableSoftwarePayloadKind.ExecutablePath, first);
        Assert.Equal("c:/portable/tool.exe", payload.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(payload.Text), payload.Utf8Bytes.ToArray());
    }

    [Fact]
    public void NamespaceAndOrdinalTextAreNeverCollapsed()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);

        var executable = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.ExecutablePath, "c:/portable/tool.exe");
        var root = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.RootPath, "c:/portable/tool.exe");
        var caseDrift = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.ExecutablePath, "C:/portable/tool.exe");

        Assert.NotEqual(executable, root);
        Assert.NotEqual(executable, caseDrift);
        Assert.False(catalog.TryResolve(NativePortableSoftwarePayloadKind.RootPath, executable, out _));
        Assert.Throws<KeyNotFoundException>(() =>
            catalog.ResolveRequired(NativePortableSoftwarePayloadKind.RootPath, executable));
    }

    [Fact]
    public void InvalidPayloadsAndKindsFailClosedWithoutAllocatingHandles()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);

        Assert.Throws<ArgumentException>(() =>
            catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, string.Empty));
        Assert.Throws<ArgumentException>(() =>
            catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, "bad\0id"));
        Assert.Throws<ArgumentException>(() =>
            catalog.GetOrAdd(NativePortableSoftwarePayloadKind.DisplayName, "\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.GetOrAdd((NativePortableSoftwarePayloadKind)0, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.TryResolve((NativePortableSoftwarePayloadKind)7, 1, out _));
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void HandleExhaustionIsVisibleAndExistingPayloadStillResolves()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 0));
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: ulong.MaxValue);

        var last = catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:last");

        Assert.Equal(ulong.MaxValue, last);
        Assert.True(catalog.IsExhausted);
        Assert.Equal(last, catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:last"));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:overflow"));
        Assert.Equal("catalog:last", catalog.ResolveRequired(NativePortableSoftwarePayloadKind.SoftwareId, last).Text);
    }

    [Fact]
    public void ConcurrentRegistrationPublishesExactlyOneHandle()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var handles = new ulong[64];

        Parallel.For(0, handles.Length, index =>
        {
            handles[index] = catalog.GetOrAdd(
                NativePortableSoftwarePayloadKind.CatalogEntryId,
                "catalog-entry");
        });

        Assert.All(handles, handle => Assert.Equal(1UL, handle));
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void BatchReusesDuplicatesAndPublishesInRequestOrder()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 10);

        var handles = catalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool"),
            new(NativePortableSoftwarePayloadKind.ExecutablePath, "c:/portable/tool.exe"),
            new(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool")
        ]);

        Assert.Equal([10UL, 11UL, 10UL], handles);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void BatchHandleExhaustionDoesNotPublishAPartialBatch()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: ulong.MaxValue);

        Assert.Throws<InvalidOperationException>(() => catalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool"),
            new(NativePortableSoftwarePayloadKind.DisplayName, "Tool")
        ]));

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.IsExhausted);
        Assert.Equal(
            ulong.MaxValue,
            catalog.GetOrAdd(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool"));
    }

    [Fact]
    public void InvalidBatchDoesNotPublishEarlierValidPayloads()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);

        Assert.Throws<ArgumentException>(() => catalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool"),
            new(NativePortableSoftwarePayloadKind.DisplayName, "bad\0name")
        ]));

        Assert.Equal(0, catalog.Count);
    }
}
