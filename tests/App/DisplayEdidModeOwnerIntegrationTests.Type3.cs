using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DisplayEdidModeOwnerIntegrationTests
{
    [Fact]
    public async Task Type3OriginalDirectCurrentFailureAndRecoveryUseRealParserOnInactiveDisplay()
    {
        var phase = 0; var reads = 0;
        using var owner = Owner(() => {
            reads++; if (phase == 2) throw new IOException("recorded TypeIII read failure");
            return DisplayEdidType3ConsumerTestData.Snapshot(unknown: phase == 1);
        });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var initialReads = reads;
        DisplayEdidType3ConsumerTestData.AssertValue(backend.ReadCached(field).Value!.Structure!.Value);
        Assert.Equal(initialReads, reads);
        var direct = Assert.Single(await backend.CollectAsync([field], CancellationToken.None));
        DisplayEdidType3ConsumerTestData.AssertValue(direct.Value!.Structure!.Value);
        var current = backend.ReadCached(field);
        Assert.Equal(direct.Field, current.Field); Assert.Equal(direct.ObservedAt, current.ObservedAt);
        Assert.Equal(direct.Value!.Structure!.Value.GetRawText(), current.Value!.Structure!.Value.GetRawText());
        phase = 1;
        var unknown = await backend.CollectAsync([field, rawField], CancellationToken.None);
        Assert.Null(Assert.Single(unknown, row => row.Field == field).Value);
        Assert.True(Assert.Single(unknown, row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(DisplayEdidType3ConsumerTestData.Bytes(unknown: true)));
        phase = 2; Assert.Null(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value);
        phase = 0; DisplayEdidType3ConsumerTestData.AssertValue(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value!.Structure!.Value);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Type3CanceledOrFrozenCaptureNeverReplacesAcceptedCurrent(bool cancel)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var cancellation = new CancellationTokenSource();
        var reads = 0; var persisted = 0;
        using var owner = Owner(() => {
            if (Interlocked.Increment(ref reads) == 2) {
                entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return DisplayEdidType3ConsumerTestData.Snapshot(unknown: true);
            }
            return DisplayEdidType3ConsumerTestData.Snapshot();
        }, () => persisted++);
        await owner.CaptureInformationAsync(timeout.Token); var accepted = owner.ReadState();
        var gate = new object(); var active = true;
        var permission = new DeviceTopologyCapturePermission(() => { lock (gate) return active; },
            commit => { lock (gate) { if (!active) return false; commit(); return true; } }, () => true);
        using var signal = owner.SubscribeTopologyPublications(); var notification = signal.WaitAsync(timeout.Token).AsTask();
        var capture = Task.Run(() => owner.SampleTopologyAsync(permission, cancellation.Token, requireDemand: false));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            if (cancel) cancellation.Cancel(); else lock (gate) active = false;
        } finally { release.Set(); }
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await capture.WaitAsync(timeout.Token));
        Assert.Same(accepted, owner.ReadState()); Assert.Equal(1, persisted); Assert.Equal(2, reads);
        DisplayEdidType3ConsumerTestData.AssertValue(DisplayEdidModeInformationCollectorTests.Field(owner.ReadState().InformationFields).Value!.Value);
        Assert.False(notification.IsCompleted); await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
    }
}

internal static class DisplayEdidType3ConsumerTestData
{
    internal static byte[] Bytes(bool unknown = false)
    {
        var bytes = DisplayEdidType3InformationTests.Bytes(); // frozen parser/model proposal, not a hand-built capability.
        if (!unknown) return bytes;
        bytes[139] = 9; bytes[142] = 0;
        bytes[142] = unchecked((byte)-bytes.AsSpan(129, 13).ToArray().Sum(value => (int)value));
        bytes[255] = 0; bytes[255] = unchecked((byte)-bytes.AsSpan(128, 127).ToArray().Sum(value => (int)value));
        return bytes;
    }
    internal static DeviceTopologySnapshot Snapshot(bool unknown = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes(unknown)))
            with { Active = false, TargetAvailable = false });

    internal static void AssertValue(JsonElement value)
    {
        var modes = value.GetProperty("modes"); Assert.Equal(2, modes.GetArrayLength());
        Assert.Equal(0, value.GetProperty("ranges").GetArrayLength()); Assert.Equal(0, value.GetProperty("selectionDeclarations").GetArrayLength());
        for (var i = 0; i < 2; i++) {
            var mode = modes[i]; var aspect = mode.GetProperty("aspectRatioDeclared");
            Assert.Equal("displayid-iii", mode.GetProperty("encoding").GetString());
            Assert.Equal(1u, mode.GetProperty("blockIndex").GetUInt32()); Assert.Equal(i == 0 ? 8u : 11u, mode.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(i == 0 ? 1920u : 8u, mode.GetProperty("width").GetUInt32());
            Assert.Equal(i == 0, mode.GetProperty("interlaced").GetBoolean()); Assert.Equal(i == 0 ? 60u : 1u, mode.GetProperty("nominalVerticalRateHz").GetUInt32());
            Assert.Equal(i == 0 ? 4 : 8, aspect.GetProperty("code").GetInt32());
            if (i == 0) { Assert.Equal(16u, aspect.GetProperty("horizontal").GetUInt32()); Assert.Equal(9u, aspect.GetProperty("vertical").GetUInt32()); }
            else { Assert.Equal(JsonValueKind.Null, aspect.GetProperty("horizontal").ValueKind); Assert.Equal(JsonValueKind.Null, aspect.GetProperty("vertical").ValueKind); }
            Assert.Equal(i == 0 ? new byte[] { 20, 239, 187 } : new byte[] { 8, 0, 0 }, mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()));
            Assert.Equal(1, mode.GetProperty("revision").GetInt32()); Assert.Equal(1, mode.GetProperty("containerFlags").GetInt32());
            Assert.Equal(18, mode.GetProperty("containerRevision").GetInt32()); Assert.False(mode.GetProperty("preferred").GetBoolean());
            Assert.Equal("cvt", mode.GetProperty("formula").GetString()); Assert.Equal(i == 0, mode.GetProperty("reducedBlanking").GetBoolean());
            foreach (var key in new[] { "height", "timing", "code", "native", "bitIndex", "cvtParameters", "dscPassThrough", "yCbCr420Only", "yCbCr420Encoding", "yCbCr420Supported" })
                Assert.Equal(JsonValueKind.Null, mode.GetProperty(key).ValueKind);
        }
    }
}
