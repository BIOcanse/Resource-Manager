using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeTransactionJournalOwnerTests
{
    private const uint AbiV5 = 0x0005_0000U;
    private const ulong ResidentBudget = 16UL * 1024 * 1024;

    [Fact]
    public async Task OpenOrCreateFromNoDataPersistsEmptyImageThatReopens()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x1111_2222_3333_4444,
            journalInstanceHigh: 0xAAAA_BBBB_CCCC_DDDD);

        try
        {
            var fileStore = new NativeTransactionJournalFileStore(journalPath);
            var missing = await fileStore.TryOpenAsync(
                openConfiguration,
                CancellationToken.None);
            Assert.Equal(NativeTransactionJournalStatus.NoData, missing.Status);
            Assert.Null(missing.Session);

            await using (var created = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                fileStore,
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                var snapshot = await created.ReadSnapshotAsync(CancellationToken.None);
                Assert.Equal(NativeTransactionJournalOwnerState.Ready, created.State);
                Assert.Equal(1UL, snapshot.Header.JournalRevision);
                Assert.Equal(0U, snapshot.Header.EntryCount);
                Assert.Empty(snapshot.Records);
                Assert.True(File.Exists(journalPath));
                Assert.True(new FileInfo(journalPath).Length >= (long)NativeTransactionJournalAbi.ImageHeaderSize);
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var reopenedSnapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(NativeTransactionJournalOwnerState.Ready, reopened.State);
            Assert.Equal(1UL, reopenedSnapshot.Header.JournalRevision);
            Assert.Equal(0U, reopenedSnapshot.Header.EntryCount);
            Assert.Empty(reopenedSnapshot.Records);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulPrepareIsDurableBeforeReturnAndLaterCallerCancellationCannotInterruptIt()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x2222_3333_4444_5555,
            journalInstanceHigh: 0xBBBB_CCCC_DDDD_EEEE);
        var identity = CreateIdentity(actionId: 101);

        try
        {
            await using (var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                using var callerCancellation = new CancellationTokenSource();
                var prepare = CreatePrepare(
                    identity,
                    expectedJournalRevision: 1,
                    nowUtcMilliseconds: 1_000);

                var prepareTask = owner.PrepareAsync(prepare, callerCancellation.Token);
                Assert.Equal(NativeTransactionJournalStatus.Ok, await prepareTask);
                callerCancellation.Cancel();

                Assert.True(prepareTask.IsCompletedSuccessfully);
                Assert.True(callerCancellation.IsCancellationRequested);
                Assert.True(new FileInfo(journalPath).Length > (long)NativeTransactionJournalAbi.ImageHeaderSize);
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var persisted = await reopened.GetAsync(identity, CancellationToken.None);
            var snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(NativeTransactionJournalStatus.Ok, persisted.Status);
            Assert.Equal(identity.ActionId, persisted.Record.Identity.ActionId);
            Assert.Equal((uint)NativeTransactionJournalPhase.Prepared, persisted.Record.Phase);
            Assert.Equal(2UL, snapshot.Header.JournalRevision);
            Assert.Equal(1U, snapshot.Header.EntryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicPrepareBatchIsPersistedAsOneRevisionBeforeReturn()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x2424_3535_4646_5757,
            journalInstanceHigh: 0xBABA_CBCB_DCDC_EDED);

        try
        {
            await using (var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                var first = CreatePrepare(CreateIdentity(111), 1, 1_000);
                first.Identity.ProcessStartKey = 1_111;
                first.Identity.ProcessId = 111;
                first.AtomicGroupId = 501;
                first.GroupMemberIndex = 0;
                first.GroupMemberCount = 2;
                first.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
                var second = CreatePrepare(CreateIdentity(112), 1, 1_000);
                second.Identity.ProcessStartKey = 1_112;
                second.Identity.ProcessId = 112;
                second.AtomicGroupId = 501;
                second.GroupMemberIndex = 1;
                second.GroupMemberCount = 2;
                second.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
                var batch = new NativeTransactionJournalPrepareBatchInput
                {
                    AbiVersion = AbiV5,
                    StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareBatchInput>(),
                    ExpectedJournalRevision = 1,
                    InputCount = 2,
                    Flags = 0
                };

                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await owner.PrepareBatchAsync(batch, new[] { first, second }, CancellationToken.None));
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var persisted = await reopened.ReadSnapshotAsync(CancellationToken.None);
            Assert.Equal(2UL, persisted.Header.JournalRevision);
            Assert.Equal(2U, persisted.Header.EntryCount);
            Assert.All(persisted.Records, record => Assert.Equal(501UL, record.AtomicGroupId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NonOkMutationLeavesRevisionAndDurableImageUnchanged()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x3333_4444_5555_6666,
            journalInstanceHigh: 0xCCCC_DDDD_EEEE_FFFF);
        var identity = CreateIdentity(actionId: 202);

        try
        {
            await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            Assert.Equal(
                NativeTransactionJournalStatus.Ok,
                await owner.PrepareAsync(
                    CreatePrepare(
                        identity,
                        expectedJournalRevision: 1,
                        nowUtcMilliseconds: 2_000),
                    CancellationToken.None));

            var beforeSnapshot = await owner.ReadSnapshotAsync(CancellationToken.None);
            var beforeImage = await File.ReadAllBytesAsync(journalPath);
            var staleMutation = CreateEffectObservedMutation(
                identity,
                expectedJournalRevision: 1,
                expectedEntryRevision: 1,
                nowUtcMilliseconds: 2_100);

            var status = await owner.MutateAsync(staleMutation, CancellationToken.None);
            var afterSnapshot = await owner.ReadSnapshotAsync(CancellationToken.None);
            var afterImage = await File.ReadAllBytesAsync(journalPath);

            Assert.Equal(NativeTransactionJournalStatus.StaleRevision, status);
            Assert.Equal(NativeTransactionJournalOwnerState.Ready, owner.State);
            Assert.Equal(beforeSnapshot.Header.JournalRevision, afterSnapshot.Header.JournalRevision);
            Assert.Equal(beforeSnapshot.Header.EntryCount, afterSnapshot.Header.EntryCount);
            Assert.Equal(beforeImage, afterImage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceFailureFaultsOwnerAndSubsequentOperationsFailClosed()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x4444_5555_6666_7777,
            journalInstanceHigh: 0xDDDD_EEEE_FFFF_0001);
        var identity = CreateIdentity(actionId: 303);

        try
        {
            await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            await using var journalLock = new FileStream(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var persistenceException = await Assert.ThrowsAsync<NativeTransactionJournalPersistenceException>(
                () => owner.PrepareAsync(
                    CreatePrepare(
                        identity,
                        expectedJournalRevision: 1,
                        nowUtcMilliseconds: 3_000),
                    CancellationToken.None));

            Assert.Equal(NativeTransactionJournalOwnerState.PersistenceFaulted, owner.State);
            Assert.NotNull(owner.PersistenceFailure);
            Assert.Same(owner.PersistenceFailure, persistenceException.InnerException);

            var getException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.GetAsync(identity, CancellationToken.None));
            var mutationException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.MutateAsync(
                    CreateEffectObservedMutation(
                        identity,
                        expectedJournalRevision: 2,
                        expectedEntryRevision: 1,
                        nowUtcMilliseconds: 3_100),
                    CancellationToken.None));

            Assert.Same(owner.PersistenceFailure, getException.InnerException);
            Assert.Same(owner.PersistenceFailure, mutationException.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestTransitionFailureBeforeCanonicalCommitIsNotCommitted(
        bool failAfterManifestReplace)
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x4545_0101_6767_2323,
            journalInstanceHigh: 0xABAB_CDCF_EFEF_4545);
        var identity = CreateIdentity(actionId: 353);
        var manifestCommitter = new FaultInjectingManifestCommitter();

        try
        {
            byte[] priorImage;
            await using (var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(
                    journalPath,
                    WindowsNativeTransactionJournalFileCommitter.Instance,
                    NativeTransactionJournalCommitHook.None,
                    manifestCommitter),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                priorImage = await File.ReadAllBytesAsync(journalPath);
                var expectedFailure = new IOException(
                    "Injected pending-manifest commit failure.");
                manifestCommitter.FailAfterSuccessfulCommits(
                    successfulCommitsBeforeFailure: 0,
                    failAfterCommit: failAfterManifestReplace,
                    expectedFailure);

                var persistenceException = await Assert.ThrowsAsync<NativeTransactionJournalPersistenceException>(
                    () => owner.PrepareAsync(
                        CreatePrepare(
                            identity,
                            expectedJournalRevision: 1,
                            nowUtcMilliseconds: 3_500),
                        CancellationToken.None));
                var commitFailure = Assert.IsType<NativeTransactionJournalCommitException>(
                    persistenceException.InnerException);

                Assert.Equal(
                    NativeTransactionJournalCommitOutcome.NotCommitted,
                    commitFailure.Outcome);
                Assert.Same(expectedFailure, commitFailure.InnerException);
                Assert.False(
                    NativeTransactionJournalCommitException.IsCommitAmbiguous(
                        persistenceException));
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(0U, snapshot.Header.EntryCount);
            Assert.Equal(priorImage, await File.ReadAllBytesAsync(journalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestFinalizeFailureAfterCanonicalCommitIsAmbiguousAndRecoverable(
        bool failAfterManifestReplace)
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x4646_0202_6868_2424,
            journalInstanceHigh: 0xBCBC_DEDE_F0F0_5656);
        var identity = CreateIdentity(actionId: 363);
        var manifestCommitter = new FaultInjectingManifestCommitter();

        try
        {
            await using (var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(
                    journalPath,
                    WindowsNativeTransactionJournalFileCommitter.Instance,
                    NativeTransactionJournalCommitHook.None,
                    manifestCommitter),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                var expectedFailure = new IOException(
                    "Injected finalized-manifest commit failure.");
                manifestCommitter.FailAfterSuccessfulCommits(
                    successfulCommitsBeforeFailure: 1,
                    failAfterCommit: failAfterManifestReplace,
                    expectedFailure);

                var persistenceException = await Assert.ThrowsAsync<NativeTransactionJournalPersistenceException>(
                    () => owner.PrepareAsync(
                        CreatePrepare(
                            identity,
                            expectedJournalRevision: 1,
                            nowUtcMilliseconds: 3_600),
                        CancellationToken.None));
                var commitFailure = Assert.IsType<NativeTransactionJournalCommitException>(
                    persistenceException.InnerException);

                Assert.Equal(
                    NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                    commitFailure.Outcome);
                Assert.Same(expectedFailure, commitFailure.InnerException);
                Assert.True(
                    NativeTransactionJournalCommitException.IsCommitAmbiguous(
                        persistenceException));
            }

            for (var reopenAttempt = 0; reopenAttempt < 3; reopenAttempt++)
            {
                await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                    new NativeTransactionJournalFileStore(journalPath),
                    createConfiguration,
                    openConfiguration,
                    CancellationToken.None);
                var persisted = await reopened.GetAsync(
                    identity,
                    CancellationToken.None);
                var snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

                Assert.Equal(NativeTransactionJournalStatus.Ok, persisted.Status);
                Assert.Equal(identity.ActionId, persisted.Record.Identity.ActionId);
                Assert.Equal((uint)NativeTransactionJournalPhase.Prepared, persisted.Record.Phase);
                Assert.Equal(1U, snapshot.Header.EntryCount);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SamePathRejectsSecondLiveOwnerAndReopensAfterDisposeWithExactState()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x4545_5656_6767_7878,
            journalInstanceHigh: 0xDEDE_EFEF_0101_1212);
        var identity = CreateIdentity(actionId: 404);

        try
        {
            await using (var firstOwner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await firstOwner.PrepareAsync(
                        CreatePrepare(identity, expectedJournalRevision: 1, nowUtcMilliseconds: 4_000),
                        CancellationToken.None));

                using var rejectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAnyAsync<IOException>(
                    () => NativeTransactionJournalOwner.OpenOrCreateAsync(
                        new NativeTransactionJournalFileStore(journalPath),
                        createConfiguration,
                        openConfiguration,
                        rejectionDeadline.Token));
                Assert.False(rejectionDeadline.IsCancellationRequested);
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var persisted = await reopened.GetAsync(identity, CancellationToken.None);
            var snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(NativeTransactionJournalStatus.Ok, persisted.Status);
            Assert.Equal(identity.ActionId, persisted.Record.Identity.ActionId);
            Assert.Equal((uint)NativeTransactionJournalPhase.Prepared, persisted.Record.Phase);
            Assert.Equal(2UL, snapshot.Header.JournalRevision);
            Assert.Equal(1U, snapshot.Header.EntryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationAfterNativeMutationCannotInterruptPausedCommit()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x5656_6767_7878_8989,
            journalInstanceHigh: 0xEFEF_0101_1212_2323);
        var identity = CreateIdentity(actionId: 505);
        var commitHook = new ControlledCommitHook();

        try
        {
            await using (var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath, commitHook),
                createConfiguration,
                openConfiguration,
                CancellationToken.None))
            {
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await owner.PrepareAsync(
                        CreatePrepare(identity, expectedJournalRevision: 1, nowUtcMilliseconds: 5_000),
                        CancellationToken.None));

                var pausedCommit = commitHook.PauseNextCommit();
                using var callerCancellation = new CancellationTokenSource();
                var mutationTask = owner.MutateAsync(
                    CreateEffectObservedMutation(
                        identity,
                        expectedJournalRevision: 2,
                        expectedEntryRevision: 1,
                        nowUtcMilliseconds: 5_100),
                    callerCancellation.Token);

                await pausedCommit.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                callerCancellation.Cancel();
                Assert.False(mutationTask.IsCompleted);
                pausedCommit.Release();

                Assert.Equal(NativeTransactionJournalStatus.Ok, await mutationTask);
                Assert.False(commitHook.LastCommitTokenCanBeCanceled);
            }

            await using var reopened = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            var persisted = await reopened.GetAsync(identity, CancellationToken.None);
            var snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(NativeTransactionJournalStatus.Ok, persisted.Status);
            Assert.Equal((uint)NativeTransactionJournalPhase.EffectObserved, persisted.Record.Phase);
            Assert.Equal(3UL, snapshot.Header.JournalRevision);
            Assert.Equal(1U, snapshot.Header.EntryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceFaultRejectsEveryOperationAlreadyQueuedBehindCommit()
    {
        var root = CreateTempDirectory();
        var journalPath = Path.Combine(root, "journal.bin");
        var (createConfiguration, openConfiguration) = CreateV5Configurations(
            recordCapacity: 4,
            maximumResidentBytes: ResidentBudget,
            journalInstanceLow: 0x6767_7878_8989_9A9A,
            journalInstanceHigh: 0x0101_1212_2323_3434);
        var identity = CreateIdentity(actionId: 606);
        var commitHook = new ControlledCommitHook();

        try
        {
            await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(journalPath, commitHook),
                createConfiguration,
                openConfiguration,
                CancellationToken.None);
            Assert.Equal(
                NativeTransactionJournalStatus.Ok,
                await owner.PrepareAsync(
                    CreatePrepare(identity, expectedJournalRevision: 1, nowUtcMilliseconds: 6_000),
                    CancellationToken.None));

            var expectedFailure = new IOException("Injected canonical-image commit failure.");
            var pausedCommit = commitHook.PauseNextCommit(expectedFailure);
            var failingMutation = owner.MutateAsync(
                CreateEffectObservedMutation(
                    identity,
                    expectedJournalRevision: 2,
                    expectedEntryRevision: 1,
                    nowUtcMilliseconds: 6_100),
                CancellationToken.None);
            await pausedCommit.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            var queuedGet = owner.GetAsync(identity, CancellationToken.None);
            var queuedSnapshot = owner.ReadSnapshotAsync(CancellationToken.None);
            var queuedPrepare = owner.PrepareAsync(
                CreatePrepare(CreateIdentity(607), expectedJournalRevision: 3, nowUtcMilliseconds: 6_200),
                CancellationToken.None);
            var queuedPrepareBatch = owner.PrepareBatchAsync(
                default,
                ReadOnlyMemory<NativeTransactionJournalPrepareInput>.Empty,
                CancellationToken.None);
            var queuedMutation = owner.MutateAsync(
                CreateEffectObservedMutation(
                    identity,
                    expectedJournalRevision: 3,
                    expectedEntryRevision: 2,
                    nowUtcMilliseconds: 6_300),
                CancellationToken.None);
            var queuedFeedback = owner.StageFeedbackAsync(default, CancellationToken.None);
            var queuedRecoveryEvidence = owner.ApplyRecoveryEvidenceAsync(
                default,
                CancellationToken.None);
            var queuedAcknowledge = owner.AcknowledgeAsync(default, CancellationToken.None);

            pausedCommit.Release();
            var persistenceException = await Assert.ThrowsAsync<NativeTransactionJournalPersistenceException>(
                () => failingMutation);
            var queuedFailures = await Task.WhenAll(
                CaptureInvalidOperationAsync(() => queuedGet),
                CaptureInvalidOperationAsync(() => queuedSnapshot),
                CaptureInvalidOperationAsync(() => queuedPrepare),
                CaptureInvalidOperationAsync(() => queuedPrepareBatch),
                CaptureInvalidOperationAsync(() => queuedMutation),
                CaptureInvalidOperationAsync(() => queuedFeedback),
                CaptureInvalidOperationAsync(() => queuedRecoveryEvidence),
                CaptureInvalidOperationAsync(() => queuedAcknowledge));

            var commitFailure = Assert.IsType<NativeTransactionJournalCommitException>(
                persistenceException.InnerException);
            Assert.Equal(
                NativeTransactionJournalCommitOutcome.NotCommitted,
                commitFailure.Outcome);
            Assert.Same(expectedFailure, commitFailure.InnerException);
            Assert.Equal(NativeTransactionJournalOwnerState.PersistenceFaulted, owner.State);
            Assert.Same(commitFailure, owner.PersistenceFailure);
            Assert.All(
                queuedFailures,
                failure => Assert.Same(owner.PersistenceFailure, failure.InnerException));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (
        NativeTransactionJournalCreateConfiguration Create,
        NativeTransactionJournalOpenConfiguration Open) CreateV5Configurations(
        uint recordCapacity,
        ulong maximumResidentBytes,
        ulong journalInstanceLow,
        ulong journalInstanceHigh)
    {
        Assert.Equal(AbiV5, NativeTransactionJournalAbi.Version);
        return (
            new NativeTransactionJournalCreateConfiguration
            {
                AbiVersion = AbiV5,
                StructSize = NativeTransactionJournalSession
                    .SizeOf<NativeTransactionJournalCreateConfiguration>(),
                JournalInstanceLow = journalInstanceLow,
                JournalInstanceHigh = journalInstanceHigh,
                RecordCapacity = recordCapacity,
                Flags = 0,
                MaximumResidentBytes = maximumResidentBytes
            },
            new NativeTransactionJournalOpenConfiguration
            {
                AbiVersion = AbiV5,
                StructSize = NativeTransactionJournalSession
                    .SizeOf<NativeTransactionJournalOpenConfiguration>(),
                MaximumRecordCapacity = recordCapacity,
                Flags = 0,
                MaximumResidentBytes = maximumResidentBytes
            });
    }

    private static NativeTransactionJournalIdentity CreateIdentity(ulong actionId)
        => new()
        {
            ConfigurationGeneration = 7,
            PlanEpoch = 19,
            ActionId = actionId,
            HostSessionIncarnation = 23,
            TargetId = 29,
            SoftwareId = 31,
            ProcessStartKey = 37,
            ProcessId = 41,
            Reserved = 0
        };

    private static NativeTransactionJournalPrepareInput CreatePrepare(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong nowUtcMilliseconds)
        => new()
        {
            AbiVersion = AbiV5,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = identity,
            Scope = (uint)NativeTransactionJournalScope.Process,
            Disposition = (uint)NativeTransactionJournalDisposition.Apply,
            DomainMask = (uint)NativeTransactionJournalDomain.Process,
            GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1,
            CpuFromGrade = 0,
            CpuToGrade = 0,
            GpuFromGrade = 0,
            GpuToGrade = 0,
            StableSystemStatus = 100,
            StableSystemError = 0,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = 101,
            PayloadGeneration = 3,
            PayloadReserved = 0,
            PayloadLength = 512,
            PayloadDigestLow = 0x1234,
            PayloadDigestHigh = 0x5678,
            PayloadProvenanceDigestLow = 0x9abc,
            PayloadProvenanceDigestHigh = 0xdef0,
            NowUtcMilliseconds = nowUtcMilliseconds,
            MaximumRecoveryAttempts = 3,
            RetryPolicyReserved = 0,
            RecoveryDeadlineUtcMilliseconds = nowUtcMilliseconds + 10_000
        };

    private static NativeTransactionJournalMutationInput CreateEffectObservedMutation(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision,
        ulong nowUtcMilliseconds)
        => new()
        {
            AbiVersion = AbiV5,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalMutationInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            NowUtcMilliseconds = nowUtcMilliseconds,
            Event = (uint)NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.Prepared,
            StableSystemStatus = 200,
            StableSystemError = 0
        };

    private static async Task<InvalidOperationException> CaptureInvalidOperationAsync(
        Func<Task> operation)
        => await Assert.ThrowsAsync<InvalidOperationException>(operation);

    private sealed class ControlledCommitHook : INativeTransactionJournalCommitHook
    {
        private readonly object sync = new();
        private CommitPause? nextCommit;

        public bool LastCommitTokenCanBeCanceled { get; private set; }

        public CommitPause PauseNextCommit(Exception? failure = null)
        {
            lock (sync)
            {
                Assert.Null(nextCommit);
                nextCommit = new CommitPause(failure);
                return nextCommit;
            }
        }

        public async ValueTask BeforeCanonicalImageCommitAsync(CancellationToken cancellationToken)
        {
            CommitPause? current;
            lock (sync)
            {
                current = nextCommit;
                nextCommit = null;
            }

            if (current is null)
            {
                return;
            }

            LastCommitTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            current.MarkEntered();
            await current.WaitForReleaseAsync();
            if (current.Failure is not null)
            {
                throw current.Failure;
            }
        }
    }

    private sealed class FaultInjectingManifestCommitter
        : IHostManagerDurableRootManifestCommitter
    {
        private int successfulCommitsBeforeFailure = -1;
        private bool failAfterCommit;
        private Exception? failure;

        public void FailAfterSuccessfulCommits(
            int successfulCommitsBeforeFailure,
            bool failAfterCommit,
            Exception failure)
        {
            Assert.True(successfulCommitsBeforeFailure >= 0);
            Assert.Null(this.failure);
            this.successfulCommitsBeforeFailure = successfulCommitsBeforeFailure;
            this.failAfterCommit = failAfterCommit;
            this.failure = failure;
        }

        public void Commit(
            string temporaryPath,
            string manifestPath,
            ReadOnlySpan<byte> expectedImage)
        {
            if (failure is null || successfulCommitsBeforeFailure > 0)
            {
                if (failure is not null)
                {
                    successfulCommitsBeforeFailure--;
                }
                WindowsHostManagerDurableRootManifestCommitter.Instance.Commit(
                    temporaryPath,
                    manifestPath,
                    expectedImage);
                return;
            }

            var injectedFailure = failure;
            failure = null;
            successfulCommitsBeforeFailure = -1;
            if (failAfterCommit)
            {
                WindowsHostManagerDurableRootManifestCommitter.Instance.Commit(
                    temporaryPath,
                    manifestPath,
                    expectedImage);
            }
            throw injectedFailure;
        }
    }

    private sealed class CommitPause(Exception? failure)
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public Exception? Failure { get; } = failure;

        public void MarkEntered()
            => entered.TrySetResult();

        public void Release()
            => release.TrySetResult();

        public Task WaitForReleaseAsync()
            => release.Task;
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-native-journal-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
