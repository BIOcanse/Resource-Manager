using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativePublicServiceCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0003_0000U, NativePublicServiceCoordinatorAbi.Version);
        Assert.Equal(224, Unsafe.SizeOf<NativePublicServiceCoordinatorConfiguration>());
        Assert.Equal(112, Unsafe.SizeOf<NativePublicServiceCoordinatorCapacity>());
        Assert.Equal(32, Unsafe.SizeOf<NativePublicServiceCapabilityInput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceRouteInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativePublicServiceCatalogReplaceInput>());
        Assert.Equal(40, Unsafe.SizeOf<NativePublicServiceModelInput>());
        Assert.Equal(40, Unsafe.SizeOf<NativePublicServiceModelAliasInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativePublicServiceModelReplaceInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceModelAcquisitionPlanInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePublicServiceModelAcquisitionPlanOutput>());
        Assert.Equal(
            56,
            Unsafe.SizeOf<NativePublicServiceModelAcquisitionCompletionInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativePublicServiceAccessInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativePublicServiceAccessOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceRequestCompleteInput>());
        Assert.Equal(40, Unsafe.SizeOf<NativePublicServiceRequestCompleteOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceModelResolveInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePublicServiceModelResolveOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePublicServiceLeaseBeginInput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceLeaseOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceLeaseEndInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativePublicServiceSubscriptionUpsertInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePublicServiceSubscriptionOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceSubscriptionRemoveInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativePublicServiceTaskEnqueueInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativePublicServiceTaskOutput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceTaskPlanInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceTaskPlanOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativePublicServiceTaskCompletionInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceTaskCompletionOutput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceTaskCancelInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceTaskCancelOutput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePublicServiceCapabilityOutput>());
        Assert.Equal(144, Unsafe.SizeOf<NativePublicServiceCoordinatorSnapshot>());

        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.Generation),
            8);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.MaximumCapabilityCount),
            32);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.CapabilityIndexCapacity),
            68);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.RetryableTaskOutcomeMask),
            120);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.RateWindowMilliseconds),
            128);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.ResidentByteBudget),
            176);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.Flags),
            184);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(
                NativePublicServiceCoordinatorConfiguration
                    .ModelCatalogAcquisitionIntervalMilliseconds),
            192);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.MaximumTaskAttemptCount),
            216);
        AssertOffset<NativePublicServiceCoordinatorConfiguration>(
            nameof(NativePublicServiceCoordinatorConfiguration.RetryableHttpStatusPolicyMask),
            220);
        AssertOffset<NativePublicServiceRouteInput>(
            nameof(NativePublicServiceRouteInput.Path),
            24);
        AssertOffset<NativePublicServiceAccessOutput>(
            nameof(NativePublicServiceAccessOutput.RequestHandle),
            16);
        AssertOffset<NativePublicServiceTaskOutput>(
            nameof(NativePublicServiceTaskOutput.BaseScore),
            40);
        AssertOffset<NativePublicServiceTaskCompletionInput>(
            nameof(NativePublicServiceTaskCompletionInput.Attempt),
            32);
        AssertOffset<NativePublicServiceTaskCompletionInput>(
            nameof(NativePublicServiceTaskCompletionInput.HttpStatusCode),
            36);
        AssertOffset<NativePublicServiceTaskCompletionOutput>(
            nameof(NativePublicServiceTaskCompletionOutput.NextWakeMonotonicMilliseconds),
            24);
        AssertOffset<NativePublicServiceCoordinatorSnapshot>(
            nameof(NativePublicServiceCoordinatorSnapshot.Flags),
            112);
    }

    [Fact]
    public void PublishedEnumsAndMasksMatchTheZigContract()
    {
        Assert.Equal(7, (int)NativePublicServiceCoordinatorStatus.OutOfMemory);
        Assert.Equal(1UL, (ulong)NativePublicServiceCoordinatorConfigurationFlags.Known);
        Assert.Equal(3U, (uint)NativePublicServiceCapabilityFlags.Known);
        Assert.Equal(7U, (uint)NativePublicServiceRouteFlags.Known);
        Assert.Equal(3U, (uint)NativePublicServiceModelFlags.Known);
        Assert.Equal(0x3FU, (uint)NativePublicServiceMethodMask.Known);
        Assert.Equal(0U, (uint)NativePublicServiceRequestFlags.Known);
        Assert.Equal(1U, (uint)NativePublicServiceSubscriptionFlags.Known);
        Assert.Equal(3UL, (ulong)NativePublicServiceTaskPlanFlags.Known);
        Assert.Equal(15UL, (ulong)NativePublicServiceCoordinatorSnapshotFlags.Known);
        Assert.Equal(9U, (uint)NativePublicServiceAccessReason.CoordinatorCapacity);
        Assert.Equal(3U, (uint)NativePublicServiceModelResolveStatus.CatalogUnavailable);
        Assert.Equal(2U, (uint)NativePublicServiceTaskKind.Unload);
        Assert.Equal(2U, (uint)NativePublicServiceTaskState.Running);
        Assert.Equal(7U, (uint)NativePublicServiceTaskEffectOutcome.Failed);
        Assert.Equal(8U, (uint)NativePublicServiceTaskEffectOutcome.Cancelled);
        Assert.Equal(0x0000_00DCU, (uint)NativePublicServiceTaskOutcomeMask.Known);
        Assert.Equal(
            0x0000_0007U,
            (uint)NativePublicServiceTaskHttpRetryPolicyMask.Known);
        Assert.Equal(
            3U,
            (uint)NativePublicServiceTaskCompletionDisposition.RetryScheduled);
    }

    [Fact]
    public void RealNativeLibraryCreatesAndReportsExactCapacity()
    {
        var configuration = CreateConfiguration();
        using var session = new NativePublicServiceCoordinatorSession(in configuration);

        Assert.Equal(NativePublicServiceCoordinatorAbi.Version, NativePublicServiceCoordinatorSession.GetAbiVersion());
        Assert.Equal(configuration.MaximumCapabilityCount, session.Capacity.CapabilityCapacity);
        Assert.Equal(configuration.MaximumRouteCount, session.Capacity.RouteCapacity);
        Assert.Equal(configuration.MaximumModelCount, session.Capacity.ModelCapacity);
        Assert.Equal(configuration.MaximumModelAliasCount, session.Capacity.ModelAliasCapacity);
        Assert.Equal(configuration.MaximumTaskCount, session.Capacity.TaskCapacity);
        Assert.InRange(session.Capacity.ResidentByteCount, 1UL, configuration.ResidentByteBudget);

        Assert.Equal(
            NativePublicServiceCoordinatorStatus.Ok,
            session.Snapshot(out var snapshot));
        Assert.Equal(configuration.Generation, snapshot.Generation);
        Assert.Equal(configuration.SessionInstanceLow, snapshot.SessionInstanceLow);
        Assert.Equal(configuration.SessionInstanceHigh, snapshot.SessionInstanceHigh);
    }

    private static NativePublicServiceCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativePublicServiceCoordinatorAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativePublicServiceCoordinatorConfiguration>()),
            Generation = 1,
            SessionInstanceLow = 1,
            SessionInstanceHigh = 2,
            MaximumCapabilityCount = 4,
            MaximumRouteCount = 8,
            MaximumModelCount = 8,
            MaximumModelAliasCount = 16,
            MaximumRequestCount = 8,
            MaximumRateBucketCount = 8,
            MaximumLeaseCount = 8,
            MaximumSubscriptionCount = 8,
            MaximumTaskCount = 8,
            CapabilityIndexCapacity = 8,
            ModelIndexCapacity = 16,
            AliasIndexCapacity = 32,
            RequestIndexCapacity = 16,
            RateBucketIndexCapacity = 16,
            LeaseIndexCapacity = 16,
            SubscriptionIndexCapacity = 16,
            TaskIndexCapacity = 16,
            MaximumCatalogTextBytes = 1024,
            MaximumModelTextBytes = 2048,
            MaximumConcurrentModelTasks = 2,
            MaximumRequestsPerRateWindow = 16,
            MaximumInflightRequestsPerCaller = 4,
            RetryableTaskOutcomeMask = (uint)(
                NativePublicServiceTaskOutcomeMask.ProviderUnavailable
                | NativePublicServiceTaskOutcomeMask.Timeout
                | NativePublicServiceTaskOutcomeMask.TransportFailure),
            RateWindowMilliseconds = 1000,
            RequestTimeoutMilliseconds = 30000,
            LeaseTimeoutMilliseconds = 30000,
            SubscriptionTimeoutMilliseconds = 30000,
            TaskTimeoutMilliseconds = 30000,
            RetryDelayMilliseconds = 1000,
            ResidentByteBudget = 4 * 1024 * 1024,
            Flags = (ulong)NativePublicServiceCoordinatorConfigurationFlags.LoopbackOnly,
            ModelCatalogAcquisitionIntervalMilliseconds = 10_000,
            ModelCatalogLastGoodLifetimeMilliseconds = 60_000,
            ModelCatalogAcquisitionTimeoutMilliseconds = 5_000,
            MaximumTaskAttemptCount = 3,
            RetryableHttpStatusPolicyMask =
                (uint)NativePublicServiceTaskHttpRetryPolicyMask.Known
        };

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
