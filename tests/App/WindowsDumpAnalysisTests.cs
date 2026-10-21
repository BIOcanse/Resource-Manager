using System.Text;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Diagnostics;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsDumpAnalysisTests
{
    [Fact]
    public async Task CallerAssignedResourceIdentitiesComposeAndDeclareExactAnalysisDependencies()
    {
        const string firstIdentity = "recorded-dump|first";
        const string secondIdentity = "recorded-dump|second";
        using var first = new WindowsDumpAnalysisOwner(firstIdentity);
        using var second = new WindowsDumpAnalysisOwner(secondIdentity);
        var firstCurrent = first.ReadCurrent();
        var secondCurrent = second.ReadCurrent();
        Assert.Equal(firstIdentity, first.DeviceIdentity);
        Assert.Equal(secondIdentity, second.DeviceIdentity);
        Assert.Equal(firstIdentity, firstCurrent.Information.Devices.Single().DeviceIdentity);
        Assert.Equal(secondIdentity, secondCurrent.Information.Devices.Single().DeviceIdentity);
        var composite = new CompositeReadWriteBackend([first.CreateBackend(), second.CreateBackend()]);
        var catalog = await composite.DiscoverAsync(default);
        Assert.Equal(2, catalog.Devices.Length);
        Assert.Equal(10, catalog.Fields.Length);
        Assert.Equal(5, catalog.GetFields(firstIdentity).Length);
        Assert.Equal(5, catalog.GetFields(secondIdentity).Length);
        foreach (var capability in catalog.Fields)
        {
            Assert.True(capability.Read.Supported);
            Assert.Null(capability.Read.Reason);
            Assert.Equal(new[] { "resource-manager.native.dump-analysis-helper", "windows.dbgeng" }, capability.Read.Dependencies.ToArray());
            var reading = composite.ReadCached(capability.Field);
            Assert.Equal(capability.Field, reading.Field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
        }
        var collected = await composite.CollectAsync([catalog.Fields[0].Field, catalog.Fields[5].Field], default);
        Assert.All(collected, static reading => Assert.Null(reading.Value));
        Assert.Same(firstCurrent, first.ReadCurrent());
        Assert.Same(secondCurrent, second.ReadCurrent());
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResourceIdentityMustBeExplicitAndNonempty(string? identity)
        => Assert.ThrowsAny<ArgumentException>(() => new WindowsDumpAnalysisOwner(identity!));
    [Fact]
    public async Task PassiveBackendDiscoveryAndCollectObserveOneCurrentWithoutAnEngine()
    {
        using var owner = new WindowsDumpAnalysisOwner("recorded-dump|passive");
        var current = owner.ReadCurrent();
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(5, catalog.Fields.Length);
        foreach (var field in catalog.Fields)
        {
            Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(field.Field).Status);
            Assert.Equal(FieldObservationStatus.Unavailable, (await backend.CollectAsync([field.Field], default))[0].Status);
        }
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([catalog.Fields[0].Field]);
        Assert.Same(current, owner.ReadCurrent());
        Assert.Equal(WindowsDumpAnalysisStatus.NotAnalyzed, current.Status);
    }
    [Fact]
    public async Task PassiveBorrowCanCommitCurrentWithoutClaimingASourceQuery()
    {
        using var owner = new WindowsDumpAnalysisOwner("recorded-dump|passive-borrow");
        var catalog = await owner.CreateBackend().DiscoverAsync(default);
        var field = catalog.Fields[0].Field;
        using var borrow = owner.BorrowSubscriptionCapture();

        var binding = Assert.Single(borrow.Bind([field]));
        Assert.Equal(FieldCollectionSourceKind.CurrentValue, binding.SourceKind);
        var (legacyField, legacyItems, legacyKeys) = binding;
        Assert.Equal(field, legacyField);
        Assert.Equal(binding.CollectionItemIds, legacyItems);
        Assert.Equal(binding.PublicationKeys, legacyKeys);

        var result = await borrow.CollectAsync([field], new AcceptingPermit());
        Assert.True(result.Committed);
        Assert.Empty(result.QueriedFields);
        Assert.Equal(FieldObservationStatus.Unavailable, Assert.Single(result.Readings).Status);
        var (legacyReadings, legacyCommitted) = result;
        Assert.Equal(result.Readings, legacyReadings);
        Assert.Equal(result.Committed, legacyCommitted);
    }
    [Fact]
    public void RecordedWireExercisesProductionDecoderWithFullWidthAndSeparateInterpretation()
    {
        var result = WindowsDumpAnalysisReader.Decode(RecordedWire(), 1, "recorded-dump|wire");
        Assert.Equal(uint.MaxValue, result.BugcheckCodeReported);
        Assert.Equal(ulong.MaxValue, result.BugcheckParametersReported!.Value[0]);
        Assert.Equal("suspect", result.SuspectedModuleInterpreted);
        Assert.Equal("suspect!frame", result.SymbolizedStackInterpreted!.Value[0].Symbol);
        Assert.Equal(0xc0000005u, result.ExceptionContextInterpreted!.ExceptionCode);
        Assert.Equal(WindowsDumpFieldStatus.Unsupported, result.ExceptionContextInterpreted.ThreadContextStatus);
        Assert.Null(result.ExceptionContextInterpreted.Amd64ThreadContext);
    }
    [Fact]
    public void ParserRejectsWrongVersionTrailingDataCountsAndMalformedUtf8()
    {
        var wrongVersion = RecordedWire(); wrongVersion[4] = 2;
        Assert.Throws<InvalidDataException>(() => WindowsDumpAnalysisReader.Decode(wrongVersion, 1, "recorded-dump|invalid"));
        Assert.Throws<InvalidDataException>(() => WindowsDumpAnalysisReader.Decode([.. RecordedWire(), 0], 1, "recorded-dump|invalid"));
        var malformed = RecordedWire(); malformed[^1] = 0xff;
        Assert.Throws<InvalidDataException>(() => WindowsDumpAnalysisReader.Decode(malformed, 1, "recorded-dump|invalid"));
        var excessFrames = RecordedWire(); excessFrames[112] = 65;
        Assert.Throws<InvalidDataException>(() => WindowsDumpAnalysisReader.Decode(excessFrames, 1, "recorded-dump|invalid"));
    }
    [Fact]
    public void FailedEngineReplyCannotFabricateZeroOrRetainFields()
    {
        using var stream = new MemoryStream(); using var write = new BinaryWriter(stream);
        write.Write(0x524D4441u); write.Write(1u); write.Write(unchecked((int)0x80070005));
        var result = WindowsDumpAnalysisReader.Decode(stream.ToArray(), 1, "recorded-dump|denied");
        Assert.Equal(WindowsDumpAnalysisStatus.PermissionDenied, result.Status);
        Assert.Null(result.BugcheckCodeReported); Assert.Null(result.BugcheckParametersReported);
        Assert.Null(result.ExceptionContextInterpreted); Assert.Null(result.SymbolizedStackInterpreted);
        Assert.Equal("recorded-dump|denied", result.Information.Devices.Single().DeviceIdentity);
        Assert.All(result.Information.Devices.Single().Fields, static field => Assert.Null(field.Value));
    }
    [Fact]
    public void SymbolPolicyRejectsServerExpressionsAndNonFiniteLimits()
    {
        var request = new WindowsDumpAnalysisRequest(@"C:\dump.dmp", @"C:\helper.exe", @"C:\dbgeng.dll", [], TimeSpan.FromSeconds(5));
        WindowsDumpAnalysisReader.Validate(request);
        Assert.Throws<ArgumentException>(() => WindowsDumpAnalysisReader.Validate(request with { LocalSymbolDirectories = ["srv*https://server"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsDumpAnalysisReader.Validate(request with { Timeout = Timeout.InfiniteTimeSpan }));
    }
    private static byte[] RecordedWire()
    {
        using var stream = new MemoryStream(); using var write = new BinaryWriter(stream, Encoding.UTF8);
        write.Write(0x524D4441u); write.Write(1u); write.Write(0);
        write.Write(0); write.Write(0); write.Write(unchecked((int)0x80004001)); write.Write(0);
        write.Write(0x8664u); write.Write(uint.MaxValue);
        write.Write(ulong.MaxValue); write.Write(0UL); write.Write(1UL); write.Write(2UL);
        write.Write(1u); write.Write(2u); write.Write(0xc0000005u); write.Write(0u); write.Write(ulong.MaxValue);
        write.Write(1u); write.Write(1u); write.Write(ulong.MaxValue); write.Write(0u); write.Write(1u);
        write.Write(ulong.MaxValue); write.Write(0UL); write.Write(1UL); write.Write(2UL); write.Write(3UL);
        Text("suspect!frame"); Text("suspect");
        return stream.ToArray();
        void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text); write.Write((uint)bytes.Length); write.Write(bytes); }
    }

    private sealed class AcceptingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
