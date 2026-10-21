using System.Buffers.Binary;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Diagnostics;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsWheaHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static WindowsWheaHistoryRequest Request(string channel = "System", ulong? after = null)
        => new(channel, Start, Start.AddDays(1), 8, TimeSpan.FromSeconds(1), afterRecordId: after);
    // Synthetic supported Event XML + packed CPER fixture, not a machine incident capture.
    private static string Xml(string raw, string channel = "System", string provider = WindowsWheaHistoryRequest.Provider,
        string id = "42", string time = "2026-10-01T06:00:00.1234567+06:00", int level = 1)
        => $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/><EventID>17</EventID><Level>{level}</Level><TimeCreated SystemTime='{time}'/><EventRecordID>{id}</EventRecordID><Channel>{channel}</Channel></System><EventData><Data Name='RawData'>{raw}</Data></EventData></Event>";
    private static byte[] Cper(uint severity = 2)
    {
        var bytes = new byte[201]; "CPER"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0x0210);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), severity);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132), 1);
        bytes[200] = 7; return bytes;
    }
    [Fact]
    public void OriginalXmlSelectsExactEventAndPreservesUtcRawBytesAndHeaderSeverity()
    {
        var bytes = Cper();
        var item = WindowsWheaEventParser.Parse(Xml(Convert.ToHexString(bytes)), Request())!;
        Assert.Equal(42UL, item.EventRecordId);
        Assert.Equal("windows-whea-event|System|Microsoft-Windows-WHEA-Logger|42", item.DeviceIdentity);
        Assert.Equal("2026-10-01T00:00:00.1234567+00:00", item.EventTimeUtc!.Value.ToString("O"));
        Assert.Equal(bytes, item.RawRecord!.Value.ToArray());
        Assert.Equal(WindowsWheaSeverity.Corrected, item.Severity); // Level 1 does not mean CPER fatal.
        Assert.NotEqual(item.DeviceIdentity, WindowsWheaEventParser.Parse(Xml(Convert.ToHexString(bytes), "Other"), Request("Other"))!.DeviceIdentity);
        Assert.Null(WindowsWheaEventParser.Parse(Xml("", provider: "Vendor-Health"), Request()));
        Assert.Null(WindowsWheaEventParser.Parse(Xml("", channel: "Other"), Request()));
        Assert.Null(WindowsWheaEventParser.Parse(Xml(""), Request(after: 42)));
        Assert.Null(WindowsWheaEventParser.Parse(Xml("", time: "2026-11-01T00:00:00Z"), Request()));
    }
    [Fact]
    public void MissingMalformedUnknownOrTruncatedCperNeverUsesLevelOrZeroFallback()
    {
        var badLength = Cper(); BinaryPrimitives.WriteUInt32LittleEndian(badLength.AsSpan(20), 400);
        var badSection = Cper(); BinaryPrimitives.WriteUInt32LittleEndian(badSection.AsSpan(128), 1);
        foreach (var hex in new[] { "", "ZZ", Convert.ToHexString(Cper(4)), Convert.ToHexString(Cper()[..128]),
            Convert.ToHexString(badLength), Convert.ToHexString(badSection) })
        {
            var item = WindowsWheaEventParser.Parse(Xml(hex), Request())!;
            Assert.NotNull(item.EventTimeUtc); Assert.Null(item.RawRecord); Assert.Null(item.Severity);
        }
        for (uint severity = 0; severity < 4; severity++)
            Assert.Equal((WindowsWheaSeverity)severity, WindowsWheaEventParser.ReadSeverity(Cper(severity)));
        var localTime = WindowsWheaEventParser.Parse(Xml("", time: "2026-10-01T12:00:00"), Request())!;
        Assert.Null(localTime.EventTimeUtc); // No local timezone invention.
    }
    [Fact]
    public void ExplicitHistoryScopeIsBoundedAndNeverQueriesEveryLog()
    {
        var request = Request(after: 123);
        Assert.Contains("Provider[@Name='Microsoft-Windows-WHEA-Logger']", request.XPath);
        Assert.Contains("EventRecordID>123", request.XPath);
        Assert.Contains("TimeCreated", request.XPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsWheaHistoryRequest("System", Start, Start.AddDays(32), 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsWheaHistoryRequest("System", Start, Start.AddDays(1), 1025, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsWheaHistoryRequest("System", Start, Start.AddDays(1), 1, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Request("System' or *"));
    }
    [Fact]
    public async Task DirectAndBorrowShareCurrentAndRejectedPublicationOrCancellationRetainsWholeInventory()
    {
        var calls = 0;
        var item = WindowsWheaEventParser.Parse(Xml(Convert.ToHexString(Cper())), Request())!;
        using var owner = new WindowsWheaInformationOwner(Request(), (_, token) => { calls++; token.ThrowIfCancellationRequested(); return new(true, [item], false, null); });
        var backend = owner.CreateBackend();
        Assert.Empty(backend.Catalog.Fields); Assert.Null(owner.ReadHistory()); Assert.Equal(0, calls);
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(3, catalog.Fields.Length); Assert.Equal(1, calls);
        var selected = catalog.Fields.Select(static capability => capability.Field).ToImmutableArray();
        using var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(backend, borrow.Backend); Assert.Same(owner.Publications, borrow.Publications);
        Assert.All(borrow.Bind(selected), binding =>
        {
            Assert.Equal(WindowsWheaInformationOwner.CollectionItemId, Assert.Single(binding.CollectionItemIds));
            Assert.Equal(FieldCollectionSourceKind.SourceQuery, binding.SourceKind);
        });
        var snapshot = owner.ReadInformation();
        var callsBeforeRejectedCapture = calls;
        var rejected = await borrow.CollectAsync(selected, new Permit(false));
        Assert.False(rejected.Committed);
        Assert.Equal(callsBeforeRejectedCapture + 1, calls); // Source query ran despite rejected publication.
        Assert.Equal(selected.ToArray(), rejected.QueriedFields.ToArray());
        Assert.Same(snapshot, owner.ReadInformation());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync(selected, cancellation.Token));
        Assert.Same(snapshot, owner.ReadInformation());
        var committed = await borrow.CollectAsync(selected, new Permit(true));
        Assert.True(committed.Committed);
        Assert.Equal(selected.ToArray(), committed.QueriedFields.ToArray());
        var direct = await backend.CollectAsync(selected, default);
        Assert.All(direct, reading => SameReading(reading, backend.ReadCached(reading.Field)));
        Assert.Equal("UTC", catalog.Fields.Single(field => field.Field.FieldName == "whea_all_event_time_reported").Definition.Unit);
        Assert.Equal("B", catalog.Fields.Single(field => field.Field.FieldName == "whea_all_raw_record_reported").Definition.Unit);
        Assert.Equal("enum", catalog.Fields.Single(field => field.Field.FieldName == "whea_all_severity_reported").Definition.Unit);
        borrow.Dispose(); Assert.Throws<ObjectDisposedException>(() => borrow.Bind(selected));
        _ = await backend.CollectAsync(selected, default); // Borrow retirement retains caller's source owner.
        owner.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(async () => await owner.CaptureInformationAsync());
    }
    [Fact]
    public async Task CancellationAfterSourceReturnsRejectsEntirePublication()
    {
        var item = WindowsWheaEventParser.Parse(Xml(Convert.ToHexString(Cper())), Request())!;
        using var cancellation = new CancellationTokenSource();
        var cancelInsideSource = false;
        using var owner = new WindowsWheaInformationOwner(Request(), (_, _) =>
        {
            if (cancelInsideSource) cancellation.Cancel();
            return new(true, [item], false, null);
        });
        await owner.CaptureInformationAsync(); var original = owner.ReadInformation();
        cancelInsideSource = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.CaptureInformationAsync(cancellation.Token));
        Assert.Same(original, owner.ReadInformation());
    }
    [Fact]
    public async Task SourceFailureClearsKnownFieldsAndEmptyHistoryDoesNotFabricateHealthyRecord()
    {
        var item = WindowsWheaEventParser.Parse(Xml(""), Request())!;
        WindowsWheaHistoryCapture capture = new(true, [item], false, null);
        using var owner = new WindowsWheaInformationOwner(Request(), (_, _) => capture);
        var backend = owner.CreateBackend(); var catalog = await backend.DiscoverAsync(default);
        var selected = catalog.Fields.Select(static field => field.Field).ToImmutableArray();
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(selected.Single(field => field.FieldName == "whea_all_event_time_reported")).Status);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(selected.Single(field => field.FieldName == "whea_all_severity_reported")).Status);
        capture = new(false, [], false, "access denied");
        var cleared = await backend.CollectAsync(selected, default);
        Assert.All(cleared, reading => { Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); });
        capture = new(true, [], false, null); await owner.CaptureInformationAsync();
        Assert.Empty(backend.Catalog.Fields); Assert.Empty(owner.ReadHistory()!.Events);
        Assert.All(InformationReadWriteBackend.Read(Selection(catalog), owner.ReadInformation()), reading => Assert.Null(reading.Value));
    }
    private static ImmutableArray<InformationFieldSelection> Selection(ReadWriteCatalog catalog)
        => [.. catalog.Fields.Select(field => new InformationFieldSelection(field.Field, field.Field.FieldName switch
        { "whea_all_event_time_reported" => DeviceInformationFieldRegistry.WheaAllEventTimeReported,
          "whea_all_raw_record_reported" => DeviceInformationFieldRegistry.WheaAllRawRecordReported,
          _ => DeviceInformationFieldRegistry.WheaAllSeverityReported }))];
    private static void SameReading(FieldReading actual, FieldReading expected)
    {
        Assert.Equal(expected.Field, actual.Field); Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ObservedAt, actual.ObservedAt); Assert.Equal(expected.SourceId, actual.SourceId);
        Assert.Equal(expected.Value?.Text, actual.Value?.Text); Assert.Equal(expected.Value?.Integer, actual.Value?.Integer);
        Assert.Equal(expected.Value?.Bytes?.ToArray(), actual.Value?.Bytes?.ToArray());
    }
    private sealed class Permit(bool active) : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active; public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; } }
}
