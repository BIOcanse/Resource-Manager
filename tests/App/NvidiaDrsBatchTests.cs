using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsBatchTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(3, 4, 8, 2, 200_000);
    private static NvidiaDrsInformationRequest Scope(string name, uint id, NvidiaDrsReadLimits? limits = null)
        => new([new(name, new uint[] { id })], limits ?? Limits);

    [Fact]
    public void EqualLimitsUnionSharedKeysAndDifferentLimitsOrOverflowKeepOriginalRequests()
    {
        var a = Scope("Game", 7); var b = Scope("Game", 8);
        var union = Assert.Single(NvidiaDrsInformationBatch.Create(new[] { a, b, a }));
        Assert.Equal(new uint[] { 7, 8 }, union.Profiles.Single().SettingIds);
        var other = Scope("Game", 7, Limits with { PageSize = 1 });
        Assert.Equal(2, NvidiaDrsInformationBatch.Create(new[] { a, other }).Length);
        var tight = new NvidiaDrsReadLimits(1, 1, 1, 1, 20_000);
        var first = Scope("Game", 7, tight); var second = Scope("Game", 8, tight);
        var split = NvidiaDrsInformationBatch.Create(new[] { first, second, first });
        Assert.Equal(2, split.Length); Assert.Same(first, split[0]); Assert.Same(second, split[1]);
    }

    [Fact]
    public async Task SplitSessionsMergeLocallyAndPublishOnceWithLastActualFailureClearingOnlyItsKey()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var retained = Scope("Other", 99);
        _ = await owner.CreateNvidiaDrsInformation(retained, (profiles, _, _) => Values(profiles, 99))
            .Backend.CollectAsync(retained.Fields, default);
        var original = owner.ReadState();
        var a = Scope("Game", 7); var b = Scope("Game", 8);
        var sameLast = Scope("Game", 7, Limits with { PageSize = 1 });
        var calls = 0;
        var result = await owner.CaptureNvidiaDrsBatchAsync([a, b, sameLast], (profiles, _, _) =>
        {
            Assert.Same(original, owner.ReadState());
            return ++calls == 3 ? null : Values(profiles, (uint)calls);
        }, Accept(), default);
        Assert.True(result.Committed); Assert.Equal(3, calls);
        Assert.Equal(original.StateRevision + 1, owner.ReadState().StateRevision);
        Assert.Null(owner.ReadState().InformationFields.FindField(a.Fields[0])!.Value);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(b.Fields[0])!.Value);
        Assert.Same(original.InformationFields.FindField(retained.Fields[0]), owner.ReadState().InformationFields.FindField(retained.Fields[0]));
        Assert.Equal(2, result.QueriedFields.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectionOrCancellationAfterFirstSessionCannotPartiallyCommit(bool cancel)
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        using var cts = new CancellationTokenSource();
        var before = owner.ReadState(); var active = true;
        var permission = new DeviceTopologyCapturePermission(() => active, publish =>
        { if (!active) return false; publish(); return true; }, () => true);
        async Task Run()
        {
            var result = await owner.CaptureNvidiaDrsBatchAsync([Scope("A", 1), Scope("B", 2)], (profiles, _, _) =>
            { if (cancel) cts.Cancel(); else active = false; return Values(profiles, 1); }, permission, cts.Token);
            Assert.False(result.Committed);
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run); else await Run();
        Assert.Same(before, owner.ReadState());
    }

    [Fact]
    public async Task FreshCaptureRunsInsideTheAcceptedCommitBeforeDirectCanReplaceCurrent()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = Scope("Game", 7);
        var access = owner.CreateNvidiaDrsInformation(scope, (profiles, _, _) => Values(profiles, 2));
        FieldReading? fresh = null;
        var first = Task.Run(async () => await owner.CaptureNvidiaDrsBatchAsync([scope], (profiles, _, _) => Values(profiles, 1),
            Accept(), deadline.Token, keys =>
            {
                Assert.Equal(new[] { scope.Fields[0].DeviceIdentity }, keys);
                fresh = access.Backend.ReadCached(scope.Fields[0]);
                entered.SetResult(true); release.Wait(deadline.Token);
            }), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        var direct = access.Backend.CollectAsync(scope.Fields, deadline.Token).AsTask();
        try { Assert.False(direct.IsCompleted); }
        finally { release.Set(); }
        var accepted = await first.WaitAsync(deadline.Token); await direct.WaitAsync(deadline.Token);
        Assert.True(accepted.Committed);
        Assert.Equal(1U, fresh!.Value!.Structure!.Value.GetProperty("currentValue").GetProperty("value").GetUInt32());
        Assert.Equal(2U, access.Backend.ReadCached(scope.Fields[0]).Value!.Structure!.Value.GetProperty("currentValue").GetProperty("value").GetUInt32());
    }

    private static DeviceTopologyCapturePermission Accept() => new(() => true, action => { action(); return true; }, () => true);
    private static NvidiaDrsSnapshot Values(IReadOnlyList<NvidiaDrsProfileRequest> profiles, uint value)
        => new([.. profiles.Select(profile => new NvidiaDrsProfile(profile.ProfileName, 7, 0, 0,
            (uint)profile.SettingIds.Count, [.. profile.SettingIds.Select(id => new NvidiaDrsSetting(id, "setting", 0, 0, 0, 1,
                new NvidiaDrsDwordValue(value)))]))]);
}
