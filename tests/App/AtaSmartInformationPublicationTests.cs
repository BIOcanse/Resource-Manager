using System.Collections;
using System.Runtime.Versioning;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Storage;

namespace Resource_Manager_APP.Tests;

public sealed class AtaSmartInformationPublicationTests
{
    private const string Disk = "recorded-physical-disk-A";

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RecordedAccessKeepsSelectionAndOwnerPublicationSemantics()
    {
        using var owner = CreateOwner();
        var calls = new List<AtaSmartAttributeInformationRequest>();
        var failSource = false;
        var failRawPage = false;
        AtaSmartAttributeInformationSource source = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(request);
            if (failSource) throw new IOException("recorded source failure");
            var fields = new List<DeviceInformationField>();
            if ((request.Devices[Disk] & AtaSmartAttributeInformationFields.Normalized) != 0)
                fields.Add(new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized,
                    Json("normalized", 60 + calls.Count)));
            if ((request.Devices[Disk] & AtaSmartAttributeInformationFields.Raw) != 0)
                fields.Add(new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw,
                    failRawPage ? null : Json("raw", calls.Count)));
            if ((request.Devices[Disk] & AtaSmartAttributeInformationFields.Threshold) != 0)
                fields.Add(new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold, Json("threshold", 40)));
            return new(null, [new(Disk, "physical_disk", fields)]);
        };

        var all = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw
            | AtaSmartAttributeInformationFields.Threshold;
        var access = owner.CreateAtaSmartAttributeInformation(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal) { [Disk] = all }, 1), source);

        var raw = new FieldReference(Disk, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw.Name);
        var normalized = new FieldReference(Disk, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized.Name);
        var threshold = new FieldReference(Disk, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold.Name);
        Assert.Equal(3, access.Backend.Catalog.Fields.Length);
        Assert.Empty(calls);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(raw).Status);

        await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(all, calls[0].Devices[Disk]);
        Assert.Equal(1, calls[0].MaximumDeviceCount);
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(raw).Status);

        failRawPage = true;
        await access.Backend.CollectAsync([raw], CancellationToken.None);
        Assert.Equal(AtaSmartAttributeInformationFields.Raw, calls[^1].Devices[Disk]);
        Assert.Null(access.Backend.ReadCached(raw).Value);
        Assert.NotNull(access.Backend.ReadCached(normalized).Value?.Structure);
        Assert.NotNull(access.Backend.ReadCached(threshold).Value?.Structure);

        failSource = true;
        await access.Backend.CollectAsync([normalized], CancellationToken.None);
        Assert.Equal(AtaSmartAttributeInformationFields.Normalized, calls[^1].Devices[Disk]);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(normalized).Status);
        Assert.NotNull(access.Backend.ReadCached(threshold).Value?.Structure);

        var beforeRejected = owner.ReadState();
        using (var borrow = access.BorrowSubscriptionCapture())
        {
            var rejected = await borrow.CollectAsync([threshold], new RejectingPermit());
            Assert.False(rejected.Committed);
            Assert.Same(beforeRejected, owner.ReadState());
        }

        using (var canceled = new CancellationTokenSource())
        {
            var beforeCanceled = owner.ReadState();
            var enumerationCount = 0;
            AtaSmartAttributeInformationSource cancelAfterProjection = (request, _) =>
            {
                var fields = new CancelOnEnumerationList(
                    [new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold, Json("threshold", 99))],
                    canceled, () => Interlocked.Increment(ref enumerationCount) == 5);
                return new(null, [new(Disk, "physical_disk", fields)]);
            };
            // A fresh access keeps the public source seam while the same owner remains authoritative.
            var cancelAccess = owner.CreateAtaSmartAttributeInformation(new(
                new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
                { [Disk] = AtaSmartAttributeInformationFields.Threshold }), cancelAfterProjection);
            using var cancelBorrow = cancelAccess.BorrowSubscriptionCapture();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await cancelBorrow.CollectAsync([threshold], new AcceptingPermit(), canceled.Token);
            });
            Assert.Same(beforeCanceled, owner.ReadState());
        }

        await owner.CaptureInformationAsync(CancellationToken.None);
        Assert.Null(owner.ReadState().InformationFields.FindField(raw)?.Value);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(threshold)?.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await access.Backend.DiscoverAsync(CancellationToken.None);
        });
        Assert.Null(owner.ReadState().InformationFields.FindField(threshold)?.Value);
    }

    private static JsonElement Json(string name, int value)
    {
        using var document = JsonDocument.Parse($"{{\"{name}\":{value}}}");
        return document.RootElement.Clone();
    }

    private static DeviceTopologyCaptureOwner CreateOwner()
    {
        var policy = new DeviceTopologyCapturePolicy(new RecordedTopologyReader(), static _ => "recorded",
            static (snapshot, _) => new(snapshot, DeviceInformationSnapshot.Empty), static (_, _) => Task.CompletedTask,
            static _ => new("recorded-failure", []), static (_, _, _, _) => { }, static _ => { }, static () => { });
        return new(new EmptyAcpiReader(), policy);
    }

    private sealed class RecordedTopologyReader : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot()
            => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
    }

    private sealed class EmptyAcpiReader : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }

    private sealed class RejectingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) => false;
    }

    private sealed class AcceptingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }

    private sealed class CancelOnEnumerationList(IReadOnlyList<DeviceInformationField> fields,
        CancellationTokenSource cancellation, Func<bool> shouldCancel) : IReadOnlyList<DeviceInformationField>
    {
        public int Count => fields.Count;
        public DeviceInformationField this[int index] => fields[index];
        public IEnumerator<DeviceInformationField> GetEnumerator()
        {
            if (shouldCancel()) cancellation.Cancel();
            return fields.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
