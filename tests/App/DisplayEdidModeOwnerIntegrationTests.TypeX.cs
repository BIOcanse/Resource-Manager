using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class DisplayEdidModeOwnerIntegrationTests
{
    [Fact]
    public async Task TypeXOriginalDirectCurrentFailureAndRecoveryUseRealParserOnInactiveDisplay()
        => await VerifyTypeXDirectCurrentAsync(false, 0x20);

    [Theory]
    [InlineData((byte)0x20)]
    public async Task TypeXWithNonModeCtaUsesOriginalOwnerAndClearsOnlyModesForUnsupportedCta(byte version)
        => await VerifyTypeXDirectCurrentAsync(true, version);

    private static async Task VerifyTypeXDirectCurrentAsync(bool ctaMetadata, byte version)
    {
        var phase = 0; var reads = 0;
        using var owner = Owner(() => {
            reads++; if (phase == 2) throw new IOException("recorded TypeX read failure");
            return DisplayEdidTypeXConsumerTestData.Snapshot(unknown: phase == 1 && !ctaMetadata,
                ctaMetadata: ctaMetadata, version: version, modeBearingCta: phase == 1 && ctaMetadata);
        });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field;
        var rawField = new FieldReference(field.DeviceIdentity, "display_all_edid_raw_declared");
        var initialReads = reads;
        DisplayEdidTypeXConsumerTestData.AssertValue(backend.ReadCached(field).Value!.Structure!.Value, version);
        Assert.Equal(initialReads, reads);
        var direct = Assert.Single(await backend.CollectAsync([field], CancellationToken.None));
        DisplayEdidTypeXConsumerTestData.AssertValue(direct.Value!.Structure!.Value, version);
        var current = backend.ReadCached(field);
        Assert.Equal(direct.Field, current.Field); Assert.Equal(direct.ObservedAt, current.ObservedAt);
        Assert.Equal(direct.Value!.Structure!.Value.GetRawText(), current.Value!.Structure!.Value.GetRawText());
        phase = 1;
        var unknown = await backend.CollectAsync([field, rawField], CancellationToken.None);
        Assert.Null(Assert.Single(unknown, row => row.Field == field).Value);
        Assert.True(Assert.Single(unknown, row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(DisplayEdidTypeXConsumerTestData.Bytes(unknown: !ctaMetadata,
                ctaMetadata: ctaMetadata, version: version, modeBearingCta: ctaMetadata)));
        phase = 2; Assert.Null(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value);
        phase = 0; DisplayEdidTypeXConsumerTestData.AssertValue(Assert.Single(await backend.CollectAsync([field], CancellationToken.None)).Value!.Structure!.Value, version);
        Assert.Equal(field, Assert.Single(backend.Catalog.Fields, entry => entry.Field.FieldName == DisplayEdidModeInformationCollectorTests.Name).Field);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeXCanceledOrFrozenCaptureNeverReplacesAcceptedCurrent(bool cancel)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var cancellation = new CancellationTokenSource();
        var reads = 0; var persisted = 0;
        using var owner = Owner(() => {
            if (Interlocked.Increment(ref reads) == 2) {
                entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return DisplayEdidTypeXConsumerTestData.Snapshot(unknown: true);
            }
            return DisplayEdidTypeXConsumerTestData.Snapshot();
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
        DisplayEdidTypeXConsumerTestData.AssertValue(DisplayEdidModeInformationCollectorTests.Field(owner.ReadState().InformationFields).Value!.Value);
        Assert.False(notification.IsCompleted); await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
    }
}

internal static class DisplayEdidTypeXConsumerTestData
{
    internal static byte[] Bytes(bool unknown = false, bool ctaMetadata = false, byte version = 0x20, bool modeBearingCta = false)
    {
        return ctaMetadata ? DisplayIdNonModeCtaFixture.Bytes(version, unknown, modeBearingCta)
            : DisplayEdidTypeXInformationTests.Bytes(unknown, version);
    }
    internal static DeviceTopologySnapshot Snapshot(bool unknown = false, bool ctaMetadata = false, byte version = 0x20, bool modeBearingCta = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes(unknown, ctaMetadata, version, modeBearingCta)))
            with { Active = false, TargetAvailable = false });

    internal static void AssertValue(JsonElement value, byte version = 0x20)
    {
        DisplayEdidTypeXInformationTests.AssertValue(value, version);
    }
}
