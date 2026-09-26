using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DiskUsage;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// 磁盘占用的扫描结果要作为一条独立资源进内存账本。
///
/// 它是一份缓存：整块盘扫完是几十上百 MB，一直留到下次扫描，
/// 而工作集那两条只能 trim —— trim 换不回活着的托管内存。
/// 所以它必须有自己的条目和自己的 discard。
/// </summary>
[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class DiskUsageSnapshotLedgerEntryTests
{
    [Fact]
    public void TreeReportsWhatItActuallyOccupiesIncludingNames()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", isDirectory: true, 0, 0);
        _ = builder.Add(root, "a-file-with-a-long-name.bin", isDirectory: false, 1024, 1024);
        var tree = builder.Build();

        // 名字缓冲往往比索引列还大，漏掉它会少算将近一半，账本就会低估。
        var indexOnly = (long)tree.Count * 41;
        Assert.True(
            tree.ApproximateByteSize > indexOnly,
            $"名字那部分必须算进去：{tree.ApproximateByteSize} 应当大于 {indexOnly}。");
    }

    [Fact]
    public async Task AScannedTreeTakesALedgerSlotAndAnEmptyStoreDoesNot()
    {
        var store = new DiskUsageTreeStore();
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(),
            store);

        var withoutScan = await manager.TickAsync();
        store.Replace(CreateScanResult());
        var withScan = await manager.TickAsync();

        Assert.True(
            withScan.RegisteredResourceCount > withoutScan.RegisteredResourceCount,
            "扫描结果必须多占一条账本条目，不能混进工作集里。");
        Assert.Equal(withScan.RegisteredResourceCount, withScan.Capacity.OccupiedCount);
    }

    [Fact]
    public async Task DiscardingTheSnapshotFreesTheTreeAndTheSlot()
    {
        var store = new DiskUsageTreeStore();
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(),
            store);
        store.Replace(CreateScanResult());
        var withScan = await manager.TickAsync();

        // 账本真的执行回收时走的就是这个处理器：整棵树丢掉，槽位跟着腾出来。
        await InvokeDiscardAsync(manager);

        Assert.Null(store.Current);
        var afterDiscard = await manager.TickAsync();
        Assert.True(
            afterDiscard.RegisteredResourceCount < withScan.RegisteredResourceCount,
            "树没了，这条资源也不该继续占着槽位。");
    }

    private static async Task InvokeDiscardAsync(ResourceManagerSelfLocalResourceManager manager)
    {
        var type = typeof(ResourceManagerSelfLocalResourceManager);
        var resourceId = (LocalResourceId)type
            .GetField(
                "DiskUsageSnapshotId",
                System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        var method = type.GetMethod(
            "DiscardDiskUsageSnapshotAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var context = CreateExecutionContext(resourceId);
        var effect = (ValueTask<LocalResourceEffect>)method.Invoke(
            manager,
            [context, CancellationToken.None])!;
        var result = await effect;
        Assert.Equal(LocalResourceEffectOutcome.Applied, result.Outcome);
        Assert.True(result.ReleasedBytes > 0, "回收必须如实报出释放了多少字节。");
    }

    private static LocalResourceExecutionContext CreateExecutionContext(
        LocalResourceId resourceId)
        => new(
            TableId: default,
            TableIncarnation: 1,
            ResourceUid: resourceId,
            CapabilityId: 2,
            CapabilityGeneration: 1,
            ActionCode: 2,
            ExpectedEffects: LocalResourceEffects.ReleasesLedgerSlot,
            Reason: LocalResourceIntentReason.Capacity,
            SizeBytes: 0);

    private static DiskUsageScanResult CreateScanResult()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", isDirectory: true, 0, 0);
        _ = builder.Add(root, "payload.bin", isDirectory: false, 4096, 4096);
        var tree = builder.Build();
        return new DiskUsageScanResult(tree, new DiskUsageScanSummary(
            DiskUsageScanScopes.Volume,
            DiskUsageScanModes.Fast,
            "C:",
            ["C:\\"],
            DiskUsageScanKinds.MasterFileTable,
            [],
            DateTimeOffset.UtcNow,
            0.1,
            4096,
            4096,
            1,
            1,
            0));
    }

}
