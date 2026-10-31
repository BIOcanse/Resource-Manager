using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class GpuVfCurveServiceTests
{
    [Fact]
    public async Task GroupMoveRejectsEntireEditAndReportsCommonFeasibleDelta()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        var failure = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft,
            "move-selection", delta: 90000));
        Assert.Equal("gpu-vf-curve-common-delta-out-of-range", failure.Reason.Code);
        Assert.Equal(-85000, failure.Reason.Arguments!.Minimum);
        Assert.Equal(75000, failure.Reason.Arguments.Maximum);
        var unchanged = await fixture.Command(draft, "undo");
        Assert.Equal(draft.Points, unchanged.Points);
        Assert.False(unchanged.CanUndo);
    }

    [Fact]
    public async Task AlignUsesCapturedAbsoluteFrequenciesAndResetUsesNonzeroDefaultOffsets()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        var aligned = await fixture.Command(draft, "align-left");
        Assert.Equal(15000, aligned.Points[0].FrequencyOffsetKhz);
        Assert.Equal(-35000, aligned.Points[1].FrequencyOffsetKhz);
        // New telemetry must not redefine this draft's default snapshot.
        fixture.Provider.Value = fixture.Provider.Value with
        {
            Points = fixture.Provider.Value.Points.Select(p => p with { DefaultOffsetKhz = 6000 }).ToArray()
        };
        var reset = await fixture.Command(aligned, "reset-default");
        Assert.All(reset.Points, p => Assert.Equal(5000, p.FrequencyOffsetKhz));
        Assert.True(reset.CanUndo);
        var undone = await fixture.Command(reset, "undo");
        Assert.Equal(aligned.Points, undone.Points);
        Assert.True(undone.CanRedo);
    }

    [Fact]
    public async Task StepsUseBackendRulesUndoIsBoundedAndNewEditDiscardsRedo()
    {
        using var fixture = new Fixture();
        fixture.Provider.Value = fixture.Provider.Value with { Rules = Rules with { UndoCapacity = 1 } };
        var first = await fixture.Begin();
        var fine = await fixture.Command(first, "step", direction: 1);
        Assert.Equal(16000, fine.Points[0].FrequencyOffsetKhz);
        var coarse = await fixture.Command(fine, "step", direction: 1, coarse: true);
        Assert.Equal(26000, coarse.Points[0].FrequencyOffsetKhz);
        var undone = await fixture.Command(coarse, "undo");
        Assert.Equal(fine.Points, undone.Points);
        Assert.False(undone.CanUndo);
        var replaced = await fixture.Command(undone, "uniform-offset", offset: 0);
        Assert.False(replaced.CanRedo);
        Assert.All(replaced.Points, p => Assert.Equal(0, p.FrequencyOffsetKhz));
    }

    [Fact]
    public async Task BindingChangesAndUnknownOrDuplicatePointIdsAreRejected()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Service.ExecuteAsync(new(
            "step", "gpu:a", draft.Binding, draft.Id, PointIds: [7, 7], Direction: 1), default));
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Service.ExecuteAsync(new(
            "step", "gpu:a", draft.Binding, draft.Id, PointIds: [99], Direction: 1), default));
        fixture.Provider.Value = fixture.Provider.Value with { Binding = draft.Binding with { DeviceIdentity = "replacement" } };
        var changed = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft, "step", direction: 1));
        Assert.Equal("gpu-vf-curve-binding-changed", changed.Reason.Code);
        // Closing must release the draft even after hardware disappears.
        Assert.Null(await fixture.Service.ExecuteAsync(new("close", "gpu:a", draft.Binding, draft.Id), default));
    }

    [Fact]
    public async Task DefaultAndFrequencyOperationsRejectMissingHardwareFacts()
    {
        using var fixture = new Fixture();
        fixture.Provider.Value = fixture.Provider.Value with
        {
            Points = fixture.Provider.Value.Points.Select(p => p with { DefaultOffsetKhz = null, DefaultFrequencyKhz = null }).ToArray()
        };
        var draft = await fixture.Begin();
        var reset = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft, "reset-default"));
        Assert.Equal("gpu-vf-curve-default-unavailable", reset.Reason.Code);
        var align = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft, "align-left"));
        Assert.Equal("gpu-vf-curve-default-frequency-unavailable", align.Reason.Code);
        Assert.NotNull(await fixture.Command(draft, "uniform-offset", offset: 1000));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonfiniteCommandsNeverEnterDraftHistory(double value)
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft, "uniform-offset", offset: value));
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Command(draft, "move-selection", delta: value));
        Assert.False((await fixture.Command(draft, "undo")).CanRedo);
    }

    [Fact]
    public async Task SaveCloneRenameAndAcceptedExecutionAreIndependentOfLaterLibraryEdits()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        var saved = await fixture.Service.SaveAsync("new", new("gpu:a", "First", draft.Binding, DraftId: draft.Id), default);
        var execution = await fixture.Service.ResolveAsync("gpu:a", saved.Id, default);
        var clone = await fixture.Service.SaveAsync("new", new("gpu:a", "Copy", draft.Binding, CloneFromId: saved.Id), default);
        Assert.NotEqual(saved.Id, clone.Id);
        Assert.Equal(saved.Points, clone.Points);
        await fixture.Service.SaveAsync(saved.Id, new("gpu:a", "Renamed", draft.Binding,
            Points: saved.Points.Select(p => p with { FrequencyOffsetKhz = 0 }).ToArray()), default);
        Assert.Equal(15000, execution.Points[0].FrequencyOffsetKhz);
        Assert.Equal(0, (await fixture.Service.ResolveAsync("gpu:a", saved.Id, default)).Points[0].FrequencyOffsetKhz);
        Assert.Equal(2, fixture.Store.Value.Presets.Count);
    }

    [Fact]
    public async Task ModelSharingIsExplicitAndRequiresCompleteNativeSequence()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        var device = await fixture.Service.SaveAsync("new", new("gpu:a", "Device", draft.Binding), default);
        var model = await fixture.Service.SaveAsync("new", new("gpu:a", "Model", draft.Binding, BindingScope: "model"), default);
        fixture.Provider.Value = fixture.Provider.Value with { Binding = draft.Binding with { DeviceIdentity = "other-device" } };
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Service.ResolveAsync("gpu:a", device.Id, default));
        Assert.NotNull(await fixture.Service.ResolveAsync("gpu:a", model.Id, default));
        var altered = fixture.Provider.Value.Points.Select(p => p with { VoltageMicrovolts = p.VoltageMicrovolts + 1 }).ToArray();
        fixture.Provider.Value = fixture.Provider.Value with
        {
            Points = altered,
            Binding = fixture.Provider.Value.Binding! with { PointSignature = GpuVfCurveBinding.ComputePointSignature(altered.Select(p => (p.PointId, p.VoltageMicrovolts))) }
        };
        await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Service.ResolveAsync("gpu:a", model.Id, default));
    }

    [Fact]
    public async Task DeleteReturnsEveryReferenceAndChecksInsideSharedMutationGate()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Begin();
        var saved = await fixture.Service.SaveAsync("new", new("gpu:a", "Used", draft.Binding), default);
        fixture.References.Items = [new("preset", "gpu-preset", "gpu:a", "Gaming"), new("pending-release", "release", "gpu:a")];
        fixture.References.Check = () => Assert.Equal(0, fixture.Gate.Semaphore.CurrentCount);
        var used = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => fixture.Service.DeleteAsync(saved.Id, default));
        Assert.Equal(2, used.References.Count);
        Assert.Single(fixture.Store.Value.Presets);
        fixture.References.Items = [];
        await fixture.Service.DeleteAsync(saved.Id, default);
        Assert.Empty(fixture.Store.Value.Presets);
    }

    [Fact]
    public async Task MigrationReusesSameDeviceAndValueAndExecutionCopiesMutableInputs()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.EnsureUniformAsync("gpu:a", 2000, default);
        Assert.Equal(first, await fixture.Service.EnsureUniformAsync("gpu:a", 2000, default));
        Assert.Equal("迁移自偏移 +2 MHz", Assert.Single(fixture.Store.Value.Presets).Name);
        var points = new List<GpuVfCurvePoint> { new(7, 700000, 2000) };
        var execution = new GpuVfCurveExecution(fixture.Provider.Value.Binding!, points);
        points[0] = points[0] with { FrequencyOffsetKhz = 9000 };
        Assert.Equal(2000, execution.Points[0].FrequencyOffsetKhz);
    }

    internal static GpuVfCurveEditorRules Rules { get; } = new(1000, 10000, 1000, 0, 3, .05, 12, 8, 64, 2, 8, "device", null, null, 1000);
    internal static GpuVfCurveDescriptor Descriptor()
    {
        GpuVfCurveHardwarePoint[] points = [new(7, 700000, 1500000, 1510000, 5000, 15000, -100000, 100000),
            new(19, 725000, 1550000, 1570000, 5000, 25000, -60000, 100000)];
        return new("gpu:a", true, false, null, new("nvidia", "device-a", "model-a",
            GpuVfCurveBinding.ComputePointSignature(points.Select(p => (p.PointId, p.VoltageMicrovolts))), "device"), points, Rules, DateTimeOffset.UtcNow);
    }

    internal sealed class Fixture : IDisposable
    {
        internal MemoryStore Store { get; } = new();
        internal Provider Provider { get; } = new();
        internal ReferenceProvider References { get; } = new();
        internal GpuVfCurveMutationGate Gate { get; } = new();
        internal GpuVfCurveService Service { get; }
        internal Fixture() => Service = new(Store, Provider, References, Gate, TimeProvider.System);
        internal async Task<GpuVfCurveDraft> Begin() => (await Service.ExecuteAsync(new("begin", "gpu:a", Provider.Value.Binding!), default))!;
        internal async Task<GpuVfCurveDraft> Command(GpuVfCurveDraft draft, string operation, double? delta = null,
            double? offset = null, int? direction = null, bool coarse = false)
            => (await Service.ExecuteAsync(new(operation, draft.ObjectId, draft.Binding, draft.Id,
                PointIds: draft.Points.Select(p => p.PointId).ToArray(), DeltaKhz: delta, OffsetKhz: offset,
                Direction: direction, Coarse: coarse), default))!;
        public void Dispose() => Gate.Dispose();
    }
    internal sealed class MemoryStore : IGpuVfCurvePresetStore
    {
        internal GpuVfCurveCatalog Value { get; set; } = GpuVfCurveCatalog.Empty;
        public Task<GpuVfCurveCatalog> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(GpuVfCurveCatalog catalog, CancellationToken token) { Value = catalog; return Task.CompletedTask; }
    }
    internal sealed class Provider : IGpuVfCurveDescriptorProvider
    {
        internal GpuVfCurveDescriptor Value { get; set; } = Descriptor();
        public Task<GpuVfCurveDescriptor> GetAsync(string objectId, CancellationToken token) => Task.FromResult(Value);
    }
    internal sealed class ReferenceProvider : IGpuVfCurveReferences
    {
        internal IReadOnlyList<GpuVfCurveReference> Items { get; set; } = [];
        internal Action? Check { get; set; }
        public Task<IReadOnlyList<GpuVfCurveReference>> GetAsync(string id, CancellationToken token) { Check?.Invoke(); return Task.FromResult(Items); }
    }
}
