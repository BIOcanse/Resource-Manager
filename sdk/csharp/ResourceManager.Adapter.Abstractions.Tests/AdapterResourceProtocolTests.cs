using ResourceManager.Adapter;
using System.Text.Json;

public sealed class AdapterResourceProtocolTests
{
    [Fact]
    public void Schema11_SerializesUnsigned64BitWireFieldsAsCanonicalDecimalStrings()
    {
        var snapshot = new AdapterResourceSnapshot(
            AdapterResourceProtocol.SnapshotSchemaVersion,
            "app",
            "App",
            100,
            AdapterSoftwareSurfaceState.PureBackground,
            long.MaxValue,
            DateTimeOffset.UnixEpoch,
            [
                new TieredResourceEntry(
                    ulong.MaxValue,
                    1,
                    ulong.MaxValue,
                    AdapterResourceTier.PhysicalMemory,
                    AdapterResourceKind.Cache,
                    AdapterResourceRecoveryKind.BuiltData,
                    AdapterResourceGranularity.PartialUsable,
                    AdapterResourceActionMask.None,
                    AdapterResourceActionRoute.AdapterHandler,
                    0,
                    AdapterResourceDemandMask.None)
            ]);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"schemaVersion\":11", json, StringComparison.Ordinal);
        Assert.Contains($"\"sequence\":\"{long.MaxValue}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"resourceKey\":\"{ulong.MaxValue}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"sizeBytes\":\"{ulong.MaxValue}\"", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<AdapterResourceSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(roundTrip);
        Assert.Equal(long.MaxValue, roundTrip.Sequence);
        Assert.Equal(ulong.MaxValue, Assert.Single(roundTrip.Resources).ResourceKey);
    }
    [Fact]
    public void Stable_key_hash_is_deterministic()
    {
        var first = AdapterResourceKey.FromString("physical-memory.word-list-cache.default");
        var second = AdapterResourceKey.FromString("physical-memory.word-list-cache.default");

        Assert.NotEqual(0UL, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Action_dispatcher_executes_registered_handler()
    {
        var dispatcher = new AdapterResourceActionDispatcher();
        var resourceKey = AdapterResourceKey.FromString("physical-memory.sample-cache");
        var handled = false;

        dispatcher.Register(
            resourceKey,
            AdapterResourceActionMask.Discard | AdapterResourceActionMask.Trim,
            (in AdapterResourceActionRequest request) =>
            {
                handled = true;
                return AdapterResourceActionResult.Completed(
                    request,
                    AdapterResourceTier.PhysicalMemory,
                    AdapterResourceTier.VirtualMemory,
                    releasedBytes: 4096,
                    residentBytes: 0);
            });

        var result = dispatcher.Execute(new AdapterResourceActionRequest(
            requestId: 0,
            resourceKey,
            AdapterResourceActionMask.Discard));

        Assert.True(handled);
        Assert.Equal(0UL, result.RequestId);
        Assert.Equal(AdapterResourceActionStatus.Completed, result.Status);
        Assert.Equal(4096UL, result.ReleasedBytes);
        Assert.Equal(AdapterResourceTier.VirtualMemory, result.CurrentTier);
    }

    [Fact]
    public void Action_dispatcher_rejects_unknown_resource_without_handler_call()
    {
        var dispatcher = new AdapterResourceActionDispatcher();
        var result = dispatcher.Execute(new AdapterResourceActionRequest(
            requestId: 8,
            resourceKey: AdapterResourceKey.FromString("physical-memory.missing"),
            AdapterResourceActionMask.Trim));

        Assert.Equal(AdapterResourceActionStatus.ResourceNotFound, result.Status);
    }

    [Fact]
    public void Action_dispatcher_rejects_unsupported_or_compound_actions()
    {
        var dispatcher = new AdapterResourceActionDispatcher();
        var resourceKey = AdapterResourceKey.FromString("physical-memory.sample-cache");
        var calls = 0;

        dispatcher.Register(
            resourceKey,
            AdapterResourceActionMask.Trim,
            (in AdapterResourceActionRequest request) =>
            {
                calls++;
                return AdapterResourceActionResult.Completed(
                    request,
                    AdapterResourceTier.PhysicalMemory,
                    AdapterResourceTier.PhysicalMemory,
                    releasedBytes: 0,
                    residentBytes: 1024);
            });

        var unsupported = dispatcher.Execute(new AdapterResourceActionRequest(
            requestId: 9,
            resourceKey,
            AdapterResourceActionMask.MoveUp));
        var compound = dispatcher.Execute(new AdapterResourceActionRequest(
            requestId: 10,
            resourceKey,
            AdapterResourceActionMask.Trim | AdapterResourceActionMask.Discard));

        Assert.Equal(AdapterResourceActionStatus.ActionNotSupported, unsupported.Status);
        Assert.Equal(AdapterResourceActionStatus.InvalidRequest, compound.Status);
        Assert.Equal(0, calls);
    }

}
