using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

/// <summary>Runs the backend's compiled production conditions through the native ABI.</summary>
public sealed class BackendNativeRuleEquivalenceTests
{
    private static readonly PublicationValue[] Values =
    [
        new(false), new(true), new(NoValueReason.NotPublished),
        new(NoValueReason.OwnerStopped), new(NoValueReason.ReadFailed)
    ];
    private static readonly BusinessBlockReason Reason = new(
        BusinessBlockRequestKind.Configuration, "native-rule-equivalence", "matrix", 1);

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task EveryExternalDemandConditionMatchesEveryNativeReadState(uint profile)
    {
        await using var native = CreateNative(profile);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var targets = native.Graph.Nodes.Where(node => node.Id != BackendUids.HostMemoryProtection
            && !BackendExpectedProductionTree.Wrappers.Contains(node.Id)
            && node.ParentId != BackendUids.NativeEtwLifetimeService).ToArray();
        Assert.Equal(profile switch { 1 => 34, 2 => 35, 3 => 22, _ => throw new ArgumentOutOfRangeException(nameof(profile)) }, targets.Length);
        var demands = targets.ToDictionary(node => node.Id,
            node => authority.ClaimCell<bool>(new(BackendItems.BlockDemand, node.Id)));
        native.Activate(_ => new HoldingBlock());
        foreach (var node in targets)
        {
            // Each actual owner must run while its child's rule is evaluated.
            // An earlier matrix may have withdrawn this owner's own demand.
            if (demands.TryGetValue(node.ParentId, out var parentDemand))
                await parentDemand.PublishAsync(true, Reason);
            await BusinessBlockTestSupport.Until(() => native.ReadState(node.ParentId) == BusinessBlockState.Running);
            foreach (var input in Values)
            {
                await PublishAsync(demands[node.Id], input);
                AssertCondition(native, node.ParentId, ResourceManager.App.StateItems.ConditionUids.HasExternalDemand, node.Id, input.IsTrue);
            }
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task WrappersRemainWantedWithoutDemandUntilRootShutdown(uint profile)
    {
        await using var native = CreateNative(profile);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var wrappers = native.Graph.Nodes.Where(node => BackendExpectedProductionTree.Wrappers.Contains(node.Id)).ToArray();
        Assert.Equal(profile == 3 ? 6 : 9, wrappers.Length);
        native.Activate(_ => new HoldingBlock());
        await BusinessBlockTestSupport.Until(() => wrappers.All(node => native.ReadState(node.Id) == BusinessBlockState.Running));
        foreach (var node in wrappers)
        {
            var demand = authority.ClaimCell<bool>(new(BackendItems.BlockDemand, node.Id));
            foreach (var value in Values)
            {
                await PublishAsync(demand, value);
                AssertCondition(native, node.ParentId, ResourceManager.App.StateItems.ConditionUids.AlwaysWanted, node.Id, true);
                Assert.Equal(BusinessBlockState.Running, native.ReadState(node.Id));
                Assert.Equal([BackendUids.Backend], Assert.Single(native.Read().CurrentDecisions,
                    row => row.ParentId == BackendUids.Program).ShouldRun);
            }
        }
        await native.ShutdownAsync(Reason);
        await BusinessBlockTestSupport.Until(() => native.Read().Blocks.All(row => row.State == BusinessBlockState.Stopped));
        Assert.Empty(Assert.Single(native.Read().CurrentDecisions, row => row.ParentId == BackendUids.Program).ShouldRun);
    }

    [Fact]
    public async Task HostMemoryProtectionConditionRequiresAllThreeTrueFacts()
    {
        await using var native = CreateNative();
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var enabled = authority.ClaimCell<bool>(BackendItems.HostMemoryProtectionEnabled);
        var normalPoint = authority.ClaimCell<bool>(BackendItems.MemoryProtectionNormalRunningPointExists);
        var reserveBuilt = authority.ClaimCell<bool>(BackendItems.MemoryProtectionReserveBuilt);
        native.Activate(_ => new HoldingBlock());
        await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.Backend) == BusinessBlockState.Running);

        var cells = new[] { enabled, normalPoint, reserveBuilt };
        var previous = new PublicationValue?[cells.Length];
        foreach (var enabledValue in Values)
        foreach (var normalPointValue in Values)
        foreach (var reserveValue in Values)
        {
            await PublishChangedAsync(cells, [enabledValue, normalPointValue, reserveValue], previous);
            AssertCondition(native, BackendUids.Backend, ResourceManager.App.StateItems.ConditionUids.HostMemoryProtectionReady, null,
                enabledValue.IsTrue && normalPointValue.IsTrue && reserveValue.IsTrue);
        }
    }

    [Fact]
    public async Task NativeProducerConditionsMatchAllDemandAvailabilityAndModeStates()
    {
        await AssertNativeProducerMatrix(
            BackendUids.PresentFrameEtwSource,
            BackendItems.PresentNativeSessionAvailable,
            BackendItems.PresentUnfrozen,
            ResourceManager.App.StateItems.ConditionUids.PresentEtwChildShouldRun,
            modeAllowsWhenAbsent: true);
        await AssertNativeProducerMatrix(
            BackendUids.GpuAllocationCollector,
            BackendItems.GpuAllocationNativeSessionAvailable,
            null,
            ResourceManager.App.StateItems.ConditionUids.GpuAllocationEtwChildShouldRun,
            modeAllowsWhenAbsent: false);
        await AssertNativeProducerMatrix(
            BackendUids.DxgkrnlVidMmEtwTelemetryZone,
            BackendItems.VidMmNativeSessionAvailable,
            BackendItems.VidMmUnfrozen,
            ResourceManager.App.StateItems.ConditionUids.VidMmEtwChildShouldRun,
            modeAllowsWhenAbsent: true);
    }

    private static async Task AssertNativeProducerMatrix(int target, int availabilityItem,
        int? modeItem, int conditionUid, bool modeAllowsWhenAbsent)
    {
        await using var native = CreateNative();
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var demand = authority.ClaimCell<bool>(new(BackendItems.BlockDemand, target));
        var available = authority.ClaimCell<bool>(availabilityItem);
        var mode = modeItem is { } uid ? authority.ClaimCell<bool>(uid) : null;
        native.Activate(_ => new HoldingBlock());
        await ActivateNativeEtwGroupAsync(native, authority);

        StateCell<bool>[] cells = mode is null ? [demand, available] : [demand, available, mode];
        var previous = new PublicationValue?[cells.Length];
        if (mode is null)
        {
            foreach (var demandValue in Values)
            foreach (var availabilityValue in Values)
            {
                await PublishChangedAsync(cells, [demandValue, availabilityValue], previous);
                AssertCondition(native, BackendUids.NativeEtwLifetimeService, conditionUid, null,
                    demandValue.IsTrue && availabilityValue.IsTrue);
            }
            return;
        }

        foreach (var demandValue in Values)
        foreach (var availabilityValue in Values)
        foreach (var modeValue in Values)
        {
            await PublishChangedAsync(cells, [demandValue, availabilityValue, modeValue], previous);
            var modeAllows = modeValue.HasValue ? modeValue.Value
                : modeAllowsWhenAbsent && modeValue.NoValueReason == NoValueReason.NotPublished;
            AssertCondition(native, BackendUids.NativeEtwLifetimeService, conditionUid, null,
                demandValue.IsTrue && availabilityValue.IsTrue && modeAllows);
        }
    }

    private static NativeControl CreateNative(uint profile = 1)
    {
        var hostUids = BackendExpectedProductionTree.Parents(profile).Keys.ToArray();
        return new(hostUids, new NativeControlOptions
        {
            Profile = profile,
            DemandItemUid = null,
            ShutdownItemUid = BackendItems.ProgramShuttingDown,
            HistoryCapacity = 32
        }, NullLogger<NativeControl>.Instance);
    }

    private static async Task PublishAsync(StateCell<bool> cell, PublicationValue value)
    {
        if (value.HasValue) await cell.PublishAsync(value.Value, Reason);
        else await cell.PublishNoValueAsync(value.NoValueReason!.Value, Reason);
    }

    private static async Task ActivateNativeEtwGroupAsync(NativeControl native, StateAuthority authority)
    {
        var demand = authority.ClaimCell<bool>(new(BackendItems.BlockDemand, BackendUids.NativeEtwLifetimeService));
        await demand.PublishAsync(true, Reason);
        await BusinessBlockTestSupport.Until(() =>
            native.ReadState(BackendUids.NativeEtwLifetimeService) == BusinessBlockState.Running);
    }

    private static void AssertCondition(NativeControl native, int nodeUid, int conditionUid,
        int? conditionBlockUid, bool expected)
    {
        var node = native.SnapshotJson().GetProperty("nodes").EnumerateArray()
            .Single(value => value.GetProperty("uid").GetInt32() == nodeUid);
        var condition = node.GetProperty("conditions").EnumerateArray().Single(value =>
            value.GetProperty("uid").GetInt32() == conditionUid
            && (conditionBlockUid is null
                ? !value.TryGetProperty("blockUid", out _)
                : value.TryGetProperty("blockUid", out var block) && block.GetInt32() == conditionBlockUid));
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("lastError").ValueKind);
        Assert.Equal(expected, condition.GetProperty("value").GetBoolean());
    }

    private static async Task PublishChangedAsync(StateCell<bool>[] cells,
        PublicationValue[] values, PublicationValue?[] previous)
    {
        Assert.Equal(cells.Length, values.Length);
        Assert.Equal(cells.Length, previous.Length);
        for (var index = 0; index < cells.Length; index++)
        {
            if (previous[index] is not { } prior || prior != values[index])
                await PublishAsync(cells[index], values[index]);
            previous[index] = values[index];
        }
    }

    private readonly record struct PublicationValue(bool HasValue, bool Value, NoValueReason? NoValueReason)
    {
        internal PublicationValue(bool value) : this(true, value, null) { }
        internal PublicationValue(NoValueReason noValueReason) : this(false, false, noValueReason) { }
        internal bool IsTrue => HasValue && Value;
    }

    private sealed class HoldingBlock : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}
