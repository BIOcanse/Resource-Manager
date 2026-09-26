using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeAppliedOwnershipAbiTests
{
    [Fact]
    public void FixedPodLayoutsMatchZigAbiV2()
    {
        Assert.Equal(0x0002_0000U, NativeAppliedOwnershipAbi.Version);
        Assert.Equal(0x524d_4150_4f57_4e32UL, NativeAppliedOwnershipAbi.ImageMagic);

        AssertLayout<NativeAppliedOwnershipCreateConfiguration>(96,
            (nameof(NativeAppliedOwnershipCreateConfiguration.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipCreateConfiguration.StructSize), 4),
            (nameof(NativeAppliedOwnershipCreateConfiguration.LedgerInstanceLow), 8),
            (nameof(NativeAppliedOwnershipCreateConfiguration.LedgerInstanceHigh), 16),
            (nameof(NativeAppliedOwnershipCreateConfiguration.RecordCapacity), 24),
            (nameof(NativeAppliedOwnershipCreateConfiguration.PrimaryIndexCapacity), 28),
            (nameof(NativeAppliedOwnershipCreateConfiguration.PayloadIndexCapacity), 32),
            (nameof(NativeAppliedOwnershipCreateConfiguration.Flags), 36),
            (nameof(NativeAppliedOwnershipCreateConfiguration.MaximumResidentBytes), 40),
            (nameof(NativeAppliedOwnershipCreateConfiguration.MaximumImageBytes), 48),
            (nameof(NativeAppliedOwnershipCreateConfiguration.Reserved), 56));

        AssertLayout<NativeAppliedOwnershipOpenConfiguration>(72,
            (nameof(NativeAppliedOwnershipOpenConfiguration.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipOpenConfiguration.StructSize), 4),
            (nameof(NativeAppliedOwnershipOpenConfiguration.MaximumRecordCapacity), 8),
            (nameof(NativeAppliedOwnershipOpenConfiguration.MaximumPrimaryIndexCapacity), 12),
            (nameof(NativeAppliedOwnershipOpenConfiguration.MaximumPayloadIndexCapacity), 16),
            (nameof(NativeAppliedOwnershipOpenConfiguration.Flags), 20),
            (nameof(NativeAppliedOwnershipOpenConfiguration.MaximumResidentBytes), 24),
            (nameof(NativeAppliedOwnershipOpenConfiguration.MaximumImageBytes), 32),
            (nameof(NativeAppliedOwnershipOpenConfiguration.Reserved), 40));

        AssertLayout<NativeAppliedOwnershipCapacity>(64,
            (nameof(NativeAppliedOwnershipCapacity.StructSize), 0),
            (nameof(NativeAppliedOwnershipCapacity.RecordCapacity), 4),
            (nameof(NativeAppliedOwnershipCapacity.PrimaryIndexCapacity), 8),
            (nameof(NativeAppliedOwnershipCapacity.PayloadIndexCapacity), 12),
            (nameof(NativeAppliedOwnershipCapacity.RecordSize), 16),
            (nameof(NativeAppliedOwnershipCapacity.Flags), 20),
            (nameof(NativeAppliedOwnershipCapacity.MaximumImageBytes), 24),
            (nameof(NativeAppliedOwnershipCapacity.ResidentBytes), 32),
            (nameof(NativeAppliedOwnershipCapacity.Reserved), 40));

        AssertLayout<NativeAppliedOwnershipPrimaryIdentity>(40,
            (nameof(NativeAppliedOwnershipPrimaryIdentity.Scope), 0),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.ReservedUInt32), 4),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.TargetId), 8),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.SoftwareId), 16),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.ProcessStartKey), 24),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.ProcessId), 32),
            (nameof(NativeAppliedOwnershipPrimaryIdentity.ProcessReserved), 36));

        AssertLayout<NativeAppliedOwnershipActionIdentity>(64,
            (nameof(NativeAppliedOwnershipActionIdentity.ConfigurationGeneration), 0),
            (nameof(NativeAppliedOwnershipActionIdentity.PlanEpoch), 8),
            (nameof(NativeAppliedOwnershipActionIdentity.ActionId), 16),
            (nameof(NativeAppliedOwnershipActionIdentity.HostSessionIncarnation), 24),
            (nameof(NativeAppliedOwnershipActionIdentity.TargetId), 32),
            (nameof(NativeAppliedOwnershipActionIdentity.SoftwareId), 40),
            (nameof(NativeAppliedOwnershipActionIdentity.ProcessStartKey), 48),
            (nameof(NativeAppliedOwnershipActionIdentity.ProcessId), 56),
            (nameof(NativeAppliedOwnershipActionIdentity.Reserved), 60));

        AssertLayout<NativeAppliedOwnershipOriginalBinding>(160,
            (nameof(NativeAppliedOwnershipOriginalBinding.JournalInstanceLow), 0),
            (nameof(NativeAppliedOwnershipOriginalBinding.JournalInstanceHigh), 8),
            (nameof(NativeAppliedOwnershipOriginalBinding.ActionIdentity), 16),
            (nameof(NativeAppliedOwnershipOriginalBinding.Scope), 80),
            (nameof(NativeAppliedOwnershipOriginalBinding.Disposition), 84),
            (nameof(NativeAppliedOwnershipOriginalBinding.DomainMask), 88),
            (nameof(NativeAppliedOwnershipOriginalBinding.GradeValidMask), 92),
            (nameof(NativeAppliedOwnershipOriginalBinding.ProcessFromGrade), 96),
            (nameof(NativeAppliedOwnershipOriginalBinding.ProcessToGrade), 100),
            (nameof(NativeAppliedOwnershipOriginalBinding.CpuFromGrade), 104),
            (nameof(NativeAppliedOwnershipOriginalBinding.CpuToGrade), 108),
            (nameof(NativeAppliedOwnershipOriginalBinding.GpuFromGrade), 112),
            (nameof(NativeAppliedOwnershipOriginalBinding.GpuToGrade), 116),
            (nameof(NativeAppliedOwnershipOriginalBinding.StableSystemStatus), 120),
            (nameof(NativeAppliedOwnershipOriginalBinding.StableSystemError), 124),
            (nameof(NativeAppliedOwnershipOriginalBinding.MaximumRecoveryAttempts), 128),
            (nameof(NativeAppliedOwnershipOriginalBinding.RecoveryReserved), 132),
            (nameof(NativeAppliedOwnershipOriginalBinding.RecoveryDeadlineUtcMilliseconds), 136),
            (nameof(NativeAppliedOwnershipOriginalBinding.AtomicGroupId), 144),
            (nameof(NativeAppliedOwnershipOriginalBinding.GroupMemberIndex), 152),
            (nameof(NativeAppliedOwnershipOriginalBinding.GroupMemberCount), 156));

        AssertLayout<NativeAppliedOwnershipDurablePayloadReference>(32,
            (nameof(NativeAppliedOwnershipDurablePayloadReference.Slot), 0),
            (nameof(NativeAppliedOwnershipDurablePayloadReference.Generation), 4),
            (nameof(NativeAppliedOwnershipDurablePayloadReference.Length), 8),
            (nameof(NativeAppliedOwnershipDurablePayloadReference.DigestLow), 16),
            (nameof(NativeAppliedOwnershipDurablePayloadReference.DigestHigh), 24));

        AssertLayout<NativeAppliedOwnershipCurrentGrades>(24,
            (nameof(NativeAppliedOwnershipCurrentGrades.ValidMask), 0),
            (nameof(NativeAppliedOwnershipCurrentGrades.ReservedUInt32), 4),
            (nameof(NativeAppliedOwnershipCurrentGrades.ProcessGrade), 8),
            (nameof(NativeAppliedOwnershipCurrentGrades.CpuGrade), 12),
            (nameof(NativeAppliedOwnershipCurrentGrades.GpuGrade), 16),
            (nameof(NativeAppliedOwnershipCurrentGrades.ReservedInt32), 20));

        AssertLayout<NativeAppliedOwnershipCas>(192,
            (nameof(NativeAppliedOwnershipCas.Primary), 0),
            (nameof(NativeAppliedOwnershipCas.JournalInstanceLow), 40),
            (nameof(NativeAppliedOwnershipCas.JournalInstanceHigh), 48),
            (nameof(NativeAppliedOwnershipCas.OriginalActionIdentity), 56),
            (nameof(NativeAppliedOwnershipCas.Payload), 120),
            (nameof(NativeAppliedOwnershipCas.ExpectedRecordRevision), 152),
            (nameof(NativeAppliedOwnershipCas.ExpectedCurrentGrades), 160),
            (nameof(NativeAppliedOwnershipCas.Reserved), 184));

        AssertLayout<NativeAppliedOwnershipRecord>(320,
            (nameof(NativeAppliedOwnershipRecord.Primary), 0),
            (nameof(NativeAppliedOwnershipRecord.OriginalBinding), 40),
            (nameof(NativeAppliedOwnershipRecord.Payload), 200),
            (nameof(NativeAppliedOwnershipRecord.CurrentGrades), 232),
            (nameof(NativeAppliedOwnershipRecord.RecordRevision), 256),
            (nameof(NativeAppliedOwnershipRecord.PromotedAtUtcMilliseconds), 264),
            (nameof(NativeAppliedOwnershipRecord.UpdatedAtUtcMilliseconds), 272),
            (nameof(NativeAppliedOwnershipRecord.Flags), 280),
            (nameof(NativeAppliedOwnershipRecord.ReservedUInt32), 284),
            (nameof(NativeAppliedOwnershipRecord.Reserved), 288));

        AssertLayout<NativeAppliedOwnershipPromoteInput>(320,
            (nameof(NativeAppliedOwnershipPromoteInput.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipPromoteInput.StructSize), 4),
            (nameof(NativeAppliedOwnershipPromoteInput.ExpectedLedgerRevision), 8),
            (nameof(NativeAppliedOwnershipPromoteInput.Primary), 16),
            (nameof(NativeAppliedOwnershipPromoteInput.OriginalBinding), 56),
            (nameof(NativeAppliedOwnershipPromoteInput.Payload), 216),
            (nameof(NativeAppliedOwnershipPromoteInput.CurrentGrades), 248),
            (nameof(NativeAppliedOwnershipPromoteInput.PromotedAtUtcMilliseconds), 272),
            (nameof(NativeAppliedOwnershipPromoteInput.Reserved), 280));

        AssertLayout<NativeAppliedOwnershipTransitionInput>(448,
            (nameof(NativeAppliedOwnershipTransitionInput.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipTransitionInput.StructSize), 4),
            (nameof(NativeAppliedOwnershipTransitionInput.ExpectedLedgerRevision), 8),
            (nameof(NativeAppliedOwnershipTransitionInput.Cas), 16),
            (nameof(NativeAppliedOwnershipTransitionInput.TransitionBinding), 208),
            (nameof(NativeAppliedOwnershipTransitionInput.TransitionPayload), 368),
            (nameof(NativeAppliedOwnershipTransitionInput.NewCurrentGrades), 400),
            (nameof(NativeAppliedOwnershipTransitionInput.UpdatedAtUtcMilliseconds), 424),
            (nameof(NativeAppliedOwnershipTransitionInput.Reserved), 432));

        AssertLayout<NativeAppliedOwnershipRemoveInput>(224,
            (nameof(NativeAppliedOwnershipRemoveInput.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipRemoveInput.StructSize), 4),
            (nameof(NativeAppliedOwnershipRemoveInput.ExpectedLedgerRevision), 8),
            (nameof(NativeAppliedOwnershipRemoveInput.Cas), 16),
            (nameof(NativeAppliedOwnershipRemoveInput.RemovedAtUtcMilliseconds), 208),
            (nameof(NativeAppliedOwnershipRemoveInput.Reserved), 216));

        AssertLayout<NativeAppliedOwnershipSnapshotHeader>(96,
            (nameof(NativeAppliedOwnershipSnapshotHeader.AbiVersion), 0),
            (nameof(NativeAppliedOwnershipSnapshotHeader.StructSize), 4),
            (nameof(NativeAppliedOwnershipSnapshotHeader.LedgerRevision), 8),
            (nameof(NativeAppliedOwnershipSnapshotHeader.LedgerInstanceLow), 16),
            (nameof(NativeAppliedOwnershipSnapshotHeader.LedgerInstanceHigh), 24),
            (nameof(NativeAppliedOwnershipSnapshotHeader.EntryCount), 32),
            (nameof(NativeAppliedOwnershipSnapshotHeader.RecordCapacity), 36),
            (nameof(NativeAppliedOwnershipSnapshotHeader.PrimaryIndexCapacity), 40),
            (nameof(NativeAppliedOwnershipSnapshotHeader.PayloadIndexCapacity), 44),
            (nameof(NativeAppliedOwnershipSnapshotHeader.MaximumImageBytes), 48),
            (nameof(NativeAppliedOwnershipSnapshotHeader.ResidentBytes), 56),
            (nameof(NativeAppliedOwnershipSnapshotHeader.Reserved), 64));

        AssertLayout<NativeAppliedOwnershipImageHeader>(128,
            (nameof(NativeAppliedOwnershipImageHeader.Magic), 0),
            (nameof(NativeAppliedOwnershipImageHeader.AbiVersion), 8),
            (nameof(NativeAppliedOwnershipImageHeader.HeaderSize), 12),
            (nameof(NativeAppliedOwnershipImageHeader.RecordSize), 16),
            (nameof(NativeAppliedOwnershipImageHeader.Flags), 20),
            (nameof(NativeAppliedOwnershipImageHeader.LedgerInstanceLow), 24),
            (nameof(NativeAppliedOwnershipImageHeader.LedgerInstanceHigh), 32),
            (nameof(NativeAppliedOwnershipImageHeader.LedgerRevision), 40),
            (nameof(NativeAppliedOwnershipImageHeader.EntryCount), 48),
            (nameof(NativeAppliedOwnershipImageHeader.RecordCapacity), 52),
            (nameof(NativeAppliedOwnershipImageHeader.ImageLength), 56),
            (nameof(NativeAppliedOwnershipImageHeader.Crc64Ecma), 64),
            (nameof(NativeAppliedOwnershipImageHeader.PrimaryIndexCapacity), 72),
            (nameof(NativeAppliedOwnershipImageHeader.PayloadIndexCapacity), 76),
            (nameof(NativeAppliedOwnershipImageHeader.MaximumImageBytes), 80),
            (nameof(NativeAppliedOwnershipImageHeader.Reserved), 88));
    }

    [Fact]
    public void StatusAndKnownBitValuesMatchZigProtocol()
    {
        Assert.Equal(19, (int)NativeAppliedOwnershipStatus.InvalidTime);
        Assert.Equal(1U, (uint)NativeAppliedOwnershipScope.Process);
        Assert.Equal(2U, (uint)NativeAppliedOwnershipScope.Adapter);
        Assert.Equal(1U, (uint)NativeAppliedOwnershipJournalScope.Process);
        Assert.Equal(2U, (uint)NativeAppliedOwnershipJournalScope.Software);
        Assert.Equal(1U, (uint)NativeAppliedOwnershipDisposition.Apply);
        Assert.Equal(2U, (uint)NativeAppliedOwnershipDisposition.Restore);
        Assert.Equal(0xFU, (uint)NativeAppliedOwnershipDomain.Known);
        Assert.Equal(0xFU, (uint)NativeAppliedOwnershipGradeValidity.Known);
        Assert.Equal(-4, (int)NativeAppliedOwnershipProcessGrade.Level4);
        Assert.Equal(1, (int)NativeAppliedOwnershipProcessGrade.A1);
        Assert.Equal(0, (int)NativeAppliedOwnershipAdapterGrade.Freeze);
        Assert.Equal(3, (int)NativeAppliedOwnershipAdapterGrade.Extreme);
    }

    private static void AssertLayout<T>(
        int expectedSize,
        params (string Field, int Offset)[] fields)
        where T : struct
    {
        Assert.Equal(expectedSize, Marshal.SizeOf<T>());
        foreach (var (field, offset) in fields)
        {
            Assert.Equal(offset, Marshal.OffsetOf<T>(field).ToInt32());
        }
    }
}
