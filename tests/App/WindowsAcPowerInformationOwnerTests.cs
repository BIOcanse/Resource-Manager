using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsAcPowerInformationOwnerTests
{
    private static readonly FieldReference AcPowerField = new("machine", "machine_all_ac_power_actual");

    [Fact]
    public async Task ExplicitAcPowerCaptureMapsDocumentedAndUnexpectedStatusWithoutOtherReads()
    {
        foreach (var (raw, expected) in new (byte? Raw, bool? Expected)[]
        {
            (0, false), (1, true), (255, null), (2, null), (null, null)
        })
        {
            var reader = new RecordedReader(raw is { } value ? new WindowsAcPowerStatusReading(value) : null);
            var queries = new List<string>(); var uptimeReads = 0; var platformReads = new List<WindowsOperatingSystemInformationFields>();
            using var owner = CreateOwner(reader, queries, platformReads,
                () => { uptimeReads++; return new(TimeSpan.FromSeconds(30), 30); });
            var backend = owner.CreateBackend();

            Assert.Empty(backend.Catalog.Fields);
            Assert.Equal(0, reader.Reads); Assert.Equal(0, uptimeReads); Assert.Empty(queries); Assert.Empty(platformReads);
            await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower);

            var capability = Assert.Single(backend.Catalog.Fields.Where(field => field.Definition.Name == AcPowerField.FieldName));
            Assert.Equal(AcPowerField, capability.Field);
            Assert.Equal("machine", capability.Field.DeviceIdentity);
            Assert.Equal("machine", backend.Catalog.Devices.Single(device => device.Identity == "machine").Resource);
            Assert.Equal("boolean", capability.Definition.ValueKind);
            Assert.Equal("state", capability.Definition.Unit);
            Assert.Equal("windows.get-system-power-status", capability.ReadSource);
            Assert.True(capability.Read.Supported); Assert.False(capability.Write.Supported);
            Assert.Equal(1, reader.Reads); Assert.Equal(0, uptimeReads); Assert.Empty(queries); Assert.Empty(platformReads);

            var cached = backend.ReadCached(AcPowerField);
            if (expected is { } known)
            {
                Assert.Equal(FieldObservationStatus.Current, cached.Status);
                Assert.Equal(known, cached.Value!.Boolean);
                Assert.NotNull(cached.ObservedAt);
            }
            else
            {
                Assert.Equal(FieldObservationStatus.Unavailable, cached.Status);
                Assert.Null(cached.Value); Assert.Null(cached.ObservedAt);
            }
            Assert.Equal(1, reader.Reads); // Catalog and cached reads do not invoke the source.
        }
    }

    [Fact]
    public async Task UnrelatedCaptureDoesNotCreateMachineObjectAndPreservesPreviouslyCapturedAcPower()
    {
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(1));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Uptime);
        Assert.DoesNotContain(owner.ReadInformation().Devices, device => device.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineObjectIdentity);
        Assert.Equal(0, reader.Reads);

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower);
        var before = backend.ReadCached(AcPowerField);
        Assert.Equal(FieldObservationStatus.Current, before.Status);
        Assert.True(before.Value!.Boolean);
        var readsAfterAcCapture = reader.Reads;

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Uptime);
        var after = backend.ReadCached(AcPowerField);
        Assert.Single(owner.ReadInformation().Devices, device => device.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineObjectIdentity);
        Assert.Equal(before, after);
        Assert.Equal(readsAfterAcCapture, reader.Reads);
    }

    [Fact]
    public async Task FailedSelectedReadClearsOnlyAcPowerAndPreservesEveryOtherOsAndPlatformField()
    {
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(1));
        var queries = new List<string>(); var platformReads = new List<WindowsOperatingSystemInformationFields>();
        using var owner = CreateOwner(reader, queries, platformReads,
            () => new(TimeSpan.FromSeconds(30), 30));
        var backend = owner.CreateBackend();
        await owner.CaptureInformationAsync();
        var fields = backend.Catalog.Fields;
        var unselected = fields.Where(field => field.Field != AcPowerField).Select(field => field.Field).ToArray();
        Assert.NotEmpty(unselected);
        var before = unselected.Select(backend.ReadCached).ToArray();
        var priorAc = backend.ReadCached(AcPowerField);
        Assert.Equal(FieldObservationStatus.Current, priorAc.Status);
        Assert.Equal(true, priorAc.Value!.Boolean);
        var priorQueryCount = queries.Count;
        var priorPlatformReadCount = platformReads.Count;

        reader.Value = null; // GetSystemPowerStatus failure is represented as a missing typed reading.
        var failed = Assert.Single(await backend.CollectAsync([AcPowerField], default));

        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value); Assert.Null(failed.ObservedAt);
        Assert.Equal(failed, backend.ReadCached(AcPowerField));
        Assert.All(before, reading => Assert.Equal(reading, backend.ReadCached(reading.Field)));
        Assert.Equal(priorQueryCount, queries.Count); // AC-only capture issues no WMI query.
        Assert.Equal(priorPlatformReadCount, platformReads.Count); // AC-only capture selects no platform category.
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task CancellationAndRejectedSubscriptionPermitLeaveOriginalCurrentUntouched()
    {
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(0));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower);
        var prior = owner.ReadInformation();
        var priorReading = backend.ReadCached(AcPowerField);
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([AcPowerField]);

        reader.Value = new WindowsAcPowerStatusReading(1);
        using var canceled = new CancellationTokenSource();
        reader.AfterRead = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await borrow.CollectAsync([AcPowerField], new Permit(true), canceled.Token));
        Assert.Same(prior, owner.ReadInformation());
        Assert.Equal(priorReading, backend.ReadCached(AcPowerField));

        reader.AfterRead = null;
        var rejected = await borrow.CollectAsync([AcPowerField], new Permit(false));
        Assert.False(rejected.Committed);
        Assert.Same(prior, owner.ReadInformation());
        Assert.Equal(priorReading, backend.ReadCached(AcPowerField));
    }

    [Fact]
    public async Task RoleTwoCompositionUsesSameCurrentAndOwnerRemainsDirectlyCollectibleAfterStop()
    {
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(1));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower);
        var field = Assert.Single(backend.Catalog.Fields.Where(capability => capability.Field == AcPowerField)).Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
            in configuration, failure => throw new InvalidOperationException("Unexpected role-2 worker failure", failure.Error));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("machine-ac-power", [field], TimeSpan.FromSeconds(1),
            new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var first = Assert.Single(stream.Current);
        Assert.Equal(FieldObservationStatus.Current, first.Status);
        Assert.Equal(true, first.Value!.Boolean);
        Assert.Equal(first, backend.ReadCached(field));

        reader.Value = new WindowsAcPowerStatusReading(0);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var next = Assert.Single(stream.Current);
        Assert.Equal(false, next.Value!.Boolean);
        Assert.Equal(next, backend.ReadCached(field));

        await composition.StopAsync();
        Assert.False(await stream.MoveNextAsync());
        var direct = Assert.Single(await backend.CollectAsync([field], timeout.Token));
        Assert.Equal(FieldObservationStatus.Current, direct.Status);
        Assert.Equal(false, direct.Value!.Boolean);
        Assert.Equal(direct, backend.ReadCached(field));
        Assert.True(reader.Reads >= 3);
    }

    [Fact]
    public void SystemPowerStatusUsesTheWindowsSdkTwelveByteLayout()
    {
        Assert.Equal(12, Marshal.SizeOf<WindowsAcPowerStatusReader.SystemPowerStatus>());
        Assert.Equal(0, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("ACLineStatus").ToInt32());
        Assert.Equal(1, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("BatteryFlag").ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("BatteryLifePercent").ToInt32());
        Assert.Equal(3, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("SystemStatusFlag").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("BatteryLifeTime").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<WindowsAcPowerStatusReader.SystemPowerStatus>("BatteryFullLifeTime").ToInt32());
    }

    private static WindowsOperatingSystemInformationOwner CreateOwner(RecordedReader reader,
        ICollection<string>? queries = null,
        ICollection<WindowsOperatingSystemInformationFields>? platformReads = null,
        Func<WindowsMachineUptime?>? uptime = null)
    {
        var declarations = new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, query, _) =>
        {
            queries?.Add(query);
            return new([new Dictionary<string, object?>
            {
                ["BuildNumber"] = "26100",
                ["LastBootUpTime"] = "20240229120000.123456+330"
            }], null);
        });
        var platformFacts = RecordedPlatformFacts();
        var platformDeclarations = new WindowsPlatformDeclarationReader((selected, _) =>
        {
            platformReads?.Add(selected);
            return platformFacts;
        });
        return new(uptime ?? (() => new(TimeSpan.FromSeconds(30), 30)), declarations, platformDeclarations,
            acPowerStatusReader: reader);
    }

    private static WindowsPlatformDeclarationFacts RecordedPlatformFacts()
    {
        var application = new Guid("55c92734-d682-4d71-983e-d6ec3f16059f");
        var product = new Guid("b1f8c327-ec84-46c8-9e9e-1ea2640d6d80");
        return new(
            [new(application, product, 1)],
            [new("machine", RegistryView.Default, null, "Environment", "Path", RegistryValueKind.String, "C:\\Windows")],
            [new("machine", RegistryView.Default, null, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "App", RegistryValueKind.String, "app.exe")],
            [new("S-1-5-18", "Package_1.0_x64__publisher", "Package.publisher")])
        {
            Sessions = [new(1, 132000000000000000, "console", "DOMAIN", "user", WindowsSessionConnectionState.Active)],
            StartupAdditional = [new(WindowsStartupConfigurationSource.StartupFolderFile, "machine", "C:\\ProgramData\\Startup\\app.lnk", "app.exe")]
        };
    }

    private sealed class RecordedReader(WindowsAcPowerStatusReading? value) : IWindowsAcPowerStatusReader
    {
        private readonly object gate = new();
        private WindowsAcPowerStatusReading? current = value;
        private int reads;
        public WindowsAcPowerStatusReading? Value
        {
            get { lock (gate) return current; }
            set { lock (gate) current = value; }
        }
        public Action? AfterRead { get; set; }
        public int Reads => Volatile.Read(ref reads);

        public WindowsAcPowerStatusReading? Read()
        {
            Interlocked.Increment(ref reads);
            var current = Value;
            AfterRead?.Invoke();
            return current;
        }
    }

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
}
