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
    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedSource(); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "construction/catalog passive");
        var catalog = await core.DiscoverAsync();
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        Require(catalog.Fields.Any(item => item.Field == field), "canonical field present");
        var capability = core.Query(field);
        Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported
            && capability.Definition.ValueKind == "structured" && capability.Definition.Unit == "mode"
            && capability.Definition.Meaning == "display.windows_registry_edid_mode_capability_declared", "declared structured read-only capability");
        var reads = source.Reads; Verify(core.ReadCached(field)); Require(reads == source.Reads, "cache never captures");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null,
            "inactive current resolution is unknown while static EDID remains available");
        var direct = (await core.CollectAsync([field])).Single(); Verify(direct);
        Require(direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "direct and original current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.Catalog.Fields.Any(item => item.Field == field), "source failure clears current value and preserves field identity");
        source.Fail = false; Verify((await core.CollectAsync([field])).Single());
        await RunAdvancedRecordedAsync();
        await RunAdvancedRecordedAsync(legacy: true);
        await RunAdvancedRecordedAsync(originalVic: true);
        await RunAdvancedRecordedAsync(legacy: true, originalVic: true);
        await RunAdvancedRecordedAsync(legacy: true, originalVic: true, bitmap: true, containerRevision: 0x12);
        await RunType3RecordedAsync();
        await RunTypeXRecordedAsync(0x20);
        await RunTypeXRecordedAsync(0x20, ctaMetadata: true);
        await RunEmbeddedSvdRecordedAsync(0x20);
        await RunEmbeddedColourRecordedAsync(0x20);
        await RunEmbeddedHdmiRecordedAsync(0x20);
        await RunFormulaRecordedAsync(0x20);
        await RunChromaticityRecordedAsync();
        await RunCompactRecordedAsync();
        await RunDynamicRangeRecordedAsync();
        Console.WriteLine("[EDID-MODE-RW-RECORDED] parser; exact-display; two-DTD; rational; inactive; direct/current; failure/recovery; T7-exact-CTA-DSCnull; ordered-SVR-source-references; NVR-codes-unit; T8-full-DMT-code-raw-also420; T10-CVT-parameters-SVR147; DisplayID-VIII-full-DMT-raw-null-CTA-domain; DisplayID-IV-independent-legacy-byte-DMT; DisplayID-original-CTA-VIC-HDMI-VIC13-full-domain; wire12-bitmap17-raw-bitindex-code; TypeIII-independent2-width-aspect-nominal-field-frame; TypeX-independent3-CVT-wire20; nonmode-CTA-wire20-original-owner-raw-recovery; no-live-display");
    }

    private static async Task RunAdvancedRecordedAsync(bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12)
    {
        var source = new RecordedSource(advanced: true, legacy: legacy, originalVic: originalVic, bitmap: bitmap, containerRevision: containerRevision); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "advanced construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(item => item.Field == field), "advanced same canonical field");
        var reads = source.Reads; VerifyAdvanced(core.ReadCached(field), legacy, originalVic, bitmap, containerRevision); Require(reads == source.Reads, "advanced passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "advanced static declarations on inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyAdvanced(direct, legacy, originalVic, bitmap, containerRevision);
        Require(source.Reads > reads && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "advanced actual source query and original current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.Catalog.Fields.Any(item => item.Field == field), "advanced failure clears selected value, identity retained");
        source.Fail = false; VerifyAdvanced((await core.CollectAsync([field])).Single(), legacy, originalVic, bitmap, containerRevision);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        source.SetUnknownDmt(true);
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(item => item.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(item => item.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), bitmap ? "unknown bitmap header clears all17 modes while exact640B raw remains available" : legacy ? "unknown IV code0 clears mode while exact640B raw remains available" : "unknown full DMT0104 clears mode while exact640B raw remains available");
        source.SetUnknownDmt(false);
        VerifyAdvanced((await core.CollectAsync([field])).Single(), legacy, originalVic, bitmap, containerRevision);
    }

    private static async Task RunTypeXRecordedAsync(byte version, bool ctaMetadata = false)
    {
        var source = new RecordedSource(typeX: true, typeXVersion: version, ctaMetadata: ctaMetadata); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "TypeX construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "TypeX uses original canonical field");
        var reads = source.Reads; VerifyTypeX(core.ReadCached(field), version); Require(reads == source.Reads, "TypeX passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "TypeX static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyTypeX(direct, version);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "TypeX real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "TypeX actual source failure clears current");
        source.Fail = false; VerifyTypeX((await core.CollectAsync([field])).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnknownTypeX(true);
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "TypeX unsupported final RB0 rejects all three declarations while exact256B raw survives");
        source.SetUnknownTypeX(false); VerifyTypeX((await core.CollectAsync([field])).Single(), version);
        if (ctaMetadata) {
            source.SetModeBearingCta(true);
            var unsupported = await core.CollectAsync([field, rawField]);
            Require(unsupported.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
                && core.ReadCached(field).Value is null && unsupported.Single(row => row.Field == rawField).Value?.Bytes is { } exact
                && exact.AsSpan().SequenceEqual(source.RawBytes), "mode-bearing embedded CTA clears all modes while complete raw survives");
            source.SetModeBearingCta(false);
            VerifyTypeX((await core.CollectAsync([field])).Single(), version);
        }
    }

    private static async Task RunEmbeddedSvdRecordedAsync(byte version)
    {
        var source = new RecordedSource(embeddedSvd: true, typeXVersion: version); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "embedded-SVD construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "embedded-SVD uses original canonical field");
        var reads = source.Reads; VerifyEmbeddedSvd(core.ReadCached(field), version); Require(reads == source.Reads, "embedded-SVD passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-SVD static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyEmbeddedSvd(direct, version);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "embedded-SVD real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-SVD actual source failure clears current");
        source.Fail = false; VerifyEmbeddedSvd((await core.CollectAsync([field])).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnsupportedSvd(true);
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "embedded-SVD unsupported inner VFDB clears all four declarations while exact256B raw survives");
        source.SetUnsupportedSvd(false); VerifyEmbeddedSvd((await core.CollectAsync([field])).Single(), version);
        Console.WriteLine($"[EDID-MODE-RW-EMBEDDED-SVD] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedSvd(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded SVD exact original identity/current/source");
        DisplayIdCtaSvdExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunEmbeddedColourRecordedAsync(byte version)
    {
        var source = new RecordedSource(embeddedColour: true, typeXVersion: version); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "embedded-colour construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "embedded-colour uses original canonical field");
        var reads = source.Reads; VerifyEmbeddedColour(core.ReadCached(field), version); Require(reads == source.Reads, "embedded-colour passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-colour static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyEmbeddedColour(direct, version);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "embedded-colour real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-colour actual source failure clears current");
        source.Fail = false; VerifyEmbeddedColour((await core.CollectAsync([field])).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidColourMap(true); var expectedRaw = source.RawBytes;
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "embedded-colour out-of-range embedded colour bitmap clears all five declarations while exact256B raw survives");
        source.SetInvalidColourMap(false); VerifyEmbeddedColour((await core.CollectAsync([field])).Single(), version);
        Console.WriteLine($"[EDID-MODE-RW-EMBEDDED-COLOUR] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedColour(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded colour exact original identity/current/source");
        DisplayIdCtaColourExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunEmbeddedHdmiRecordedAsync(byte version)
    {
        var source = new RecordedSource(embeddedHdmi: true, typeXVersion: version); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "embedded-hdmi construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "embedded-hdmi uses original canonical field");
        var reads = source.Reads; VerifyEmbeddedHdmi(core.ReadCached(field), version); Require(reads == source.Reads, "embedded-hdmi passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-hdmi static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyEmbeddedHdmi(direct, version);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "embedded-hdmi real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-hdmi actual source failure clears current");
        source.Fail = false; VerifyEmbeddedHdmi((await core.CollectAsync([field])).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidHdmiCode(true); var expectedRaw = source.RawBytes;
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "embedded-hdmi unsupported embedded HDMI code clears all nine declarations while exact256B raw survives");
        source.SetInvalidHdmiCode(false); VerifyEmbeddedHdmi((await core.CollectAsync([field])).Single(), version);
        Console.WriteLine($"[EDID-MODE-RW-EMBEDDED-HDMI] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedHdmi(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded HDMI exact original identity/current/source");
        DisplayIdCtaHdmiExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunFormulaRecordedAsync(byte version)
    {
        var source = new RecordedSource(formula: true, typeXVersion: version); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "type-ix construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "type-ix uses original canonical field");
        var reads = source.Reads; VerifyFormula(core.ReadCached(field), version); Require(reads == source.Reads, "type-ix passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "type-ix static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyFormula(direct, version);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "type-ix real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "type-ix actual source failure clears current");
        source.Fail = false; VerifyFormula((await core.CollectAsync([field])).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidFormulaOption(true); var expectedRaw = source.RawBytes;
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "type-ix unsupported Type IX option clears all three declarations while exact256B raw survives");
        source.SetInvalidFormulaOption(false); VerifyFormula((await core.CollectAsync([field])).Single(), version);
        Console.WriteLine($"[EDID-MODE-RW-FORMULA] version={version:X2}; standard-CVT; RB1; RB2; stereo; fractional-support; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyFormula(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "Type IX formula exact original identity/current/source");
        DisplayIdFormulaExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static void VerifyTypeX(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "TypeX exact original owner identity/current/source");
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        Require(value.GetProperty("ctaFlags").ValueKind == System.Text.Json.JsonValueKind.Null, "embedded CTA does not invent extension flags");
        Require(modes.GetArrayLength() == 3 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "TypeX complete independent directory outside CTA ordinals");
        byte[][] bodies = [[0x12,0x7F,7,0x37,4,0x3B], [2,0x7F,7,0x37,4,0x2B,1], [0x9B,0x7F,7,0x37,4,0x3B,0x58,1]];
        uint[] offsets = [8,17,27];
        for (var i = 0; i < 3; i++) {
            var mode = modes[i]; var cvt = mode.GetProperty("cvtParameters");
            Require(mode.GetProperty("encoding").GetString() == "displayid-x" && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == offsets[i] && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == i * 16 && mode.GetProperty("containerRevision").GetByte() == version
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == (i == 1 ? 300u : 60u) && !mode.GetProperty("interlaced").GetBoolean()
                && mode.GetProperty("formula").GetString() == "cvt" && mode.GetProperty("reducedBlanking").GetBoolean()
                && mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(bodies[i])
                && cvt.GetProperty("reducedBlankingVersion").GetByte() == (i == 2 ? 3 : 2) && cvt.GetProperty("stereoCode").GetByte() == 0
                && mode.GetProperty("yCbCr420Encoding").GetBoolean() == (i == 2), "TypeX complete source/body/nominal/formula/container values");
            if (i < 2) {
                Require(cvt.GetProperty("alternativeRefresh1000Over1001Supported").GetBoolean() == (i == 0)
                    && mode.GetProperty("yCbCr420Supported").ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX RB2 original fractional flag");
                foreach (var key in new[] { "horizontalBlankingBase160Pixels", "earlyVsyncRequired", "deltaHorizontalBlankingCode", "additionalVerticalBlankingCode", "alternativeMinimumVerticalBlanking" })
                    Require(cvt.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX unreported RB3 parameter " + key);
            } else Require(cvt.GetProperty("alternativeRefresh1000Over1001Supported").ValueKind == System.Text.Json.JsonValueKind.Null
                && cvt.GetProperty("horizontalBlankingBase160Pixels").GetBoolean() && cvt.GetProperty("earlyVsyncRequired").GetBoolean()
                && cvt.GetProperty("deltaHorizontalBlankingCode").GetByte() == 6 && cvt.GetProperty("additionalVerticalBlankingCode").GetByte() == 2
                && cvt.GetProperty("alternativeMinimumVerticalBlanking").GetBoolean() && mode.GetProperty("yCbCr420Supported").GetBoolean(), "TypeX actual8B RB3 parameters");
            foreach (var key in new[] { "timing", "code", "native", "preferred", "bitIndex", "horizontalPixelRepetition", "dscPassThrough", "yCbCr420Only", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX never invents " + key);
            Require(!mode.TryGetProperty("aspectRatioDeclared", out _), "TypeX does not inherit TypeIII aspect");
        }
    }

    private static async Task RunType3RecordedAsync()
    {
        var source = new RecordedSource(type3: true); using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "TypeIII construction/catalog passive");
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name); var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(entry => entry.Field == field), "TypeIII uses original canonical field");
        var reads = source.Reads; VerifyType3(core.ReadCached(field)); Require(reads == source.Reads, "TypeIII passive current");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "TypeIII static declaration survives inactive path");
        var direct = (await core.CollectAsync([field])).Single(); VerifyType3(direct);
        Require(source.Reads == reads + 1 && direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == core.ReadCached(field).ObservedAt, "TypeIII real Direct source and current agree");
        source.Fail = true;
        Require((await core.CollectAsync([field])).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "TypeIII actual source failure clears current");
        source.Fail = false; VerifyType3((await core.CollectAsync([field])).Single());
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnknownType3(true);
        var unknown = await core.CollectAsync([field, rawField]);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "TypeIII reserved last aspect rejects both declarations while exact256B raw survives");
        source.SetUnknownType3(false); VerifyType3((await core.CollectAsync([field])).Single());
    }

    private static void VerifyType3(FieldReading reading)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "TypeIII exact original display identity/current/source");
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 2 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "TypeIII independent complete two declarations");
        for (var i = 0; i < 2; i++)
        {
            var mode = modes[i]; var aspect = mode.GetProperty("aspectRatioDeclared"); var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == "displayid-iii" && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == (i == 0 ? 8u : 11u) && mode.GetProperty("revision").GetByte() == 1
                && mode.GetProperty("containerFlags").GetByte() == 1 && mode.GetProperty("containerRevision").GetByte() == 0x12
                && mode.GetProperty("width").GetUInt32() == (i == 0 ? 1920u : 8u)
                && mode.GetProperty("interlaced").GetBoolean() == (i == 0)
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == (i == 0 ? 60u : 1u)
                && !mode.GetProperty("preferred").GetBoolean() && mode.GetProperty("formula").GetString() == "cvt"
                && mode.GetProperty("reducedBlanking").GetBoolean() == (i == 0)
                && raw.EnumerateArray().Select(item => item.GetByte()).SequenceEqual(i == 0 ? new byte[] { 20, 239, 187 } : new byte[] { 8, 0, 0 })
                && aspect.GetProperty("code").GetByte() == (i == 0 ? 4 : 8), "TypeIII width/interlace/preferred/nominal/raw/source declarations");
            Require(i == 0 ? aspect.GetProperty("horizontal").GetUInt32() == 16 && aspect.GetProperty("vertical").GetUInt32() == 9
                : aspect.GetProperty("horizontal").ValueKind == System.Text.Json.JsonValueKind.Null && aspect.GetProperty("vertical").ValueKind == System.Text.Json.JsonValueKind.Null,
                "TypeIII exact aspect or explicitly undefined original code");
            foreach (var key in new[] { "height", "timing", "code", "native", "bitIndex", "dscPassThrough", "cvtParameters", "yCbCr420Encoding", "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeIII never generates unreported " + key);
        }
    }

    private sealed partial class RecordedSource : IDeviceTopologyReader
    {
        internal const string Identity = "recorded-display-path|monitor-1|connector-7";
        internal const string Name = "display_all_mode_capability";
        private readonly byte[] bytes;
        private readonly bool legacy;
        private readonly bool originalVic;
        private readonly bool bitmap;
        internal RecordedSource(bool advanced = false, bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12, bool type3 = false, bool typeX = false, byte typeXVersion = 0x20, bool ctaMetadata = false, bool embeddedSvd = false, bool embeddedColour = false, bool embeddedHdmi = false, bool formula = false)
        {
            this.legacy = legacy; this.originalVic = originalVic; this.bitmap = bitmap;
            bytes = formula ? DisplayIdFormulaFixture.Bytes(typeXVersion) : embeddedHdmi ? DisplayIdCtaHdmiFixture.Bytes(typeXVersion) : embeddedColour ? DisplayIdCtaColourFixture.Bytes(typeXVersion) : embeddedSvd ? DisplayIdCtaSvdFixture.Bytes(typeXVersion) : ctaMetadata ? DisplayIdNonModeCtaFixture.Bytes(typeXVersion) : typeX ? Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIB4CACoABhJ/BzcEOyoQBwJ/BzcEKwEqIAibfwc3BDtYARAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==") : type3 ? Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEgkCAAUBBhTvuwgAABEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==") : advanced ? AdvancedBytes(legacy, originalVic, bitmap, containerRevision) : Bytes();
            if (typeX) { bytes[129] = typeXVersion; RecheckTypeX(); }
        }
        private DeviceTopologyDisplayPath? captured;
        internal int Reads { get; private set; }
        internal bool Fail { get; set; }
        internal byte[] RawBytes => bytes.ToArray();
        internal void SetUnknownTypeX(bool unknown)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x2A, "only independent TypeX input has this body layout");
            bytes[155] = unknown ? (byte)0 : (byte)0x9B; RecheckTypeX();
        }
        internal void SetModeBearingCta(bool modeBearing) => DisplayIdNonModeCtaFixture.SetModeBearingCta(bytes, modeBearing);
        internal void SetUnsupportedSvd(bool unsupported) => DisplayIdCtaSvdFixture.SetUnsupportedCta(bytes, unsupported);
        internal void SetInvalidColourMap(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[130] == 22 && bytes[142] == 0x81, "independent embedded colour literal");
            bytes[154] = invalid ? (byte)8 : (byte)5; RecheckTypeX();
        }
        internal void SetInvalidHdmiCode(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[130] == 37 && bytes[142] == 0x81, "independent embedded HDMI literal");
            bytes[169] = invalid ? (byte)5 : (byte)4; RecheckTypeX();
        }
        internal void SetInvalidFormulaOption(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x24, "independent Type IX literal");
            bytes[148] = invalid ? (byte)3 : (byte)0x52;
            DisplayIdFormulaFixture.Repair(bytes);
        }
        private void RecheckTypeX()
        {
            var end = checked(133 + bytes[130]); bytes[end] = 0; var sum = 0;
            for (var i = 129; i < end; i++) sum += bytes[i]; bytes[end] = unchecked((byte)-sum);
            bytes[255] = 0; sum = 0;
            for (var i = 128; i < 255; i++) sum += bytes[i]; bytes[255] = unchecked((byte)-sum);
        }
        internal void SetUnknownType3(bool unknown)
        {
            Require(bytes.Length == 256, "only the independent TypeIII input has this descriptor layout");
            bytes[139] = unknown ? (byte)9 : (byte)8;
            bytes[142] = 0; var sum = 0;
            for (var index = 129; index < 142; index++) sum += bytes[index];
            bytes[142] = unchecked((byte)-sum);
            bytes[255] = 0; sum = 0;
            for (var index = 128; index < 255; index++) sum += bytes[index];
            bytes[255] = unchecked((byte)-sum);
        }
        internal void SetUnknownDmt(bool unknown)
        {
            Require(bytes.Length == 640, "only a recorded advanced source carries a DisplayID code list");
            if (bitmap) bytes[533] = unknown ? (byte)1 : (byte)0; // Unknown bitmap header makes the whole mode value unavailable.
            else if (originalVic)
            {
                if (legacy) bytes[525] = unknown ? (byte)129 : (byte)16; // Full byte129 is unassigned, not native-bit VIC1.
                else bytes[528] = unknown ? (byte)1 : (byte)0; // Full0x0110 must not alias original CTA-VIC16.
            }
            else if (legacy) bytes[520] = unknown ? (byte)0 : (byte)4; // Type IV code0 is unknown; no high byte exists.
            else bytes[521] = unknown ? (byte)1 : (byte)0; // VIII full ID0104 must never alias DMT04.
            var checksumOffset = checked(517 + bytes[514]);
            bytes[checksumOffset] = 0; var sum = 0;
            for (var index = 513; index < checksumOffset; index++) sum += bytes[index];
            bytes[checksumOffset] = unchecked((byte)-sum);
            bytes[639] = 0; sum = 0;
            for (var index = 512; index < 639; index++) sum += bytes[index];
            bytes[639] = unchecked((byte)-sum);
        }
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Reads++;
            if (Fail) throw new InvalidOperationException("recorded EDID read failure");
            captured = new DeviceTopologyDisplayPath(1, 2, 3, 4, 5, 0, "Recorded Panel", @"\\?\DISPLAY#RECORDED#1",
                3840, 2160, 60000, 1001, false, false, @"PCI\RECORDED_GPU") {
                Edid = EdidParser.Parse(bytes), EdidRawDeclaredBase64 = Convert.ToBase64String(bytes),
                EdidObservationComplete = true };
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
        }
        internal DeviceTopologyCaptureOwner CreateOwner()
            => new(new NoAcpi(), new(this,
                _ => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                (_, time) => WindowsInformationFieldProjection.Displays(
                    captured is null ? [] : [WindowsDisplayPathTopologyReader.ToInformation(captured, Identity)], time),
                static (_, _) => Task.CompletedTask, static _ => new("recorded-edid-failure", []),
                static (_, _, _, _) => { }, static _ => { }, static () => { }));
        private static byte[] AdvancedBytes(bool legacy, bool originalVic, bool bitmap, byte containerRevision)
        {
            var bytes = new byte[640]; Bytes().CopyTo(bytes, 0); bytes[126] = 4;
            byte[] selection = [0xE6, 0x0D, 146, 129, 16, 146, 130, 0xE7, 0x08, 145, 0x81, 255, 255, 254, 255];
            byte[] timing = [0x13, 0x44, 2, 0x84, 0x7F, 7, 0x17, 1, 0x57, 0x80, 0x2B, 0, 0x37, 4, 0x2C, 0, 3, 0x80, 4, 0];
            var fractional = timing.ToArray(); fractional[0] = 0x7D; fractional[1] = 0x43;
            byte[] timings = [0xF6, 0x22, 2, .. timing, 0xF6, 0x22, 2, .. fractional];
            byte[] additional = [0xE6, 0x23, 0x29, 4, 0, 0x58, 0,
                0xEA, 0x2A, 0x20, 0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1, 0xE2, 0x0D, 147];
            for (var block = 1; block <= 3; block++)
            {
                var offset = block * 128; var payload = block switch { 1 => selection, 2 => timings, _ => additional };
                bytes[offset] = 2; bytes[offset + 1] = 3; bytes[offset + 2] = checked((byte)(4 + payload.Length));
                payload.CopyTo(bytes, offset + 4);
            }
            byte[] displayId = legacy
                ? [0x70, 0x12, 5, 2, 0, 6, 1, 2, 4, 0x58]
                : [0x70, 0x20, 7, 2, 0, 0x23, 8, 4, 4, 0, 0x58, 0];
            if (originalVic)
            {
                byte[] codes = legacy ? [6, 0x41, 2, 16, 193, 6, 0x81, 2, 1, 4] : [0x23, 0x48, 4, 16, 0, 193, 0, 0x23, 0x88, 4, 1, 0, 4, 0];
                displayId = [.. displayId, .. codes]; displayId[2] = checked((byte)(displayId[2] + codes.Length));
            }
            if (bitmap)
            {
                Require(legacy && originalVic && containerRevision is 0x12, "bitmap only extends either explicit legacy13-mode fixture");
                byte[] bitmaps = [0x07, 0, 1, 0x88, 0x08, 0, 1, 0x81];
                displayId = [.. displayId, .. bitmaps];
                displayId[1] = containerRevision; displayId[2] = checked((byte)(displayId[2] + bitmaps.Length));
            }
            displayId.CopyTo(bytes, 512); var displayIdSum = 0; var checksumOffset = checked(517 + bytes[514]);
            for (var index = 513; index < checksumOffset; index++) displayIdSum += bytes[index];
            bytes[checksumOffset] = unchecked((byte)-displayIdSum);
            for (var block = 0; block < 5; block++)
            {
                var offset = block * 128; bytes[offset + 127] = 0; var sum = 0;
                for (var index = 0; index < 127; index++) sum += bytes[offset + index];
                bytes[offset + 127] = unchecked((byte)-sum);
            }
            return bytes;
        }

        private static byte[] Bytes()
        {
            var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
            bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[24] = 2;
            for (var offset = 38; offset < 54; offset += 2) { bytes[offset] = 1; bytes[offset + 1] = 1; }
            new byte[] { 0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C, 0x45, 0, 0, 0, 0, 0, 0, 0x1E }.CopyTo(bytes, 54);
            bytes.AsSpan(54, 18).CopyTo(bytes.AsSpan(72)); bytes[72] = 0xF3; bytes[73] = 0x39;
            bytes[93] = 0x10; bytes[111] = 0x10;
            bytes[127] = unchecked((byte)-bytes.Sum(static value => (int)value)); return bytes;
        }
    }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
    private static void Require(bool value, string contract)
    { if (!value) throw new InvalidOperationException("EDID mode recorded consumer: " + contract); }
    private static void VerifyAdvanced(FieldReading reading, bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12)
    {
        Verify(reading, bitmap ? 17 : originalVic ? 13 : 9); // The original two-DTD assertions still apply to the first two modes.
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 2]; var timing = mode.GetProperty("timing"); var rate = timing.GetProperty("frameRate");
            Require(mode.GetProperty("encoding").GetString() == "cta-vtdb-vii"
                && mode.GetProperty("blockIndex").GetUInt32() == 2 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 7u : 30u)
                && mode.GetProperty("revision").GetByte() == 2 && mode.GetProperty("containerFlags").GetByte() == 2
                && mode.GetProperty("containerRevision").GetByte() == 3
                && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("preferred").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("native").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Encoding").GetBoolean()
                && mode.GetProperty("rawDescriptor").GetArrayLength() == 20
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && timing.GetProperty("pixelClockHz").GetUInt64() == (index == 0 ? 148_500_000UL : 148_350_000UL)
                && rate.GetProperty("numerator").GetUInt64() == (index == 0 ? 60UL : 1978UL)
                && rate.GetProperty("denominator").GetUInt64() == (index == 0 ? 1UL : 33UL),
                "CTA T7 source, exact rational and null DSC, independent of DisplayID container or live mode");
        }
        var selections = value.GetProperty("selectionDeclarations");
        Require(selections.GetArrayLength() == 3, "two original and one formula selection blocks retained");
        var vfp = selections[0]; var references = vfp.GetProperty("references");
        Require(vfp.GetProperty("encoding").GetString() == "cta-vfpdb" && vfp.GetProperty("blockIndex").GetUInt32() == 1
            && vfp.GetProperty("byteOffset").GetUInt32() == 6 && vfp.GetProperty("revision").GetByte() == 3
            && references.GetArrayLength() == 5, "original VFP container and complete ordered preference sequence");
        byte[] svrs = [146, 129, 16, 146, 130]; int[] targets = [3, 0, -1, 3, 1];
        uint[] blocks = [2, 0, 0, 2, 0]; uint[] offsets = [30, 54, 0, 30, 72];
        for (var index = 0; index < svrs.Length; index++)
        {
            var reference = references[index];
            Require(vfp.GetProperty("rawDescriptor")[index].GetByte() == svrs[index]
                && reference.GetProperty("shortVideoReference").GetByte() == svrs[index]
                && reference.GetProperty("byteOffset").GetUInt32() == (uint)(6 + index), "original SVR bytes/order/duplicates retained");
            if (targets[index] < 0)
            {
                Require(reference.GetProperty("targetEncoding").GetString() == "cta-vic"
                    && reference.GetProperty("code").GetUInt32() == 16
                    && reference.GetProperty("target").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "direct VIC needs no VDB and does not synthesize a mode");
                continue;
            }
            var target = reference.GetProperty("target");
            Require(reference.GetProperty("targetEncoding").GetString() == (blocks[index] == 0 ? "edid-detailed" : "cta-vtdb-vii")
                && reference.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
                && target.GetProperty("blockIndex").GetUInt32() == blocks[index]
                && target.GetProperty("byteOffset").GetUInt32() == offsets[index]
                && target.GetProperty("rawDescriptor").GetRawText() == modes[targets[index]].GetProperty("rawDescriptor").GetRawText(),
                "same-call global DTD/T7 target exact source and raw bytes");
        }
        var nvr = selections[1]; var native = nvr.GetProperty("references")[0]; var size = nvr.GetProperty("imageSize");
        Require(nvr.GetProperty("encoding").GetString() == "cta-nvrdb" && nvr.GetProperty("blockIndex").GetUInt32() == 1
            && nvr.GetProperty("byteOffset").GetUInt32() == 13 && nvr.GetProperty("flags").GetByte() == 0x81
            && nvr.GetProperty("rawDescriptor").GetArrayLength() == 6 && nvr.GetProperty("references").GetArrayLength() == 1
            && native.GetProperty("shortVideoReference").GetByte() == 145
            && native.GetProperty("targetEncoding").GetString() == "cta-vtdb-vii"
            && native.GetProperty("target").GetProperty("blockIndex").GetUInt32() == 2
            && native.GetProperty("target").GetProperty("byteOffset").GetUInt32() == 7
            && native.GetProperty("target").GetProperty("rawDescriptor").GetRawText() == modes[2].GetProperty("rawDescriptor").GetRawText()
            && size.GetProperty("widthCode").GetUInt32() == 65535 && size.GetProperty("heightCode").GetUInt32() == 65534
            && size.GetProperty("unitMicrometers").GetUInt32() == 1000, "NVR original code and declared unit, not observed panel dimensions");
        VerifyCodeTimings(value);
        VerifyFormulaTiming(value);
        VerifyDisplayIdCodes(value, legacy, containerRevision);
        if (originalVic) VerifyDisplayIdOriginalVicDomains(value, legacy, containerRevision);
        if (bitmap) VerifyDisplayIdBitmaps(value, containerRevision);
    }


    private static void VerifyDisplayIdBitmaps(System.Text.Json.JsonElement value, byte containerRevision)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 17, "all original13 plus four bitmap declarations");
        uint[] codes = [4, 8, 1, 8], widths = [640, 800, 640, 720], heights = [480, 600, 480, 240], rates = [60, 0, 60, 60];
        int[] bits = [3, 7, 0, 7];
        for (var i = 0; i < 4; i++)
        {
            var mode = modes[i + 13]; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == (i < 2 ? "displayid-dmt-bitmap" : "displayid-cta-bitmap")
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == (i < 2 ? 23u : 27u)
                && mode.GetProperty("bitIndex").GetInt32() == bits[i] && mode.GetProperty("code").GetUInt32() == codes[i]
                && mode.GetProperty("width").GetUInt32() == widths[i] && mode.GetProperty("height").GetUInt32() == heights[i]
                && !mode.GetProperty("interlaced").GetBoolean() && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == 0 && mode.GetProperty("containerRevision").GetByte() == containerRevision
                && raw.GetArrayLength() == 1 && raw[0].GetByte() == (i < 2 ? 0x88 : 0x81)
                && (i == 1 ? mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null
                    : mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[i])
                && (i < 2 ? !mode.GetProperty("reducedBlanking").GetBoolean()
                    : mode.GetProperty("reducedBlanking").ValueKind == System.Text.Json.JsonValueKind.Null),
                "bitmap code/BitIndex/full raw/source/container/verified nominal facts");
            foreach (var name in new[] { "timing", "native", "preferred", "dscPassThrough", "yCbCr420Encoding",
                "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap", "formula", "cvtParameters" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null, "bitmap never borrows metadata: " + name);
        }
        foreach (var selection in value.GetProperty("selectionDeclarations").EnumerateArray())
        foreach (var reference in selection.GetProperty("references").EnumerateArray())
        {
            var target = reference.GetProperty("target");
            Require(!reference.GetProperty("targetEncoding").GetString()!.Contains("bitmap", StringComparison.Ordinal)
                && (target.ValueKind == System.Text.Json.JsonValueKind.Null || target.GetProperty("blockIndex").GetUInt32() != 4),
                "bitmap declarations remain outside original CTA reference domain");
        }
    }

    private static void VerifyCodeTimings(System.Text.Json.JsonElement value)
    {
        var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 4]; var code = index == 0 ? 4u : 0x58u; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == "cta-dmt-code" && mode.GetProperty("code").GetUInt32() == code
                && mode.GetProperty("blockIndex").GetUInt32() == 3 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 7u : 9u)
                && mode.GetProperty("revision").GetByte() == 1 && mode.GetProperty("containerFlags").GetByte() == 0x29
                && mode.GetProperty("containerRevision").GetByte() == 3
                && raw.GetArrayLength() == 2 && raw[0].GetByte() == code && raw[1].GetByte() == 0
                && mode.GetProperty("width").GetUInt32() == (index == 0 ? 640u : 4096u)
                && mode.GetProperty("height").GetUInt32() == (index == 0 ? 480u : 2160u)
                && mode.GetProperty("reducedBlanking").GetBoolean() == (index != 0)
                && (index == 0 ? mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60
                    : mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null)
                && mode.GetProperty("timing").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("cvtParameters").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Encoding").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Only").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Supported").GetBoolean(),
                "T8 full LE16 DMT code/raw/source, known nominal only, also420 capability without encoding or generated timing");
        }
    }

    private static void VerifyDisplayIdOriginalVicDomains(System.Text.Json.JsonElement value, bool legacy, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes");
        uint[] codes = [16, 193, 1, 4]; uint[] widths = [1920, 5120, 3840, 4096]; uint[] rates = [60, 120, 30, 24];
        uint[] offsets = legacy ? [13, 14, 18, 19] : [15, 17, 22, 24];
        for (var i = 0; i < codes.Length; i++)
        {
            var mode = modes[i + 9]; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("code").GetUInt32() == codes[i]
                && mode.GetProperty("encoding").GetString() == (legacy ? "displayid-iv-" : "displayid-") + (i < 2 ? "cta-vic" : "hdmi-vic")
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == offsets[i]
                && mode.GetProperty("width").GetUInt32() == widths[i] && mode.GetProperty("height").GetUInt32() == (i == 0 ? 1080u : 2160u)
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[i] && !mode.GetProperty("interlaced").GetBoolean()
                && mode.GetProperty("revision").GetByte() == (legacy ? 1 : 0)
                && mode.GetProperty("containerRevision").GetByte() == (legacy ? containerRevision : 0x20)
                && mode.GetProperty("containerFlags").GetByte() == ((i < 2 ? 0x40 : 0x80) | (legacy ? 1 : 8))
                && raw.GetArrayLength() == (legacy ? 1 : 2) && raw[0].GetUInt32() == codes[i] && (legacy || raw[1].GetByte() == 0),
                "DisplayID original CTA-VIC/HDMI-VIC complete code, source and reliable nominal facts");
            foreach (var name in new[] { "timing", "native", "preferred", "reducedBlanking", "dscPassThrough", "yCbCr420Supported", "yCbCr420Only", "formula", "cvtParameters" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null, "DisplayID code domain does not borrow wrapper metadata: " + name);
        }
    }

    private static void VerifyDisplayIdCodes(System.Text.Json.JsonElement value, bool legacy, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 7]; var code = index == 0 ? 4u : 0x58u; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == (legacy ? "displayid-iv-dmt-code" : "displayid-dmt-code") && mode.GetProperty("code").GetUInt32() == code
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 8u : legacy ? 9u : 10u)
                && mode.GetProperty("revision").GetByte() == (legacy ? 1 : 0) && mode.GetProperty("containerFlags").GetByte() == (legacy ? 1 : 8)
                && mode.GetProperty("containerRevision").GetByte() == (legacy ? containerRevision : 0x20)
                && raw.GetArrayLength() == (legacy ? 1 : 2) && raw[0].GetByte() == code && (legacy || raw[1].GetByte() == 0)
                && mode.GetProperty("width").GetUInt32() == (index == 0 ? 640u : 4096u)
                && mode.GetProperty("height").GetUInt32() == (index == 0 ? 480u : 2160u)
                && !mode.GetProperty("interlaced").GetBoolean() && mode.GetProperty("reducedBlanking").GetBoolean() == (index != 0)
                && (index == 0 ? mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60
                    : mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null),
                legacy ? "DisplayID13 IV byte DMT/raw/source/header and reliable nominal facts" : "DisplayID20 VIII full DMT/raw/source/header and reliable nominal facts");
            foreach (var name in new[] { "timing", "dscPassThrough", "preferred", "native", "yCbCr420Only", "yCbCr420Encoding",
                "yCbCr420Supported", "yCbCr420CapabilityMap", "formula", "cvtParameters", "bitIndex" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null,
                    "DisplayID IV/VIII does not borrow CTA metadata or generate timing: " + name);
        }
        foreach (var selection in value.GetProperty("selectionDeclarations").EnumerateArray())
        foreach (var reference in selection.GetProperty("references").EnumerateArray())
        {
            var target = reference.GetProperty("target");
            Require(reference.GetProperty("targetEncoding").GetString() is not ("displayid-dmt-code" or "displayid-iv-dmt-code")
                && (target.ValueKind == System.Text.Json.JsonValueKind.Null || target.GetProperty("blockIndex").GetUInt32() != 4),
                "DisplayID codes remain outside the unchanged CTA DTD/T7/T10 selection domain");
        }
    }

    private static void VerifyFormulaTiming(System.Text.Json.JsonElement value)
    {
        var mode = value.GetProperty("modes")[6]; var raw = mode.GetProperty("rawDescriptor");
        byte[] body = [0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1];
        Require(raw.GetArrayLength() == body.Length, "T10 complete eight-byte descriptor");
        for (var index = 0; index < body.Length; index++) Require(raw[index].GetByte() == body[index], "T10 original raw parameter bytes");
        Require(mode.GetProperty("encoding").GetString() == "cta-vtdb-x" && mode.GetProperty("blockIndex").GetUInt32() == 3
            && mode.GetProperty("byteOffset").GetUInt32() == 14 && mode.GetProperty("revision").GetByte() == 0
            && mode.GetProperty("containerFlags").GetByte() == 0x20 && mode.GetProperty("containerRevision").GetByte() == 3
            && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
            && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60 && mode.GetProperty("reducedBlanking").GetBoolean()
            && mode.GetProperty("formula").GetString() == "cvt" && !mode.GetProperty("interlaced").GetBoolean()
            && mode.GetProperty("yCbCr420Encoding").GetBoolean() && mode.GetProperty("yCbCr420Supported").GetBoolean()
            && mode.GetProperty("timing").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("preferred").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("native").ValueKind == System.Text.Json.JsonValueKind.Null,
            "T10 CTA source and declared geometry/formula, no generated clock or winning/native mode");
        var parameters = mode.GetProperty("cvtParameters");
        Require(parameters.GetProperty("reducedBlankingVersion").GetByte() == 3 && parameters.GetProperty("stereoCode").GetByte() == 0
            && parameters.GetProperty("alternativeRefresh1000Over1001Supported").ValueKind == System.Text.Json.JsonValueKind.Null
            && parameters.GetProperty("horizontalBlankingBase160Pixels").GetBoolean()
            && parameters.GetProperty("earlyVsyncRequired").GetBoolean()
            && parameters.GetProperty("deltaHorizontalBlankingCode").GetByte() == 6
            && parameters.GetProperty("additionalVerticalBlankingCode").GetByte() == 2
            && parameters.GetProperty("alternativeMinimumVerticalBlanking").GetBoolean(), "T10 original RB3 CVT parameter codes");
        var selection = value.GetProperty("selectionDeclarations")[2]; var references = selection.GetProperty("references");
        Require(selection.GetProperty("encoding").GetString() == "cta-vfpdb" && selection.GetProperty("blockIndex").GetUInt32() == 3
            && selection.GetProperty("byteOffset").GetUInt32() == 24 && selection.GetProperty("revision").GetByte() == 3
            && selection.GetProperty("rawDescriptor").GetArrayLength() == 1 && selection.GetProperty("rawDescriptor")[0].GetByte() == 147
            && references.GetArrayLength() == 1, "new VFP preserves original SVR147 and its source");
        var reference = references[0]; var target = reference.GetProperty("target");
        Require(reference.GetProperty("shortVideoReference").GetByte() == 147 && reference.GetProperty("byteOffset").GetUInt32() == 24
            && reference.GetProperty("targetEncoding").GetString() == "cta-vtdb-x"
            && reference.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
            && target.GetProperty("blockIndex").GetUInt32() == 3 && target.GetProperty("byteOffset").GetUInt32() == 14
            && target.GetProperty("rawDescriptor").GetRawText() == raw.GetRawText(),
            "same-call third T7/T10 target is exact T10 source; intervening T8 modes do not consume the reference domain");
    }

    private static void Verify(FieldReading reading, int expectedModeCount = 2)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid"
            && reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.Value?.Structure is not null, "exact caller-owned display identity and structured declared value");
        var value = reading.Value!.Structure!.Value;
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == expectedModeCount && value.GetProperty("ranges").GetArrayLength() == 0,
            "complete two-DTD directory preserved, not collapsed by resolution");
        for (var i = 0; i < 2; i++)
        {
            var mode = modes[i]; var timing = mode.GetProperty("timing"); var rate = timing.GetProperty("frameRate");
            Require(mode.GetProperty("encoding").GetString() == "edid-detailed"
                && mode.GetProperty("blockIndex").GetUInt32() == 0
                && mode.GetProperty("byteOffset").GetUInt32() == (uint)(54 + 18 * i)
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && mode.GetProperty("rawDescriptor").GetArrayLength() == 18
                && timing.GetProperty("pixelClockHz").GetUInt64() == (i == 0 ? 148_500_000UL : 148_350_000UL)
                && rate.GetProperty("numerator").GetUInt64() == (i == 0 ? 60UL : 1978UL)
                && rate.GetProperty("denominator").GetUInt64() == (i == 0 ? 1UL : 33UL),
                "source position/bytes/exact rational timing independent of active stream");
        }
    }
}
