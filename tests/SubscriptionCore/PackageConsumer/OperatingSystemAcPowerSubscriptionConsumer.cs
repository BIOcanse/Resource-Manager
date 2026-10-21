using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class OperatingSystemAcPowerSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(1));
        using var owner = new WindowsOperatingSystemInformationOwner(reader, TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower, timeout.Token);
        var backend = owner.CreateBackend();
        var field = backend.Catalog.Fields.Single(capability => capability.Definition.Name == "machine_all_ac_power_actual").Field;
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
            Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
            in configuration, failure => throw new InvalidOperationException("Unexpected role-2 AC-power worker failure", failure.Error),
            TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("machine-ac-power", [field], TimeSpan.FromSeconds(1),
            new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Role-2 package stream ended before its first sample.");
        var online = stream.Current.Single();
        FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(online, backend.ReadCached(field));
        if (online.Status != FieldObservationStatus.Current || online.Value?.Boolean != true
            || reader.Reads < 2)
            throw new InvalidDataException("Role-2 subscription did not publish the recorded online state to the same current.");

        reader.Value = new WindowsAcPowerStatusReading(0);
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Role-2 package stream ended before the recorded offline update.");
        var offline = stream.Current.Single();
        FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(offline, backend.ReadCached(field));
        if (offline.Status != FieldObservationStatus.Current || offline.Value?.Boolean != false)
            throw new InvalidDataException("Role-2 subscription diverged from the recorded offline current.");

        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Stopped role-2 composition emitted a late AC-power reading.");
        var direct = (await backend.CollectAsync([field], timeout.Token)).Single();
        if (direct.Status != FieldObservationStatus.Current || direct.Value?.Boolean != false
            || direct != backend.ReadCached(field))
            throw new InvalidDataException("Stopping the external subscription released the original AC-power owner/current.");
        Console.WriteLine("Actual SubscriptionCore role-2 AC field: online then offline; stop drained demand; original owner remained directly collectible; no WMI/real native fallback.");
    }

    private sealed class RecordedReader(WindowsAcPowerStatusReading? value) : IWindowsAcPowerStatusReader
    {
        private readonly object gate = new();
        private WindowsAcPowerStatusReading? current = value;
        private int reads;
        internal WindowsAcPowerStatusReading? Value
        {
            get { lock (gate) return current; }
            set { lock (gate) current = value; }
        }
        internal int Reads => Volatile.Read(ref reads);

        public WindowsAcPowerStatusReading? Read()
        {
            Interlocked.Increment(ref reads);
            return Value;
        }
    }
}
