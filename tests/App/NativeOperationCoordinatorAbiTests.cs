using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeOperationCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0001_0000U, NativeOperationCoordinatorAbi.Version);
        Assert.Equal(16, Unsafe.SizeOf<NativeOperationHandle128>());
        Assert.Equal(168, Unsafe.SizeOf<NativeOperationCoordinatorConfiguration>());
        Assert.Equal(112, Unsafe.SizeOf<NativeOperationCoordinatorCapacity>());
        Assert.Equal(224, Unsafe.SizeOf<NativeOperationSubmitInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeOperationSubmitOutput>());
        Assert.Equal(96, Unsafe.SizeOf<NativeOperationCancelInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeOperationCancelOutput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeOperationPlanInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativeOperationPlanOutput>());
        Assert.Equal(112, Unsafe.SizeOf<NativeOperationActionOutput>());
        Assert.Equal(168, Unsafe.SizeOf<NativeOperationActionFeedbackInput>());
        Assert.Equal(152, Unsafe.SizeOf<NativeOperationCompletionInput>());
        Assert.Equal(192, Unsafe.SizeOf<NativeOperationProgressInput>());
        Assert.Equal(120, Unsafe.SizeOf<NativeOperationSnapshotOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeOperationReadInput>());
        Assert.Equal(400, Unsafe.SizeOf<NativeOperationOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeOperationPersistenceInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativeOperationPersistenceImportInput>());
        Assert.Equal(200, Unsafe.SizeOf<NativeOperationPersistenceHeader>());

        AssertOffset<NativeOperationCoordinatorConfiguration>(
            nameof(NativeOperationCoordinatorConfiguration.SessionInstanceId),
            16);
        AssertOffset<NativeOperationCoordinatorConfiguration>(
            nameof(NativeOperationCoordinatorConfiguration.MaximumOperationCount),
            48);
        AssertOffset<NativeOperationCoordinatorConfiguration>(
            nameof(NativeOperationCoordinatorConfiguration.MaximumFutureSkewMilliseconds),
            96);
        AssertOffset<NativeOperationCoordinatorConfiguration>(
            nameof(NativeOperationCoordinatorConfiguration.Reserved),
            128);
        AssertOffset<NativeOperationSubmitInput>(
            nameof(NativeOperationSubmitInput.OperationId),
            24);
        AssertOffset<NativeOperationSubmitInput>(
            nameof(NativeOperationSubmitInput.ValidMask),
            176);
        AssertOffset<NativeOperationActionOutput>(
            nameof(NativeOperationActionOutput.AttemptToken),
            40);
        AssertOffset<NativeOperationActionOutput>(
            nameof(NativeOperationActionOutput.Flags),
            80);
        AssertOffset<NativeOperationOutput>(
            nameof(NativeOperationOutput.ProgressSequence),
            264);
        AssertOffset<NativeOperationOutput>(
            nameof(NativeOperationOutput.AttemptConfigurationGeneration),
            368);
        AssertOffset<NativeOperationPersistenceHeader>(
            nameof(NativeOperationPersistenceHeader.OperationCount),
            136);
        AssertOffset<NativeOperationPersistenceHeader>(
            nameof(NativeOperationPersistenceHeader.ChecksumHigh),
            152);
    }

    [Fact]
    public void PublishedEnumsAndMasksMatchTheZigContract()
    {
        Assert.Equal(10U, (uint)NativeOperationState.StateUncertain);
        Assert.Equal(3U, (uint)NativeOperationActionKind.Recover);
        Assert.Equal(
            10U,
            (uint)NativeOperationActionFeedbackOutcome.RecoveredUncertain);
        Assert.Equal(5U, (uint)NativeOperationCompletionOutcome.StateUncertain);
        Assert.Equal(7UL, (ulong)NativeOperationSubmitValidity.Known);
        Assert.Equal(0x1FFUL, (ulong)NativeOperationFlags.Known);
        Assert.Equal(7U, (uint)NativeOperationSubmitOutputFlags.Known);
        Assert.Equal(7UL, (ulong)NativeOperationActionFlags.Known);
        Assert.Equal(3UL, (ulong)NativeOperationResultValidity.Known);
        Assert.Equal(0x7FUL, (ulong)NativeOperationProgressValidity.Known);
        Assert.Equal(1UL, (ulong)NativeOperationSnapshotFlags.Known);
    }

    [Fact]
    public void RealNativeLibraryCreatesAndReportsExactCapacity()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeOperationCoordinatorSession(in configuration);

        Assert.Equal(
            NativeOperationCoordinatorAbi.Version,
            NativeOperationCoordinatorSession.GetAbiVersion());
        Assert.Equal(
            configuration.MaximumOperationCount,
            session.Capacity.OperationCapacity);
        Assert.Equal(
            configuration.MaximumActionCount,
            session.Capacity.ActionCapacity);
        Assert.Equal(
            configuration.SessionInstanceId,
            session.Capacity.SessionInstanceId);
        Assert.InRange(
            session.Capacity.ResidentByteCount,
            1UL,
            configuration.ResidentByteBudget);
        Assert.Equal(
            NativeOperationCoordinatorStatus.Ok,
            session.Snapshot(out var snapshot));
        Assert.Equal(configuration.Generation, snapshot.ConfigurationGeneration);
        Assert.Equal(0U, snapshot.OperationCount);
    }

    private static NativeOperationCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize =
                checked((uint)Unsafe.SizeOf<NativeOperationCoordinatorConfiguration>()),
            Generation = 1,
            SessionInstanceId = new(1, 2),
            ClockInstanceId = new(3, 4),
            MaximumOperationCount = 8,
            MaximumDomainCount = 8,
            MaximumActionCount = 8,
            OperationIndexCapacity = 16,
            DomainIndexCapacity = 16,
            MaximumGlobalRunningCount = 2,
            MaximumRecentTerminalCount = 8,
            MaximumReadCount = 8,
            MaximumStartActionsPerPlan = 2,
            MaximumCancelActionsPerPlan = 2,
            MaximumRecoverActionsPerPlan = 2,
            MaximumFutureSkewMilliseconds = 30_000,
            MaximumPersistenceByteCount = 200 + 8 * 400,
            ResidentByteBudget = 1024 * 1024
        };

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
