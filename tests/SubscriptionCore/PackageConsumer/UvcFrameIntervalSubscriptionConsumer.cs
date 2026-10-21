using System.Collections.Immutable;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Usb;
using System.Runtime.CompilerServices;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class UvcFrameIntervalSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedSource();
        using var owner = source.CreateOwner();
        var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "construction remains passive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await backend.DiscoverAsync(timeout.Token);
        var field = new FieldReference(RecordedSource.Identity, "camera_all_frame_interval_capability");
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000 };
        var failures = new List<SubscriptionWorkerFailure>();
        await using (var subscription = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(timeout.Token);
            await using var stream = subscription.SubscribeAsync("recorded-uvc", [field],
                TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "original native composition delivers the selected field");
            var reading = stream.Current.Single();
            Verify(reading);
            await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "stop drains the real subscription");
            var cached = backend.ReadCached(field);
            Require(cached.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && cached.ObservedAt == reading.ObservedAt, "subscription and original owner share current");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], timeout.Token)).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "stopped subscription leaves direct owner usable");
        source.Fail = false;
        Verify((await backend.CollectAsync([field], timeout.Token)).Single());
        Console.WriteLine("[UVC-INTERVAL-SUB-RECORDED] native-composition; full-list/default; same-current; stop/drain; direct-recovery; no-live-camera");
    }

    private sealed class RecordedSource : IDeviceTopologyReader
    {
        internal const string Identity = @"windows-pnp|USB\RECORDED_UVC";
        private const string DriverKey = "recorded-uvc-driver";
        private readonly byte[] configuration = Convert.FromBase64String("CQJPAAEBAIAyCQQCAQAOAgAAGyQEAwFZVVkyAAAAAAAAAAAAAAAAEAEAAAAAIiQFAQCABzgEAAAAAAAAAAAAAAAAKywKAAJjFwUAKywKAA==");
        private IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> captured = [];
        internal int Reads { get; private set; }
        internal bool Fail { get; set; }
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Reads++;
            if (Fail) throw new InvalidOperationException("recorded UVC descriptor failure");
            captured = UsbDescriptorParser.ParseUvcCameraModes(configuration);
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
        }
        internal DeviceTopologyCaptureOwner CreateOwner()
            => new(new NoAcpi(), new(this,
                _ => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(configuration)),
                (_, time) => Project(time), static (_, _) => Task.CompletedTask,
                static _ => new("recorded-uvc-failure", []),
                static (_, _, _, _) => { }, static _ => { }, static () => { }));
        private DeviceInformationSnapshot Project(DateTimeOffset? time)
        {
            var descriptor = new DeviceTopologyUsbDescriptor("USB 2.0", "1.0", "UVC", null, "Recorded", null,
                [], [], [], captured);
            var port = new DeviceTopologyUsbPort("recorded-hub", 7, true, 1, "HighSpeed", 1, 2, 3, false,
                DriverKey, null, null, descriptor);
            var native = new DeviceTopologyNativeDevice(Identity["windows-pnp|".Length..], null, null, null,
                null, DriverKey, null, null, null, null, [], [], [], null, null, null);
            return WindowsUsbHubIoctlReader.ProjectFields(new([port],
                new Dictionary<string, DeviceTopologyUsbPort>(StringComparer.OrdinalIgnoreCase) { [WindowsUsbHubIoctlReader.NormalizeDriverKey(DriverKey)] = port }, null), [native], time);
        }
    }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("UVC does not query ACPI"); }
    private static void Require(bool value, string contract)
    { if (!value) throw new InvalidOperationException("UVC recorded consumer: " + contract); }
    private static void Verify(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is not null,
            "original public current is structured");
        var row = reading.Value!.Structure!.Value[0];
        Require(row.GetProperty("usbDeviceIdentity").GetString() == RecordedSource.Identity, "exact parent PnP identity");
        var evidence = row.GetProperty("frameIntervalsDeclared");
        Require(evidence.GetProperty("interfaceNumber").GetByte() == 2
            && evidence.GetProperty("alternateSetting").GetByte() == 1
            && evidence.GetProperty("formatIndex").GetByte() == 3
            && evidence.GetProperty("frameIndex").GetByte() == 1
            && evidence.GetProperty("defaultFrameInterval100Nanoseconds").GetUInt32() == 666667
            && evidence.GetProperty("discreteIntervals100Nanoseconds").EnumerateArray()
                .Select(static item => item.GetUInt32()).SequenceEqual(new uint[] { 333667, 666667 }),
            "complete raw integer list/default and descriptor directory identities");
    }
}
