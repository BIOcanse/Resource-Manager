using System.Reflection;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTransactionJournalRuntimeTests
{
    private const long ResidentByteBudget = 1_048_576;
    private const long PayloadByteBudget = 2_097_152;

    [Fact]
    public void AdmissionDoesNotExposeRawJournalOrPayloadOwners()
    {
        var admissionType = typeof(HostManagerTransactionJournalAdmission);
        Assert.Empty(admissionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));

        var forbiddenTypes = new[]
        {
            typeof(NativeTransactionJournalOwner),
            typeof(NativeTransactionJournalPayloadStore)
        };
        Assert.DoesNotContain(
            admissionType.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => forbiddenTypes.Contains(property.PropertyType));
        Assert.DoesNotContain(
            admissionType.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => forbiddenTypes.Contains(method.ReturnType) ||
                method.GetParameters().Any(parameter =>
                    forbiddenTypes.Contains(parameter.ParameterType)));
    }

    [Fact]
    public async Task FactoryRejectsImplicitOrEscapingPathsBeforeOpeningStorage()
    {
        var (recreate, hot) = CreatePlans();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            HostManagerTransactionJournalRuntime.OpenAsync(
                "relative-data-root",
                recreate,
                hot,
                CancellationToken.None));

        var root = CreateTempDirectory();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate with { JournalRelativePath = "../outside/journal.bin" },
                    hot,
                    CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate with { JournalRelativePath = "state\\journal.bin" },
                    hot,
                    CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate with { PayloadRelativeDirectory = Path.Combine(root, "payloads") },
                    hot,
                    CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate with
                    {
                        JournalRelativePath = "state/journal.bin",
                        PayloadRelativeDirectory = "state/journal.bin/payloads"
                    },
                    hot,
                    CancellationToken.None));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyJournalPublishesReadyWithExactCompiledConfiguration()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.Equal(HostManagerTransactionJournalRuntimeState.ReadyForExecution, runtime.State);
            Assert.Null(runtime.Failure);
            Assert.NotNull(runtime.StartupSnapshot);
            Assert.Empty(runtime.StartupSnapshot!.Records);
            Assert.Equal((uint)recreate.RecordCapacity, runtime.StartupSnapshot.Header.Capacity);
            Assert.Equal(Path.GetFullPath(root), runtime.DataRoot);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(root), "state", "journal.bin"),
                runtime.JournalPath);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(root), "state", "payloads"),
                runtime.PayloadDirectory);
            Assert.Equal((uint)recreate.RecordCapacity, runtime.RecordCapacity);
            Assert.Equal((ulong)recreate.ResidentByteBudget, runtime.ResidentByteBudget);
            Assert.Equal(recreate.PayloadCount, runtime.PayloadCount);
            Assert.Equal(recreate.PayloadByteBudget, runtime.PayloadByteBudget);
            Assert.Equal(hot.ConfigurationGeneration, runtime.ConfigurationGeneration);
            Assert.Equal(hot.MaximumRecoveryAttempts, runtime.MaximumRecoveryAttempts);
            Assert.Equal(
                TimeSpan.FromMilliseconds(hot.RetryDelayMilliseconds),
                runtime.RetryDelay);
            Assert.Equal(
                TimeSpan.FromMilliseconds(hot.RecoveryDeadlineMilliseconds),
                runtime.RecoveryDeadline);
            Assert.Equal(
                TimeSpan.FromMilliseconds(hot.MaximumFutureSkewMilliseconds),
                runtime.MaximumFutureSkew);
            Assert.Equal(
                TimeSpan.FromMilliseconds(hot.ShutdownDrainTimeoutMilliseconds),
                runtime.ShutdownDrainTimeout);

            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            Assert.False(runtime.TryAcquireRecoveryAdmission(out _));
            Assert.NotNull(admission);
            Assert.Equal(1, runtime.ActiveAdmissionCount);
            await admission!.DisposeAsync();
            Assert.Equal(0, runtime.ActiveAdmissionCount);

            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await runtime.ShutdownAsync(CancellationToken.None));
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Disposed, runtime.State);
            Assert.Equal(HostManagerTransactionJournalShutdownResult.Clean, runtime.LastShutdownResult);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HotPublishRequiresMonotonicGenerationAndRejectsSameGenerationDrift()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var existingAdmission));
            Assert.NotNull(existingAdmission);
            Assert.True(await runtime.TryApplyHotPublishAsync(hot, CancellationToken.None));
            Assert.False(await runtime.TryApplyHotPublishAsync(
                hot with { MaximumRecoveryAttempts = hot.MaximumRecoveryAttempts + 1 },
                CancellationToken.None));
            Assert.False(await runtime.TryApplyHotPublishAsync(
                hot with { ConfigurationGeneration = hot.ConfigurationGeneration - 1 },
                CancellationToken.None));

            var next = hot with
            {
                ConfigurationGeneration = hot.ConfigurationGeneration + (1UL << 32),
                MaximumRecoveryAttempts = hot.MaximumRecoveryAttempts + 2,
                RetryDelayMilliseconds = hot.RetryDelayMilliseconds + 25,
                RecoveryDeadlineMilliseconds = hot.RecoveryDeadlineMilliseconds + 5_000,
                MaximumFutureSkewMilliseconds = hot.MaximumFutureSkewMilliseconds + 50,
                ShutdownDrainTimeoutMilliseconds = hot.ShutdownDrainTimeoutMilliseconds + 500
            };
            Assert.False(await runtime.TryApplyHotPublishAsync(next, CancellationToken.None));
            await existingAdmission!.DisposeAsync();
            Assert.True(await runtime.TryApplyHotPublishAsync(next, CancellationToken.None));
            Assert.Equal(next.ConfigurationGeneration, runtime.ConfigurationGeneration);
            Assert.Equal(next.MaximumRecoveryAttempts, runtime.MaximumRecoveryAttempts);
            Assert.Equal(
                TimeSpan.FromMilliseconds(next.RetryDelayMilliseconds),
                runtime.RetryDelay);
            Assert.Equal(
                TimeSpan.FromMilliseconds(next.RecoveryDeadlineMilliseconds),
                runtime.RecoveryDeadline);
            Assert.Equal(
                TimeSpan.FromMilliseconds(next.MaximumFutureSkewMilliseconds),
                runtime.MaximumFutureSkew);
            Assert.Equal(
                TimeSpan.FromMilliseconds(next.ShutdownDrainTimeoutMilliseconds),
                runtime.ShutdownDrainTimeout);

            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await runtime.ShutdownAsync(CancellationToken.None));
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryAdmissionSettlesStartupJournalBeforeExecutionAdmissionOpens()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var first = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(first.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await using (admission)
            {
                var initialJournalRevision = first.StartupSnapshot!.Header.JournalRevision;
                var prepare = CreatePrepare(
                    hot.ConfigurationGeneration,
                    initialJournalRevision,
                    actionId: 101);
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await admission!.PrepareAsync(prepare, CancellationToken.None));
                var preparedSnapshot = await admission.ReadSnapshotAsync(CancellationToken.None);
                Assert.Single(preparedSnapshot.Records);
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.ReadyForExecution,
                    first.State);
                Assert.False(first.TryAcquireExecutionAdmission(out _));
                Assert.False(first.TryAcquireRecoveryAdmission(out _));
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await admission.MutateAsync(
                        CreateEffectObservedMutation(
                            prepare.Identity,
                            initialJournalRevision + 1,
                            expectedEntryRevision: 1),
                        CancellationToken.None));
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await admission.StageFeedbackAsync(
                        CreateSucceededFeedback(
                            prepare.Identity,
                            initialJournalRevision + 2,
                            expectedEntryRevision: 2),
                        CancellationToken.None));
            }

            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.RecoveryRequired,
                await first.ShutdownAsync(CancellationToken.None));
            Assert.Single(first.LastSnapshot!.Records);

            var reopened = await HostManagerTransactionJournalRuntime.OpenAsync(
                root,
                recreate,
                hot,
                CancellationToken.None);
            try
            {
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired,
                    reopened.State);
                Assert.Single(reopened.StartupSnapshot!.Records);
                Assert.False(reopened.TryAcquireExecutionAdmission(out _));
                Assert.True(reopened.TryAcquireRecoveryAdmission(out var recoveryAdmission));
                Assert.NotNull(recoveryAdmission);
                Assert.Equal(
                    HostManagerTransactionJournalAdmissionKind.Recovery,
                    recoveryAdmission!.Kind);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recoveryAdmission.PrepareAsync(
                        CreatePrepare(
                            hot.ConfigurationGeneration,
                            reopened.StartupSnapshot.Header.JournalRevision,
                            actionId: 102),
                        CancellationToken.None));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recoveryAdmission.PrepareBatchAsync(
                        default,
                        ReadOnlyMemory<NativeTransactionJournalPrepareInput>.Empty,
                        CancellationToken.None));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recoveryAdmission.MutateAsync(default, CancellationToken.None));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recoveryAdmission.StageFeedbackAsync(default, CancellationToken.None));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recoveryAdmission.PersistPayloadAsync(
                        default,
                        new byte[] { 1 },
                        CancellationToken.None));

                var recoverySnapshot = await recoveryAdmission.ReadSnapshotAsync(
                    CancellationToken.None);
                var pendingRecord = Assert.Single(recoverySnapshot.Records);
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired,
                    reopened.State);
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await recoveryAdmission.AcknowledgeAsync(
                        CreateAcceptedAcknowledgement(
                            pendingRecord.Identity,
                            recoverySnapshot.Header.JournalRevision,
                            pendingRecord.EntryRevision),
                        CancellationToken.None));
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired,
                    reopened.State);

                var settledSnapshot = await recoveryAdmission.ReadSnapshotAsync(
                    CancellationToken.None);
                Assert.Empty(settledSnapshot.Records);
                Assert.Empty(reopened.LastSnapshot!.Records);
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired,
                    reopened.State);
                await recoveryAdmission.DisposeAsync();

                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.ReadyForExecution,
                    reopened.State);
                Assert.False(reopened.TryAcquireRecoveryAdmission(out _));
                Assert.True(reopened.TryAcquireExecutionAdmission(out var executionAdmission));
                Assert.NotNull(executionAdmission);
                await executionAdmission!.DisposeAsync();
                Assert.Equal(
                    HostManagerTransactionJournalShutdownResult.Clean,
                    await reopened.ShutdownAsync(CancellationToken.None));
            }
            finally
            {
                await reopened.DisposeAsync();
            }
        }
        finally
        {
            await first.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveOwnerConflictRejectsInitializationAndReleasesAfterOwnerShutdown()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var first = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate,
                    hot,
                    CancellationToken.None));
            Assert.IsAssignableFrom<IOException>(conflict.InnerException);

            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await first.ShutdownAsync(CancellationToken.None));

            var reopened = await HostManagerTransactionJournalRuntime.OpenAsync(
                root,
                recreate,
                hot,
                CancellationToken.None);
            try
            {
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.ReadyForExecution,
                    reopened.State);
            }
            finally
            {
                await reopened.DisposeAsync();
            }
        }
        finally
        {
            await first.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptImageAndCapacityDriftFailClosed()
    {
        var corruptRoot = CreateTempDirectory();
        var driftRoot = CreateTempDirectory();
        var (recreate, hot) = CreatePlans(recordCapacity: 8);
        try
        {
            var corruptPath = Path.Combine(corruptRoot, "state", "journal.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
            await File.WriteAllBytesAsync(corruptPath, new byte[256]);

            var corrupt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    corruptRoot,
                    recreate,
                    hot,
                    CancellationToken.None));
            Assert.IsType<InvalidDataException>(corrupt.InnerException);

            await SeedEmptyJournalAsync(
                Path.Combine(driftRoot, "state", "journal.bin"),
                recordCapacity: 4,
                residentByteBudget: (ulong)ResidentByteBudget);
            var drifted = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    driftRoot,
                    recreate,
                    hot,
                    CancellationToken.None));
            Assert.IsType<InvalidDataException>(drifted.InnerException);
        }
        finally
        {
            Directory.Delete(corruptRoot, recursive: true);
            Directory.Delete(driftRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownClosesAdmissionAndWaitsForExactLeaseRelease()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans(shutdownDrainTimeoutMilliseconds: 2_000);
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            var shutdown = runtime.ShutdownAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => runtime.State == HostManagerTransactionJournalRuntimeState.Draining,
                TimeSpan.FromSeconds(2));

            Assert.False(shutdown.IsCompleted);
            Assert.False(runtime.TryAcquireExecutionAdmission(out var rejected));
            Assert.False(runtime.TryAcquireRecoveryAdmission(out _));
            Assert.Null(rejected);
            Assert.Equal(1, runtime.ActiveAdmissionCount);

            await admission!.DisposeAsync();
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await shutdown);
            Assert.Equal(0, runtime.ActiveAdmissionCount);
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Disposed, runtime.State);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DrainTimeoutRemainsDrainingUntilOutstandingAdmissionLeaves()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans(shutdownDrainTimeoutMilliseconds: 50);
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.TimedOut,
                await runtime.ShutdownAsync(CancellationToken.None));
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Draining, runtime.State);
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.TimedOut,
                runtime.LastShutdownResult);
            Assert.False(runtime.TryAcquireExecutionAdmission(out _));
            Assert.False(runtime.TryAcquireRecoveryAdmission(out _));

            var contender = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostManagerTransactionJournalRuntime.OpenAsync(
                    root,
                    recreate,
                    hot,
                    CancellationToken.None));
            Assert.IsAssignableFrom<IOException>(contender.InnerException);

            await admission!.DisposeAsync();
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await runtime.ShutdownAsync(CancellationToken.None));
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Disposed, runtime.State);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceFaultCannotBeReportedAsCleanShutdown()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            await using (admission)
            using (var blocker = new FileStream(
                runtime.JournalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var prepare = CreatePrepare(
                    hot.ConfigurationGeneration,
                    runtime.StartupSnapshot!.Header.JournalRevision,
                    actionId: 202);
                await Assert.ThrowsAsync<NativeTransactionJournalPersistenceException>(() =>
                    admission!.PrepareAsync(prepare, CancellationToken.None));
            }

            Assert.Equal(HostManagerTransactionJournalRuntimeState.Faulted, runtime.State);
            Assert.NotNull(runtime.Failure);
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Faulted,
                await runtime.ShutdownAsync(CancellationToken.None));
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Faulted,
                runtime.LastShutdownResult);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PayloadBudgetIsNonFatalButPayloadCorruptionFaultsRuntime()
    {
        var root = CreateTempDirectory();
        var (baseRecreate, hot) = CreatePlans();
        var recreate = baseRecreate with
        {
            PayloadCount = 1,
            PayloadByteBudget = 4
        };
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            await using (admission)
            {
                _ = await admission!.ReconcilePayloadsAsync(
                    ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                    CancellationToken.None);
                var firstBinding = CreateBinding(runtime, actionId: 301);
                var reference = await admission.PersistPayloadAsync(
                    firstBinding,
                    new byte[] { 1, 2, 3, 4 },
                    CancellationToken.None);

                await Assert.ThrowsAsync<NativeTransactionJournalPayloadCapacityException>(() =>
                    admission.PersistPayloadAsync(
                        CreateBinding(runtime, actionId: 302),
                        new byte[] { 5 },
                        CancellationToken.None));
                Assert.Equal(
                    HostManagerTransactionJournalRuntimeState.ReadyForExecution,
                    runtime.State);

                var payloadPath = Directory
                    .EnumerateFiles(runtime.PayloadDirectory, "payload-*.bin")
                    .Where(path => !string.Equals(
                        Path.GetFileName(path),
                        "payload-sequence.bin",
                        StringComparison.Ordinal))
                    .Single();
                var bytes = await File.ReadAllBytesAsync(payloadPath);
                bytes[^1] ^= 0xFF;
                await File.WriteAllBytesAsync(payloadPath, bytes);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    admission.ReadPayloadAsync(
                        reference,
                        firstBinding,
                        CancellationToken.None));
            }

            Assert.Equal(HostManagerTransactionJournalRuntimeState.Faulted, runtime.State);
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Faulted,
                await runtime.ShutdownAsync(CancellationToken.None));
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectPayloadDeleteFailureRequiresExactReconciliationBeforeNextPersist()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            await using (admission)
            {
                _ = await admission!.ReconcilePayloadsAsync(
                    ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                    CancellationToken.None);
                var binding = CreateBinding(runtime, actionId: 351);
                var reference = await admission.PersistPayloadAsync(
                    binding,
                    new byte[] { 3, 5, 1 },
                    CancellationToken.None);
                var path = Path.Combine(
                    runtime.PayloadDirectory,
                    $"payload-{reference.Generation:D10}-{reference.Slot:D10}.bin");

                await using (var lockStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    await Assert.ThrowsAnyAsync<IOException>(() =>
                        admission.DeletePayloadAsync(
                            reference,
                            binding,
                            CancellationToken.None));
                }

                Assert.True(admission.PayloadReconciliationRequired);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    admission.PersistPayloadAsync(
                        CreateBinding(runtime, actionId: 352),
                        new byte[] { 3, 5, 2 },
                        CancellationToken.None));

                var result = await admission.ReconcilePayloadsAsync(
                    ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                    CancellationToken.None);
                Assert.False(admission.PayloadReconciliationRequired);
                Assert.Equal(1, result.DeletedPayloadCount);
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdmissionDisposeWaitsForInflightPayloadOperationBeforeRuntimeDrain()
    {
        const int payloadLength = 128 * 1024 * 1024;
        var root = CreateTempDirectory();
        var (baseRecreate, hot) = CreatePlans(shutdownDrainTimeoutMilliseconds: 5_000);
        var recreate = baseRecreate with
        {
            PayloadCount = 1,
            PayloadByteBudget = payloadLength
        };
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
            _ = await admission!.ReconcilePayloadsAsync(
                ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                CancellationToken.None);
            var persist = admission.PersistPayloadAsync(
                CreateBinding(runtime, actionId: 401),
                payload,
                CancellationToken.None);
            await WaitUntilAsync(
                () => Directory.Exists(runtime.PayloadDirectory) &&
                    Directory.EnumerateFiles(
                        runtime.PayloadDirectory,
                        "*.tmp",
                        SearchOption.TopDirectoryOnly).Any(),
                TimeSpan.FromSeconds(5));

            var disposeAdmission = admission.DisposeAsync().AsTask();
            var shutdown = runtime.ShutdownAsync(CancellationToken.None);

            Assert.Equal(1, runtime.ActiveAdmissionCount);
            Assert.False(disposeAdmission.IsCompleted);
            Assert.False(shutdown.IsCompleted);

            _ = await persist;
            await disposeAdmission;
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await shutdown);
            Assert.Equal(0, runtime.ActiveAdmissionCount);
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Disposed, runtime.State);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposedAdmissionRejectsEveryJournalAndPayloadOperation()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        try
        {
            Assert.True(runtime.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await admission!.DisposeAsync();
            await admission.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.PrepareAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.PrepareBatchAsync(
                    default,
                    ReadOnlyMemory<NativeTransactionJournalPrepareInput>.Empty,
                    CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.MutateAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.StageFeedbackAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.ApplyRecoveryEvidenceAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.AcknowledgeAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.GetAsync(default, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.ReadSnapshotAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.PersistPayloadAsync(
                    default,
                    new byte[] { 1 },
                    CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.ReadPayloadAsync(
                    default,
                    default(NativeTransactionJournalPayloadBinding),
                    CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                admission.DeletePayloadAsync(
                    default,
                    default(NativeTransactionJournalPayloadBinding),
                    CancellationToken.None));

            Assert.Equal(0, runtime.ActiveAdmissionCount);
            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.Clean,
                await runtime.ShutdownAsync(CancellationToken.None));
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeIsIdempotentAfterCleanShutdown()
    {
        var root = CreateTempDirectory();
        var (recreate, hot) = CreatePlans();
        var runtime = await HostManagerTransactionJournalRuntime.OpenAsync(
            root,
            recreate,
            hot,
            CancellationToken.None);

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();

        Assert.Equal(HostManagerTransactionJournalRuntimeState.Disposed, runtime.State);
        Assert.Equal(HostManagerTransactionJournalShutdownResult.Clean, runtime.LastShutdownResult);
        Directory.Delete(root, recursive: true);
    }

    private static (
        CompiledHostManagerTransactionJournalRecreatePlan Recreate,
        CompiledHostManagerTransactionJournalHotPublishPlan Hot) CreatePlans(
        int recordCapacity = 8,
        int shutdownDrainTimeoutMilliseconds = 1_000)
        => (
            new CompiledHostManagerTransactionJournalRecreatePlan(
                recordCapacity,
                ResidentByteBudget,
                PayloadCount: 16,
                PayloadByteBudget,
                JournalRelativePath: "state/journal.bin",
                PayloadRelativeDirectory: "state/payloads"),
            new CompiledHostManagerTransactionJournalHotPublishPlan(
                ConfigurationGeneration: 19UL << 32,
                MaximumRecoveryAttempts: 3,
                RetryDelayMilliseconds: 100,
                RecoveryDeadlineMilliseconds: 60_000,
                MaximumFutureSkewMilliseconds: 1_000,
                ShutdownDrainTimeoutMilliseconds: shutdownDrainTimeoutMilliseconds));

    private static NativeTransactionJournalPrepareInput CreatePrepare(
        ulong configurationGeneration,
        ulong expectedJournalRevision,
        ulong actionId)
    {
        var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new NativeTransactionJournalPrepareInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = configurationGeneration,
                PlanEpoch = 7,
                ActionId = actionId,
                HostSessionIncarnation = 11,
                TargetId = 13,
                SoftwareId = 17,
                ProcessStartKey = 19,
                ProcessId = 23,
                Reserved = 0
            },
            Scope = (uint)NativeTransactionJournalScope.Process,
            Disposition = (uint)NativeTransactionJournalDisposition.Apply,
            DomainMask = (uint)NativeTransactionJournalDomain.Process,
            GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = 1,
            PayloadGeneration = 1,
            PayloadLength = 128,
            PayloadDigestLow = 0x1010,
            PayloadDigestHigh = 0x2020,
            PayloadProvenanceDigestLow = 0x3030,
            PayloadProvenanceDigestHigh = 0x4040,
            NowUtcMilliseconds = now,
            MaximumRecoveryAttempts = 3,
            RecoveryDeadlineUtcMilliseconds = now + 60_000
        };
    }

    private static NativeTransactionJournalMutationInput CreateEffectObservedMutation(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalMutationInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            NowUtcMilliseconds = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Event = (uint)NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.Prepared,
            StableSystemStatus = 202
        };

    private static NativeTransactionJournalStageFeedbackInput CreateSucceededFeedback(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalStageFeedbackInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            CompletedAtUtcMilliseconds = checked(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ExpectedPhase = (uint)NativeTransactionJournalPhase.EffectObserved,
            FeedbackValidMask = (uint)(NativeTransactionJournalFeedbackValidity.CompletedAt |
                NativeTransactionJournalFeedbackValidity.ActualProcessGrade),
            FeedbackFlags = (uint)(NativeTransactionJournalFeedbackFlags.ProcessOwned |
                NativeTransactionJournalFeedbackFlags.RollbackPayloadPersisted),
            FeedbackStatus = (uint)NativeTransactionJournalFeedbackStatus.Succeeded,
            FeedbackSystemStatus = 300,
            ActualProcessGrade = (int)NativeTransactionJournalProcessGrade.Level1
        };

    private static NativeTransactionJournalAckInput CreateAcceptedAcknowledgement(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalAckInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            AcknowledgedAtUtcMilliseconds = checked(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Result = (uint)NativeTransactionJournalAckResult.Accepted,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.FeedbackPending,
            StableSystemStatus = 501
        };

    private static NativeTransactionJournalPayloadBinding CreateBinding(
        HostManagerTransactionJournalRuntime runtime,
        ulong actionId)
        => new(
            JournalInstanceLow: runtime.StartupSnapshot!.Header.JournalInstanceLow,
            JournalInstanceHigh: runtime.StartupSnapshot.Header.JournalInstanceHigh,
            ActionIdentity: new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = runtime.ConfigurationGeneration,
                PlanEpoch = 7,
                ActionId = actionId,
                HostSessionIncarnation = 11,
                TargetId = 13,
                SoftwareId = 17,
                ProcessStartKey = 19,
                ProcessId = 23,
                Reserved = 0
            },
            Scope: NativeTransactionJournalScope.Process,
            Disposition: NativeTransactionJournalDisposition.Apply,
            Domain: NativeTransactionJournalDomain.Process,
            GradeValidMask: NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade: (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade: (int)NativeTransactionJournalProcessGrade.Level1,
            CpuFromGrade: 0,
            CpuToGrade: 0,
            GpuFromGrade: 0,
            GpuToGrade: 0,
            StableSystemStatus: 0,
            StableSystemError: 0,
            MaximumRecoveryAttempts: checked((uint)runtime.MaximumRecoveryAttempts),
            RecoveryDeadlineUtcMilliseconds: checked(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                (ulong)runtime.RecoveryDeadline.TotalMilliseconds),
            AtomicGroupId: 0,
            GroupMemberIndex: 0,
            GroupMemberCount: 0);

    private static async Task SeedEmptyJournalAsync(
        string path,
        uint recordCapacity,
        ulong residentByteBudget)
    {
        var create = new NativeTransactionJournalCreateConfiguration
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalCreateConfiguration>(),
            JournalInstanceLow = 0x1111,
            JournalInstanceHigh = 0x2222,
            RecordCapacity = recordCapacity,
            MaximumResidentBytes = residentByteBudget
        };
        var open = new NativeTransactionJournalOpenConfiguration
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalOpenConfiguration>(),
            MaximumRecordCapacity = recordCapacity,
            MaximumResidentBytes = residentByteBudget
        };
        await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
            new NativeTransactionJournalFileStore(path),
            create,
            open,
            CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected runtime state was not observed.");
            }
            await Task.Delay(1);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-transaction-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
