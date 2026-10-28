using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class OperatingSystemKeyboardScanCodeRemapSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        using var owner = WindowsOperatingSystemInformationOwner.CreateWithKeyboardScanCodeRemapReader(reader, TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap, timeout.Token);
        var backend = owner.CreateBackend();
        var field = backend.Catalog.Fields.Single(capability => capability.Definition.Name == "keyboard_all_scan_code_remap_configured").Field;
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
            in configuration, failure => throw new InvalidOperationException("Unexpected role-2 keyboard-remap worker failure", failure.Error));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("machine-keyboard-remap", [field], TimeSpan.FromSeconds(1),
            new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Role-2 package stream ended before its first map sample.");
        var first = stream.Current.Single();
        FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(first, backend.ReadCached(field));
        if (first.Status != FieldObservationStatus.Current || first.Value?.Structure is not { } firstMap
            || firstMap.GetProperty("entries")[0].GetProperty("targetScanCode").GetString() != "0x001D"
            || reader.Reads < 2)
            throw new InvalidDataException("Role-2 subscription did not publish the recorded map to the same current.");

        reader.Value = new(true, [new(0x003A, 0x001E)]);
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Role-2 package stream ended before the recorded map update.");
        var updated = stream.Current.Single();
        FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(updated, backend.ReadCached(field));
        if (updated.Status != FieldObservationStatus.Current || updated.Value?.Structure is not { } updatedMap
            || updatedMap.GetProperty("entries")[0].GetProperty("targetScanCode").GetString() != "0x001E")
            throw new InvalidDataException("Role-2 subscription diverged from the recorded updated map current.");

        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Stopped role-2 composition emitted a late keyboard-remap reading.");
        var direct = (await backend.CollectAsync([field], timeout.Token)).Single();
        if (direct.Status != FieldObservationStatus.Current || direct.Value?.Structure is not { } directMap
            || directMap.GetProperty("entries")[0].GetProperty("targetScanCode").GetString() != "0x001E"
            || direct != backend.ReadCached(field))
            throw new InvalidDataException("Stopping the subscription released the original owner/current.");
        Console.WriteLine("Actual SubscriptionCore role-2 keyboard-remap field: recorded map update; stop drained demand; original owner remained directly collectible; no registry/WMI fallback.");
    }

    private sealed class RecordedReader(WindowsKeyboardScanCodeRemapReading? value) : IWindowsKeyboardScanCodeRemapReader
    {
        private WindowsKeyboardScanCodeRemapReading? current = value;
        private int reads;
        internal WindowsKeyboardScanCodeRemapReading? Value
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }
        internal int Reads => Volatile.Read(ref reads);
        public WindowsKeyboardScanCodeRemapReading? Read()
        {
            Interlocked.Increment(ref reads);
            return Value;
        }
    }
}
