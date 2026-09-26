using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSamplingSubscriptionAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0003_0000U, NativeSamplingSubscriptionAbi.Version);
        Assert.Equal(112, Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSamplingSubscriptionCapacity>());
        Assert.Equal(32, Unsafe.SizeOf<NativeSamplingSubscriptionItemReference>());
        Assert.Equal(96, Unsafe.SizeOf<NativeSamplingSubscriptionTrackInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeSamplingSubscriptionRemoveInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSamplingSubscriptionControlInput>());
        Assert.Equal(120, Unsafe.SizeOf<NativeSamplingSubscriptionPlanHeader>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSamplingSubscriptionDueItem>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSamplingSubscriptionSourceView>());
        Assert.Equal(48, Unsafe.SizeOf<NativeSamplingSubscriptionExpiredSource>());
        Assert.Equal(88, Unsafe.SizeOf<NativeSamplingSubscriptionCompletionInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeSamplingSubscriptionCompletionOutput>());
        Assert.Equal(112, Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSamplingSubscriptionItemState>());

        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.Generation), 8);
        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.MaximumSourceCount), 16);
        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.DefaultIntervalMilliseconds), 40);
        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.Flags), 72);
        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.ReservedU64_0), 80);
        AssertOffset<NativeSamplingSubscriptionConfiguration>(nameof(NativeSamplingSubscriptionConfiguration.ReservedU32_1), 108);
        AssertOffset<NativeSamplingSubscriptionTrackInput>(nameof(NativeSamplingSubscriptionTrackInput.SourceHandle), 40);
        AssertOffset<NativeSamplingSubscriptionTrackInput>(nameof(NativeSamplingSubscriptionTrackInput.ValidMask), 56);
        AssertOffset<NativeSamplingSubscriptionTrackInput>(nameof(NativeSamplingSubscriptionTrackInput.Reserved), 72);
        AssertOffset<NativeSamplingSubscriptionPlanHeader>(nameof(NativeSamplingSubscriptionPlanHeader.DueItemCapacity), 48);
        AssertOffset<NativeSamplingSubscriptionPlanHeader>(nameof(NativeSamplingSubscriptionPlanHeader.NextWakeMilliseconds), 80);
        AssertOffset<NativeSamplingSubscriptionPlanHeader>(nameof(NativeSamplingSubscriptionPlanHeader.RequestFlags), 104);
        AssertOffset<NativeSamplingSubscriptionDueItem>(nameof(NativeSamplingSubscriptionDueItem.ScheduledDueMilliseconds), 56);
        AssertOffset<NativeSamplingSubscriptionCompletionInput>(nameof(NativeSamplingSubscriptionCompletionInput.ValidMask), 48);
        AssertOffset<NativeSamplingSubscriptionCompletionInput>(nameof(NativeSamplingSubscriptionCompletionInput.Reserved), 72);
        AssertOffset<NativeSamplingSubscriptionSnapshotHeader>(nameof(NativeSamplingSubscriptionSnapshotHeader.ActiveSourceCount), 56);
        AssertOffset<NativeSamplingSubscriptionSnapshotHeader>(nameof(NativeSamplingSubscriptionSnapshotHeader.Flags), 88);
        AssertOffset<NativeSamplingSubscriptionItemState>(nameof(NativeSamplingSubscriptionItemState.LastPlannedEpoch), 40);
        AssertOffset<NativeSamplingSubscriptionItemState>(nameof(NativeSamplingSubscriptionItemState.ScheduledDueMilliseconds), 56);
    }

    [Fact]
    public void PublishedMasksAndStatusesMatchTheZigContract()
    {
        Assert.Equal(0x03U, (uint)NativeSamplingSubscriptionSourceFlags.Known);
        Assert.Equal(0x00UL, (ulong)NativeSamplingSubscriptionConfigurationFlags.Known);
        Assert.Equal(0x0FUL, (ulong)NativeSamplingSubscriptionPlanFlags.Known);
        Assert.Equal(0x00UL, (ulong)NativeSamplingSubscriptionPlanRequestFlags.Known);
        Assert.Equal(0x00U, (uint)NativeSamplingSubscriptionDueItemFlags.Known);
        Assert.Equal(0x03UL, (ulong)NativeSamplingSubscriptionCompletionFlags.Known);
        Assert.Equal(0x0FUL, (ulong)NativeSamplingSubscriptionTrackValidity.Required);
        Assert.Equal(0x1FUL, (ulong)NativeSamplingSubscriptionTrackValidity.Known);
        Assert.Equal(0x03UL, (ulong)NativeSamplingSubscriptionRemoveValidity.Required);
        Assert.Equal(0x01UL, (ulong)NativeSamplingSubscriptionPlanValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeSamplingSubscriptionCompletionValidity.Required);
        Assert.Equal(1U, (uint)NativeSamplingSubscriptionCompletionStatus.Sampled);
        Assert.Equal(3U, (uint)NativeSamplingSubscriptionCompletionStatus.Skipped);
        Assert.Equal(7, (int)NativeSamplingSubscriptionStatus.OutOfMemory);
    }

    [Fact]
    public void RootDllExportsCreateAndCapacityRoundTrip()
    {
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
            Generation = 1,
            MaximumSourceCount = 4,
            MaximumItemCount = 4,
            MaximumMembershipCount = 8,
            MaximumDueItemCount = 4,
            MaximumSourceViewCount = 4,
            MaximumExpiredSourceCount = 4,
            DefaultIntervalMilliseconds = 1000,
            MinimumIntervalMilliseconds = 100,
            ActiveTtlMilliseconds = 5000,
            MaximumFutureSkewMilliseconds = 1000,
            Flags = 0,
            ReservedU64_0 = 0,
            ReservedU64_1 = 0,
            ReservedU64_2 = 0,
            ReservedU32_0 = 0,
            ReservedU32_1 = 0
        };

        Assert.Equal(NativeSamplingSubscriptionAbi.Version, NativeSamplingSubscriptionSession.GetAbiVersion());
        using var session = new NativeSamplingSubscriptionSession(in configuration);
        Assert.Equal(4U, session.Capacity.SourceCapacity);
        Assert.Equal(4U, session.Capacity.ItemCapacity);
        Assert.Equal(8U, session.Capacity.MembershipCapacity);
    }

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
