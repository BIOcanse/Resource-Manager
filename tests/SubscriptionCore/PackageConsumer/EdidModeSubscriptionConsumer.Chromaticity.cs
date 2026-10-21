using ResourceManager.TestFixtures;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using System.Runtime.CompilerServices;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static partial class EdidModeSubscriptionConsumer
{
    private static async Task RunChromaticityRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration)
    {
        var source = new RecordedSource(); source.SetLegacyChromaticity(true, 1);
        using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await backend.DiscoverAsync(timeout.Token);
        var field = new FieldReference(RecordedSource.Identity, "display_all_chromaticity_declared");
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        var failures = new List<SubscriptionWorkerFailure>();
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow, settings,
            in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(timeout.Token);
            await using var stream = subscription.SubscribeAsync("recorded-edid13-chromaticity", [field],
                TimeSpan.FromMilliseconds(250), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "installed subscription package delivers real legacy chromaticity");
            var delivered = stream.Current.Single(); VerifyChromaticity(delivered);
            await borrow.SecondQueryStarted.WaitAsync(timeout.Token);
            await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "chromaticity Native subscription stops/drains normally");
            var reads = source.Reads; var cached = backend.ReadCached(field);
            Require(source.Reads == reads && cached.ObservedAt == delivered.ObservedAt
                && cached.Value!.Structure!.Value.GetRawText() == delivered.Value!.Structure!.Value.GetRawText(),
                "subscription uses original owner's exact current");
        }
        source.SetLegacyChromaticity(false, 2);
        VerifyChromaticity((await backend.CollectAsync([field], timeout.Token)).Single(), rgb: false);
        source.SetLegacyChromaticity(true, 1, validChecksum: false);
        var invalid = await backend.CollectAsync([field, rawField], timeout.Token);
        Require(invalid.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && invalid.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "stopped original owner retains bad-checksum raw without coordinates");
        source.SetLegacyChromaticity(true, 1);
        VerifyChromaticity((await backend.CollectAsync([field], timeout.Token)).Single());
        Console.WriteLine("[EDID13-CHROMATICITY-SUB] actual-native-delivery; same-current; stop/drain; direct-type/checksum/recovery; no-live-display");
    }

    private sealed partial class RecordedSource
    {
        internal void SetLegacyChromaticity(bool digital, int type, bool validChecksum = true)
        {
            Require(bytes.Length == 128 && type is >= 0 and <= 3, "bounded recorded base chromaticity input");
            bytes[18] = 1; bytes[19] = 3; bytes[20] = digital ? (byte)0x81 : (byte)0;
            bytes[24] = (byte)(type << 3);
            new byte[] { 0x1B, 0x33, 0, 0, 0, 0, 1, 127, 128, 255 }.CopyTo(bytes, 25);
            bytes[127] = 0; var sum = 0;
            for (var i = 0; i < 127; i++) sum += bytes[i];
            bytes[127] = unchecked((byte)-sum);
            if (!validChecksum) bytes[127] ^= 1;
        }
    }

    private static void VerifyChromaticity(FieldReading reading, bool rgb = true)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, "display_all_chromaticity_declared")
            && reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null,
            "packaged chromaticity keeps original identity/source/current");
        var value = reading.Value!.Structure!.Value;
        if (rgb)
        {
            Require(value.GetProperty("red").GetProperty("x").GetDouble() == 0
                && value.GetProperty("red").GetProperty("y").GetDouble() == 1 / 1024d
                && value.GetProperty("green").GetProperty("x").GetDouble() == 2 / 1024d
                && value.GetProperty("green").GetProperty("y").GetDouble() == 3 / 1024d
                && value.GetProperty("blue").GetProperty("x").GetDouble() == 4 / 1024d
                && value.GetProperty("blue").GetProperty("y").GetDouble() == 511 / 1024d,
                "packaged EDID1.3 preserves all RGB coefficient bits");
        }
        else Require(new[] { "red", "green", "blue" }.All(name => value.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null),
            "digital/analog non-RGB type does not invent primaries");
        Require(value.GetProperty("defaultWhite").GetProperty("x").GetDouble() == 0.5
            && value.GetProperty("defaultWhite").GetProperty("y").GetDouble() == 1023 / 1024d,
            "default white remains an exact independent declaration");
    }
}
