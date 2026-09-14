using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Paths;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryCleanupAttemptJournalTests
{
    private const string AttemptJournalStoreKind = "memory-cleanup-attempt-journal";
    private static readonly JsonSerializerOptions JournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public void RestartRetiresPreparedBeforeWriterWithoutInspectingTheLiveProcess()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(90_001);
        try
        {
            using (var journal = CreateJournal(path, capacity: 1))
            {
                _ = journal.PrepareBeforeWriter(
                    6,
                    [CreateDecision(9, startedAt)]);
            }

            using var restarted = CreateJournal(path, capacity: 1);
            var blocked = restarted.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException(
                    "A pre-writer batch must retire without process inspection."));

            Assert.Empty(blocked);
            _ = restarted.PrepareBeforeWriter(
                7,
                [CreateDecision(9, startedAt)]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriterArmSeparatesRetirablePreparationFromUnknownOutcome()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(95_001);
        try
        {
            using (var journal = CreateJournal(path, capacity: 1))
            {
                var batch = journal.PrepareBeforeWriter(
                    8,
                    [CreateDecision(95, startedAt)]);
                journal.ArmForWriter(batch);
                Assert.Throws<InvalidDataException>(() =>
                    journal.RetirePreparedBeforeWriter(batch));
            }

            using var restarted = CreateJournal(path, capacity: 1);
            var blocked = restarted.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(95, startedAt)));

            Assert.Contains(new HostManagerComputeProcessIdentity(95, 95_001), blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestartPreservesUnknownUntilTheExactProcessInstanceExits()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(100_001);
        try
        {
            using (var journal = CreateJournal(path, capacity: 2))
            {
                _ = journal.Prepare(7, [CreateDecision(10, startedAt)]);
            }

            using (var restarted = CreateJournal(path, capacity: 2))
            {
                var blocked = restarted.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(10, startedAt)));
                Assert.Contains(new HostManagerComputeProcessIdentity(10, 100_001), blocked);
            }

            using (var afterExit = CreateJournal(path, capacity: 2))
            {
                var blocked = afterExit.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                        0,
                        "exited"));
                Assert.Empty(blocked);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TerminalSettlementHasNoCrossTickCooldown()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(200_001);
        try
        {
            using var journal = CreateJournal(path, capacity: 1);
            var first = journal.Prepare(8, [CreateDecision(20, startedAt)]);
            journal.Settle(first, first.Processes);

            var blocked = journal.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("No terminal entry should be read."));
            Assert.Empty(blocked);

            var second = journal.Prepare(9, [CreateDecision(20, startedAt)]);
            Assert.Equal(first.Processes, second.Processes);
            Assert.NotEqual(first.BatchId, second.BatchId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PartialSettlementKeepsOnlyMissingResultsUnknown()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(300_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(300_002);
        try
        {
            using var journal = CreateJournal(path, capacity: 2);
            var batch = journal.Prepare(
                10,
                [
                    CreateDecision(30, firstStartedAt),
                    CreateDecision(31, secondStartedAt)
                ]);
            journal.Settle(
                batch,
                new HashSet<HostManagerComputeProcessIdentity>
                {
                    new(30, 300_001)
                });

            var blocked = journal.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    processId == 30
                        ? new(30, firstStartedAt)
                        : new(31, secondStartedAt)));
            Assert.DoesNotContain(new HostManagerComputeProcessIdentity(30, 300_001), blocked);
            Assert.Contains(new HostManagerComputeProcessIdentity(31, 300_002), blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TamperedDocumentFailsClosed()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(400_001);
        try
        {
            using (var journal = CreateJournal(path, capacity: 1))
            {
                _ = journal.Prepare(11, [CreateDecision(40, startedAt)]);
            }

            var json = File.ReadAllText(path);
            Assert.Contains("\"processId\": 40", json, StringComparison.Ordinal);
            File.WriteAllText(
                path,
                json.Replace("\"processId\": 40", "\"processId\": 41", StringComparison.Ordinal));

            using var tampered = CreateJournal(path, capacity: 1);
            Assert.Throws<InvalidDataException>(() =>
                tampered.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                        0,
                        "exited")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FullJournalRejectsNewIrreversibleAttempts()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using var journal = CreateJournal(path, capacity: 1);
            _ = journal.Prepare(
                12,
                [CreateDecision(50, DateTimeOffset.FromFileTime(500_001))]);

            Assert.Throws<InvalidOperationException>(() => journal.Prepare(
                13,
                [CreateDecision(51, DateTimeOffset.FromFileTime(500_002))]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OwnerLeaseRejectsConcurrentJournalOwners()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using var first = CreateJournal(path, capacity: 1);
            _ = first.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("The empty journal has no process to read."));

            using var second = CreateJournal(path, capacity: 1);
            Assert.Throws<IOException>(() => second.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("The owner lease must fail first.")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnavailableProcessStateRemainsBlocked()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(600_001);
        try
        {
            using var journal = CreateJournal(path, capacity: 1);
            _ = journal.Prepare(14, [CreateDecision(60, startedAt)]);

            var blocked = journal.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                    5,
                    "access denied"));

            Assert.Contains(new HostManagerComputeProcessIdentity(60, 600_001), blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplacedProcessInstanceReclaimsTheOldAttempt()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using var journal = CreateJournal(path, capacity: 1);
            _ = journal.Prepare(
                15,
                [CreateDecision(70, DateTimeOffset.FromFileTime(700_001))]);

            var blocked = journal.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(70, DateTimeOffset.FromFileTime(700_002))));

            Assert.Empty(blocked);
            _ = journal.Prepare(
                16,
                [CreateDecision(70, DateTimeOffset.FromFileTime(700_002))]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductionPathsUseStableStorageGenerationAndRetainBothLegacyPaths()
    {
        var root = CreateTemporaryRoot();
        var appRoot = Path.Combine(root, "Resource Manager-APP");
        Directory.CreateDirectory(appRoot);
        try
        {
            var paths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(appRoot);

            Assert.Equal(
                Path.Combine(
                    root,
                    "UserData",
                    "HostManager",
                    "MemoryCleanup",
                    "generation-00000001",
                    "host-manager-memory-cleanup-attempts.json"),
                paths.CanonicalPath);
            Assert.Equal(2, paths.LegacyPaths.Count);
            Assert.Equal(
                Path.Combine(
                    root,
                    "UserData",
                    "HostManager",
                    "MemoryCleanup",
                    "host-manager-memory-cleanup-attempts.json"),
                paths.LegacyPaths[0]);
            Assert.Equal(
                Path.Combine(
                    root,
                    "Config",
                    "host-manager-memory-cleanup-attempts.json"),
                paths.LegacyPaths[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegisteredInstallRootKeepsCanonicalStableAcrossVersionRoots()
    {
        var root = CreateTemporaryRoot();
        var installRoot = Path.Combine(root, "Installed");
        var firstContentRoot = Path.Combine(root, "Packages", "1.0.0", "Resource Manager-APP");
        var secondContentRoot = Path.Combine(root, "Packages", "2.0.0", "Resource Manager-APP");
        Directory.CreateDirectory(firstContentRoot);
        Directory.CreateDirectory(secondContentRoot);
        try
        {
            var first = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
                firstContentRoot,
                installRoot);
            var second = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
                secondContentRoot,
                installRoot);

            Assert.Equal(first.CanonicalPath, second.CanonicalPath);
            Assert.Equal(
                Path.Combine(
                    installRoot,
                    "UserData",
                    "HostManager",
                    "MemoryCleanup",
                    "generation-00000001",
                    "host-manager-memory-cleanup-attempts.json"),
                first.CanonicalPath);
            Assert.Contains(
                Path.Combine(
                    root,
                    "Packages",
                    "1.0.0",
                    "UserData",
                    "HostManager",
                    "MemoryCleanup",
                    "host-manager-memory-cleanup-attempts.json"),
                first.LegacyPaths);
            Assert.Contains(
                Path.Combine(
                    root,
                    "Packages",
                    "2.0.0",
                    "UserData",
                    "HostManager",
                    "MemoryCleanup",
                    "host-manager-memory-cleanup-attempts.json"),
                second.LegacyPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitPackageRootOverridesAnUnrelatedRegisteredInstallRoot()
    {
        var root = CreateTemporaryRoot();
        var contentRoot = Path.Combine(root, "Candidate", "Resource Manager-APP");
        var registeredInstallRoot = Path.Combine(root, "Installed");
        var explicitPackageRoot = Path.Combine(root, "Candidate");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var resolved = HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                contentRoot,
                registeredInstallRoot,
                explicitPackageRoot,
                allowUnregisteredFallback: false);

            Assert.Equal(explicitPackageRoot, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegisteredInstallRootMustBeAnAbsolutePath()
    {
        var root = CreateTemporaryRoot();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                    root,
                    "relative-install-root"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstalledModeWithoutRegisteredRootFailsClosed()
    {
        var root = CreateTemporaryRoot();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                    root,
                    registeredInstallRoot: null,
                    allowUnregisteredFallback: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MachineRegistrationRejectsNonstringsAndConflictingRegistryViews()
    {
        var root = CreateTemporaryRoot();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveRegisteredInstallRoot([42]));
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveRegisteredInstallRoot(
                [
                    Path.Combine(root, "First"),
                    Path.Combine(root, "Second")
                ]));
            Assert.Equal(
                Path.Combine(root, "Same"),
                HostManagerDurableDataRootResolver.ResolveRegisteredInstallRoot(
                [
                    Path.Combine(root, "Same"),
                    Path.Combine(root, "Same") + Path.DirectorySeparatorChar
                ]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeploymentRegistrationPrefersMachineAndRequiresCompleteGreenInstall()
    {
        var root = CreateTemporaryRoot();
        var machineRoot = Path.Combine(root, "Machine");
        var userRoot = Path.Combine(root, "Green");
        try
        {
            Assert.Equal(
                machineRoot,
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [machineRoot],
                    ["invalid user path"],
                    [0]));
            Assert.Equal(
                userRoot,
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [userRoot, userRoot + Path.DirectorySeparatorChar],
                    [1, 1]));
            Assert.Null(
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [],
                    []));
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [userRoot],
                    []));
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [],
                    [1]));
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [userRoot],
                    [0]));
            Assert.Throws<InvalidDataException>(() =>
                HostManagerDurableDataRootResolver.ResolveDeploymentInstallRoot(
                    [],
                    [userRoot],
                    ["1"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BinLayoutKeepsCanonicalStableAcrossNestedVersionRoots()
    {
        var root = CreateTemporaryRoot();
        var firstContentRoot = Path.Combine(root, "Bin", "Service", "versions", "1.0.0");
        var secondContentRoot = Path.Combine(root, "Bin", "Service", "versions", "2.0.0");
        Directory.CreateDirectory(firstContentRoot);
        Directory.CreateDirectory(secondContentRoot);
        try
        {
            var first = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(firstContentRoot);
            var second = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(secondContentRoot);

            Assert.Equal(first.CanonicalPath, second.CanonicalPath);
            Assert.StartsWith(
                Path.Combine(root, "UserData", "HostManager"),
                first.CanonicalPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VirginJournalStaysVirginUntilTheFirstPrepare()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using var journal = CreateJournal(path, capacity: 1);
            var blocked = journal.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("A virgin journal has no process."));

            Assert.Empty(blocked);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".root.manifest"));

            _ = journal.Prepare(
                20,
                [CreateDecision(80, DateTimeOffset.FromFileTime(800_001))]);

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".root.manifest"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializedCanonicalDeletionFailsClosedEvenWhenLegacyExists()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(900_001);
        try
        {
            using (var journal = CreateJournal(path, legacyPath, capacity: 1))
            {
                _ = journal.Prepare(21, [CreateDecision(90, startedAt)]);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.Copy(path, legacyPath);
            File.Delete(path);

            using var reopened = CreateJournal(path, legacyPath, capacity: 1);
            Assert.Throws<IOException>(() => reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(90, startedAt))));
            Assert.True(File.Exists(legacyPath));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveCanonicalDeletionBlocksTheNextPrepare()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using var journal = CreateJournal(path, capacity: 2);
            _ = journal.Prepare(
                22,
                [CreateDecision(100, DateTimeOffset.FromFileTime(1_000_001))]);
            File.Delete(path);

            Assert.Throws<IOException>(() => journal.Prepare(
                23,
                [CreateDecision(101, DateTimeOffset.FromFileTime(1_000_002))]));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyConfigMigrationPreservesUnknownAndApplyingEntries()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(1_100_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(1_100_002);
        try
        {
            SeedLegacyUnknownAndApplying(
                legacyPath,
                firstProcessId: 110,
                firstStartedAt,
                secondProcessId: 111,
                secondStartedAt);

            var legacyJson = File.ReadAllText(legacyPath);
            var legacyCanonicalImage = File.ReadAllBytes(legacyPath);
            Assert.Contains("\"phase\": 2", legacyJson, StringComparison.Ordinal);
            Assert.Contains("\"phase\": 1", legacyJson, StringComparison.Ordinal);

            var recordingCommitter = new RecordingCanonicalCommitter(
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance);
            using (var journal = CreateJournal(
                path,
                [legacyPath],
                capacity: 2,
                recordingCommitter,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance))
            {
                var blocked = journal.ReconcileAndCaptureBlocked(processId =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(
                            processId,
                            processId == 110 ? firstStartedAt : secondStartedAt)));

                Assert.Contains(new HostManagerComputeProcessIdentity(110, 1_100_001), blocked);
                Assert.Contains(new HostManagerComputeProcessIdentity(111, 1_100_002), blocked);
            }

            Assert.NotEmpty(recordingCommitter.ExpectedImages);
            Assert.Equal(legacyCanonicalImage, recordingCommitter.ExpectedImages[0]);

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".root.manifest"));
            Assert.False(File.Exists(legacyPath));

            using var reopened = CreateJournal(path, legacyPath, capacity: 2);
            var reopenedBlocked = reopened.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 110 ? firstStartedAt : secondStartedAt)));
            Assert.Equal(2, reopenedBlocked.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GenerationlessUserDataWinsOverStaleConfigDuringMigration()
    {
        var root = CreateTemporaryRoot();
        var appRoot = Path.Combine(root, "Resource Manager-APP");
        Directory.CreateDirectory(appRoot);
        var paths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(appRoot);
        var generationlessUserData = paths.LegacySources.Single(
            static source => source.UsesDurableRootManifest).Path;
        var legacyConfig = paths.LegacySources.Single(
            static source => !source.UsesDurableRootManifest).Path;
        var currentStartedAt = DateTimeOffset.FromFileTime(1_150_001);
        var staleStartedAt = DateTimeOffset.FromFileTime(1_150_002);
        try
        {
            using (var current = CreateJournal(generationlessUserData, capacity: 1))
            {
                _ = current.Prepare(102, [CreateDecision(115, currentStartedAt)]);
            }
            SeedLegacySingle(
                legacyConfig,
                generation: 103,
                processId: 116,
                startedAt: staleStartedAt);

            using var migrated = CreateJournal(paths, capacity: 1);
            var blocked = migrated.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 115 ? currentStartedAt : staleStartedAt)));

            Assert.Single(blocked);
            Assert.Contains(new HostManagerComputeProcessIdentity(115, 1_150_001), blocked);
            Assert.DoesNotContain(new HostManagerComputeProcessIdentity(116, 1_150_002), blocked);
            Assert.False(File.Exists(generationlessUserData));
            Assert.False(File.Exists(legacyConfig));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializedGenerationlessUserDataMissingCanonicalBlocksConfigFallback()
    {
        var root = CreateTemporaryRoot();
        var appRoot = Path.Combine(root, "Resource Manager-APP");
        Directory.CreateDirectory(appRoot);
        var paths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(appRoot);
        var generationlessUserData = paths.LegacySources.Single(
            static source => source.UsesDurableRootManifest).Path;
        var legacyConfig = paths.LegacySources.Single(
            static source => !source.UsesDurableRootManifest).Path;
        var currentStartedAt = DateTimeOffset.FromFileTime(1_160_001);
        var staleStartedAt = DateTimeOffset.FromFileTime(1_160_002);
        try
        {
            using (var current = CreateJournal(generationlessUserData, capacity: 1))
            {
                _ = current.Prepare(104, [CreateDecision(117, currentStartedAt)]);
            }
            SeedLegacySingle(
                legacyConfig,
                generation: 105,
                processId: 118,
                startedAt: staleStartedAt);
            File.Delete(generationlessUserData);

            using var migrated = CreateJournal(paths, capacity: 1);
            Assert.Throws<IOException>(() => migrated.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(118, staleStartedAt))));
            Assert.False(File.Exists(paths.CanonicalPath));
            Assert.True(File.Exists(legacyConfig));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConflictingSameTierUserDataAuthoritiesFailClosedWithoutRetirement()
    {
        var root = CreateTemporaryRoot();
        var canonicalPath = Path.Combine(root, "Stable", "generation-00000001", "attempts.json");
        var firstLegacyPath = Path.Combine(root, "Stable", "attempts.json");
        var secondLegacyPath = Path.Combine(root, "Version", "attempts.json");
        var paths = new HostManagerMemoryCleanupAttemptJournalPaths(
            canonicalPath,
            [
                new(firstLegacyPath, UsesDurableRootManifest: true),
                new(secondLegacyPath, UsesDurableRootManifest: true)
            ]);
        try
        {
            using (var first = CreateJournal(firstLegacyPath, capacity: 2))
            {
                _ = first.Prepare(
                    106,
                    [CreateDecision(119, DateTimeOffset.FromFileTime(1_190_001))]);
            }
            using (var second = CreateJournal(secondLegacyPath, capacity: 2))
            {
                _ = second.Prepare(
                    107,
                    [CreateDecision(120, DateTimeOffset.FromFileTime(1_200_001))]);
            }
            var firstImage = File.ReadAllBytes(firstLegacyPath);
            var secondImage = File.ReadAllBytes(secondLegacyPath);

            using var migrating = CreateJournal(paths, capacity: 2);
            Assert.Throws<InvalidDataException>(() => migrating.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("Conflicting authorities must fail first.")));

            Assert.False(File.Exists(canonicalPath));
            Assert.Equal(firstImage, File.ReadAllBytes(firstLegacyPath));
            Assert.Equal(secondImage, File.ReadAllBytes(secondLegacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetainedUserDataSurvivesPackageConfigReplacement()
    {
        var root = CreateTemporaryRoot();
        var appRoot = Path.Combine(root, "Resource Manager-APP");
        Directory.CreateDirectory(appRoot);
        var paths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(appRoot);
        var legacyConfigPath = paths.LegacyPaths[1];
        var startedAt = DateTimeOffset.FromFileTime(1_200_001);
        try
        {
            using (var legacy = CreateJournal(legacyConfigPath, capacity: 1))
            {
                _ = legacy.Prepare(24, [CreateDecision(120, startedAt)]);
            }
            File.Delete(legacyConfigPath + ".root.manifest");

            using (var migrated = CreateJournal(
                paths.CanonicalPath,
                legacyConfigPath,
                capacity: 1))
            {
                var blocked = migrated.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(120, startedAt)));
                Assert.Single(blocked);
            }

            Directory.Delete(Path.Combine(root, "Config"), recursive: true);
            Directory.CreateDirectory(Path.Combine(root, "Config"));

            using var reopened = CreateJournal(
                paths.CanonicalPath,
                legacyConfigPath,
                capacity: 1);
            var reopenedBlocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(120, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(120, 1_200_001), reopenedBlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingNewCanonicalWinsAndRetiresStaleLegacyState()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var currentStartedAt = DateTimeOffset.FromFileTime(1_300_001);
        var staleStartedAt = DateTimeOffset.FromFileTime(1_300_002);
        try
        {
            using (var current = CreateJournal(path, capacity: 1))
            {
                _ = current.Prepare(25, [CreateDecision(130, currentStartedAt)]);
            }
            using (var normalizedCurrent = CreateJournal(path, capacity: 1))
            {
                var normalized = normalizedCurrent.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(130, currentStartedAt)));
                Assert.Single(normalized);
            }
            using (var legacy = CreateJournal(legacyPath, capacity: 1))
            {
                _ = legacy.Prepare(26, [CreateDecision(131, staleStartedAt)]);
            }
            File.Delete(legacyPath + ".root.manifest");
            var canonicalImage = File.ReadAllBytes(path);

            using var reopened = CreateJournal(path, legacyPath, capacity: 1);
            var blocked = reopened.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 130 ? currentStartedAt : staleStartedAt)));

            Assert.Single(blocked);
            Assert.Contains(new HostManagerComputeProcessIdentity(130, 1_300_001), blocked);
            Assert.DoesNotContain(new HostManagerComputeProcessIdentity(131, 1_300_002), blocked);
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingCanonicalAdmissionWaitsForLegacyOwnerAndThenRetiresWithoutRewrite()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(1_350_001);
        try
        {
            var canonicalImage = SeedAndNormalize(
                path,
                capacity: 1,
                generation: 108,
                processId: 135,
                startedAt: startedAt);
            SeedLegacySingle(
                legacyPath,
                generation: 109,
                processId: 136,
                startedAt: DateTimeOffset.FromFileTime(1_360_001));

            using var reopened = CreateJournal(path, legacyPath, capacity: 1);
            using (var legacyLease = new FileStream(
                legacyPath + ".owner.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.Throws<IOException>(() => reopened.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(135, startedAt))));
                Assert.Equal(canonicalImage, File.ReadAllBytes(path));
                Assert.True(File.Exists(legacyPath));
            }

            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(135, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(135, 1_350_001), blocked);
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingCanonicalRetirementFailureRetriesOnSameInstanceWithoutRewrite()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(1_370_001);
        try
        {
            var canonicalImage = SeedAndNormalize(
                path,
                capacity: 1,
                generation: 110,
                processId: 137,
                startedAt: startedAt);
            SeedLegacySingle(
                legacyPath,
                generation: 111,
                processId: 138,
                startedAt: DateTimeOffset.FromFileTime(1_380_001));
            using var reopened = CreateJournal(
                path,
                [legacyPath],
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                new FailOnceLegacyFileRetirer());

            Assert.Throws<IOException>(() => reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(137, startedAt))));
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));
            Assert.True(File.Exists(legacyPath));

            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(137, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(137, 1_370_001), blocked);
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PartialMultiLegacyRetirementResumesFromExistingCanonical()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var firstLegacyPath = Path.Combine(root, "Config", "attempts.json");
        var secondLegacyPath = Path.Combine(root, "Version", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(1_390_001);
        try
        {
            var canonicalImage = SeedAndNormalize(
                path,
                capacity: 1,
                generation: 112,
                processId: 139,
                startedAt: startedAt);
            SeedLegacySingle(
                firstLegacyPath,
                generation: 113,
                processId: 140,
                startedAt: DateTimeOffset.FromFileTime(1_400_001));
            SeedLegacySingle(
                secondLegacyPath,
                generation: 114,
                processId: 141,
                startedAt: DateTimeOffset.FromFileTime(1_410_001));
            using var reopened = CreateJournal(
                path,
                [firstLegacyPath, secondLegacyPath],
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                new DeleteFirstThenFailOnceLegacyFileRetirer());

            Assert.Throws<IOException>(() => reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(139, startedAt))));
            Assert.False(File.Exists(firstLegacyPath));
            Assert.True(File.Exists(secondLegacyPath));
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));

            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(139, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(139, 1_390_001), blocked);
            Assert.False(File.Exists(secondLegacyPath));
            Assert.Equal(canonicalImage, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyOwnerLeaseBlocksMigration()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        try
        {
            using (var legacy = CreateJournal(legacyPath, capacity: 1))
            {
                _ = legacy.Prepare(
                    27,
                    [CreateDecision(140, DateTimeOffset.FromFileTime(1_400_001))]);
            }
            File.Delete(legacyPath + ".root.manifest");

            using var legacyLease = new FileStream(
                legacyPath + ".owner.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var migrating = CreateJournal(path, legacyPath, capacity: 1);

            Assert.Throws<IOException>(() => migrating.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("Migration must fail at the legacy lease.")));
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyOwnerLeaseBlocksVirginCutoverWithoutALegacyCanonical()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        try
        {
            using var legacyLease = new FileStream(
                legacyPath + ".owner.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var migrating = CreateJournal(path, legacyPath, capacity: 1);

            Assert.Throws<IOException>(() => migrating.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("The legacy lease must fail first.")));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void FirstCreateManifestFailureBeforeCanonicalIsNotCommitted(
        int successfulManifestCommits,
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var manifestCommitter = new FaultInjectingManifestCommitter();
        manifestCommitter.FailAfterSuccessfulCommits(
            successfulManifestCommits,
            failAfterManifestReplace,
            new IOException("Injected manifest transition failure."));
        try
        {
            using (var journal = CreateJournal(
                path,
                legacyPath: null,
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                manifestCommitter))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => journal.Prepare(
                        28,
                        [CreateDecision(150, DateTimeOffset.FromFileTime(1_500_001))]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
                Assert.False(File.Exists(path));
            }

            using var reopened = CreateJournal(path, capacity: 1);
            Assert.Empty(reopened.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("No effect was admitted.")));
            _ = reopened.Prepare(
                29,
                [CreateDecision(150, DateTimeOffset.FromFileTime(1_500_001))]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanonicalRenameFailureIsNotCommittedAndRestartRemainsVirgin()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            using (var journal = CreateJournal(
                path,
                legacyPath: null,
                capacity: 1,
                RejectingCanonicalCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => journal.Prepare(
                        30,
                        [CreateDecision(160, DateTimeOffset.FromFileTime(1_600_001))]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
                Assert.False(File.Exists(path));
            }

            using var reopened = CreateJournal(path, capacity: 1);
            Assert.Empty(reopened.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("No canonical was committed.")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsCanonicalCommitterReportsMissingTemporaryFileAsNotCommitted()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        try
        {
            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(() =>
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance.Commit(
                    Path.Combine(root, "missing.tmp"),
                    path,
                    [1, 2, 3]));

            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                exception.Outcome);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TemporaryCleanupFailureDoesNotMaskTheTypedCommitOutcome()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var committer = new LockingRejectingCanonicalCommitter();
        try
        {
            using var journal = CreateJournal(
                path,
                legacyPath: null,
                capacity: 1,
                committer,
                WindowsHostManagerDurableRootManifestCommitter.Instance);

            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                () => journal.Prepare(
                    37,
                    [CreateDecision(210, DateTimeOffset.FromFileTime(2_100_001))]));
            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                exception.Outcome);
        }
        finally
        {
            committer.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanonicalReadbackFailureIsAmbiguousAndRestartRollsForward()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(1_700_001);
        try
        {
            using (var journal = CreateJournal(
                path,
                legacyPath: null,
                capacity: 1,
                MismatchedReadbackCanonicalCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => journal.Prepare(31, [CreateDecision(170, startedAt)]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception.Outcome);
                Assert.True(File.Exists(path));
            }

            AssertBlockedAcrossReopens(path, processId: 170, startedAt, reopenCount: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManifestFinalizeFailureIsAmbiguousAndRestartRollsForward(
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(1_800_001);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        manifestCommitter.FailAfterSuccessfulCommits(
            successfulCommitsBeforeFailure: 2,
            failAfterManifestReplace,
            new IOException("Injected manifest finalize failure."));
        try
        {
            using (var journal = CreateJournal(
                path,
                legacyPath: null,
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                manifestCommitter))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => journal.Prepare(32, [CreateDecision(180, startedAt)]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception.Outcome);
                Assert.True(File.Exists(path));
            }

            AssertBlockedAcrossReopens(path, processId: 180, startedAt, reopenCount: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializedReplaceNotCommittedKeepsOnlyThePriorEntry()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(1_900_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(1_900_002);
        try
        {
            using (var seeded = CreateJournal(path, capacity: 2))
            {
                _ = seeded.Prepare(33, [CreateDecision(190, firstStartedAt)]);
            }
            var priorCanonicalImage = File.ReadAllBytes(path);

            using (var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                RejectingCanonicalCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => updating.Prepare(34, [CreateDecision(191, secondStartedAt)]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
            }
            Assert.Equal(priorCanonicalImage, File.ReadAllBytes(path));

            using var reopened = CreateJournal(path, capacity: 2);
            var blocked = reopened.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 190 ? firstStartedAt : secondStartedAt)));
            Assert.Single(blocked);
            Assert.Contains(new HostManagerComputeProcessIdentity(190, 1_900_001), blocked);
            Assert.DoesNotContain(new HostManagerComputeProcessIdentity(191, 1_900_002), blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializedReplaceUnknownCommitFailureRollsForwardBothEntries()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(2_000_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(2_000_002);
        var recordingCommitter = new RecordingCanonicalCommitter(
            CommitThenThrowCanonicalCommitter.Instance);
        try
        {
            using (var seeded = CreateJournal(path, capacity: 2))
            {
                _ = seeded.Prepare(35, [CreateDecision(200, firstStartedAt)]);
            }

            using (var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                recordingCommitter,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => updating.Prepare(36, [CreateDecision(201, secondStartedAt)]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception.Outcome);
            }

            Assert.Single(recordingCommitter.ExpectedImages);
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(path));

            using var reopened = CreateJournal(path, capacity: 2);
            var blocked = reopened.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 200 ? firstStartedAt : secondStartedAt)));
            Assert.True(blocked.SetEquals(
            [
                new HostManagerComputeProcessIdentity(200, 2_000_001),
                new HostManagerComputeProcessIdentity(201, 2_000_002)
            ]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StableCanonicalSurvivesContentRootVersionSwitch()
    {
        var root = CreateTemporaryRoot();
        var installRoot = Path.Combine(root, "Installed");
        var firstContentRoot = Path.Combine(root, "Packages", "1.0.0", "Resource Manager-APP");
        var secondContentRoot = Path.Combine(root, "Packages", "2.0.0", "Resource Manager-APP");
        Directory.CreateDirectory(firstContentRoot);
        Directory.CreateDirectory(secondContentRoot);
        var firstPaths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
            firstContentRoot,
            installRoot);
        var secondPaths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
            secondContentRoot,
            installRoot);
        var startedAt = DateTimeOffset.FromFileTime(2_200_001);
        try
        {
            using (var firstVersion = CreateJournal(firstPaths, capacity: 1))
            {
                _ = firstVersion.Prepare(38, [CreateDecision(220, startedAt)]);
            }

            Directory.Delete(firstContentRoot, recursive: true);

            using var secondVersion = CreateJournal(secondPaths, capacity: 1);
            var blocked = secondVersion.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(220, startedAt)));

            Assert.Contains(new HostManagerComputeProcessIdentity(220, 2_200_001), blocked);
            Assert.Equal(firstPaths.CanonicalPath, secondPaths.CanonicalPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreSwitchBridgeMigratesVersionLocalAuthorityBeforeContentRootChanges()
    {
        var root = CreateTemporaryRoot();
        var installRoot = Path.Combine(root, "Installed");
        var firstPackageRoot = Path.Combine(root, "Packages", "1.0.0");
        var firstContentRoot = Path.Combine(firstPackageRoot, "Resource Manager-APP");
        var secondContentRoot = Path.Combine(root, "Packages", "2.0.0", "Resource Manager-APP");
        Directory.CreateDirectory(firstContentRoot);
        Directory.CreateDirectory(secondContentRoot);
        var firstPaths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
            firstContentRoot,
            installRoot);
        var secondPaths = HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(
            secondContentRoot,
            installRoot);
        var oldVersionAuthority = firstPaths.LegacySources.Single(source =>
            source.UsesDurableRootManifest
            && source.Path.StartsWith(firstPackageRoot, StringComparison.OrdinalIgnoreCase)).Path;
        var startedAt = DateTimeOffset.FromFileTime(2_250_001);
        try
        {
            using (var oldVersion = CreateJournal(oldVersionAuthority, capacity: 1))
            {
                _ = oldVersion.Prepare(119, [CreateDecision(225, startedAt)]);
            }

            using (var bridge = CreateJournal(firstPaths, capacity: 1))
            {
                var blocked = bridge.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(225, startedAt)));
                Assert.Contains(new HostManagerComputeProcessIdentity(225, 2_250_001), blocked);
            }
            Assert.True(File.Exists(firstPaths.CanonicalPath));
            Assert.False(File.Exists(oldVersionAuthority));

            Directory.Delete(firstPackageRoot, recursive: true);
            using var nextVersion = CreateJournal(secondPaths, capacity: 1);
            var nextBlocked = nextVersion.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(225, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(225, 2_250_001), nextBlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializedReplaceBeginFailureIsNotCommittedAndSameInstanceRecovers(
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(2_300_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(2_300_002);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        try
        {
            var priorCanonicalImage = SeedAndNormalize(
                path,
                capacity: 2,
                generation: 39,
                processId: 230,
                startedAt: firstStartedAt);
            manifestCommitter.FailAfterSuccessfulCommits(
                successfulCommitsBeforeFailure: 0,
                failAfterManifestReplace,
                new IOException("Injected initialized begin failure."));

            using var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                manifestCommitter);
            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                () => updating.Prepare(40, [CreateDecision(231, secondStartedAt)]));

            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                exception.Outcome);
            Assert.Equal(priorCanonicalImage, File.ReadAllBytes(path));
            var blocked = updating.ReconcileAndCaptureBlocked(processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(
                        processId,
                        processId == 230 ? firstStartedAt : secondStartedAt)));
            Assert.Single(blocked);
            Assert.Contains(new HostManagerComputeProcessIdentity(230, 2_300_001), blocked);
            Assert.DoesNotContain(new HostManagerComputeProcessIdentity(231, 2_300_002), blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializedReplaceReadbackFailureIsAmbiguousAndSameInstanceRollsForward()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(2_400_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(2_400_002);
        var committer = new RecordingCanonicalCommitter(
            new OneShotReadbackFailureCanonicalCommitter());
        try
        {
            var priorCanonicalImage = SeedAndNormalize(
                path,
                capacity: 2,
                generation: 41,
                processId: 240,
                startedAt: firstStartedAt);
            using var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                committer,
                WindowsHostManagerDurableRootManifestCommitter.Instance);

            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                () => updating.Prepare(42, [CreateDecision(241, secondStartedAt)]));

            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                exception.Outcome);
            Assert.Single(committer.ExpectedImages);
            Assert.NotEqual(priorCanonicalImage, committer.ExpectedImages[0]);
            Assert.Equal(committer.ExpectedImages[0], File.ReadAllBytes(path));
            AssertTwoBlocked(
                updating,
                240,
                firstStartedAt,
                241,
                secondStartedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializedReplaceFinalizeFailureIsAmbiguousAndSameInstanceRollsForward(
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(2_500_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(2_500_002);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        var recordingCommitter = new RecordingCanonicalCommitter(
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance);
        try
        {
            var priorCanonicalImage = SeedAndNormalize(
                path,
                capacity: 2,
                generation: 43,
                processId: 250,
                startedAt: firstStartedAt);
            manifestCommitter.FailAfterSuccessfulCommits(
                successfulCommitsBeforeFailure: 1,
                failAfterManifestReplace,
                new IOException("Injected initialized finalize failure."));
            using var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                recordingCommitter,
                manifestCommitter);

            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                () => updating.Prepare(44, [CreateDecision(251, secondStartedAt)]));

            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                exception.Outcome);
            Assert.Single(recordingCommitter.ExpectedImages);
            Assert.NotEqual(priorCanonicalImage, recordingCommitter.ExpectedImages[0]);
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(path));
            AssertTwoBlocked(
                updating,
                250,
                firstStartedAt,
                251,
                secondStartedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NonzeroPriorPendingWithMissingCanonicalFailsClosed()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var firstStartedAt = DateTimeOffset.FromFileTime(2_600_001);
        var secondStartedAt = DateTimeOffset.FromFileTime(2_600_002);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        try
        {
            _ = SeedAndNormalize(
                path,
                capacity: 2,
                generation: 45,
                processId: 260,
                startedAt: firstStartedAt);
            manifestCommitter.FailAfterSuccessfulCommits(
                successfulCommitsBeforeFailure: 0,
                failAfterCommit: true,
                new IOException("Injected failure after pending manifest commit."));
            using (var updating = CreateJournal(
                path,
                legacyPath: null,
                capacity: 2,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                manifestCommitter))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => updating.Prepare(46, [CreateDecision(261, secondStartedAt)]));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
            }

            SeedLegacySingle(
                legacyPath,
                generation: 115,
                processId: 262,
                startedAt: DateTimeOffset.FromFileTime(2_620_001));
            var legacyImage = File.ReadAllBytes(legacyPath);
            File.Delete(path);
            using var reopened = CreateJournal(path, legacyPath, capacity: 2);
            Assert.Throws<IOException>(() => reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(260, firstStartedAt))));
            Assert.False(File.Exists(path));
            Assert.Equal(legacyImage, File.ReadAllBytes(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrationNotCommittedPreservesLegacyAndCanRetry()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_700_001);
        try
        {
            SeedLegacySingle(
                legacyPath,
                generation: 47,
                processId: 270,
                startedAt: startedAt);
            var legacyCanonicalImage = File.ReadAllBytes(legacyPath);
            using (var migrating = CreateJournal(
                path,
                legacyPath,
                capacity: 1,
                RejectingCanonicalCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => migrating.ReconcileAndCaptureBlocked(
                        _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                            new(270, startedAt))));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
            }

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(legacyCanonicalImage, File.ReadAllBytes(legacyPath));
            using var retry = CreateJournal(path, legacyPath, capacity: 1);
            var blocked = retry.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(270, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(270, 2_700_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrationAmbiguousPreservesLegacyAndRestartConvergesCanonical()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_800_001);
        var recordingCommitter = new RecordingCanonicalCommitter(
            CommitThenThrowCanonicalCommitter.Instance);
        try
        {
            SeedLegacySingle(
                legacyPath,
                generation: 48,
                processId: 280,
                startedAt: startedAt);
            using (var migrating = CreateJournal(
                path,
                legacyPath,
                capacity: 1,
                recordingCommitter,
                WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => migrating.ReconcileAndCaptureBlocked(
                        _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                            new(280, startedAt))));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception.Outcome);
            }

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(legacyPath));
            Assert.Single(recordingCommitter.ExpectedImages);
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(path));
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(legacyPath));
            using var reopened = CreateJournal(path, legacyPath, capacity: 1);
            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(280, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(280, 2_800_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void MigrationPrecanonicalManifestFailurePreservesExactLegacy(
        int successfulManifestCommits,
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_850_001);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        try
        {
            SeedLegacySingle(
                legacyPath,
                generation: 116,
                processId: 285,
                startedAt: startedAt);
            var legacyImage = File.ReadAllBytes(legacyPath);
            manifestCommitter.FailAfterSuccessfulCommits(
                successfulManifestCommits,
                failAfterManifestReplace,
                new IOException("Injected migration manifest failure before canonical."));
            using (var migrating = CreateJournal(
                path,
                legacyPath,
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                manifestCommitter))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => migrating.ReconcileAndCaptureBlocked(
                        _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                            new(285, startedAt))));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception.Outcome);
            }

            Assert.False(File.Exists(path));
            Assert.Equal(legacyImage, File.ReadAllBytes(legacyPath));
            using var retry = CreateJournal(path, legacyPath, capacity: 1);
            var blocked = retry.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(285, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(285, 2_850_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigrationFinalizeFailurePreservesExactSourceAndRollsForward(
        bool failAfterManifestReplace)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_860_001);
        var manifestCommitter = new FaultInjectingManifestCommitter();
        var recordingCommitter = new RecordingCanonicalCommitter(
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance);
        try
        {
            SeedLegacySingle(
                legacyPath,
                generation: 117,
                processId: 286,
                startedAt: startedAt);
            var legacyImage = File.ReadAllBytes(legacyPath);
            manifestCommitter.FailAfterSuccessfulCommits(
                successfulCommitsBeforeFailure: 2,
                failAfterManifestReplace,
                new IOException("Injected migration finalize failure."));
            using (var migrating = CreateJournal(
                path,
                [legacyPath],
                capacity: 1,
                recordingCommitter,
                manifestCommitter,
                WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance))
            {
                var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                    () => migrating.ReconcileAndCaptureBlocked(
                        _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                            new(286, startedAt))));
                Assert.Equal(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception.Outcome);
            }

            Assert.Single(recordingCommitter.ExpectedImages);
            Assert.Equal(legacyImage, recordingCommitter.ExpectedImages[0]);
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(path));
            Assert.Equal(legacyImage, File.ReadAllBytes(legacyPath));
            using var reopened = CreateJournal(path, legacyPath, capacity: 1);
            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(286, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(286, 2_860_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GenerationlessUserDataMigrationReadbackFailurePreservesExactSource()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "Stable", "generation-00000001", "attempts.json");
        var legacyPath = Path.Combine(root, "Stable", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_870_001);
        var legacySources = new HostManagerMemoryCleanupAttemptLegacySource[]
        {
            new(legacyPath, UsesDurableRootManifest: true)
        };
        var recordingCommitter = new RecordingCanonicalCommitter(
            new OneShotReadbackFailureCanonicalCommitter());
        try
        {
            using (var legacy = CreateJournal(legacyPath, capacity: 1))
            {
                _ = legacy.Prepare(118, [CreateDecision(287, startedAt)]);
            }
            var legacyImage = File.ReadAllBytes(legacyPath);
            using var migrating = CreateJournal(
                path,
                legacySources,
                capacity: 1,
                recordingCommitter,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance);

            var exception = Assert.Throws<HostManagerMemoryCleanupAttemptCommitException>(
                () => migrating.ReconcileAndCaptureBlocked(
                    _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new(287, startedAt))));
            Assert.Equal(
                HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                exception.Outcome);
            Assert.Single(recordingCommitter.ExpectedImages);
            Assert.Equal(legacyImage, recordingCommitter.ExpectedImages[0]);
            Assert.Equal(recordingCommitter.ExpectedImages[0], File.ReadAllBytes(path));
            Assert.Equal(legacyImage, File.ReadAllBytes(legacyPath));

            var blocked = migrating.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(287, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(287, 2_870_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyRetirementFailureBlocksAdmissionAndSameInstanceRetries()
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        var startedAt = DateTimeOffset.FromFileTime(2_900_001);
        var retirer = new FailOnceLegacyFileRetirer();
        try
        {
            SeedLegacySingle(
                legacyPath,
                generation: 49,
                processId: 290,
                startedAt: startedAt);
            var legacyCanonicalImage = File.ReadAllBytes(legacyPath);
            using var migrating = CreateJournal(
                path,
                [legacyPath],
                capacity: 1,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                retirer);

            Assert.Throws<IOException>(() => migrating.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(290, startedAt))));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(legacyCanonicalImage, File.ReadAllBytes(path));
            Assert.Equal(legacyCanonicalImage, File.ReadAllBytes(legacyPath));

            var blocked = migrating.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(290, startedAt)));
            Assert.Contains(new HostManagerComputeProcessIdentity(290, 2_900_001), blocked);
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("oversize")]
    [InlineData("capacity")]
    [InlineData("schema")]
    [InlineData("checksum")]
    [InlineData("duplicate")]
    [InlineData("phase")]
    public void InvalidLegacyCanonicalFailsClosedWithoutCreatingNewAuthority(string failureKind)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "UserData", "attempts.json");
        var legacyPath = Path.Combine(root, "Config", "attempts.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            if (failureKind == "corrupt")
            {
                File.WriteAllText(legacyPath, "{");
            }
            else if (failureKind == "oversize")
            {
                using var stream = new FileStream(legacyPath, FileMode.CreateNew, FileAccess.Write);
                stream.SetLength((4L * 1024 * 1024) + 1);
            }
            else if (failureKind == "capacity")
            {
                SeedLegacyUnknownAndApplying(
                    legacyPath,
                    firstProcessId: 300,
                    DateTimeOffset.FromFileTime(3_000_001),
                    secondProcessId: 301,
                    DateTimeOffset.FromFileTime(3_000_002));
            }
            else
            {
                SeedInvalidLegacyDocument(legacyPath, failureKind);
            }

            var legacyImage = File.ReadAllBytes(legacyPath);
            var capacity = failureKind == "capacity" ? 1 : 2;

            using var migrating = CreateJournal(path, legacyPath, capacity);
            var exception = Record.Exception(() => migrating.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException("Invalid legacy state must fail first.")));
            Assert.NotNull(exception);
            if (failureKind == "corrupt")
            {
                Assert.IsType<System.Text.Json.JsonException>(exception);
            }
            else
            {
                Assert.IsType<InvalidDataException>(exception);
            }
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(legacyImage, File.ReadAllBytes(legacyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("canonical-corrupt")]
    [InlineData("manifest-corrupt")]
    [InlineData("wrong-store-kind")]
    [InlineData("pending-mismatch")]
    public void InvalidDurableUserDataAuthorityBlocksValidConfigFallback(
        string failureKind)
    {
        var root = CreateTemporaryRoot();
        var path = Path.Combine(root, "generation", "attempts.json");
        var userDataPath = Path.Combine(root, "UserData", "attempts.json");
        var configPath = Path.Combine(root, "Config", "attempts.json");
        var userDataManifestPath = userDataPath + ".root.manifest";
        try
        {
            var userDataStartedAt = DateTimeOffset.FromFileTime(3_100_001);
            using (var userData = CreateJournal(userDataPath, capacity: 1))
            {
                _ = userData.Prepare(
                    310,
                    [CreateDecision(310, userDataStartedAt)]);
            }
            SeedLegacySingle(
                configPath,
                generation: 311,
                processId: 311,
                DateTimeOffset.FromFileTime(3_100_002));

            if (failureKind == "canonical-corrupt")
            {
                File.WriteAllText(userDataPath, "{");
            }
            else if (failureKind == "manifest-corrupt")
            {
                File.WriteAllText(userDataManifestPath, "invalid manifest");
            }
            else
            {
                File.Delete(userDataManifestPath);
                var manifest = new HostManagerDurableRootManifest(
                    userDataPath,
                    failureKind == "wrong-store-kind"
                        ? "not-the-attempt-journal"
                        : AttemptJournalStoreKind);
                if (failureKind == "wrong-store-kind")
                {
                    manifest.ValidateOrAdoptCanonical(File.ReadAllBytes(userDataPath));
                }
                else
                {
                    manifest.EnsureInitializing();
                    _ = manifest.BeginCanonicalTransition("different canonical"u8);
                }
            }

            var userDataImage = File.ReadAllBytes(userDataPath);
            var userDataManifestImage = File.ReadAllBytes(userDataManifestPath);
            var configImage = File.ReadAllBytes(configPath);
            var legacySources = new HostManagerMemoryCleanupAttemptLegacySource[]
            {
                new(userDataPath, UsesDurableRootManifest: true),
                new(configPath, UsesDurableRootManifest: false)
            };
            using var migrating = CreateJournal(
                path,
                legacySources,
                capacity: 2,
                WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
                WindowsHostManagerDurableRootManifestCommitter.Instance,
                WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance);

            var exception = Record.Exception(() => migrating.ReconcileAndCaptureBlocked(
                _ => throw new InvalidOperationException(
                    "A lower-tier Config authority must not be read.")));
            Assert.NotNull(exception);
            if (failureKind == "canonical-corrupt")
            {
                Assert.IsType<JsonException>(exception);
            }
            else
            {
                Assert.IsType<InvalidDataException>(exception);
            }
            Assert.False(File.Exists(path));
            Assert.Equal(userDataImage, File.ReadAllBytes(userDataPath));
            Assert.Equal(userDataManifestImage, File.ReadAllBytes(userDataManifestPath));
            Assert.Equal(configImage, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        string path,
        int capacity)
        => new(path, () => capacity, TimeProvider.System);

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        string path,
        string? legacyPath,
        int capacity)
        => CreateJournal(
            path,
            legacyPath,
            capacity,
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance);

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        string path,
        string? legacyPath,
        int capacity,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter)
        => new(
            path,
            legacyPath,
            () => capacity,
            TimeProvider.System,
            committer,
            manifestCommitter);

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        HostManagerMemoryCleanupAttemptJournalPaths paths,
        int capacity)
        => new(paths, () => capacity, TimeProvider.System);

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        string path,
        IReadOnlyList<string> legacyPaths,
        int capacity,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter,
        IHostManagerMemoryCleanupLegacyFileRetirer legacyRetirer)
        => new(
            path,
            legacyPaths,
            () => capacity,
            TimeProvider.System,
            committer,
            manifestCommitter,
            legacyRetirer);

    private static HostManagerMemoryCleanupAttemptJournal CreateJournal(
        string path,
        IReadOnlyList<HostManagerMemoryCleanupAttemptLegacySource> legacySources,
        int capacity,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter,
        IHostManagerMemoryCleanupLegacyFileRetirer legacyRetirer)
        => new(
            path,
            legacySources,
            () => capacity,
            TimeProvider.System,
            committer,
            manifestCommitter,
            legacyRetirer);

    private static byte[] SeedAndNormalize(
        string path,
        int capacity,
        ulong generation,
        int processId,
        DateTimeOffset startedAt)
    {
        using (var seeded = CreateJournal(path, capacity))
        {
            _ = seeded.Prepare(generation, [CreateDecision(processId, startedAt)]);
        }

        using (var normalized = CreateJournal(path, capacity))
        {
            var blocked = normalized.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(processId, startedAt)));
            Assert.Single(blocked);
        }

        return File.ReadAllBytes(path);
    }

    private static void AssertTwoBlocked(
        HostManagerMemoryCleanupAttemptJournal journal,
        int firstProcessId,
        DateTimeOffset firstStartedAt,
        int secondProcessId,
        DateTimeOffset secondStartedAt)
    {
        var blocked = journal.ReconcileAndCaptureBlocked(processId =>
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(
                    processId,
                    processId == firstProcessId ? firstStartedAt : secondStartedAt)));
        Assert.Equal(2, blocked.Count);
        Assert.Contains(
            new HostManagerComputeProcessIdentity(
                firstProcessId,
                checked((ulong)firstStartedAt.ToFileTime())),
            blocked);
        Assert.Contains(
            new HostManagerComputeProcessIdentity(
                secondProcessId,
                checked((ulong)secondStartedAt.ToFileTime())),
            blocked);
    }

    private static void SeedLegacySingle(
        string legacyPath,
        ulong generation,
        int processId,
        DateTimeOffset startedAt)
    {
        using (var legacy = CreateJournal(legacyPath, capacity: 1))
        {
            _ = legacy.Prepare(generation, [CreateDecision(processId, startedAt)]);
        }

        File.Delete(legacyPath + ".root.manifest");
    }

    private static void SeedLegacyUnknownAndApplying(
        string legacyPath,
        int firstProcessId,
        DateTimeOffset firstStartedAt,
        int secondProcessId,
        DateTimeOffset secondStartedAt)
    {
        using (var initial = CreateJournal(legacyPath, capacity: 2))
        {
            _ = initial.Prepare(
                100,
                [CreateDecision(firstProcessId, firstStartedAt)]);
        }

        using (var continued = CreateJournal(legacyPath, capacity: 2))
        {
            var blocked = continued.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(firstProcessId, firstStartedAt)));
            Assert.Single(blocked);
            _ = continued.Prepare(
                101,
                [CreateDecision(secondProcessId, secondStartedAt)]);
        }

        File.Delete(legacyPath + ".root.manifest");
    }

    private static void SeedInvalidLegacyDocument(
        string legacyPath,
        string failureKind)
    {
        SeedLegacyUnknownAndApplying(
            legacyPath,
            firstProcessId: 300,
            DateTimeOffset.FromFileTime(3_000_001),
            secondProcessId: 301,
            DateTimeOffset.FromFileTime(3_000_002));
        var document = JsonSerializer.Deserialize<HostManagerMemoryCleanupAttemptDocument>(
            File.ReadAllBytes(legacyPath),
            JournalJsonOptions)
            ?? throw new InvalidDataException("The seeded legacy document was empty.");
        document = failureKind switch
        {
            "schema" => document with { SchemaVersion = document.SchemaVersion + 1 },
            "checksum" => document with { ChecksumSha256 = new string('0', 64) },
            "duplicate" => WithRecomputedChecksum(
                document,
                [document.Entries[0], document.Entries[1] with
                {
                    ProcessId = document.Entries[0].ProcessId,
                    ProcessStartKey = document.Entries[0].ProcessStartKey
                }]),
            "phase" => WithRecomputedChecksum(
                document,
                [document.Entries[0] with
                {
                    Phase = (HostManagerMemoryCleanupAttemptPhase)byte.MaxValue
                }, document.Entries[1]]),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        File.WriteAllBytes(
            legacyPath,
            JsonSerializer.SerializeToUtf8Bytes(document, JournalJsonOptions));
    }

    private static HostManagerMemoryCleanupAttemptDocument WithRecomputedChecksum(
        HostManagerMemoryCleanupAttemptDocument document,
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
        => document with
        {
            Entries = entries,
            ChecksumSha256 = ComputeFixtureChecksum(entries)
        };

    private static string ComputeFixtureChecksum(
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: true))
        {
            writer.Write(1U);
            writer.Write(entries.Count);
            foreach (var entry in entries
                .OrderBy(static item => item.ProcessId)
                .ThenBy(static item => item.ProcessStartKey))
            {
                writer.Write(entry.BatchId.ToByteArray());
                writer.Write(entry.ProcessId);
                writer.Write(entry.ProcessStartKey);
                writer.Write(entry.TargetKey);
                writer.Write(entry.AttemptGeneration);
                writer.Write((byte)entry.Phase);
                writer.Write(entry.PreparedAtUtcTicks);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void AssertBlockedAcrossReopens(
        string path,
        int processId,
        DateTimeOffset startedAt,
        int reopenCount)
    {
        for (var index = 0; index < reopenCount; index++)
        {
            using var reopened = CreateJournal(path, capacity: 1);
            var blocked = reopened.ReconcileAndCaptureBlocked(
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new(processId, startedAt)));
            Assert.Contains(
                new HostManagerComputeProcessIdentity(
                    processId,
                    checked((ulong)startedAt.ToFileTime())),
                blocked);
        }
    }

    private sealed class FaultInjectingManifestCommitter
        : IHostManagerDurableRootManifestCommitter
    {
        private int successfulCommitsBeforeFailure = -1;
        private bool failAfterCommit;
        private Exception? failure;

        internal void FailAfterSuccessfulCommits(
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

    private sealed class RejectingCanonicalCommitter
        : IHostManagerMemoryCleanupAttemptFileCommitter
    {
        internal static RejectingCanonicalCommitter Instance { get; } = new();

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
            => throw new HostManagerMemoryCleanupAttemptCommitException(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                new IOException("Injected failure before canonical rename."));
    }

    private sealed class CommitThenThrowCanonicalCommitter
        : IHostManagerMemoryCleanupAttemptFileCommitter
    {
        internal static CommitThenThrowCanonicalCommitter Instance { get; } = new();

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
        {
            WindowsNativeAtomicFileCommitter.CommitReplace(
                temporaryPath,
                canonicalPath);
            throw new IOException("Injected unknown failure after canonical rename.");
        }
    }

    private sealed class LockingRejectingCanonicalCommitter
        : IHostManagerMemoryCleanupAttemptFileCommitter,
          IDisposable
    {
        private FileStream? heldTemporaryFile;

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
        {
            heldTemporaryFile = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            throw new HostManagerMemoryCleanupAttemptCommitException(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                new IOException("Injected failure while the temporary file remains locked."));
        }

        public void Dispose()
        {
            heldTemporaryFile?.Dispose();
            heldTemporaryFile = null;
        }
    }

    private sealed class MismatchedReadbackCanonicalCommitter
        : IHostManagerMemoryCleanupAttemptFileCommitter
    {
        internal static MismatchedReadbackCanonicalCommitter Instance { get; } = new();

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
        {
            var mismatchedImage = expectedImage.ToArray();
            mismatchedImage[^1] ^= 0x01;
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance.Commit(
                temporaryPath,
                canonicalPath,
                mismatchedImage);
        }
    }

    private sealed class OneShotReadbackFailureCanonicalCommitter
        : IHostManagerMemoryCleanupAttemptFileCommitter
    {
        private bool failed;

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
        {
            if (!failed)
            {
                failed = true;
                MismatchedReadbackCanonicalCommitter.Instance.Commit(
                    temporaryPath,
                    canonicalPath,
                    expectedImage);
                return;
            }

            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance.Commit(
                temporaryPath,
                canonicalPath,
                expectedImage);
        }
    }

    private sealed class RecordingCanonicalCommitter(
        IHostManagerMemoryCleanupAttemptFileCommitter inner)
        : IHostManagerMemoryCleanupAttemptFileCommitter
    {
        internal List<byte[]> ExpectedImages { get; } = [];

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage)
        {
            ExpectedImages.Add(expectedImage.ToArray());
            inner.Commit(temporaryPath, canonicalPath, expectedImage);
        }
    }

    private sealed class FailOnceLegacyFileRetirer
        : IHostManagerMemoryCleanupLegacyFileRetirer
    {
        private bool failed;

        public WindowsNativeFileDeleteResult Retire(string path)
        {
            if (!failed)
            {
                failed = true;
                throw new IOException("Injected legacy canonical retirement failure.");
            }

            return WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance.Retire(path);
        }
    }

    private sealed class DeleteFirstThenFailOnceLegacyFileRetirer
        : IHostManagerMemoryCleanupLegacyFileRetirer
    {
        private int invocationCount;
        private bool failed;

        public WindowsNativeFileDeleteResult Retire(string path)
        {
            invocationCount++;
            if (!failed && invocationCount == 2)
            {
                failed = true;
                throw new IOException("Injected failure after one legacy canonical retirement.");
            }

            return WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance.Retire(path);
        }
    }

    private static AutomaticMemoryCleanupDecision CreateDecision(
        int processId,
        DateTimeOffset startedAt)
        => new(
            SourceInputIndex: 0,
            Candidate: new(
                TargetId: $"process:{processId}",
                DisplayName: $"Process {processId}",
                ProcessId: processId,
                ProcessStartedAt: startedAt,
                RuntimeState: "background",
                BaseScore: 1,
                CpuScore: 1,
                MemoryUsedPercent: 1,
                CanApply: true),
            AutomaticMemoryCleanupMode.Normal,
            new(StateSlot: checked((uint)processId), StateGeneration: 1));

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-memory-cleanup-attempts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
