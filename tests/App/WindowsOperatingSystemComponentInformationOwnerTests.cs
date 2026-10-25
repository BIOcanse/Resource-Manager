using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsOperatingSystemComponentInformationOwnerTests
{
    private const string FeatureIdentity = "windows-os-component|feature:Feature.One";
    private const string PackageIdentity = "windows-os-component|package:Package.One";
    private const string DriverIdentity = "windows-os-component|driver-package:oem42.inf";

    [Fact]
    public void ComponentFlagsRemainDistinctFromOriginalRegisteredPackagesAndAcPowerFlags()
    {
        Assert.Equal((ushort)64, (ushort)WindowsOperatingSystemInformationFields.Packages);
        Assert.Equal((ushort)256, (ushort)WindowsOperatingSystemInformationFields.AcPower);
        Assert.Equal((ushort)1024, (ushort)WindowsOperatingSystemInformationFields.Features);
        Assert.Equal((ushort)2048, (ushort)WindowsOperatingSystemInformationFields.ComponentPackages);
        Assert.Equal((ushort)4096, (ushort)WindowsOperatingSystemInformationFields.DriverPackages);

        Assert.Equal(WindowsOperatingSystemInformationFields.Packages,
            WindowsOperatingSystemInformationOwner.SelectFields([Selection("registered-package", DeviceInformationFieldRegistry.SoftwareAllPackageFamilyDeclared)]));
        Assert.Equal(WindowsOperatingSystemInformationFields.AcPower,
            WindowsOperatingSystemInformationOwner.SelectFields([Selection(WindowsOperatingSystemInformationOwner.MachineObjectIdentity,
                DeviceInformationFieldRegistry.MachineAllAcPowerActual)]));
    }

    [Fact]
    public async Task ThreeCanonicalComponentFieldsUseSelectedReaderAndOneOriginalCurrent()
    {
        var source = new RecordedReader(new(
            [new("Feature.One", 0)],
            [new("Package.One", 2)],
            [new("oem42.inf")]));
        var scope = new WindowsOperatingSystemComponentInformationScope();
        using var owner = new WindowsOperatingSystemInformationOwner(scope, source, TimeSpan.FromSeconds(1));
        var backend = owner.CreateBackend();

        Assert.Empty(backend.Catalog.Fields);
        Assert.Empty(source.Calls);
        var selected = WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages;
        await owner.CaptureSelectedInformationAsync(selected);

        var call = Assert.Single(source.Calls);
        Assert.Equal(scope, call.Scope);
        Assert.Equal(selected, call.Selected);
        Assert.Equal(selected, WindowsOperatingSystemInformationOwner.SelectFields([
            Selection(FeatureIdentity, DeviceInformationFieldRegistry.OsAllFeatureStateActual),
            Selection(PackageIdentity, DeviceInformationFieldRegistry.OsAllPackageStateActual),
            Selection(DriverIdentity, DeviceInformationFieldRegistry.DriverAllPackageIdentityDeclared)]));

        var feature = Capability(backend, FeatureIdentity, DeviceInformationFieldRegistry.OsAllFeatureStateActual);
        var package = Capability(backend, PackageIdentity, DeviceInformationFieldRegistry.OsAllPackageStateActual);
        var driver = Capability(backend, DriverIdentity, DeviceInformationFieldRegistry.DriverAllPackageIdentityDeclared);
        Assert.Equal("os_feature", backend.Catalog.Devices.Single(item => item.Identity == FeatureIdentity).Resource);
        Assert.Equal("os_package", backend.Catalog.Devices.Single(item => item.Identity == PackageIdentity).Resource);
        Assert.Equal("driver_package", backend.Catalog.Devices.Single(item => item.Identity == DriverIdentity).Resource);
        Assert.Equal("enum", feature.Definition.Unit);
        Assert.Equal("enum", package.Definition.Unit);
        Assert.Equal("text", driver.Definition.Unit);
        Assert.Equal("windows.dism.features", feature.ReadSource);
        Assert.Equal("windows.dism.packages", package.ReadSource);
        Assert.Equal("windows.dism.driver-packages", driver.ReadSource);
        Assert.Equal("unsigned128", feature.Definition.ValueKind);
        Assert.Equal("unsigned128", package.Definition.ValueKind);
        Assert.Equal("string", driver.Definition.ValueKind);
        Assert.Equal("os_feature", Assert.Single(owner.ReadInformation().Devices, item => item.DeviceIdentity == FeatureIdentity).ObjectType);
        Assert.Equal("os_package", Assert.Single(owner.ReadInformation().Devices, item => item.DeviceIdentity == PackageIdentity).ObjectType);
        Assert.Equal("driver_package", Assert.Single(owner.ReadInformation().Devices, item => item.DeviceIdentity == DriverIdentity).ObjectType);

        var originalCurrent = owner.ReadInformation();
        var zero = backend.ReadCached(feature.Field);
        var packageReading = backend.ReadCached(package.Field);
        var driverReading = backend.ReadCached(driver.Field);
        Assert.Equal(FieldObservationStatus.Current, zero.Status);
        Assert.Equal(MetricInteger.Parse("0"), zero.Value!.Integer);
        Assert.Equal(FieldObservationStatus.Current, packageReading.Status);
        Assert.Equal(MetricInteger.Parse("2"), packageReading.Value!.Integer);
        Assert.Equal(FieldObservationStatus.Current, driverReading.Status);
        Assert.Equal("oem42.inf", driverReading.Value!.Text);
        Assert.NotNull(zero.ObservedAt);
        Assert.Same(originalCurrent, owner.ReadInformation());

        var callsBeforeCache = source.Calls.Count;
        Assert.Equal(zero, backend.ReadCached(feature.Field));
        Assert.Equal(callsBeforeCache, source.Calls.Count);
        var direct = Assert.Single(await backend.CollectAsync([feature.Field], default));
        Assert.Equal(direct, backend.ReadCached(feature.Field));
        Assert.Equal(WindowsOperatingSystemInformationFields.Features, source.Calls.Last().Selected);
        Assert.Equal(packageReading, backend.ReadCached(package.Field));

        using var borrow = owner.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([package.Field]));
        Assert.Equal(WindowsOperatingSystemInformationOwner.CollectionItemId, Assert.Single(binding.CollectionItemIds));
        Assert.Equal(WindowsOperatingSystemInformationOwner.CollectionItemId, Assert.Single(binding.PublicationKeys));
        var borrowed = await borrow.CollectAsync([package.Field], new Permit(true));
        Assert.True(borrowed.Committed);
        Assert.Equal(package.Field, Assert.Single(borrowed.Readings).Field);
        Assert.Equal(Assert.Single(borrowed.Readings), backend.ReadCached(package.Field));
        Assert.Equal(PackageIdentity, package.Field.DeviceIdentity);
        owner.Dispose();
        Assert.Equal(0, source.DisposeCalls); // Caller-supplied reader remains caller-owned.
    }

    [Fact]
    public async Task FailedCategoriesRemainKnownButUnavailableAndSuccessfulEmptyRetiresObjects()
    {
        var source = new RecordedReader(new(
            [new("Feature.One", 0)],
            [new("Package.One", 1)],
            [new("oem42.inf")]));
        using var owner = new WindowsOperatingSystemInformationOwner(new(), source, TimeSpan.FromSeconds(1));
        var backend = owner.CreateBackend();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages);
        var feature = new FieldReference(FeatureIdentity, DeviceInformationFieldRegistry.OsAllFeatureStateActual.Name);
        var driver = new FieldReference(DriverIdentity, DeviceInformationFieldRegistry.DriverAllPackageIdentityDeclared.Name);

        source.Value = new(null, [], null);
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages);

        var current = owner.ReadInformation();
        var retainedFeature = Assert.Single(current.Devices, item => item.DeviceIdentity == FeatureIdentity);
        var retainedDriver = Assert.Single(current.Devices, item => item.DeviceIdentity == DriverIdentity);
        Assert.Null(Assert.Single(retainedFeature.Fields).Value);
        Assert.Null(Assert.Single(retainedFeature.Fields).ObservedAt);
        Assert.Null(Assert.Single(retainedDriver.Fields).Value);
        Assert.Null(Assert.Single(retainedDriver.Fields).ObservedAt);
        Assert.DoesNotContain(current.Devices, item => item.DeviceIdentity == PackageIdentity);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(feature).Status);
        Assert.Null(backend.ReadCached(feature).ObservedAt);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(driver).Status);
    }

    [Fact]
    public async Task CancellationBeforeProjectionAndRetiredPublicationPermitDoNotReplaceOriginalCurrent()
    {
        var source = new RecordedReader(new([new("Feature.One", 0)], null, null));
        using var owner = new WindowsOperatingSystemInformationOwner(new(
            WindowsOperatingSystemInformationFields.Features), source, TimeSpan.FromSeconds(1));
        var backend = owner.CreateBackend();
        var field = new FieldReference(FeatureIdentity, DeviceInformationFieldRegistry.OsAllFeatureStateActual.Name);
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features);
        var previous = owner.ReadInformation();
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([field]);

        using var canceledDuringRead = new CancellationTokenSource();
        source.AfterRead = canceledDuringRead.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features, canceledDuringRead.Token));
        Assert.Same(previous, owner.ReadInformation());

        source.AfterRead = null;
        using var canceledAtCommit = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await borrow.CollectAsync([field], new CancelAtPublicationPermit(canceledAtCommit), canceledAtCommit.Token));
        Assert.Same(previous, owner.ReadInformation());

        var rejected = await borrow.CollectAsync([field], new Permit(false));
        Assert.False(rejected.Committed);
        Assert.Same(previous, owner.ReadInformation());
        Assert.Equal(MetricInteger.Parse("0"), backend.ReadCached(field).Value!.Integer);
    }

    private static FieldCapability Capability(InformationReadWriteBackend backend, string identity,
        DeviceInformationDefinition definition)
        => Assert.Single(backend.Catalog.Fields, field => field.Field == new FieldReference(identity, definition.Name));

    private static InformationFieldSelection Selection(string identity, DeviceInformationDefinition definition)
        => new(new(identity, definition.Name), definition);

    private sealed class RecordedReader(WindowsOperatingSystemComponentInformationReadResult value)
        : IWindowsOperatingSystemComponentInformationReader, IDisposable
    {
        private readonly object gate = new();
        private WindowsOperatingSystemComponentInformationReadResult current = value;
        private readonly List<Call> calls = [];
        private int disposeCalls;
        internal WindowsOperatingSystemComponentInformationReadResult Value
        {
            get { lock (gate) return current; }
            set { lock (gate) current = value; }
        }
        internal IReadOnlyList<Call> Calls { get { lock (gate) return calls.ToArray(); } }
        internal Action? AfterRead { get; set; }
        internal int DisposeCalls => Volatile.Read(ref disposeCalls);

        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsOperatingSystemComponentInformationReadResult reading;
            lock (gate)
            {
                calls.Add(new(scope, selected));
                reading = current;
            }
            AfterRead?.Invoke();
            return new(
                (selected & WindowsOperatingSystemInformationFields.Features) != 0 ? reading.Features : null,
                (selected & WindowsOperatingSystemInformationFields.ComponentPackages) != 0 ? reading.Packages : null,
                (selected & WindowsOperatingSystemInformationFields.DriverPackages) != 0 ? reading.DriverPackages : null);
        }

        public void Dispose() => Interlocked.Increment(ref disposeCalls);
    }

    private sealed record Call(WindowsOperatingSystemComponentInformationScope Scope,
        WindowsOperatingSystemInformationFields Selected);

    private sealed class Permit(bool active) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit)
        {
            if (!active) return false;
            commit();
            return true;
        }
    }

    private sealed class CancelAtPublicationPermit(CancellationTokenSource cancellation) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit)
        {
            cancellation.Cancel();
            commit();
            return true;
        }
    }
}
