using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSmartCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionAndFixedPodLayouts()
    {
        Assert.Equal(NativeSmartCoordinatorAbi.Version, NativeSmartCoordinatorSession.GetAbiVersion());
        Assert.Equal(80, Unsafe.SizeOf<NativeSmartCoordinatorAdapterPolicyConfiguration>());
        Assert.Equal(424, Unsafe.SizeOf<NativeSmartCoordinatorConfiguration>());
        Assert.Equal(88, Unsafe.SizeOf<NativeSmartCoordinatorCapacity>());
        Assert.Equal(96, Unsafe.SizeOf<NativeSmartCoordinatorCycleInput>());
        Assert.Equal(112, Unsafe.SizeOf<NativeSmartCoordinatorInputRow>());
        Assert.Equal(136, Unsafe.SizeOf<NativeSmartCoordinatorAction>());
        Assert.Equal(96, Unsafe.SizeOf<NativeSmartCoordinatorFeedback>());
        Assert.Equal(120, Unsafe.SizeOf<NativeSmartCoordinatorSnapshot>());
        Assert.Equal(192, Unsafe.SizeOf<NativeSmartCoordinatorSnapshotRow>());
    }

    [Fact]
    public void AbiUsesPublishedOffsetsForEveryConfigurationField()
    {
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.AbiVersion), 0);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.StructSize), 4);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.Generation), 8);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.FieldMask), 16);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.FeatureFlags), 24);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumProcesses), 32);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumSoftwareGroups), 36);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumGpuStates), 40);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumInputRows), 44);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumActions), 48);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumReservations), 52);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MaximumAtomicGroups), 56);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.NormalIntervalMilliseconds), 60);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.EventIntervalMilliseconds), 64);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.EventBoostMilliseconds), 68);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.GameStartGraceMilliseconds), 72);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.RequiredConsecutiveDecisions), 76);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.FailureRetryMilliseconds), 80);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.ReservationTimeoutMilliseconds), 84);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.ReservedTiming), 88);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.ProcessStateMultipliers), 104);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.A1MinimumCpuScore), 160);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.DefaultMinimumCpuScoreScale), 168);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.Level1MaximumCpuScoreScale), 176);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.Level2MaximumCpuScoreScale), 184);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.Level3MaximumCpuScoreScale), 192);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.LowTierLevel4MaximumCpuScoreScale), 200);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.ReservedProcessPolicy0), 208);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.HighTierMinimumBaseScore), 216);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.MiddleTierMinimumBaseScore), 224);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.CpuAdapter), 232);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.GpuAdapter), 312);
        AssertOffset<NativeSmartCoordinatorConfiguration>(nameof(NativeSmartCoordinatorConfiguration.Reserved), 392);

        AssertOffset<NativeSmartCoordinatorAdapterPolicyConfiguration>(nameof(NativeSmartCoordinatorAdapterPolicyConfiguration.StateMultipliers), 0);
        AssertOffset<NativeSmartCoordinatorAdapterPolicyConfiguration>(nameof(NativeSmartCoordinatorAdapterPolicyConfiguration.ExtremeMinimumScore), 56);
        AssertOffset<NativeSmartCoordinatorAdapterPolicyConfiguration>(nameof(NativeSmartCoordinatorAdapterPolicyConfiguration.NormalMinimumScore), 64);
        AssertOffset<NativeSmartCoordinatorAdapterPolicyConfiguration>(nameof(NativeSmartCoordinatorAdapterPolicyConfiguration.OptimizeMinimumScore), 72);
    }

    [Fact]
    public void AbiUsesPublishedOffsetsForEveryRuntimeContractSection()
    {

        AssertOffset<NativeSmartCoordinatorCapacity>(nameof(NativeSmartCoordinatorCapacity.InputRowStructSize), 16);
        AssertOffset<NativeSmartCoordinatorCapacity>(nameof(NativeSmartCoordinatorCapacity.InputRowCapacity), 40);
        AssertOffset<NativeSmartCoordinatorCapacity>(nameof(NativeSmartCoordinatorCapacity.Reserved), 72);

        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.ValidMask), 40);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.Flags), 48);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.InputCount), 56);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.ScoreSchedulingGeneration), 64);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.CpuScoreSourceFingerprint), 72);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.GpuScoreSourceFingerprint), 80);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.MaximumActionsThisCycle), 88);
        AssertOffset<NativeSmartCoordinatorCycleInput>(nameof(NativeSmartCoordinatorCycleInput.Reserved), 92);

        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.ValidMask), 8);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.TargetKey), 24);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.SourceIndex), 64);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.ScoreMemberCount), 76);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.AppliedEpoch), 88);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.ProcessScore), 96);
        AssertOffset<NativeSmartCoordinatorInputRow>(nameof(NativeSmartCoordinatorInputRow.SoftwareScore), 104);

        AssertOffset<NativeSmartCoordinatorAction>(nameof(NativeSmartCoordinatorAction.ActionId), 24);
        AssertOffset<NativeSmartCoordinatorAction>(nameof(NativeSmartCoordinatorAction.CpuScore), 80);
        AssertOffset<NativeSmartCoordinatorAction>(nameof(NativeSmartCoordinatorAction.SourceIndex), 88);
        AssertOffset<NativeSmartCoordinatorAction>(nameof(NativeSmartCoordinatorAction.Scope), 112);
        AssertOffset<NativeSmartCoordinatorAction>(nameof(NativeSmartCoordinatorAction.GpuScore), 128);

        AssertOffset<NativeSmartCoordinatorFeedback>(nameof(NativeSmartCoordinatorFeedback.CompletedAtMilliseconds), 64);
        AssertOffset<NativeSmartCoordinatorFeedback>(nameof(NativeSmartCoordinatorFeedback.Status), 80);
        AssertOffset<NativeSmartCoordinatorFeedback>(nameof(NativeSmartCoordinatorFeedback.Reserved1), 88);

        AssertOffset<NativeSmartCoordinatorSnapshot>(nameof(NativeSmartCoordinatorSnapshot.WakeAfterMilliseconds), 64);
        AssertOffset<NativeSmartCoordinatorSnapshot>(nameof(NativeSmartCoordinatorSnapshot.ReasonMask), 96);
        AssertOffset<NativeSmartCoordinatorSnapshot>(nameof(NativeSmartCoordinatorSnapshot.Reserved), 104);

        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.BaseScore), 88);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.CpuScore), 96);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.CpuOccupancyPercent), 104);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.ReservedProcessScore0), 112);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.ReservedProcessRatio0), 120);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.SourceIndex), 144);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.DesiredProcessGrade), 172);
        AssertOffset<NativeSmartCoordinatorSnapshotRow>(nameof(NativeSmartCoordinatorSnapshotRow.Reserved1), 184);
    }

    [Fact]
    public void PublishedMasksKeepTheirExactWireValues()
    {
        Assert.Equal(0x3FUL, (ulong)NativeSmartCoordinatorConfigFields.Required);
        Assert.Equal(0x0FUL, (ulong)NativeSmartCoordinatorFeatures.Known);
        Assert.Equal(1UL << 2, (ulong)NativeSmartCoordinatorCycleValidity.ScoreOnlyMode);
        Assert.Equal(1UL << 3, (ulong)NativeSmartCoordinatorCycleValidity.MaximumActionsThisCycle);
        Assert.Equal(1UL << 4, (ulong)NativeSmartCoordinatorCycleValidity.ScoreSchedulingGeneration);
        Assert.Equal(1UL << 5, (ulong)NativeSmartCoordinatorCycleValidity.CpuScoreSourceFingerprint);
        Assert.Equal(1UL << 6, (ulong)NativeSmartCoordinatorCycleValidity.GpuScoreSourceFingerprint);
        Assert.Equal(0x7CUL, (ulong)NativeSmartCoordinatorCycleValidity.Known);
        Assert.Equal(0x0CUL, (ulong)NativeSmartCoordinatorCycleValidity.Required);
        Assert.Equal(1UL << 3, (ulong)NativeSmartCoordinatorCycleFlags.ScoreOnly);
        Assert.Equal(1UL << 4, (ulong)NativeSmartCoordinatorCycleFlags.ExternalEvent);
        Assert.Equal(0x1DUL, (ulong)NativeSmartCoordinatorCycleFlags.Known);
        Assert.Equal(0xFFFFUL, (ulong)NativeSmartCoordinatorInputValidity.Known);
        Assert.Equal(0x1FFFUL, (ulong)NativeSmartCoordinatorInputFlags.Known);
        Assert.Equal(0xFFUL, (ulong)NativeSmartCoordinatorActionValidity.Known);
        Assert.Equal(
            ((1UL << 38) - 1) & ~((1UL << 5) | (1UL << 15) | (1UL << 16) | (1UL << 36)),
            (ulong)NativeSmartCoordinatorReason.Known);
        Assert.Equal(11, (int)NativeSmartCoordinatorStatus.RecreateRequired);
    }

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(
            new IntPtr(expected),
            Marshal.OffsetOf<T>(fieldName));
}
