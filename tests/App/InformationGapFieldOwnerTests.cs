using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class InformationGapFieldOwnerTests
{
    [Fact]
    public void EstimatedSizeUsesUnsignedDwordKilobytesAndRejectsOtherRegistryKinds()
    {
        Assert.Equal(1024UL, WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes(1, RegistryValueKind.DWord));
        Assert.Equal(0UL, WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes(0, RegistryValueKind.DWord));
        Assert.Equal((ulong)uint.MaxValue * 1024, WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes(-1, RegistryValueKind.DWord));
        Assert.Null(WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes(null, RegistryValueKind.DWord));
        Assert.Null(WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes("12", RegistryValueKind.String));
        Assert.Null(WindowsInstalledSoftwareInventory.DecodeEstimatedSizeBytes(12L, RegistryValueKind.QWord));
    }

    [Fact]
    public async Task SoftwareSizeSharesOriginalInventoryCurrentAndFailureIsNotZero()
    {
        var reads = 0;
        var entry = new InstalledSoftwareEntry("legacy", "App", null, null, null, [], null, "HKLM/64/A", 4096);
        var inventory = new WindowsInstalledSoftwareInventory(_ => { reads++; return [entry]; });
        var backend = inventory.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(4, catalog.Fields.Length);
        var size = Assert.Single(catalog.Fields, field => field.Field.FieldName == "software_all_reported_size");
        Assert.Equal("B", size.Definition.Unit);
        Assert.Equal("unsigned128", size.Definition.ValueKind);
        Assert.Equal(MetricInteger.Parse("4096"), backend.ReadCached(size.Field).Value!.Integer);
        Assert.Equal(1, reads);
        using var scope = inventory.BorrowSubscriptionCapture();
        _ = scope.Bind([size.Field]);
        var collected = await scope.CollectAsync([size.Field], new Permit(), default);
        Assert.True(collected.Committed);
        Assert.Equal(Assert.Single(collected.Readings), backend.ReadCached(size.Field));
        entry = entry with { ReportedSizeBytes = null };
        var missing = await backend.CollectAsync([size.Field], default);
        Assert.Equal(FieldObservationStatus.Unavailable, missing[0].Status);
        Assert.Null(missing[0].Value);
    }

    [Fact]
    public void NativeEnumerationRetainsStoppedPidZeroAndExplicitScopeWithAllResourcesReleased()
    {
        var native = new RecordedNative();
        var reader = new WindowsServiceReader(native);
        var facts = reader.Read();
        Assert.True(facts.Available);
        Assert.Equal(0x30u, native.LastType);
        Assert.Equal(4u, native.ManagerAccess);
        Assert.Equal(0, native.ConfigCalls);
        var stopped = Assert.Single(facts.Services, service => service.ServiceName == "Service-A");
        Assert.Equal(0u, stopped.ProcessId);
        Assert.Equal(1u, stopped.State);
        var snapshot = ServiceInformationFieldProjection.Create(facts.Services, DateTimeOffset.UtcNow);
        Assert.Contains(snapshot.Devices, device => device.DeviceIdentity == "windows-service|service-a");
        Assert.Contains(snapshot.Devices, device => device.DeviceIdentity == "windows-service|service_a");
        var pid = snapshot.FindField(new("windows-service|service-a", "service_all_process_pid_actual"));
        Assert.Equal("0", pid!.Value!.Value.GetString());
        _ = reader.Read(WindowsServiceScope.Drivers);
        Assert.Equal(0x0Bu, native.LastType);
        _ = reader.Read(WindowsServiceScope.Win32AndDrivers);
        Assert.Equal(0x3Bu, native.LastType);
        Assert.Equal(native.Allocated, native.Freed);
        var selected = new RecordedNative();
        var selectedFacts = new WindowsServiceReader(selected).Read(includeConfiguration: true,
            configurationServiceNames: new HashSet<string> { "service-a" });
        Assert.Equal(2u, selectedFacts.Services[0].StartType);
        Assert.Null(selectedFacts.Services[1].StartType);
        Assert.Equal(2, selected.ConfigCalls);
        Assert.Equal(1, selected.ServiceClosed);
        Assert.Equal(3, native.ManagerClosed);
    }

    [Fact]
    public void ConfigPermissionFailureKeepsStatusAndPidAndClosesSuccessfulHandles()
    {
        var native = new RecordedNative { DeniedService = "Service-A" };
        var facts = new WindowsServiceReader(native).Read(includeConfiguration: true);
        Assert.True(facts.Available);
        Assert.Equal(5u, native.ManagerAccess);
        Assert.Null(facts.Services[0].StartType);
        Assert.Equal(0u, facts.Services[0].ProcessId);
        Assert.Equal(2u, facts.Services[1].StartType);
        Assert.Equal(1u, native.ServiceAccess);
        Assert.Equal(2, native.ConfigCalls);
        Assert.Equal(1, native.ServiceClosed);
        Assert.Equal(1, native.ManagerClosed);
        Assert.Equal(native.Allocated, native.Freed);
    }

    [Fact]
    public void EnumerationFailureAndCancellationReleaseNativeBufferAndManager()
    {
        var failed = new RecordedNative { FailEnumeration = true };
        Assert.False(new WindowsServiceReader(failed).Read().Available);
        Assert.Equal(1, failed.Freed);
        Assert.Equal(1, failed.ManagerClosed);
        using var cancellation = new CancellationTokenSource();
        var canceled = new RecordedNative { AfterEnumeration = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => new WindowsServiceReader(canceled).Read(cancellationToken: cancellation.Token));
        Assert.Equal(canceled.Allocated, canceled.Freed);
        Assert.Equal(1, canceled.ManagerClosed);
    }

    [Fact]
    public async Task CanonicalOwnerQueriesConfigurationOnlyWhenSelectedAndFailureClearsKnownValues()
    {
        var configured = new List<bool>();
        var failed = false;
        using var owner = new WindowsServiceInformationOwner(WindowsServiceScope.Win32, (includeConfig, _) =>
        {
            configured.Add(includeConfig);
            return failed ? new(false, [], 5) : new(true, [new("Svc", "Original display", 0x10, 1, 0, includeConfig ? 3u : null)]);
        });
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(new[] { false }, configured);
        Assert.Equal(3, catalog.Fields.Length);
        var state = Assert.Single(catalog.Fields, item => item.Field.FieldName == "service_all_state_actual");
        var start = Assert.Single(catalog.Fields, item => item.Field.FieldName == "service_all_start_type_configured");
        Assert.Null(backend.ReadCached(start.Field).Value);
        Assert.Equal("Original display", owner.ReadServices()!.Services[0].DisplayName);
        Assert.Equal("unsigned128", state.Definition.ValueKind);
        _ = backend.Catalog.Require(start.Field);
        Assert.Single(configured);
        var values = await backend.CollectAsync([start.Field], default);
        Assert.Equal(MetricInteger.Parse("3"), values[0].Value!.Integer);
        Assert.True(configured[^1]);
        failed = true;
        var cleared = await backend.CollectAsync([state.Field], default);
        Assert.Equal(FieldObservationStatus.Unavailable, cleared[0].Status);
        Assert.Null(cleared[0].Value);
        Assert.Equal(catalog.Fields.Select(static field => field.Field), backend.Catalog.Fields.Select(static field => field.Field));
    }

    [Fact]
    public async Task RetiredPermitAndCanceledReadRetainCurrentWhileRemovedBoundServiceIsUnavailable()
    {
        var removed = false;
        using var owner = new WindowsServiceInformationOwner(WindowsServiceScope.Win32,
            (_, _) => new(true, removed ? [] : [new("Svc", "Svc", 0x10, 4, 42, null)]));
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        var field = catalog.Fields[0].Field;
        var before = owner.ReadInformation();
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([field]);
        removed = true;
        var retired = await borrow.CollectAsync([field], new Permit(false));
        Assert.False(retired.Committed);
        Assert.Same(before, owner.ReadInformation());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync([field], new Permit(), canceled.Token));
        Assert.Same(before, owner.ReadInformation());
        var vanished = await borrow.CollectAsync([field], new Permit());
        Assert.True(vanished.Committed);
        Assert.Equal(FieldObservationStatus.Unavailable, vanished.Readings[0].Status);
        borrow.SynchronizeDemand([field]); // Original metadata remains valid after directory retirement.
    }

    [Fact]
    public async Task OriginalNativeCompositionSubscribesSameServiceCurrentAndSelectedConfig()
    {
        var configured = false;
        using var owner = new WindowsServiceInformationOwner(WindowsServiceScope.Win32, (includeConfiguration, _) =>
        {
            configured = includeConfiguration;
            return new(true, [new("Svc", "Svc", 0x10, 1, 0, includeConfiguration ? 3u : null)]);
        });
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        Assert.False(configured);
        var field = Assert.Single(catalog.Fields, item => item.Field.FieldName == "service_all_start_type_configured").Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected native worker failure", failure.Error), TimeSpan.Zero);
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("service-config", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(configured);
        Assert.Equal(MetricInteger.Parse("3"), stream.Current[0].Value!.Integer);
        var delivered = Assert.Single(stream.Current);
        var current = backend.ReadCached(field);
        Assert.NotNull(delivered.ObservedAt);
        Assert.True(current.ObservedAt >= delivered.ObservedAt);
        Assert.Equal(delivered with { ObservedAt = current.ObservedAt }, current);
        await composition.StopAsync();
        Assert.False(await stream.MoveNextAsync());
        Assert.Equal(MetricInteger.Parse("0"), (await backend.CollectAsync([new("windows-service|svc", "service_all_process_pid_actual")], default))[0].Value!.Integer);
    }

    [Fact]
    public async Task OriginalNativeCompositionSubscribesSameSoftwareCurrentWithoutAnotherCollector()
    {
        var inventory = new WindowsInstalledSoftwareInventory(_ =>
            [new("legacy", "App", null, null, null, [], null, "HKLM/64/A", 1024)]);
        var backend = inventory.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        var field = Assert.Single(catalog.Fields, item => item.Field.FieldName == "software_all_reported_size").Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(inventory.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected native worker failure", failure.Error), TimeSpan.Zero);
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("size", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(MetricInteger.Parse("1024"), stream.Current[0].Value!.Integer);
        var delivered = Assert.Single(stream.Current);
        var current = backend.ReadCached(field);
        Assert.NotNull(delivered.ObservedAt);
        Assert.True(current.ObservedAt >= delivered.ObservedAt);
        Assert.Equal(delivered with { ObservedAt = current.ObservedAt }, current);
        await composition.StopAsync();
        Assert.False(await stream.MoveNextAsync());
        Assert.Equal(MetricInteger.Parse("1024"), (await backend.CollectAsync([field], default))[0].Value!.Integer);
    }

    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
    private sealed class RecordedNative : IWindowsServiceNativeApi
    {
        private readonly Dictionary<IntPtr, List<IntPtr>> entryPointers = [];
        internal string? DeniedService;
        internal bool FailEnumeration;
        internal Action? AfterEnumeration;
        internal uint LastType, ManagerAccess, ServiceAccess;
        internal int Allocated, Freed, ManagerClosed, ServiceClosed, ConfigCalls;
        public int LastError { get; private set; }
        public IntPtr OpenManager(uint access) { ManagerAccess = access; return new(1); }
        public bool Enumerate(IntPtr manager, uint type, IntPtr buffer, uint size, out uint bytes, out uint count, ref uint resume)
        {
            LastType = type; bytes = (uint)(Marshal.SizeOf<EnumServiceStatusProcess>() * 2); count = 2;
            if (buffer == IntPtr.Zero) { LastError = 234; return false; }
            if (FailEnumeration) { LastError = 5; return false; }
            var pointers = new List<IntPtr>();
            for (var index = 0; index < 2; index++)
            {
                var pointer = IntPtr.Add(buffer, index * Marshal.SizeOf<EnumServiceStatusProcess>());
                Marshal.StructureToPtr(new EnumServiceStatusProcess
                {
                    ServiceName = index == 0 ? "Service-A" : "Service_A", DisplayName = "Original display",
                    Status = new() { ServiceType = 0x10, CurrentState = index == 0 ? 1u : 4u, ProcessId = index == 0 ? 0u : 42u }
                }, pointer, false);
                pointers.Add(pointer);
            }
            entryPointers.Add(buffer, pointers);
            LastError = 0; AfterEnumeration?.Invoke(); return true;
        }
        public IntPtr OpenService(IntPtr manager, string name, uint access)
        { ServiceAccess = access; LastError = name == DeniedService ? 5 : 0; return name == DeniedService ? IntPtr.Zero : new(2); }
        public bool QueryConfiguration(IntPtr service, IntPtr buffer, uint size, out uint bytes)
        {
            ConfigCalls++; bytes = (uint)Marshal.SizeOf<QueryServiceConfig>();
            if (buffer == IntPtr.Zero) { LastError = 122; return false; }
            Marshal.StructureToPtr(new QueryServiceConfig { StartType = 2 }, buffer, false); LastError = 0; return true;
        }
        public IntPtr Allocate(int size) { Allocated++; return Marshal.AllocHGlobal(size); }
        public void Free(IntPtr buffer)
        {
            if (entryPointers.Remove(buffer, out var pointers))
                foreach (var pointer in pointers) Marshal.DestroyStructure<EnumServiceStatusProcess>(pointer);
            Freed++; Marshal.FreeHGlobal(buffer);
        }
        public void Close(IntPtr handle) { if (handle == new IntPtr(1)) ManagerClosed++; else ServiceClosed++; }
    }
}
