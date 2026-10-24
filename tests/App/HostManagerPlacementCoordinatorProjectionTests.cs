using System.Globalization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
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
    public void ExternalRuntimeActionProjectsRebuildWithoutDigestingItsLaterResult()
    {
        var birth = checked((ulong)DateTimeOffset.UtcNow.ToFileTime());
        var pending = ExternalGpuRuntimePlacementRecord.Create(42, birth, new Dictionary<string, string>
        {
            ["owner"] = "host-manager-automatic-placement-v1",
            ["processId"] = "42",
            ["processStartKey"] = birth.ToString(),
            ["assignedPositionId"] = "gpu-luid:0000000000010d92",
            ["targetAdapterKey"] = "69010"
        });
        var result = new RunningGpuPlacementActionResult([new("attempt", WindowsExternalGpuPlacementRuntime.ProviderId,
            new Dictionary<string, string>
            {
                ["controllerExited"] = bool.TrueString,
                ["cleanupPassed"] = bool.TrueString,
                ["controllerSucceeded"] = bool.TrueString,
                ["residentConfirmation"] = "partial"
            })], "Partial source release", RunningGpuPlacementActionStatuses.RecreateRequested);
        var settled = pending with { Metadata = new Dictionary<string, string>(pending.Metadata!)
        {
            [ExternalGpuRuntimePlacementRecord.SettledKey] = "true",
            [ExternalGpuRuntimePlacementRecord.ResultKey] = System.Text.Json.JsonSerializer.Serialize(result)
        } };
        var placement = new HostManagerAppliedPlacementReceipt("target", "Target", null, "gpu",
            [pending], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var output = new NativePlacementAppliedInput[1];
        HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], output);

        Assert.Equal((uint)NativePlacementKind.GpuRuntimeRebuild, output[0].PlacementKind);
        Assert.Equal(output[0].ReceiptDigest,
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("applied", placement, settled));
        Assert.Equal(output[0].PreviousDigest,
            HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", placement, settled));
        Assert.True(ExternalGpuRuntimePlacementRecord.TryReadResult(settled, out var saved));
        Assert.True(ExternalGpuRuntimePlacementRecord.IsConfirmed(saved!));
        Assert.False(saved!.Applied);
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

    [Theory]
    [InlineData("en-US", 0, 0, 3)]
    [InlineData("fr-FR", 1, 4, 19)]
    [InlineData("ar-SA", 2, 7, 63)]
    public void TypedIdealProcessorDesiredPreservesFullDigestAndDefersDurableMetadata(
        string cultureName,
        ushort group,
        byte previousNumber,
        byte appliedNumber)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            const ulong processStartKey = 133_900_000_000_000_031;
            const ulong threadStartKey = 133_900_000_000_000_117;
            const int processId = 42_431;
            const int threadId = 100_117;
            const string recordId = "ideal:stable-🎮";
            var processStartedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
            var previous = new ThreadIdealProcessor(group, previousNumber);
            var applied = new ThreadIdealProcessor(group, appliedNumber);
            var typed = new HostManagerIdealProcessorDesiredMetadata(
                processId,
                processStartKey,
                processStartedAt.ToUnixTimeMilliseconds(),
                "渲染器🎮",
                @"C:\Games\测试\game-🎮.exe",
                threadId,
                threadStartKey,
                previous,
                applied);
            var oldMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "host-manager-automatic-placement-v1",
                ["processId"] = processId.ToString(CultureInfo.InvariantCulture),
                ["processStartKey"] = processStartKey.ToString(CultureInfo.InvariantCulture),
                ["processStartedAt"] = processStartedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["processName"] = "渲染器🎮",
                ["executablePath"] = @"C:\Games\测试\game-🎮.exe",
                ["threadId"] = threadId.ToString(CultureInfo.InvariantCulture),
                ["threadStartKey"] = threadStartKey.ToString(CultureInfo.InvariantCulture),
                ["previousProcessorGroup"] = group.ToString(CultureInfo.InvariantCulture),
                ["previousProcessorNumber"] = previousNumber.ToString(CultureInfo.InvariantCulture),
                ["appliedProcessorGroup"] = group.ToString(CultureInfo.InvariantCulture),
                ["appliedProcessorNumber"] = appliedNumber.ToString(CultureInfo.InvariantCulture),
                ["source"] = HostManagerSmartCoordinator.AutomaticIdealProcessorSource
            };
            var oldRecord = new HostManagerAppliedRecord(
                HostManagerAppliedRecordKinds.CpuThreadIdealProcessor,
                recordId,
                oldMetadata);
            var placement = Placement(oldRecord) with
            {
                TargetId = "target:处理器🎮",
                DisplayName = "目标🎮"
            };
            var placeholder = new HostManagerAppliedRecord(
                HostManagerAppliedRecordKinds.CpuThreadIdealProcessor,
                recordId);
            var typedPlacement = placement with { Records = [placeholder] };
            var desired = new HostManagerPlacementDesired(typedPlacement, placeholder, 73)
            {
                IdealProcessorMetadata = typed
            };
            var expectedAppliedDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                "applied", placement, oldRecord);
            var expectedPreviousDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                "previous", placement, oldRecord);

            Assert.Equal(expectedAppliedDigest,
                HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("applied", desired));
            Assert.Equal(expectedPreviousDigest,
                HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("previous", desired));

            var output = new NativePlacementDesiredInput[1];
            var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], output);
            var row = Assert.Single(output);
            Assert.Equal(expectedAppliedDigest, row.DesiredDigest);
            Assert.Equal((uint)processId, row.ProcessId);
            Assert.Equal(processStartKey, row.ProcessStartKey);
            Assert.Equal(73U, row.Priority);
            var projectedRow = projected.Require(new NativePlacementAction
            {
                TargetKey = row.TargetKey,
                RecordKey = row.RecordKey
            });
            Assert.Null(projectedRow.Record.Metadata);

            var unmaterializedRecordCount = 0;
            for (var index = 0; index < 4; index++)
            {
                var noApplyProjection = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], output);
                var noApplyRow = noApplyProjection.Require(new NativePlacementAction
                {
                    TargetKey = row.TargetKey,
                    RecordKey = row.RecordKey
                });
                if (noApplyRow.Record.Metadata is null) unmaterializedRecordCount++;
            }
            Assert.Equal(4, unmaterializedRecordCount);

            var forApply = projectedRow.MaterializeForApply();
            Assert.Equal(
                oldMetadata.OrderBy(static item => item.Key, StringComparer.Ordinal),
                forApply.Record.Metadata!.OrderBy(static item => item.Key, StringComparer.Ordinal));
            Assert.Same(forApply.Record, Assert.Single(forApply.Placement.Records));
            Assert.Equal(expectedAppliedDigest,
                HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                    "applied", forApply.Placement, forApply.Record));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
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
