using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Dependencies;

internal static partial class EdidModeConsumer
{
    private static async Task RunChromaticityRecordedAsync()
    {
        var source = new RecordedSource(); source.SetLegacyChromaticity(true, 1);
        using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "chromaticity construction is passive");
        await core.DiscoverAsync();
        var field = new FieldReference(RecordedSource.Identity, "display_all_chromaticity_declared");
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var capability = core.Query(field);
        Require(capability.Read.Supported && !capability.Write.Supported
            && capability.Definition.ValueKind == "structured" && capability.Definition.Unit == "coordinate"
            && capability.Definition.Meaning == "display.windows_registry_edid_base_chromaticity_declared_xy",
            "installed read/write package exposes canonical chromaticity capability");
        foreach (var digital in new[] { false, true })
        for (var type = 0; type < 4; type++)
        {
            source.SetLegacyChromaticity(digital, type);
            var readings = await core.CollectAsync([field, rawField]);
            var reading = readings.Single(row => row.Field == field);
            VerifyChromaticity(reading, type == 1);
            Require(readings.Single(row => row.Field == rawField).Value?.Bytes is { } raw
                && raw.AsSpan().SequenceEqual(source.RawBytes), "same capture preserves exact legacy raw");
            var reads = source.Reads; var cached = core.ReadCached(field);
            Require(reads == source.Reads && cached.ObservedAt == reading.ObservedAt
                && cached.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText(),
                "packaged cache reads do not capture or change current");
        }
        source.SetLegacyChromaticity(true, 1, validChecksum: false);
        var invalid = await core.CollectAsync([field, rawField]);
        Require(invalid.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && invalid.Single(row => row.Field == rawField).Value?.Bytes is { } retained
            && retained.AsSpan().SequenceEqual(source.RawBytes), "bad base checksum clears only declaration; raw remains exact");
        source.SetLegacyChromaticity(true, 1);
        VerifyChromaticity((await core.CollectAsync([field])).Single());
        Console.WriteLine("[EDID13-CHROMATICITY-RW] both-inputs/all-types; exact-xy; raw; cached-current; checksum/recovery; no-live-display");
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
