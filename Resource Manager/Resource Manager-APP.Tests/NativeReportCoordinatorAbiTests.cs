using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeReportCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0006_0000U, NativeReportCoordinatorAbi.Version);
        Assert.Equal(216, Unsafe.SizeOf<NativeReportCoordinatorConfiguration>());
        Assert.Equal(96, Unsafe.SizeOf<NativeReportCoordinatorCapacity>());
        Assert.Equal(160, Unsafe.SizeOf<NativeReportRuleInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeReportRuleReplaceInput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeReportFactInput>());
        Assert.Equal(112, Unsafe.SizeOf<NativeReportSourceSnapshotInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativeReportTrustCommandInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeReportImportInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeReportPlanInput>());
        Assert.Equal(192, Unsafe.SizeOf<NativeReportOutput>());
        Assert.Equal(352, Unsafe.SizeOf<NativeReportPersistenceOperation>());
        Assert.Equal(72, Unsafe.SizeOf<NativeReportPersistenceFeedbackInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeReportPersistenceFeedback>());
        Assert.Equal(176, Unsafe.SizeOf<NativeReportPlanOutput>());

        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.Generation),
            8);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.MaximumSourceCount),
            32);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.BucketWidthMilliseconds),
            88);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.Flags),
            144);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.MaximumRollingObservationCount),
            152);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.PlannedPersistenceIndexCapacity),
            156);
        AssertOffset<NativeReportCoordinatorConfiguration>(
            nameof(NativeReportCoordinatorConfiguration.MetadataCheckpointIntervalMilliseconds),
            160);
        AssertOffset<NativeReportSourceSnapshotInput>(
            nameof(NativeReportSourceSnapshotInput.CommandMonotonicMilliseconds),
            24);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.IdentityHandle),
            40);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.SourceGeneration),
            64);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.FactSequence),
            320);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.ReportObservedAtUtcMilliseconds),
            328);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.CheckpointSchemaVersion),
            336);
        AssertOffset<NativeReportPersistenceOperation>(
            nameof(NativeReportPersistenceOperation.CheckpointLogicalUtcMilliseconds),
            344);
        AssertOffset<NativeReportRuleInput>(
            nameof(NativeReportRuleInput.PredicateGroupHandle),
            136);
        AssertOffset<NativeReportFactInput>(
            nameof(NativeReportFactInput.SecondaryCurrentValue),
            112);
        AssertOffset<NativeReportTrustCommandInput>(
            nameof(NativeReportTrustCommandInput.FamilyHandle),
            48);
        AssertOffset<NativeReportPlanOutput>(
            nameof(NativeReportPlanOutput.LogicalUtcMilliseconds),
            112);
    }

    [Fact]
    public void PublishedMasksAndStatusesMatchTheZigContract()
    {
        Assert.Equal(0x01U, (uint)NativeReportRuleFlags.Known);
        Assert.Equal(0xFFU, (uint)NativeReportFactFlags.Known);
        Assert.Equal(0x03U, (uint)NativeReportOutputFlags.Known);
        Assert.Equal(0x1FU, (uint)NativeReportPersistenceFlags.Known);
        Assert.Equal(0x3FUL, (ulong)NativeReportPlanFlags.Known);
        Assert.Equal(0xFFUL, (ulong)NativeReportSourceSnapshotValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeReportRuleReplaceValidity.Required);
        Assert.Equal(0x0FUL, (ulong)NativeReportTrustCommandValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeReportImportValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeReportPlanValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeReportPersistenceFeedbackValidity.Required);
        Assert.Equal(6U, (uint)NativeReportPersistenceKind.Metadata);
        Assert.Equal(7, (int)NativeReportCoordinatorStatus.OutOfMemory);
    }

    [Fact]
    public void RootDllExportsCreateAndCapacityRoundTrip()
    {
        var configuration = CreateConfiguration();

        Assert.Equal(
            NativeReportCoordinatorAbi.Version,
            NativeReportCoordinatorSession.GetAbiVersion());
        using var session = new NativeReportCoordinatorSession(in configuration);
        Assert.Equal(configuration.MaximumSourceCount, session.Capacity.SourceCapacity);
        Assert.Equal(configuration.MaximumRuleCount, session.Capacity.RuleCapacity);
        Assert.Equal(
            configuration.MaximumPersistenceOperationCount,
            session.Capacity.PersistenceOperationCapacity);
        Assert.InRange(session.Capacity.ResidentByteCount, 1UL, configuration.ResidentByteBudget);
    }

    private static NativeReportCoordinatorConfiguration CreateConfiguration()
    {
        return new NativeReportCoordinatorConfiguration
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeReportCoordinatorConfiguration>(),
            Generation = 1,
            SessionInstanceLow = 11,
            SessionInstanceHigh = 12,
            MaximumSourceCount = 2,
            MaximumRuleCount = 2,
            MaximumObservationCount = 4,
            MaximumReportCount = 4,
            MaximumTrustCount = 4,
            MaximumBucketCount = 10,
            MaximumPersistenceOperationCount = 32,
            MaximumReportOutputCount = 4,
            SourceIndexCapacity = 4,
            RuleIndexCapacity = 4,
            ObservationIndexCapacity = 8,
            ReportIndexCapacity = 8,
            TrustIndexCapacity = 8,
            BucketIndexCapacity = 32,
            BucketWidthMilliseconds = 1000,
            Window24HoursMilliseconds = 2000,
            Window7DaysMilliseconds = 4000,
            MaximumFutureSkewMilliseconds = 1000,
            DefaultStaleAfterMilliseconds = 10_000,
            DefaultRetentionMilliseconds = 20_000,
            ResidentByteBudget = 1_000_000,
            MaximumRollingObservationCount = 2,
            PlannedPersistenceIndexCapacity = 64,
            MetadataCheckpointIntervalMilliseconds = 1000
        };
    }

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
