using ResourceManager.Adapter.NativeLedger;

namespace ResourceManager.Adapter.SharedMemory.Tests;

public sealed class NativeAdapterResourceLedgerSessionTests
{
    [Fact]
    public void ExplicitDemandAndTouchActivity_ProjectOnlyNativeAcceptedState()
    {
        using var session = CreateSession(4);
        var imported = session.ImportSnapshot(
            CreateSnapshot(1, [
                CreateResource(11, 1, demand: AdapterResourceDemandMask.RequiredNow),
                CreateResource(12, 2)
            ]),
            ownerApplicationKey: 101,
            ownerProcessInstanceKey: 102,
            importActivityState: true,
            importDemandState: true);

        _ = session.TouchResources("test.app", "Test App", [2]);
        var settled = session.SettleActivity(
            "test.app",
            "Test App",
            NativeAdapterResourceLedgerSession.MonotonicNow());

        Assert.NotEqual(0UL, imported.LedgerInstanceId);
        Assert.Equal(imported.LedgerInstanceId, settled.LedgerInstanceId);
        var active = Assert.Single(settled.Snapshot.Resources, static resource => resource.ResourceId == 2);
        Assert.Equal(AdapterResourceDemandMask.None, active.FrontendDemandMask);
        Assert.Equal(48, active.ActivityScore);
        var required = Assert.Single(settled.Snapshot.Resources, static resource => resource.ResourceId == 1);
        Assert.Equal(AdapterResourceDemandMask.RequiredNow, required.FrontendDemandMask);
        Assert.Equal(0, required.ActivityScore);
        Assert.Equal(2, settled.References.Count);
    }

    [Fact]
    public void SessionRecreation_ChangesStableLedgerInstanceIdentity()
    {
        using var first = CreateSession(1);
        using var second = CreateSession(1);
        var firstSnapshot = first.ImportSnapshot(
            CreateSnapshot(1, [CreateResource(11, 1)]),
            401,
            402,
            true,
            true);
        var firstRead = first.ReadSnapshot("test.app", "Test App");
        var secondSnapshot = second.ImportSnapshot(
            CreateSnapshot(1, [CreateResource(11, 1)]),
            401,
            402,
            true,
            true);

        Assert.NotEqual(0UL, firstSnapshot.LedgerInstanceId);
        Assert.Equal(firstSnapshot.LedgerInstanceId, firstRead.LedgerInstanceId);
        Assert.NotEqual(firstSnapshot.LedgerInstanceId, secondSnapshot.LedgerInstanceId);
    }

    [Fact]
    public void ImportSnapshot_RejectsConflictingReplayAndKeepsLastGoodState()
    {
        using var session = CreateSession(2);
        _ = session.ImportSnapshot(
            CreateSnapshot(7, [CreateResource(11, 1)]),
            201,
            202,
            true,
            true);

        var error = Assert.Throws<NativeAdapterResourceLedgerException>(() => session.ImportSnapshot(
            CreateSnapshot(7, [CreateResource(12, 2)]),
            201,
            202,
            true,
            true));

        Assert.Equal(10, error.ResultCode);
        var current = session.ReadSnapshot("test.app", "Test App");
        Assert.Equal(11UL, Assert.Single(current.Snapshot.Resources).ResourceKey);
    }

    [Fact]
    public void ActionFeedback_IsAppliedByNativeLedger()
    {
        using var session = CreateSession(2);
        var imported = session.ImportSnapshot(
            CreateSnapshot(1, [CreateResource(11, 1, AdapterResourceTier.Vram)]),
            301,
            302,
            true,
            true);

        var reference = imported.References[11];
        var result = new AdapterResourceActionResult(
            1,
            11,
            AdapterResourceActionMask.MoveDown,
            AdapterResourceActionStatus.Completed,
            AdapterResourceTier.Vram,
            AdapterResourceTier.PhysicalMemory,
            2048,
            2048);
        var moved = session.ApplyActionFeedback(
            "test.app",
            "Test App",
            reference,
            result,
            AdapterResourceKind.Texture);

        var resource = Assert.Single(moved.Snapshot.Resources);
        Assert.Equal(AdapterResourceTier.PhysicalMemory, resource.Tier);
        Assert.Equal(AdapterResourceKind.StagingBuffer, resource.ResourceKind);
        Assert.Equal(2048UL, resource.SizeBytes);
    }

    private static NativeAdapterResourceLedgerSession CreateSession(int capacity)
        => new(new NativeAdapterResourceLedgerConfiguration(
            Generation: 1,
            Capacity: capacity,
            MaximumSnapshotAge: TimeSpan.FromSeconds(30),
            MaximumFutureClockSkew: TimeSpan.FromSeconds(5),
            ActivitySettlementInterval: TimeSpan.FromSeconds(5),
            ActivityIncrement: 64,
            ActivityDecayNumerator: 3,
            ActivityDecayDenominator: 4));

    private static AdapterResourceSnapshot CreateSnapshot(
        long sequence,
        IReadOnlyList<TieredResourceEntry> resources)
        => new(
            AdapterResourceProtocol.SnapshotSchemaVersion,
            "test.app",
            "Test App",
            Environment.ProcessId,
            AdapterSoftwareSurfaceState.BackgroundWindow,
            sequence,
            DateTimeOffset.UtcNow,
            resources);

    private static TieredResourceEntry CreateResource(
        ulong key,
        uint id,
        AdapterResourceTier tier = AdapterResourceTier.PhysicalMemory,
        AdapterResourceDemandMask demand = AdapterResourceDemandMask.None)
        => new(
            key,
            id,
            4096,
            tier,
            tier == AdapterResourceTier.Vram ? AdapterResourceKind.Texture : AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceActionMask.None,
            AdapterResourceActionRoute.AdapterHandler,
            0,
            demand);
}
