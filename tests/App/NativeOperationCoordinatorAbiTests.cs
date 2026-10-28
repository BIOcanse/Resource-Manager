using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeOperationCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0002_0000U, NativeOperationCoordinatorAbi.Version);
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
        Assert.Equal(1UL, (ulong)NativeOperationSubmitFlags.RetainOwnedCompletion);
        Assert.Equal(1UL, (ulong)NativeOperationSubmitFlags.Known);
        Assert.Equal(0x3FFUL, (ulong)NativeOperationFlags.Known);
        Assert.Equal(1UL << 9, (ulong)NativeOperationFlags.RetainOwnedCompletion);
        Assert.Equal(7U, (uint)NativeOperationSubmitOutputFlags.Known);
        Assert.Equal(15UL, (ulong)NativeOperationActionFlags.Known);
        Assert.Equal(1UL << 3, (ulong)NativeOperationActionFlags.RetainOwnedCompletion);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalImportPreservesFinalRecordRevisionWhileSessionRevisionAdvances(bool newClock)
    {
        var plan = NativeOperationScratchTests.Plan();
        var sessionId = new NativeOperationHandle128(1, 2);
        var originalClock = new NativeOperationHandle128(3, 4);
        NativeOperationCanonicalImage image;
        using (var source = new NativeOperationCoordinatorWorkspace(plan, sessionId, originalClock, OperationScratchFixture.Create))
        {
            var submit = new NativeOperationSubmitInput
            {
                OperationId = new(11, 12), KindHandle = new(21, 22), RequestHandle = new(31, 32),
                MaximumAttempts = 1, RetryDelayMilliseconds = 1,
                ExecutionTimeoutMilliseconds = 30_000, CancelGraceMilliseconds = 5_000,
                TerminalRetentionMilliseconds = 60_000,
                CreatedUtcMilliseconds = 1_000, ObservedUtcMilliseconds = 1_000,
                CreatedMonotonicMilliseconds = 100, ObservedMonotonicMilliseconds = 100,
                ValidMask = (ulong)NativeOperationSubmitValidity.Request
            };
            source.SubmitLocked(ref submit);
            var action = Assert.Single(source.PlanAndReadActionsLocked(1_001, 101).Actions);
            var started = new NativeOperationActionFeedbackInput
            {
                ActionId = action.ActionId, PlanEpoch = action.PlanEpoch,
                OperationId = action.OperationId, AttemptToken = action.AttemptToken,
                ActionKind = action.Kind, Outcome = (uint)NativeOperationActionFeedbackOutcome.Started,
                ObservedUtcMilliseconds = 1_002, ObservedMonotonicMilliseconds = 102
            };
            Assert.True(source.ApplyActionFeedbackLocked(ref started));
            var completion = new NativeOperationCompletionInput
            {
                OperationId = action.OperationId, AttemptToken = action.AttemptToken,
                Outcome = (uint)NativeOperationCompletionOutcome.Succeeded,
                ObservedUtcMilliseconds = 1_003, ObservedMonotonicMilliseconds = 103,
                ResultHandle = new(41, 42), ValidMask = (ulong)NativeOperationResultValidity.Result
            };
            Assert.True(source.CompleteLocked(ref completion));
            image = source.ExportCanonicalNativeImageLocked();
        }
        using var restored = new NativeOperationCoordinatorWorkspace(plan, sessionId,
            newClock ? new(5, 6) : originalClock, OperationScratchFixture.Create);
        restored.ImportCanonicalNativeImageLocked(image, 1_004, newClock ? 1_000UL : 104UL);
        var after = restored.ExportCanonicalNativeImageLocked();
        var expected = Assert.Single(image.Records);
        var actual = Assert.Single(after.Records);
        Assert.True(after.Header.StateRevision > image.Header.StateRevision);
        Assert.Equal(expected.StateRevision, actual.StateRevision);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.ResultHandle, actual.ResultHandle);
        Assert.Equal(expected.ErrorHandle, actual.ErrorHandle);
        Assert.Equal(expected.AttemptToken, actual.AttemptToken);
        Assert.Equal(expected.AttemptNumber, actual.AttemptNumber);
        Assert.Equal(expected.CreatedUtcMilliseconds, actual.CreatedUtcMilliseconds);
        Assert.Equal(expected.UpdatedUtcMilliseconds, actual.UpdatedUtcMilliseconds);
        if (!newClock)
            Assert.Equal(MemoryMarshal.AsBytes(image.Records.AsSpan()).ToArray(),
                MemoryMarshal.AsBytes(after.Records.AsSpan()).ToArray());
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
