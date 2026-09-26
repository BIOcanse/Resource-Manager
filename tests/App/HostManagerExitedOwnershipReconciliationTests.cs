using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerExitedOwnershipReconciliationTests
{
    private const int ProcessId = 404;
    private const ulong ProcessStartKey = 303;
    private const string SoftwareId = "software:test";

    [Fact]
    public void CompleteSnapshot_SelectsOnlyMissingExactProcessOwnership()
    {
        var record = CreateProcessRecord();

        Assert.Empty(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            CreateProcessSnapshot(includeProcess: true),
            CreateOwnershipSnapshot(record)));

        var missing = Assert.Single(
            HostManagerSmartCoordinator.SelectMissingProcessOwnership(
                CreateProcessSnapshot(includeProcess: false),
                CreateOwnershipSnapshot(record)));
        Assert.Equal(record.RecordRevision, missing.RecordRevision);
    }

    [Fact]
    public void CompleteSnapshot_TreatsCpuAndMemoryPolicyTargetsAsTheSameLiveProcessInstance()
    {
        var cpu = CreateProcessRecord();
        var memory = CreateMemoryProcessRecord();

        Assert.Empty(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            CreateProcessSnapshot(includeProcess: true),
            CreateOwnershipSnapshot(cpu, memory)));

        Assert.Equal(
            2,
            HostManagerSmartCoordinator.SelectMissingProcessOwnership(
                CreateProcessSnapshot(includeProcess: false),
                CreateOwnershipSnapshot(cpu, memory)).Count);
    }

    [Fact]
    public void CompleteSnapshot_SameIncarnationAttributionDriftUsesOwnedSoftwareAnchor()
    {
        var cpu = CreateProcessRecord();
        var memory = CreateMemoryProcessRecord();

        Assert.Empty(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            CreateProcessSnapshot(
                includeProcess: true,
                softwareId: "software:reattributed"),
            CreateOwnershipSnapshot(cpu, memory)));
    }

    [Fact]
    public void SkippedInventoryRowStillSelectsMissingOwnershipForLiveConfirmation()
    {
        var snapshot = CreateProcessSnapshot(includeProcess: false) with
        {
            SkippedCount = 1,
            EnumeratedCount = 1
        };

        Assert.True(snapshot.IsInventoryCurrentComplete());
        Assert.Single(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            snapshot,
            CreateOwnershipSnapshot(CreateProcessRecord())));
    }

    [Fact]
    public void ExcludedUngovernableProcess_DoesNotHideExistingOwnership()
    {
        var excluded = CreateProcessSnapshot(includeProcess: false) with
        {
            EnumeratedCount = 1,
            ExcludedCount = 1
        };

        Assert.True(excluded.IsInventoryCurrentComplete());
        Assert.Single(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            excluded,
            CreateOwnershipSnapshot(CreateProcessRecord())));
    }

    [Fact]
    public void CompleteInventory_MissingRequestedMetricsStillSelectsExitedOwnership()
    {
        var inventoryOnly = CreateProcessSnapshot(includeProcess: false) with
        {
            RequestedMetricMask = SchedulingProcessMetricMask.CpuUsage |
                SchedulingProcessMetricMask.MemoryUsage,
            CurrentMetricMask = SchedulingProcessMetricMask.None
        };

        Assert.True(inventoryOnly.IsInventoryCurrentComplete());
        Assert.False(inventoryOnly.IsCurrentComplete());
        Assert.Single(HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            inventoryOnly,
            CreateOwnershipSnapshot(CreateProcessRecord())));
    }

    [Fact]
    public void CompleteSnapshot_WithReusedPidStillSelectsOldIncarnation()
    {
        var oldProcess = CreateProcessRecord();
        var oldMemory = CreateMemoryProcessRecord();
        var reusedPidFacts = CreateProcessSnapshot(
            includeProcess: true,
            processStartKey: ProcessStartKey + 1);

        var missing = HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            reusedPidFacts,
            CreateOwnershipSnapshot(oldProcess, oldMemory));

        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, record => record.Primary.TargetId == oldProcess.Primary.TargetId);
        Assert.Contains(missing, record => record.Primary.TargetId == oldMemory.Primary.TargetId);

        var snapshotWithSkippedPeer = reusedPidFacts with
        {
            EnumeratedCount = 2,
            SkippedCount = 1
        };
        Assert.True(snapshotWithSkippedPeer.IsInventoryCurrentComplete());
        Assert.Equal(2, HostManagerSmartCoordinator.SelectMissingProcessOwnership(
            snapshotWithSkippedPeer,
            CreateOwnershipSnapshot(oldProcess, oldMemory)).Count);
    }

    [Fact]
    public void RecoveryIdentity_ConfirmsExitedOrDifferentPidIncarnationOnly()
    {
        Assert.True(ProcessInstanceRecovery.IsConfirmedExited(
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                0,
                "exited"),
            ProcessId,
            ProcessStartKey));
        Assert.False(ProcessInstanceRecovery.IsConfirmedExited(
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(
                    ProcessId,
                    DateTimeOffset.FromFileTime(checked((long)ProcessStartKey)))),
            ProcessId,
            ProcessStartKey));
        Assert.True(ProcessInstanceRecovery.IsConfirmedExited(
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(
                    ProcessId,
                    DateTimeOffset.FromFileTime(checked((long)ProcessStartKey + 1)))),
            ProcessId,
            ProcessStartKey));
        Assert.False(ProcessInstanceRecovery.IsConfirmedExited(
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                5,
                "unavailable"),
            ProcessId,
            ProcessStartKey));
    }

    [Fact]
    public void RecoveryIdentity_InvalidFoundEvidenceFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                new RecoveryReadResult<ProcessInstanceRecoverySnapshot>(
                    RecoveryReadStatus.Found,
                    null,
                    0,
                    string.Empty),
                ProcessId,
                ProcessStartKey));
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(
                        ProcessId + 1,
                        DateTimeOffset.FromFileTime(checked((long)ProcessStartKey)))),
                ProcessId,
                ProcessStartKey));
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(ProcessId, DateTimeOffset.MinValue)),
                ProcessId,
                ProcessStartKey));
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                new RecoveryReadResult<ProcessInstanceRecoverySnapshot>(
                    RecoveryReadStatus.NotFoundOrExited,
                    new ProcessInstanceRecoverySnapshot(
                        ProcessId,
                        DateTimeOffset.FromFileTime(checked((long)ProcessStartKey))),
                    0,
                    string.Empty),
                ProcessId,
                ProcessStartKey));
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                new RecoveryReadResult<ProcessInstanceRecoverySnapshot>(
                    RecoveryReadStatus.Unavailable,
                    new ProcessInstanceRecoverySnapshot(
                        ProcessId,
                        DateTimeOffset.FromFileTime(checked((long)ProcessStartKey))),
                    5,
                    "unavailable"),
                ProcessId,
                ProcessStartKey));
        Assert.Throws<InvalidDataException>(() =>
            ProcessInstanceRecovery.IsConfirmedExited(
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                    0,
                    "exited"),
                ProcessId,
                ulong.MaxValue));
    }

    [Fact]
    public void RecoveryRemove_AfterRepeatedPidReuseStillTargetsOldIncarnationCas()
    {
        var oldIncarnation = CreateProcessRecord();
        var newestIncarnation = CreateProcessRecord(ProcessStartKey + 2);
        var current = CreateOwnershipSnapshot(oldIncarnation, newestIncarnation);

        var remove = HostManagerAppliedOwnershipProjection.CreateRecoveryRemove(
            in oldIncarnation,
            current.Header.LedgerRevision,
            oldIncarnation.UpdatedAtUtcMilliseconds + 1);

        Assert.Equal(oldIncarnation.Primary.TargetId, remove.Cas.Primary.TargetId);
        Assert.Equal(
            oldIncarnation.Primary.ProcessStartKey,
            remove.Cas.Primary.ProcessStartKey);
        Assert.Equal(
            oldIncarnation.OriginalBinding.ActionIdentity.ProcessStartKey,
            remove.Cas.OriginalActionIdentity.ProcessStartKey);
        Assert.Equal(oldIncarnation.RecordRevision, remove.Cas.ExpectedRecordRevision);
        Assert.Equal(oldIncarnation.Payload, remove.Cas.Payload);
        Assert.NotEqual(newestIncarnation.Primary.TargetId, remove.Cas.Primary.TargetId);
        Assert.NotEqual(
            newestIncarnation.Primary.ProcessStartKey,
            remove.Cas.Primary.ProcessStartKey);
    }

    private static SchedulingProcessFactSnapshot CreateProcessSnapshot(
        bool includeProcess,
        ulong processStartKey = ProcessStartKey,
        string softwareId = SoftwareId)
    {
        var processes = includeProcess
            ? new[]
            {
                new SchedulingProcessFact(
                    ProcessId,
                    processStartKey,
                    "test",
                    @"c:\test\test.exe",
                    softwareId,
                    "Test",
                    "general",
                    "General",
                    100,
                    SchedulingProcessMetricMask.None,
                    0,
                    0,
                    71,
                    [])
            }
            : [];
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            71,
            DateTimeOffset.UtcNow.UtcTicks,
            checked((uint)processes.Length),
            checked((uint)processes.Length),
            0,
            0,
            SchedulingProcessMetricMask.None,
            SchedulingProcessMetricMask.None,
            processes);
    }

    private static NativeAppliedOwnershipSnapshot CreateOwnershipSnapshot(
        params NativeAppliedOwnershipRecord[] records)
        => new(
            new NativeAppliedOwnershipSnapshotHeader
            {
                AbiVersion = NativeAppliedOwnershipAbi.Version,
                StructSize = NativeAppliedOwnershipAbi.SnapshotHeaderSize,
                LedgerRevision = 51,
                LedgerInstanceLow = 11,
                LedgerInstanceHigh = 12,
                EntryCount = checked((uint)records.Length),
                RecordCapacity = 8,
                PrimaryIndexCapacity = 8,
                PayloadIndexCapacity = 8,
                MaximumImageBytes = 4096,
                ResidentBytes = 2048
            },
            records);

    private static NativeAppliedOwnershipRecord CreateProcessRecord(
        ulong processStartKey = ProcessStartKey)
    {
        var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessTargetId(ProcessId, processStartKey));
        var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(SoftwareId);
        return new NativeAppliedOwnershipRecord
        {
            Primary = new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Process,
                TargetId = targetKey,
                SoftwareId = softwareKey,
                ProcessId = ProcessId,
                ProcessStartKey = processStartKey
            },
            OriginalBinding = new NativeAppliedOwnershipOriginalBinding
            {
                JournalInstanceLow = 21,
                JournalInstanceHigh = 22,
                Scope = (uint)NativeAppliedOwnershipJournalScope.Process,
                Disposition = (uint)NativeAppliedOwnershipDisposition.Apply,
                DomainMask = (uint)NativeAppliedOwnershipDomain.Process,
                GradeValidMask = (uint)NativeAppliedOwnershipGradeValidity.Process,
                ProcessFromGrade = (int)NativeAppliedOwnershipProcessGrade.Normal,
                ProcessToGrade = (int)NativeAppliedOwnershipProcessGrade.Level2,
                MaximumRecoveryAttempts = 3,
                RecoveryDeadlineUtcMilliseconds = 10_000,
                ActionIdentity = new NativeAppliedOwnershipActionIdentity
                {
                    ConfigurationGeneration = 1UL << 32,
                    PlanEpoch = 2,
                    ActionId = 3,
                    HostSessionIncarnation = 4,
                    TargetId = targetKey,
                    SoftwareId = softwareKey,
                    ProcessId = ProcessId,
                    ProcessStartKey = processStartKey
                }
            },
            Payload = new NativeAppliedOwnershipDurablePayloadReference
            {
                Slot = 1,
                Generation = 2,
                Length = 64,
                DigestLow = 41,
                DigestHigh = 42
            },
            CurrentGrades = new NativeAppliedOwnershipCurrentGrades
            {
                ValidMask = (uint)NativeAppliedOwnershipGradeValidity.Process,
                ProcessGrade = (int)NativeAppliedOwnershipProcessGrade.Level2
            },
            RecordRevision = 7,
            PromotedAtUtcMilliseconds = 1_000,
            UpdatedAtUtcMilliseconds = 1_100
        };
    }

    private static NativeAppliedOwnershipRecord CreateMemoryProcessRecord()
    {
        var record = CreateProcessRecord();
        var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                ProcessId,
                ProcessStartKey));
        record.Primary.TargetId = targetKey;
        var binding = record.OriginalBinding;
        binding.ActionIdentity.TargetId = targetKey;
        binding.ActionIdentity.ActionId++;
        binding.DomainMask = (uint)NativeAppliedOwnershipDomain.PhysicalMemory;
        binding.GradeValidMask = (uint)NativeAppliedOwnershipGradeValidity.Memory;
        binding.ProcessFromGrade = 0;
        binding.ProcessToGrade = 3;
        record.OriginalBinding = binding;
        record.Payload.Slot++;
        record.Payload.DigestLow++;
        record.Payload.DigestHigh++;
        record.CurrentGrades = new NativeAppliedOwnershipCurrentGrades
        {
            ValidMask = (uint)NativeAppliedOwnershipGradeValidity.Memory,
            ProcessGrade = 3
        };
        return record;
    }
}
