using System.Text.Json.Nodes;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionExitRecordTests
{
    internal static RecoveryReadResult<ProcessInstanceRecoverySnapshot> Gone()
        => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(0, "Exited.");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExitSettlementRetainsTheOriginalUnknownOrPendingExecution(bool pending, bool replacement)
    {
        var prepared = Prepared();
        var result = pending ? null : Unknown(prepared);
        var original = GpuWindowActionRecord.Create(prepared, result);
        Assert.True(GpuWindowActionRecord.TryRead(original, out var fact));
        var target = prepared.Window.Request;
        var worker = prepared.Worker;
        var settlement = new GpuWindowActionExitSettlement(DateTimeOffset.UtcNow,
            replacement ? Replacement(target.ProcessId, target.CreationFileTimeUtc) : Gone(),
            replacement ? Replacement(worker.ProcessId, worker.CreationFileTimeUtc) : Gone());
        var saved = fact.SettleExited(settlement);
        Assert.True(GpuWindowActionRecord.TryRead(saved, out var read));
        Assert.Equal(prepared, read.Prepared);
        Assert.Equal(result, read.Result);
        Assert.Equal(settlement, read.ExitSettlement);
        Assert.Equal(original.RecordId, saved.RecordId);
        Assert.False(read.BlocksAutomaticAction);
        Assert.False(GpuActionFacts.HasUnsettledActions([Placement(saved)]));
        Assert.False(GpuActionFacts.HasPlacementEffects([Placement(saved)]));
        Assert.False(GpuWindowActionRecord.BlocksProcess([Placement(saved)], "different-adapter",
            target.ProcessId, (ulong)target.CreationFileTimeUtc));
        Assert.Throws<InvalidOperationException>(() => read.SettleExited(settlement));
        Assert.Throws<InvalidDataException>(() => GpuWindowActionRecord.Complete(saved, Unknown(prepared)));
    }

    [Theory]
    [InlineData("target-live")]
    [InlineData("worker-live")]
    [InlineData("target-unavailable")]
    [InlineData("worker-unavailable")]
    [InlineData("wrong-pid")]
    [InlineData("malformed")]
    [InlineData("unknown-status")]
    [InlineData("missing-time")]
    public void ExitSettlementRejectsUnconfirmedOrMalformedObservation(string scenario)
    {
        var prepared = Prepared();
        var original = GpuWindowActionRecord.Create(prepared, Unknown(prepared));
        Assert.True(GpuWindowActionRecord.TryRead(original, out var fact));
        var target = Gone();
        var worker = Gone();
        var unavailable = RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "Denied.");
        switch (scenario)
        {
            case "target-live": target = Live(prepared.Window.Request.ProcessId, prepared.Window.Request.CreationFileTimeUtc); break;
            case "worker-live": worker = Live(prepared.Worker.ProcessId, prepared.Worker.CreationFileTimeUtc); break;
            case "target-unavailable": target = unavailable; break;
            case "worker-unavailable": worker = unavailable; break;
            case "wrong-pid": target = Replacement(prepared.Window.Request.ProcessId + 1, prepared.Window.Request.CreationFileTimeUtc); break;
            case "malformed": target = new(RecoveryReadStatus.Found, null, 0, ""); break;
            case "unknown-status": target = new((RecoveryReadStatus)99, null, 0, ""); break;
        }
        var settlement = new GpuWindowActionExitSettlement(scenario == "missing-time" ? default : DateTimeOffset.UtcNow, target, worker);
        Assert.Throws<InvalidDataException>(() => fact.SettleExited(settlement));
        var payload = JsonNode.Parse(original.Metadata!["windowActionPayload"])!.AsObject();
        payload["ExitSettlement"] = System.Text.Json.JsonSerializer.SerializeToNode(settlement);
        var corrupt = original with { Metadata = new Dictionary<string, string>(original.Metadata)
        { ["windowActionPayload"] = payload.ToJsonString() } };
        Assert.False(GpuWindowActionRecord.TryRead(corrupt, out _));
        Assert.True(GpuActionFacts.HasUnsettledActions([Placement(corrupt)]));
    }

    [Fact]
    public void ExistingVersionOneWithoutExitSettlementRemainsUnsettled()
    {
        var original = GpuWindowActionRecord.Create(Prepared());
        var payload = JsonNode.Parse(original.Metadata!["windowActionPayload"])!.AsObject();
        Assert.True(payload.Remove("ExitSettlement"));
        var old = original with { Metadata = new Dictionary<string, string>(original.Metadata)
        { ["windowActionPayload"] = payload.ToJsonString() } };
        Assert.True(GpuWindowActionRecord.TryRead(old, out var fact));
        Assert.Null(fact.ExitSettlement);
        Assert.True(fact.BlocksAutomaticAction);
    }

    internal static RecoveryReadResult<ProcessInstanceRecoverySnapshot> Live(int pid, long creation)
        => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, DateTimeOffset.FromFileTime(creation)));
    private static RecoveryReadResult<ProcessInstanceRecoverySnapshot> Replacement(int pid, long creation)
        => Live(pid, creation + 1);

    [Fact]
    public async Task ExitSettlementSurvivesClosingAndReopeningTheOriginalCanonicalStore()
    {
        var path = Path.Combine(NewRoot("exit-reopen"), "recovery.json");
        var original = GpuWindowActionRecord.Create(Prepared(), Unknown(Prepared()));
        Assert.True(GpuWindowActionRecord.TryRead(original, out var fact));
        var record = fact.SettleExited(new(DateTimeOffset.UtcNow, Gone(), Gone()));
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
        {
            var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with
            { AppliedPlacements = [Placement(record)] };
            await store.SaveAsync(state, default);
        }
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var loaded = await reopened.LoadAsync(default);
        var saved = Assert.Single(Assert.Single(loaded.AppliedPlacements).Records);
        Assert.Equal(record.Metadata!["windowActionPayload"], saved.Metadata!["windowActionPayload"]);
        Assert.True(GpuWindowActionRecord.TryRead(saved, out var decoded));
        Assert.Equal(fact.Result, decoded.Result);
        Assert.False(GpuActionFacts.HasUnsettledActions(loaded.AppliedPlacements));
    }
}
