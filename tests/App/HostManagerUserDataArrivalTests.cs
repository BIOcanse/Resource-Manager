using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerUserDataArrivalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeJournalRecordAndItsRecoveryAuthorityTravelTogether(bool archive)
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope(fixture.Source);
        var identity = new NativeTransactionJournalIdentity
        {
            ConfigurationGeneration = 1, PlanEpoch = 2, ActionId = 3, HostSessionIncarnation = 4,
            TargetId = 5, SoftwareId = 6, ProcessStartKey = 100001, ProcessId = 10
        };
        scope.Open(new(Guid.NewGuid(), $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddMinutes(10), new string('a', 64), [new(10, 100001)], AllowNonAdaptedMemoryTransaction: true));
        Assert.True(scope.TryBeginAdmission(scope.Capture(), HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-before-transfer", new(10, 100001), out var permit));
        var relative = Path.GetRelativePath(fixture.Source,
            HostManagerDurableStorePaths.Resolve(fixture.Source).Single(store => store.Kind == "transaction-journal").CanonicalPath);
        var path = Path.Combine(fixture.Source, relative);
        var create = new NativeTransactionJournalCreateConfiguration
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalCreateConfiguration>(),
            JournalInstanceLow = 7, JournalInstanceHigh = 8, RecordCapacity = 4, MaximumResidentBytes = 16 * 1024 * 1024
        };
        using (var session = new NativeTransactionJournalSession(create))
        {
            var prepare = new NativeTransactionJournalPrepareInput
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
                ExpectedJournalRevision = 1, Identity = identity,
                Scope = (uint)NativeTransactionJournalScope.Process,
                Disposition = (uint)NativeTransactionJournalDisposition.Apply,
                DomainMask = (uint)NativeTransactionJournalDomain.Process,
                GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
                ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
                ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1,
                PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
                PayloadSlot = 1, PayloadGeneration = 1, PayloadLength = 512,
                PayloadDigestLow = 1, PayloadDigestHigh = 2,
                PayloadProvenanceDigestLow = 3, PayloadProvenanceDigestHigh = 4,
                NowUtcMilliseconds = 1000, MaximumRecoveryAttempts = 3, RecoveryDeadlineUtcMilliseconds = 11000
            };
            Assert.Equal(NativeTransactionJournalStatus.Ok, session.Prepare(prepare));
            await new NativeTransactionJournalFileStore(path).PersistAsync(session, new byte[4096], CancellationToken.None);
        }
        Assert.True(scope.TryDeclareNativeTransactionJournal(permit!, identity));
        Assert.True(scope.TryCommitNativeTransactionJournal(permit!, identity));
        var original = File.ReadAllBytes(path);
        if (archive) _ = fixture.Scope(fixture.Target);

        fixture.Arrive(archive);

        fixture.AssertUsable();
        var copied = Path.Combine(fixture.Target, relative);
        Assert.Equal(original, File.ReadAllBytes(copied));
        var open = new NativeTransactionJournalOpenConfiguration
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalOpenConfiguration>(),
            MaximumRecordCapacity = 4, MaximumResidentBytes = 16 * 1024 * 1024
        };
        var restored = await new NativeTransactionJournalFileStore(copied).TryOpenAsync(open, CancellationToken.None);
        using var restoredSession = restored.Session;
        Assert.Equal(NativeTransactionJournalStatus.Ok, restored.Status);
        Assert.NotNull(restoredSession);
        Assert.Equal(NativeTransactionJournalStatus.Ok, restoredSession.Get(identity, out var record));
        Assert.Equal(identity.ActionId, record.Identity.ActionId);
        Assert.True(fixture.Scope(fixture.Target).TryAuthorizeNativeRecovery(HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction, identity));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyEstablishedNamespaceGetsIndependentUsableAuthority(bool archive)
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target); // Import must replace an already initialized authority too.
        var original = File.ReadAllBytes(HostManagerUserDataArrival.Paths(fixture.Source).AuthorityManifestPath);
        if (!archive) OwnedPackageTree.Delete(Path.Combine(fixture.Target, HostManagerUserDataArrival.AuthorityDirectory));
        if (!archive) OwnedPackageTree.Delete(Path.Combine(fixture.Target, "UserData"));

        fixture.Arrive(archive);

        fixture.AssertUsable();
        var next = File.ReadAllBytes(HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(next)));
        Assert.Equal(original, File.ReadAllBytes(HostManagerUserDataArrival.Paths(fixture.Source).AuthorityManifestPath));
        Assert.True(File.Exists(Path.Combine(fixture.Target, HostManagerUserDataArrival.AuthorityDirectory, "user-data-arrival.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutstandingCleanupIsCarriedAndSettlesUnderExistingRecoveryRules(bool archive)
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope(fixture.Source);
        var startedAt = DateTimeOffset.FromFileTime(100001);
        var identity = new HostManagerComputeProcessIdentity(10, 100001);
        scope.Open(new(Guid.NewGuid(), $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddMinutes(10), new string('a', 64),
            [new(10, 100001)], AllowAutomaticMemoryCleanup: true));
        Assert.True(scope.TryBeginAdmission(scope.Capture(), HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "before-transfer", identity, out var permit));
        var relative = Path.GetRelativePath(fixture.Source,
            HostManagerDurableStorePaths.Resolve(fixture.Source).Single(store => store.Kind == "memory-cleanup-attempt-journal").CanonicalPath);
        var journalPath = Path.Combine(fixture.Source, relative);
        HostManagerMemoryCleanupAttemptBatch batch;
        using (var journal = new HostManagerMemoryCleanupAttemptJournal(journalPath, () => 4, TimeProvider.System))
        {
            AutomaticMemoryCleanupDecision decision = new(0,
                new("process:10", "Process 10", 10, startedAt, "background", 1, 1, 1, true),
                AutomaticMemoryCleanupMode.Normal, new(StateSlot: 10, StateGeneration: 1));
            batch = journal.Prepare(7, [decision]);
        }
        Assert.True(scope.TryDeclareMemoryCleanupAttemptBatch(permit!, batch, 7));
        Assert.True(scope.TryCommitMemoryCleanupAttemptBatch(permit!, batch, 7));
        var original = File.ReadAllBytes(journalPath);
        var scopeImage = File.ReadAllBytes(HostManagerUserDataArrival.Paths(fixture.Source).StatePath);
        if (archive) _ = fixture.Scope(fixture.Target);

        fixture.Arrive(archive);

        fixture.AssertUsable();
        var copied = Path.Combine(fixture.Target, relative);
        Assert.Equal(original, File.ReadAllBytes(copied));
        var retained = Directory.GetDirectories(Path.Combine(fixture.Target, "UserData", "HostManager", "Validation", "PreviousInstallations"));
        Assert.Single(retained);
        Assert.Equal(scopeImage, File.ReadAllBytes(Path.Combine(retained[0], "process-effect-scope.json")));
        using var restored = new HostManagerMemoryCleanupAttemptJournal(copied, () => 4, TimeProvider.System);
        Assert.Equal(batch.BatchId, Assert.Single(restored.CapturePendingBatches()).BatchId);
        Assert.Contains(identity, restored.ReconcileAndCaptureBlocked(_ =>
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(10, startedAt))));
        Assert.Empty(restored.ReconcileAndCaptureBlocked(_ =>
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(0, "exited")));
        Assert.Empty(restored.CapturePendingBatches());
        Assert.Equal(original, File.ReadAllBytes(journalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TamperedIncomingNamespaceRejectedAndOriginalDataPreserved(bool archive)
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope(fixture.Source);
        scope.Open(new(Guid.NewGuid(), $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddMinutes(10), new string('a', 64), [new(10, 100001)], AllowAutomaticMemoryCleanup: true));
        var state = HostManagerUserDataArrival.Paths(fixture.Source).StatePath;
        var image = File.ReadAllText(state).Replace("process-effect-validation-scope-storage-v2", "process-effect-validation-scope-storage-v9");
        File.WriteAllText(state, image);
        if (archive)
        {
            _ = fixture.Scope(fixture.Target);
            File.WriteAllText(Path.Combine(fixture.Target, "UserData", "keep.txt"), "current data");
        }

        Assert.Throws<InvalidDataException>(() => fixture.Arrive(archive));

        Assert.Equal(image, File.ReadAllText(state));
        if (archive)
        {
            Assert.Equal("current data", File.ReadAllText(Path.Combine(fixture.Target, "UserData", "keep.txt")));
            fixture.AssertUsable();
        }
    }

    [Fact]
    public void ImportAuthorityPreparationFailurePreservesCurrentAuthorityAndData()
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var manifest = HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath;
        var original = File.ReadAllBytes(manifest);
        var zip = Path.Combine(fixture.Root, "data.zip");
        UserDataArchive.Create(fixture.Source, zip);
        var arrival = new HostManagerUserDataArrival(fixture.Owner, new Committer(failReceipt: true));

        Assert.Throws<IOException>(() => UserDataArchive.Import(fixture.Target, zip, hostManagerArrival: arrival));

        Assert.Equal(original, File.ReadAllBytes(manifest));
        fixture.AssertUsable();
        Assert.Empty(Directory.GetDirectories(fixture.Root, "ResourceManager-import-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionRollbackLayoutAndSettledTargetStoresRemainUsable(bool archive)
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        var source = SeedRollback(fixture.Source);
        var original = File.ReadAllBytes(source.CanonicalPath);
        if (archive)
        {
            _ = fixture.Scope(fixture.Target);
            _ = SeedRollback(fixture.Target);
            var cleanupPath = HostManagerDurableStorePaths.Resolve(fixture.Target)
                .Single(store => store.Kind == "memory-cleanup-attempt-journal").CanonicalPath;
            using var cleanup = new HostManagerMemoryCleanupAttemptJournal(cleanupPath, () => 4, TimeProvider.System);
            Assert.Empty(cleanup.ReconcileAndCaptureBlocked(_ => throw new InvalidOperationException()));
        }

        fixture.Arrive(archive);

        fixture.AssertUsable();
        var paths = JsonHostManagerRollbackStateStore.ResolveProductionPaths(Path.Combine(fixture.Target, "Bin", "ResourceManager"), fixture.Target);
        Assert.Equal(original, File.ReadAllBytes(paths.CanonicalPath));
        Assert.Equal("rollback-state", HostManagerDurableRootManifest.ValidateForTransfer(paths.CanonicalPath, paths.RootManifestPath));
    }

    [Fact]
    public void LostSourceNamespaceCannotBeMistakenForNoHostManagerData()
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        var paths = HostManagerUserDataArrival.Paths(fixture.Source);
        var original = File.ReadAllBytes(paths.AuthorityManifestPath);
        OwnedPackageTree.Delete(Path.Combine(fixture.Source, "UserData"));

        Assert.ThrowsAny<IOException>(() => fixture.Arrive(false));

        Assert.Equal(original, File.ReadAllBytes(paths.AuthorityManifestPath));
    }

    [Fact]
    public void InterruptedFirstInitializationTransfersWithoutChangingSource()
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        var paths = HostManagerUserDataArrival.Paths(fixture.Source);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<HostManagerProcessEffectValidationAuthorityManifest>(File.ReadAllBytes(paths.AuthorityManifestPath), options)!;
        manifest = manifest with { State = HostManagerProcessEffectValidationAuthorityManifestState.Initializing };
        manifest = manifest with { ChecksumSha256 = ProcessEffectValidationScopeDocuments.ComputeAuthorityManifestChecksum(manifest) };
        File.WriteAllBytes(paths.AuthorityManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, options));
        OwnedPackageTree.Delete(Path.GetDirectoryName(paths.StatePath)!);
        var original = File.ReadAllBytes(paths.AuthorityManifestPath);

        fixture.Arrive(false);

        fixture.AssertUsable();
        Assert.Equal(original, File.ReadAllBytes(paths.AuthorityManifestPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonDirectoryAuthorityCannotBeIgnored(bool source)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(source ? fixture.Source : fixture.Target, HostManagerUserDataArrival.AuthorityDirectory);
        File.WriteAllText(path, "invalid authority namespace");

        Assert.Throws<InvalidDataException>(() => fixture.Arrive(!source));

        Assert.Equal("invalid authority namespace", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportCannotOverwriteCurrentRecoveryRecordsEvenIfManifestIsLost(bool removeManifest)
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var paths = SeedRollback(fixture.Target, outstanding: true);
        var original = File.ReadAllBytes(paths.CanonicalPath);
        if (removeManifest) File.Delete(paths.RootManifestPath);

        if (removeManifest) Assert.Throws<InvalidDataException>(() => fixture.Arrive(true));
        else Assert.Throws<InvalidOperationException>(() => fixture.Arrive(true));

        Assert.Equal(original, File.ReadAllBytes(paths.CanonicalPath));
        fixture.AssertUsable();
    }

    private static HostManagerRollbackStateStorePaths SeedRollback(string root, bool outstanding = false)
    {
        var paths = JsonHostManagerRollbackStateStore.ResolveProductionPaths(Path.Combine(root, "Bin", "ResourceManager"), root);
        var state = HostManagerRollbackStateDocument.Empty;
        if (outstanding)
            state = state with { AppliedPlacements = [new("target", "Target", null, "Cpu", [new("CpuAffinity", "record")], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)] };
        var image = HostManagerRollbackStateEnvelopeCodec.Encode(state, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CanonicalPath)!);
        File.WriteAllBytes(paths.CanonicalPath, image);
        var manifest = new HostManagerDurableRootManifest(paths.CanonicalPath, paths.RootManifestPath,
            "rollback-state", WindowsHostManagerDurableRootManifestCommitter.Instance);
        manifest.EnsureInitializing();
        manifest.ValidateOrAdoptCanonical(image);
        return paths;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-user-data-arrival-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Root, "target");
        internal SecurityIdentifier Owner { get; } = WindowsIdentity.GetCurrent().User!;
        internal Fixture() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Target); }
        internal HostManagerProcessEffectValidationScopeAuthority Scope(string root)
        {
            var paths = HostManagerUserDataArrival.Paths(root);
            return new(paths.StatePath, TimeProvider.System, new MembershipProbe(), new Committer(), authorityManifestPath: paths.AuthorityManifestPath);
        }
        internal void Arrive(bool archive)
        {
            var arrival = new HostManagerUserDataArrival(Owner, new Committer());
            if (archive)
            {
                var zip = Path.Combine(Root, "data.zip");
                UserDataArchive.Create(Source, zip);
                UserDataArchive.Import(Target, zip, hostManagerArrival: arrival);
            }
            else
            {
                var stage = Path.Combine(Root, "ResourceManager.installing-stage");
                Directory.CreateDirectory(stage);
                PackageSwitchTransaction.CopyUserData(Source, stage, arrival, Target);
                Directory.Delete(Target);
                Directory.Move(stage, Target);
            }
        }
        internal void AssertUsable()
        {
            var snapshot = Scope(Target).Capture();
            Assert.Equal(HostManagerProcessEffectValidationScopeState.ProductionUnscoped, snapshot.State);
            Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsCycleEffect(snapshot, HostManagerCycleEffectKind.AutomaticMemoryCleanup));
            Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsFreshProcessMemory(snapshot));
            Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(snapshot, 10, 100001));
        }
        public void Dispose()
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(Root);
            OwnedPackageTree.Delete(Root);
        }
    }

    private sealed class MembershipProbe : IWindowsJobMembershipProbe
    {
        public WindowsJobMembershipResult Probe(string jobName, HostManagerComputeProcessIdentity identity)
            => new(WindowsJobMembershipStatus.ExactMember, 0);
    }

    private sealed class Committer(bool failReceipt = false, Action<string>? afterCommit = null) : IHostManagerProcessEffectValidationScopeFileCommitter
    {
        public void Commit(string temporaryPath, string canonicalPath, bool replaceExisting)
        {
            if (failReceipt && Path.GetFileName(canonicalPath) == "user-data-arrival.json") throw new IOException("Injected authority preparation failure.");
            File.Move(temporaryPath, canonicalPath, replaceExisting);
            afterCommit?.Invoke(canonicalPath);
        }
        public void DeleteExact(string canonicalPath) => File.Delete(canonicalPath);
    }
}
