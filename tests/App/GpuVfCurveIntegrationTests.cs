using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class GpuVfCurveIntegrationTests
{
    [Fact]
    public async Task MigrationDeduplicatesDeviceAndOffsetAndPreservesAcceptedInputOnRepeatedUpgrade()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        ControlSetting legacy = new("gpu.core-clock-offset", Number: 30, Unit: "MHz");
        f.Stores.Presets = new([new("first", "gpu:a", "First", [legacy], now, now),
            new("second", "gpu:a", "Second", [legacy], now, now)]);
        f.Stores.Desired = new([new("gpu:a", [legacy])]);
        await f.Migration.MigrateAsync(default);
        var curve = Assert.Single(f.Stores.Curves.Presets);
        Assert.Contains("+30", curve.Name);
        Assert.All(f.Stores.Presets.Presets, preset => Assert.Equal(curve.Id, Assert.Single(preset.Settings).GpuVfCurvePresetId));
        var accepted = Assert.Single(Assert.Single(f.Stores.Desired.Objects).Settings);
        Assert.Equal(GpuVfCurveCapabilities.Core, accepted.CapabilityId);
        Assert.All(accepted.GpuVfCurveExecution!.Points, point => Assert.Equal(30000, point.FrequencyOffsetKhz));
        await f.Migration.MigrateAsync(default);
        Assert.Single(f.Stores.Curves.Presets);
        Assert.Same(accepted.GpuVfCurveExecution, f.Stores.Desired.Objects[0].Settings[0].GpuVfCurveExecution);
    }

    [Fact]
    public async Task ReadOnlyPresetGetNeverMigratesOrWritesUserData()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var old = new ControlPreset("old", "gpu:a", "Old", [new("gpu.core-clock-offset", Number: 30, Unit: "MHz")], now, now);
        f.Stores.Presets = new([old]);
        var migration = new GpuVfCurveMigration(f.Stores, f.Stores, f.Service, f.Gate,
            capabilities: ResourceManager.App.Hosting.StartupCapabilities.StartupCapabilitySet.NormalReadOnly);
        var presets = new ControlPresets(f.Stores, f.Gate, curveMigration: migration);
        var catalog = await presets.ReadAsync(default);
        Assert.Same(old, Assert.Single(catalog.Presets));
        Assert.Empty(f.Stores.Curves.Presets);
        Assert.Empty(f.Stores.Desired.Objects);
    }

    [Fact]
    public async Task SharedReferencesIncludePresetsDesiredPendingAndAppliedAndBlockDeletion()
    {
        using var f = new Fixture();
        var curve = await f.Save("Shared", 10000);
        var execution = await f.Service.ResolveAsync("gpu:a", curve.Id, default);
        var setting = new ControlSetting(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: curve.Id, GpuVfCurveExecution: execution);
        var now = DateTimeOffset.UtcNow;
        f.Stores.Presets = new([new("one", "gpu:a", "One", [setting], now, now), new("two", "gpu:a", "Two", [setting], now, now)]);
        f.Stores.Desired = new([new("gpu:a", [setting])]) { PendingReleases = [new("gpu:a", [setting])] };
        f.Applied.Record(f.Stores.Desired, new([new("gpu:a", GpuVfCurveCapabilities.Core, ControlApplyStatuses.Applied)], now), ControlApplyReport.Empty);
        var references = await f.Service.ReferencesAsync(curve.Id, default);
        Assert.Equal(2, references.Count(reference => reference.Kind == "preset"));
        Assert.Contains(references, reference => reference.Kind == "desired");
        Assert.Contains(references, reference => reference.Kind == "pending-release");
        Assert.Contains(references, reference => reference.Kind == "applied");
        var error = await Assert.ThrowsAsync<GpuVfCurveRequestException>(() => f.Service.DeleteAsync(curve.Id, default));
        Assert.Equal("gpu-vf-curve-in-use", error.Reason.Code);
        Assert.Equal(references, error.References);
    }

    [Fact]
    public async Task ExplicitApplyResolvesOnceAndReassertIgnoresSubsequentLibraryEdits()
    {
        using var f = new Fixture();
        var curve = await f.Save("Shared", 10000);
        var view = await f.Plane.SetObjectSettingsAsync("gpu:a",
            [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: curve.Id)], default);
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(view.LastApply.Outcomes).Status);
        Assert.Equal(10000, f.Adapter.Read(f.NativeTarget).Snapshot!.ScalarOffsetKhz);
        await f.Service.SaveAsync(curve.Id, new("gpu:a", "Shared", f.Provider.Descriptor.Binding!,
            Points: f.Points(30000)), default);
        Assert.Equal(10000, f.Adapter.Read(f.NativeTarget).Snapshot!.ScalarOffsetKhz);
        await f.Plane.ReassertAsync(default);
        Assert.Equal(10000, f.Adapter.Read(f.NativeTarget).Snapshot!.ScalarOffsetKhz);
        await f.Plane.SetObjectSettingsAsync("gpu:a", [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: curve.Id)], default);
        Assert.Equal(30000, f.Adapter.Read(f.NativeTarget).Snapshot!.ScalarOffsetKhz);
    }

    [Fact]
    public async Task MissingReferenceAndMixedValueAreRejectedBeforePersistenceOrAnySimulatedMutation()
    {
        using var f = new Fixture();
        var missing = await f.Plane.SetObjectSettingsAsync("gpu:a",
            [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: "missing")], default);
        Assert.Equal("gpu-vf-curve-preset-not-found", Assert.Single(missing.LastApply.Outcomes).Reason!.Code);
        Assert.Empty(f.Stores.Desired.Objects);
        Assert.Empty(f.Adapter.Operations);
        var curve = await f.Save("Valid", 10000);
        var mixed = await f.Plane.SetObjectSettingsAsync("gpu:a",
            [new(GpuVfCurveCapabilities.Core, Number: 1, GpuVfCurvePresetId: curve.Id)], default);
        Assert.Equal("gpu-vf-curve-value-shape-invalid", Assert.Single(mixed.LastApply.Outcomes).Reason!.Code);
        Assert.Empty(f.Stores.Desired.Objects);
        Assert.Empty(f.Adapter.Operations);
    }

    [Fact]
    public async Task WholeControlWriteChainChoosesScalarOrTableAndConfirmedReleaseRestoresOriginal()
    {
        using var f = new Fixture();
        var uniform = await f.Save("Uniform", 30000);
        await f.Plane.SetObjectSettingsAsync("gpu:a", [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: uniform.Id)], default);
        Assert.Contains("write-scalar", f.Adapter.Operations);
        var nonuniform = await f.Service.SaveAsync("new", new("gpu:a", "Table", f.Provider.Descriptor.Binding!,
            Points: [new(7, 800000, 30000), new(9, 900000, -10000)]), default);
        var applied = await f.Plane.SetObjectSettingsAsync("gpu:a", [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: nonuniform.Id)], default);
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(applied.LastApply.Outcomes).Status);
        var observed = f.Adapter.Read(f.NativeTarget).Snapshot!;
        Assert.Equal(0, observed.ScalarOffsetKhz);
        Assert.Equal(new[] { 30000, -10000 }, observed.Points.Select(point => point.FrequencyOffsetKhz));
        Assert.Contains("write-table", f.Adapter.Operations);
        await f.Plane.SetObjectSettingsAsync("gpu:a", [], default);
        Assert.Equal(new[] { 0, 0 }, f.Adapter.Read(f.NativeTarget).Snapshot!.Points.Select(point => point.FrequencyOffsetKhz));
        Assert.Empty(f.Applied.References(nonuniform.Id));
    }

    [Fact]
    public void ScalarOnlyVendorTranslationRequiresSinglePointAndKeepsTheNativeUnitBoundary()
    {
        var binding = new GpuVfCurveBinding("intel", "gpu:a", "fixture", "signature", "device");
        var single = new ControlSetting(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: "curve",
            GpuVfCurveExecution: new(binding, [new(0, 0, 15000)]));
        Assert.True(GpuVfCurveScalarAdapter.TrySetting(single, out var scalar));
        Assert.Equal("gpu.core-clock-offset", scalar.CapabilityId);
        Assert.Equal(15, scalar.Number);
        Assert.Equal("MHz", scalar.Unit);
        Assert.False(GpuVfCurveScalarAdapter.TrySetting(single with
        { GpuVfCurveExecution = new(binding, [new(0, 0, 15000), new(1, 0, 15000)]) }, out _));
    }

    [Fact]
    public async Task FailedInitialApplyWithConfirmedRollbackCanBeClearedWithoutInventingOwnership()
    {
        using var f = new Fixture();
        var curve = await f.Save("Failure", 30000);
        f.Adapter.FailOperation = operation => operation == "write-scalar" ? "fixture-partial-failure" : null;
        var failed = await f.Plane.SetObjectSettingsAsync("gpu:a", [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: curve.Id)], default);
        Assert.Equal(ControlApplyStatuses.Failed, Assert.Single(failed.LastApply.Outcomes).Status);
        var cleared = await f.Plane.SetObjectSettingsAsync("gpu:a", [], default);
        Assert.Empty(cleared.Desired.PendingReleases);
        Assert.Empty(cleared.Desired.Objects);
        Assert.Empty(f.Applied.References(curve.Id));
    }

    [Fact]
    public async Task ExplicitForgetDiscardsReferencesAndRecoveryDataWithoutAnotherHardwareOperation()
    {
        using var f = new Fixture();
        var curve = await f.Save("Forget", 30000);
        await f.Plane.SetObjectSettingsAsync("gpu:a", [new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: curve.Id)], default);
        Assert.NotEmpty(f.Applied.References(curve.Id));
        var operations = f.Adapter.Operations.ToArray();
        await f.Plane.ForgetInstanceAsync("gpu:a", default);
        Assert.Empty(await f.Service.ReferencesAsync(curve.Id, default));
        Assert.Equal(operations, f.Adapter.Operations);
        Assert.False(f.Writer.Resource.HasVfCurveOwnership(f.NativeTarget));
    }

    [Fact]
    public async Task LegacyPendingReleaseResolvesNewDeclarationButKeepsLegacyVendorProtocol()
    {
        ControlObject target = new("gpu:legacy", "gpu", "Legacy", new("windows", "nvidia"),
            [new(GpuVfCurveCapabilities.Core, ControlValueKinds.GpuVfCurveReference, true)], GpuAttachment: "discrete");
        var writer = new LegacyReleaseWriter();
        var layer = new ControlWriteLayer(new Catalog(target), [writer], new Access());
        var report = await layer.ReleaseAsync(new([new(target.Id, [new("gpu.core-clock-offset", Number: 30, Unit: "MHz")])]),
            ControlDesiredState.Empty, default);
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(report.Outcomes).Status);
        Assert.Equal("gpu.core-clock-offset", writer.Released);
    }

    [Fact]
    public async Task AlignUsesVoltagePositionWithoutReorderingTheNativeBindingSequence()
    {
        using var f = new Fixture();
        var points = f.Provider.Descriptor.Points.Reverse().ToArray();
        var binding = f.Provider.Descriptor.Binding! with
        { PointSignature = GpuVfCurveBinding.ComputePointSignature(points.Select(point => (point.PointId, point.VoltageMicrovolts))) };
        f.Provider.Descriptor = f.Provider.Descriptor with { Points = points, Binding = binding };
        var draft = (await f.Service.ExecuteAsync(new("begin", "gpu:a", binding), default))!;
        var aligned = (await f.Service.ExecuteAsync(new("align-left", "gpu:a", binding, draft.Id, PointIds: [7, 9]), default))!;
        Assert.Equal(new[] { 9, 7 }, aligned.Points.Select(point => point.PointId));
        Assert.Equal(-100000, aligned.Points[0].FrequencyOffsetKhz);
        Assert.Equal(0, aligned.Points[1].FrequencyOffsetKhz);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Stores Stores = new();
        internal readonly GpuVfCurveMutationGate Gate = new();
        internal readonly GpuVfCurveAppliedState Applied = new();
        internal readonly Provider Provider;
        internal readonly GpuVfCurveService Service;
        internal readonly GpuVfCurveMigration Migration;
        internal readonly NvidiaGpuVfCurveSimulatedAdapter Adapter;
        internal readonly NvidiaGpuControlWriter Writer;
        internal readonly ControlPlane Plane;
        internal readonly NvidiaControlObject NativeTarget = new("gpu:a", 0);

        internal Fixture()
        {
            NvidiaGpuVfCurvePoint[] native = [new(7, 800000, 1800000, 1800000, 0, -100000, 100000),
                new(9, 900000, 1900000, 1900000, 0, -100000, 100000)];
            var binding = new GpuVfCurveBinding("nvidia", "gpu:a", "Fixture GPU",
                GpuVfCurveBinding.ComputePointSignature(native.Select(point => (point.PointId, (long)point.VoltageMicrovolts))), "device");
            Provider = new(new("gpu:a", true, true, null, binding,
                native.Select(point => new GpuVfCurveHardwarePoint(point.PointId, point.VoltageMicrovolts,
                    point.DefaultFrequencyKhz, point.CurrentFrequencyKhz, 0, 0, point.MinimumOffsetKhz, point.MaximumOffsetKhz)).ToArray(),
                GpuVfCurveSettings.ProductDefaults.Rules, DateTimeOffset.UtcNow));
            Service = new(Stores, Provider, new GpuVfCurveReferences(Stores, Stores, Applied), Gate, TimeProvider.System);
            Migration = new(Stores, Stores, Service, Gate);
            Adapter = new(new("gpu:a", NvidiaGpuVfCurveAbis.Modern, native, true, null, 0));
            Writer = new(new NvidiaGpuControlResource(vfCurveAdapter: Adapter));
            ControlObject target = new("gpu:a", "gpu", "Fixture GPU", new("windows", "nvidia"),
                [new(GpuVfCurveCapabilities.Core, ControlValueKinds.GpuVfCurveReference, true)], GpuAttachment: "discrete", AdapterIndex: 0);
            var layer = new ControlWriteLayer(new Catalog(target), [Writer], new Access());
            Plane = new(Stores, layer, curveMutation: Gate, curveInputs: new(Service, Migration), curveMigration: Migration, appliedCurves: Applied);
        }
        internal IReadOnlyList<GpuVfCurvePoint> Points(double offset) => [new(7, 800000, offset), new(9, 900000, offset)];
        internal Task<GpuVfCurvePreset> Save(string name, double offset)
            => Service.SaveAsync("new", new("gpu:a", name, Provider.Descriptor.Binding!, Points: Points(offset)), default);
        public void Dispose() { Writer.Dispose(); Gate.Dispose(); }
    }
    private sealed class Provider(GpuVfCurveDescriptor descriptor) : IGpuVfCurveDescriptorProvider
    {
        internal GpuVfCurveDescriptor Descriptor = descriptor;
        public Task<GpuVfCurveDescriptor> GetAsync(string objectId, CancellationToken token) => Task.FromResult(Descriptor);
    }
    private sealed class Catalog(ControlObject target) : IControlObjectCatalog
    { public ControlObjectCatalog ReadObjects() => new([target], DateTimeOffset.UtcNow); }
    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class LegacyReleaseWriter : IControlWriter
    {
        internal string? Released;
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => ControlWriteAvailability.Yes();
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
            => throw new InvalidOperationException("Release must not become a new curve apply.");
        public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> configured,
            CancellationToken token, ControlSetting? previousSetting = null)
        { Released = capability.Id; return Task.FromResult(capability.Range is null && previousSetting?.Number == 30); }
    }
    private sealed class Stores : IControlPresetStore, IControlDesiredStateStore, IGpuVfCurvePresetStore
    {
        internal ControlPresetCatalog Presets = ControlPresetCatalog.Empty;
        internal ControlDesiredState Desired = ControlDesiredState.Empty;
        internal GpuVfCurveCatalog Curves = GpuVfCurveCatalog.Empty;
        Task<ControlPresetCatalog> IControlPresetStore.LoadAsync(CancellationToken token) => Task.FromResult(Presets);
        Task IControlPresetStore.SaveAsync(ControlPresetCatalog value, CancellationToken token) { Presets = value; return Task.CompletedTask; }
        Task<ControlDesiredState> IControlDesiredStateStore.LoadAsync(CancellationToken token) => Task.FromResult(Desired);
        Task IControlDesiredStateStore.SaveAsync(ControlDesiredState value, CancellationToken token) { Desired = value; return Task.CompletedTask; }
        Task<GpuVfCurveCatalog> IGpuVfCurvePresetStore.LoadAsync(CancellationToken token) => Task.FromResult(Curves);
        Task IGpuVfCurvePresetStore.SaveAsync(GpuVfCurveCatalog value, CancellationToken token) { Curves = value; return Task.CompletedTask; }
    }
}
