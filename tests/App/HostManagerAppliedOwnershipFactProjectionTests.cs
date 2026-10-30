using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerAppliedOwnershipFactProjectionTests
{
    private const ulong TargetKey = 101;
    private const ulong SoftwareKey = 202;
    private const uint ProcessId = 404;
    private const ulong ProcessStartKey = 303;
    private const ulong LedgerRevision = 51;

    [Fact]
    public void IndexUsesExactProcessIdentityAndSnapshotLedgerRevision()
    {
        var process = CreateProcessRecord();
        var index = HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
            CreateSnapshot(process));

        Assert.Equal(LedgerRevision, index.LedgerRevision);
        Assert.True(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey,
                SoftwareKey,
                ProcessId,
                ProcessStartKey)));
        Assert.False(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey + 1,
                SoftwareKey,
                ProcessId,
                ProcessStartKey)));
        Assert.False(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey,
                SoftwareKey + 1,
                ProcessId,
                ProcessStartKey)));
        Assert.False(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey,
                SoftwareKey,
                ProcessId + 1,
                ProcessStartKey)));
        Assert.False(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey,
                SoftwareKey,
                ProcessId,
                ProcessStartKey + 1)));
    }

    [Fact]
    public void IndexKeepsMemoryPolicyOwnershipSeparateFromCpuProcessOwnership()
    {
        var cpu = CreateProcessRecord();
        var memory = CreateProcessMemoryPolicyRecord();

        var index = HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
            CreateSnapshot(cpu, memory));

        Assert.Single(index.ProcessRecords);
        var indexedMemory = Assert.Single(index.MemoryProcessRecords);
        Assert.Equal(memory.Primary.TargetId, indexedMemory.Key.TargetKey);
        Assert.Equal(ProcessId, indexedMemory.Key.ProcessId);
        Assert.Equal(ProcessStartKey, indexedMemory.Key.ProcessStartKey);
        Assert.NotEqual(cpu.Primary.TargetId, indexedMemory.Key.TargetKey);
        var anchor = Assert.Single(index.ProcessAnchors).Value;
        Assert.Equal(SoftwareKey, anchor.SoftwareKey);
        Assert.True(anchor.HasProcessOwnership);
        Assert.True(anchor.HasMemoryOwnership);
    }

    [Fact]
    public void IndexRejectsConflictingSoftwareKeysForOneProcessIncarnation()
    {
        var process = CreateProcessRecord();
        var memory = CreateProcessMemoryPolicyRecord();
        memory.Primary.SoftwareId = SoftwareKey + 1;
        var binding = memory.OriginalBinding;
        binding.ActionIdentity.SoftwareId = SoftwareKey + 1;
        memory.OriginalBinding = binding;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
                CreateSnapshot(process, memory)));
    }

    [Fact]
    public void StandaloneOwnedProcessDoesNotInventNotRunningEvidence()
    {
        var process = CreateProcessRecord();
        var identity = new NativeAppliedOwnershipProcessIdentity(
            TargetKey,
            SoftwareKey,
            ProcessId,
            ProcessStartKey);

        var row = HostManagerSmartCoordinator.CreateNativeStandaloneOwnedProcessFact(
            identity,
            in process,
            sourceIndex: 8);

        Assert.Equal(
            NativeSmartCoordinatorInputValidity.ProcessIdentity
                | NativeSmartCoordinatorInputValidity.SoftwareIdentity
                | NativeSmartCoordinatorInputValidity.AppliedProcessGrade,
            row.ValidMask);
        Assert.Equal(NativeSmartCoordinatorInputFlags.OwnsProcessGrade, row.Flags);
        Assert.Equal(TargetKey, row.TargetKey);
        Assert.Equal(SoftwareKey, row.SoftwareKey);
        Assert.Equal(ProcessId, row.ProcessId);
        Assert.Equal(ProcessStartKey, row.ProcessStartKey);
        Assert.Equal(NativeSmartCoordinatorProcessGrade.Level2, row.AppliedProcessGrade);
        Assert.Equal<uint>(8, row.SourceIndex);
    }

    [Fact]
    public void ProcessIdentityPreservesExactZeroSoftwareId()
    {
        var process = CreateProcessRecord();
        process.Primary.SoftwareId = 0;
        process.OriginalBinding.ActionIdentity.SoftwareId = 0;

        var index = HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
            CreateSnapshot(process));

        Assert.True(index.ProcessRecords.ContainsKey(
            new NativeAppliedOwnershipProcessIdentity(
                TargetKey,
                0,
                ProcessId,
                ProcessStartKey)));
    }

    [Fact]
    public void IndexRejectsZeroLedgerRevisionUnknownScopeDomainGradeAndReservedData()
    {
        var process = CreateProcessRecord();
        var zeroRevision = CreateSnapshot(process) with
        {
            Header = CreateHeader(entryCount: 1, ledgerRevision: 0)
        };
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(zeroRevision));

        var unknownScope = process;
        unknownScope.Primary.Scope = 99;
        AssertInvalid(unknownScope);

        var invalidDomain = process;
        invalidDomain.OriginalBinding.DomainMask = (uint)NativeAppliedOwnershipDomain.PhysicalMemory;
        AssertInvalid(invalidDomain);

        var invalidGrade = process;
        invalidGrade.CurrentGrades.ProcessGrade = 99;
        AssertInvalid(invalidGrade);

        var reserved = process;
        reserved.ReservedUInt32 = 1;
        AssertInvalid(reserved);
    }

    [Fact]
    public void IndexRejectsDuplicateIdentityAndHeaderCountDrift()
    {
        var process = CreateProcessRecord();
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
                CreateSnapshot(process, process)));

        var countDrift = new NativeAppliedOwnershipSnapshot(
            CreateHeader(entryCount: 2, ledgerRevision: LedgerRevision),
            [process]);
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(countDrift));
    }

    [Fact]
    public void NativeFactsSourceHasNoLegacyAppliedReceiptAuthority()
    {
        var sourcePath = Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.NativeFacts.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("NativeAppliedOwnershipFactIndex ownership", source, StringComparison.Ordinal);
        Assert.Contains("ownership.ProcessAnchors", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Header.LedgerRevision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppliedTargets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HostManagerNativeReceiptCodec", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadNativeCpuGrade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadNativeGpuGrade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveAppliedEpoch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveTerminatedNativeProcessReceiptsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AllNativeReceiptsRepresented", source, StringComparison.Ordinal);
        Assert.Contains("AllAppliedOwnershipRecordsRepresented", source, StringComparison.Ordinal);
    }

    private static void AssertInvalid(NativeAppliedOwnershipRecord record)
    {
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
                CreateSnapshot(record)));
    }

    private static NativeAppliedOwnershipSnapshot CreateSnapshot(
        params NativeAppliedOwnershipRecord[] records)
        => new(
            CreateHeader(checked((uint)records.Length), LedgerRevision),
            records);

    private static NativeAppliedOwnershipSnapshotHeader CreateHeader(
        uint entryCount,
        ulong ledgerRevision)
        => new()
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.SnapshotHeaderSize,
            LedgerRevision = ledgerRevision,
            LedgerInstanceLow = 11,
            LedgerInstanceHigh = 12,
            EntryCount = entryCount,
            RecordCapacity = 8,
            PrimaryIndexCapacity = 8,
            PayloadIndexCapacity = 8,
            MaximumImageBytes = 4096,
            ResidentBytes = 2048
        };

    private static NativeAppliedOwnershipRecord CreateProcessRecord()
        => new()
        {
            Primary = new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Process,
                TargetId = TargetKey,
                SoftwareId = SoftwareKey,
                ProcessId = ProcessId,
                ProcessStartKey = ProcessStartKey
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
                    TargetId = TargetKey,
                    SoftwareId = SoftwareKey,
                    ProcessId = ProcessId,
                    ProcessStartKey = ProcessStartKey
                }
            },
            Payload = CreatePayload(),
            CurrentGrades = new NativeAppliedOwnershipCurrentGrades
            {
                ValidMask = (uint)NativeAppliedOwnershipGradeValidity.Process,
                ProcessGrade = (int)NativeAppliedOwnershipProcessGrade.Level2
            },
            RecordRevision = 7,
            PromotedAtUtcMilliseconds = 1_000,
            UpdatedAtUtcMilliseconds = 1_100
        };

    private static NativeAppliedOwnershipRecord CreateProcessMemoryPolicyRecord()
    {
        var record = CreateProcessRecord();
        var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                checked((int)ProcessId),
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
        record.CurrentGrades.ValidMask =
            (uint)NativeAppliedOwnershipGradeValidity.Memory;
        record.CurrentGrades.ProcessGrade = 3;
        record.Payload.Slot++;
        record.Payload.DigestLow++;
        record.Payload.DigestHigh++;
        return record;
    }

    private static NativeAppliedOwnershipDurablePayloadReference CreatePayload()
        => new()
        {
            Slot = 1,
            Generation = 2,
            Length = 3,
            DigestLow = 4,
            DigestHigh = 5
        };

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", "src", "Core"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
