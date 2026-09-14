using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPlacementCoordinatorProjectionTests
{
    [Fact]
    public void ShimPolicyProjectsItsOwnKindAndDigestsTheActualValueRatherThanBothStates()
    {
        var first = GpuShimPolicyRecord.Create("target", [1], [2]);
        var changedApplied = GpuShimPolicyRecord.Create("target", [1], [3]);
        var previousMatchesApplied = GpuShimPolicyRecord.Create("target", [2], [3]);
        var placement = GpuShimPolicyLedgerTests.Placement(first);
        var output = new NativePlacementAppliedInput[1];
        HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], output);
        var row = Assert.Single(output);
        Assert.Equal((uint)NativePlacementKind.GpuShimPolicy, row.PlacementKind);
        Assert.Equal((uint)NativePlacementResourceKind.Gpu, row.ResourceKind);
        Assert.Equal((uint)NativePlacementAppliedFlags.PayloadValid, row.Flags);
        Assert.NotEqual(row.ReceiptDigest, row.PreviousDigest);
        Assert.Equal(row.PreviousDigest,
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", placement, changedApplied));
        Assert.Equal(row.ReceiptDigest,
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", placement, previousMatchesApplied));
        Assert.NotEqual(
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", placement,
                GpuShimPolicyRecord.Create("target", null, [2])),
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", placement,
                GpuShimPolicyRecord.Create("target", [], [2])));
    }

    [Fact]
    public void InvalidShimPolicyProjectsIdentityOnlyForRetention()
    {
        var invalid = GpuShimPolicyRecord.Create("target", [1], [2]) with { Metadata = null };
        var output = new NativePlacementAppliedInput[1];
        HostManagerPlacementCoordinatorProjection.ProjectApplied([GpuShimPolicyLedgerTests.Placement(invalid)], output);
        Assert.Equal((uint)NativePlacementKind.Unknown, output[0].PlacementKind);
        Assert.Equal((ulong)NativePlacementAppliedValidity.Identity, output[0].ValidMask);
    }

    [Fact]
    public void KnownRecordProjectsExplicitUncheckedPayloadAndProcessIdentity()
    {
        var destination = new NativePlacementAppliedInput[1];
        var placement = Placement(
            new HostManagerAppliedRecord(
                HostManagerAppliedRecordKinds.CpuAffinity,
                "affinity",
                new Dictionary<string, string>
                {
                    ["processId"] = "42",
                    ["processStartedAt"] = "1000",
                    ["previousAffinityMask"] = "3",
                    ["appliedAffinityMask"] = "2"
                }));

        var projection = HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], destination);

        var row = Assert.Single(destination);
        Assert.Equal(1U, projection.RowCount);
        Assert.NotEqual(0UL, row.TargetKey);
        Assert.NotEqual(0UL, row.RecordKey);
        Assert.Equal((uint)NativePlacementResourceKind.Cpu, row.ResourceKind);
        Assert.Equal((uint)NativePlacementKind.CpuAffinity, row.PlacementKind);
        Assert.Equal((uint)NativePlacementObservationStatus.Unchecked, row.ObservationStatus);
        Assert.Equal((uint)NativePlacementAppliedFlags.PayloadValid, row.Flags);
        Assert.NotEqual(0UL, row.ReceiptDigest);
        Assert.NotEqual(0UL, row.PreviousDigest);
        Assert.NotEqual(row.ReceiptDigest, row.PreviousDigest);
        Assert.Equal(42U, row.ProcessId);
        Assert.Equal(
            checked((ulong)DateTimeOffset.FromUnixTimeMilliseconds(1000).ToFileTime()),
            row.ProcessStartKey);
        Assert.Equal(
            (ulong)(NativePlacementAppliedValidity.Required | NativePlacementAppliedValidity.ProcessIdentity),
            row.ValidMask);
        Assert.Same(placement, projection.Require(new NativePlacementAction
        {
            TargetKey = row.TargetKey,
            RecordKey = row.RecordKey
        }).Placement);
    }

    [Fact]
    public void ExactStartKeyAndCpuSetsProjectWithoutLegacyOrAffinityDowngrade()
    {
        var exactStartKey = checked((ulong)DateTimeOffset.UtcNow.ToFileTime());
        var destination = new NativePlacementAppliedInput[1];
        var placement = Placement(
            new HostManagerAppliedRecord(
                HostManagerAppliedRecordKinds.CpuAffinity,
                "cpu-sets",
                new Dictionary<string, string>
                {
                    ["affinityKind"] = "cpu-sets",
                    ["processId"] = "42",
                    ["processStartKey"] = exactStartKey.ToString(),
                    ["processStartedAt"] = "1000",
                    ["previousCpuSetIds"] = "1,2",
                    ["appliedCpuSetIds"] = "3,4"
                }));

        HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], destination);

        var row = Assert.Single(destination);
        Assert.Equal((uint)NativePlacementKind.CpuSets, row.PlacementKind);
        Assert.Equal(exactStartKey, row.ProcessStartKey);
    }

    [Fact]
    public void DesiredProjectionUsesSameAppliedDigestAndExactProcessIdentity()
    {
        var processStartKey = checked((ulong)DateTimeOffset.UtcNow.ToFileTime());
        var record = new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.GpuPreference,
            "gpu-preference",
            new Dictionary<string, string>
            {
                ["processId"] = "42",
                ["processStartKey"] = processStartKey.ToString(),
                ["path"] = @"C:\Games\game.exe",
                ["previousValue"] = string.Empty,
                ["appliedValue"] = "GpuPreference=2;"
            });
        var placement = Placement(record);
        var desiredDestination = new NativePlacementDesiredInput[1];
        var appliedDestination = new NativePlacementAppliedInput[1];

        var desired = HostManagerPlacementCoordinatorProjection.ProjectDesired(
            [new HostManagerPlacementDesired(placement, record, 73)],
            desiredDestination);
        HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], appliedDestination);

        var desiredRow = Assert.Single(desiredDestination);
        var appliedRow = Assert.Single(appliedDestination);
        Assert.Equal(1U, desired.RowCount);
        Assert.Equal(appliedRow.ReceiptDigest, desiredRow.DesiredDigest);
        Assert.Equal(42U, desiredRow.ProcessId);
        Assert.Equal(processStartKey, desiredRow.ProcessStartKey);
        Assert.Equal(73U, desiredRow.Priority);
        Assert.Equal((uint)NativePlacementKind.GpuPreference, desiredRow.PlacementKind);
        Assert.Equal(
            (ulong)(NativePlacementDesiredValidity.Required | NativePlacementDesiredValidity.ProcessIdentity),
            desiredRow.ValidMask);
    }

    [Fact]
    public void UnknownRecordProjectsIdentityOnlyInsteadOfInventingSemantics()
    {
        var destination = new NativePlacementAppliedInput[1];

        HostManagerPlacementCoordinatorProjection.ProjectApplied(
            [Placement(new HostManagerAppliedRecord("FutureKind", "future-record"))],
            destination);

        var row = Assert.Single(destination);
        Assert.Equal((uint)NativePlacementResourceKind.Unknown, row.ResourceKind);
        Assert.Equal((uint)NativePlacementKind.Unknown, row.PlacementKind);
        Assert.Equal((uint)NativePlacementObservationStatus.Unchecked, row.ObservationStatus);
        Assert.Equal((uint)NativePlacementAppliedFlags.None, row.Flags);
        Assert.Equal((ulong)NativePlacementAppliedValidity.Identity, row.ValidMask);
        Assert.Equal(0UL, row.ReceiptDigest);
        Assert.Equal(0UL, row.PreviousDigest);
    }

    [Fact]
    public void DuplicateIdentityAndCapacityOverflowFailClosed()
    {
        var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.CpuAffinity, "same");
        var duplicatePlacement = Placement(record, record);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerPlacementCoordinatorProjection.ProjectApplied(
                [duplicatePlacement],
                new NativePlacementAppliedInput[2]));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerPlacementCoordinatorProjection.ProjectApplied(
                [Placement(record)],
                Array.Empty<NativePlacementAppliedInput>()));
    }

    private static HostManagerAppliedPlacementReceipt Placement(params HostManagerAppliedRecord[] records)
        => new(
            "target",
            "Target",
            null,
            "cpu",
            records,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
}
