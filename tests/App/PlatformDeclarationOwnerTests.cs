using Microsoft.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace Resource_Manager_APP.Tests;

public sealed class PlatformDeclarationOwnerTests
{
    private static WindowsPlatformDeclarationFacts Facts(string environment = "%CONFIGURED_ROOT%\\bin", string family = "DeclaredFamily_publisher") => new(
        [new(WindowsPlatformDeclarationReader.WindowsLicensingApplicationId, Guid.Parse("00000000-0000-0000-0000-000000000001"), 0),
         new(WindowsPlatformDeclarationReader.WindowsLicensingApplicationId, Guid.Parse("00000000-0000-0000-0000-000000000002"), 1),
         new(WindowsPlatformDeclarationReader.WindowsLicensingApplicationId, Guid.Parse("00000000-0000-0000-0000-000000000003"), 6)],
        [new("HKLM", RegistryView.Registry64, null, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path", RegistryValueKind.ExpandString, environment),
         new("HKU", RegistryView.Registry64, "S-1-5-21-123", "Environment", "Path", RegistryValueKind.String, "")],
        [new("HKU", RegistryView.Registry32, "S-1-5-21-123", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "!Deferred", RegistryValueKind.ExpandString, "%CONFIGURED_ROOT%\\start.exe /configured")],
        [new("S-1-5-21-123", "Registered_1.0.0.0_x64__publisher", family), new("S-1-5-21-123", "Registered_2.0.0.0_x64__publisher", family)]);
    private static WindowsOperatingSystemInformationOwner Owner(WindowsPlatformDeclarationReader reader, TimeProvider? clock = null)
        => new(() => new(TimeSpan.FromSeconds(7), 7),
            new(TimeSpan.FromSeconds(2), static (_, _, _) => new([], "Recorded no OS declaration")), reader, clock);

    [Fact]
    public async Task DistinctLicenseRowsRawConfigurationAndRegisteredVersionsKeepExactMeaning()
    {
        var reads = 0;
        using var owner = Owner(new((_, _) => { reads++; return Facts(); }));
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        var catalog = await backend.DiscoverAsync(default); Assert.Equal(1, reads);
        var licensing = catalog.Fields.Where(field => field.Field.FieldName == "os_all_activation_state_reported").ToArray();
        Assert.Equal(3, licensing.Length);
        Assert.Equal(new MetricInteger?[] { new(0), new(1), new(6) }, licensing.Select(field => backend.ReadCached(field.Field).Value!.Integer).OrderBy(static value => value!.Value.LowBits).ToArray());
        Assert.All(licensing, field => { Assert.Equal("enum32", field.Definition.Unit); Assert.Null(backend.ReadCached(field.Field).Value!.Boolean); });
        var environment = catalog.Fields.Where(field => field.Field.FieldName == "environment_all_variable_configured").ToArray();
        Assert.Equal(2, environment.Length); Assert.NotEqual(environment[0].Field.DeviceIdentity, environment[1].Field.DeviceIdentity);
        Assert.Contains(environment, field => backend.ReadCached(field.Field).Value!.Text == "%CONFIGURED_ROOT%\\bin");
        Assert.Contains(environment, field => backend.ReadCached(field.Field).Value!.Text == "");
        var startup = catalog.Fields.Single(field => field.Field.FieldName == "startup_all_entry_configured");
        Assert.Contains("Registry32", startup.Field.DeviceIdentity); Assert.Contains("RunOnce", startup.Field.DeviceIdentity);
        Assert.Equal("%CONFIGURED_ROOT%\\start.exe /configured", backend.ReadCached(startup.Field).Value!.Text);
        var packages = catalog.Fields.Where(field => field.Field.FieldName == "software_all_package_family_declared").ToArray();
        Assert.Equal(2, packages.Length); Assert.NotEqual(packages[0].Field.DeviceIdentity, packages[1].Field.DeviceIdentity);
        Assert.All(packages, field => Assert.Equal("DeclaredFamily_publisher", backend.ReadCached(field.Field).Value!.Text));
        _ = backend.Catalog; foreach (var field in catalog.Fields) _ = backend.ReadCached(field.Field); Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SelectedFailureClearsOnlyItsValuesAndSuccessRetiresAbsentRowsWithOneCurrent()
    {
        var requested = new List<WindowsOperatingSystemInformationFields>(); var failed = false; var empty = false;
        var clock = new Clock();
        using var owner = Owner(new((fields, _) => { requested.Add(fields); return failed ? new(null, null, null, null)
            : empty ? new([], [], [], []) : Facts("updated"); }), clock);
        var backend = owner.CreateBackend(); var catalog = await backend.DiscoverAsync(default);
        var environment = catalog.Fields.First(field => field.Field.FieldName == "environment_all_variable_configured").Field;
        var package = catalog.Fields.First(field => field.Field.FieldName == "software_all_package_family_declared").Field;
        var originalPackage = backend.ReadCached(package);
        failed = true; clock.Now = clock.Now.AddHours(1);
        var missing = (await backend.CollectAsync([environment], default)).Single();
        Assert.Equal(WindowsOperatingSystemInformationFields.Environment, requested.Last());
        Assert.Equal(FieldObservationStatus.Unavailable, missing.Status); Assert.Null(missing.Value); Assert.Null(missing.ObservedAt);
        Assert.Equal(originalPackage, backend.ReadCached(package));
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([environment]);
        failed = false; empty = true;
        var retired = await borrow.CollectAsync([environment], new Permit());
        Assert.True(retired.Committed); Assert.Equal(FieldObservationStatus.Unavailable, retired.Readings.Single().Status);
        Assert.DoesNotContain(backend.Catalog.Fields, field => field.Field.FieldName == "environment_all_variable_configured");
        Assert.Equal(originalPackage, backend.ReadCached(package));
        Assert.Null(owner.ReadInformation().FindField(environment));
    }

    [Fact]
    public async Task BorrowPublicationRevocationCancellationAndDisposePreserveOriginalLifetime()
    {
        Action? duringRead = null;
        using var owner = Owner(new((_, _) => { duringRead?.Invoke(); return Facts(); }));
        var backend = owner.CreateBackend(); var field = (await backend.DiscoverAsync(default)).Fields.First(item => item.Field.FieldName == "software_all_package_family_declared").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([field]);
        var prior = owner.ReadInformation(); var active = true;
        duringRead = () => active = false;
        Assert.False((await borrow.CollectAsync([field], new Permit(() => active))).Committed); Assert.Same(prior, owner.ReadInformation());
        using var cancellation = new CancellationTokenSource(); duringRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([field], cancellation.Token));
        Assert.Same(prior, owner.ReadInformation()); duringRead = null;
        using var signal = owner.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        var published = await borrow.CollectAsync([field], new Permit()); Assert.True(published.Committed);
        Assert.Equal(published.Readings.Single(), backend.ReadCached(field));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await signal.WaitAsync(timeout.Token);
        owner.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(async () => await backend.CollectAsync([field], default));
    }

    [Fact]
    public async Task ExistingHostSelectedBackendAndLeaseUseTheOriginalInjectedOperatingSystemOwner()
    {
        var requested = new List<WindowsOperatingSystemInformationFields>();
        using var os = Owner(new((fields, _) => { requested.Add(fields); return Facts(); }));
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = new DeviceTopologySnapshotProvider(new Topology(), new Store(), new(), sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance, new Acpi(), operatingSystemInventory: os);
        await using var cpu = new CpuTopologyProviderTestFixture();
        var api = new ProcessBasicFieldCollectorTests.Api(); using var process = ProcessBasicFieldCollectorTests.Fixture(api);
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!), provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        await provider.CaptureOperatingSystemInformationAsync(default);
        var field = host.Reads.Catalog.Fields.Single(item => item.Field.FieldName == "startup_all_entry_configured").Field;
        var result = (await host.Reads.CollectAsync([field], default)).Single();
        Assert.Equal(WindowsOperatingSystemInformationFields.Startup, requested.Last());
        Assert.Equal(result, os.CreateBackend().ReadCached(field)); Assert.Equal(result, host.Reads.ReadCached(field));
        using var lease = host.Subscriptions.Acquire("recorded-startup", [field], TimeSpan.FromSeconds(5));
        Assert.Same(os, provider.OperatingSystemInventory);
    }

    [Fact]
    public void LicenseDecoderKeepsZeroAndUnknownNumericStatusWithoutBooleanGuess()
    {
        Assert.Equal((uint)0, WindowsPlatformDeclarationReader.DecodeStatus(0u));
        Assert.Equal(uint.MaxValue, WindowsPlatformDeclarationReader.DecodeStatus(uint.MaxValue));
        Assert.Null(WindowsPlatformDeclarationReader.DecodeStatus(-1)); Assert.Null(WindowsPlatformDeclarationReader.DecodeStatus(true));
    }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Permit(Func<bool>? active = null) : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active?.Invoke() ?? true; public bool TryPublish(Action commit) { if (!IsActive) return false; commit(); return true; } }
    private sealed class Topology : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => throw new InvalidOperationException("Unexpected topology capture."); }
    private sealed class Acpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("Unexpected ACPI capture."); }
    private sealed class Store : IDeviceTopologySnapshotStore
    { public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null); public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken token) => throw new InvalidOperationException("Unexpected journal write."); }
}
