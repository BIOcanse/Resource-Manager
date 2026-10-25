using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRollbackStateStoreTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task IdenticalCommittedDocumentDoesNotReadEncodeOrWriteAtCheckpointOrDispose()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var commits = 0;
        try
        {
            var store = new JsonHostManagerRollbackStateStore(path, time, TimeSpan.FromMinutes(1),
                beforeCommit: () => commits++);
            try
            {
                var reserved = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
                var document = reserved with { AppliedPlacements = [Placement("cpu", "cpu-record")] };
                await store.SaveAsync(document, CancellationToken.None);
                var before = await File.ReadAllBytesAsync(path);
                var committed = commits;
                time.Advance(TimeSpan.FromMinutes(2));
                // Any canonical read or write would fail while this probe owns the file.
                using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await store.SaveAsync(document with { AppliedPlacements = document.AppliedPlacements.ToArray() }, CancellationToken.None);
                    Assert.Equal(document.Coordinator, (await store.LoadAsync(CancellationToken.None)).Coordinator);
                    store.Dispose();
                }
                Assert.Equal(committed, commits);
                Assert.Equal(before, await File.ReadAllBytesAsync(path));
            }
            finally { store.Dispose(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SkippingCommittedStateDoesNotDiscardAPendingTransientWrite()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            using (var store = new JsonHostManagerRollbackStateStore(path, time, TimeSpan.FromMinutes(1)))
            {
                var reserved = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
                await store.SaveAsync(reserved, CancellationToken.None);
                var transient = reserved with { Coordinator = new(1, 1919, 0, 0) };
                await store.SaveAsync(transient, CancellationToken.None);
                await store.SaveAsync(transient, CancellationToken.None);
            }
            using var reopened = new JsonHostManagerRollbackStateStore(path, time, TimeSpan.FromMinutes(1));
            Assert.Equal(new HostManagerCoordinatorFacts(1, 1919, 0, 0), (await reopened.LoadAsync(CancellationToken.None)).Coordinator);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CallerMetadataMutationCannotTurnANeededWriteIntoAnUnchangedSave()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            using (var store = new JsonHostManagerRollbackStateStore(path, time, TimeSpan.FromMinutes(1)))
            {
                var placement = Placement("cpu", "record");
                await store.SaveAsync(HostManagerRollbackStateDocument.Empty with
                {
                    AppliedPlacements = [placement with
                    {
                        Records = [new HostManagerAppliedRecord(placement.Records[0].Kind, "record",
                            new Dictionary<string, string> { ["value"] = "before" })]
                    }]
                }, CancellationToken.None);
                var loaded = await store.LoadAsync(CancellationToken.None);
                var metadata = Assert.IsType<Dictionary<string, string>>(loaded.AppliedPlacements[0].Records[0].Metadata);
                metadata["value"] = "after";
                await store.SaveAsync(loaded, CancellationToken.None);
            }
            using var reopened = new JsonHostManagerRollbackStateStore(path, time, TimeSpan.FromMinutes(1));
            Assert.Equal("after", (await reopened.LoadAsync(CancellationToken.None))
                .AppliedPlacements[0].Records[0].Metadata!["value"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SaveAsync_KeepsTransientCycleUpdatesInMemoryUntilCheckpoint()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                time,
                TimeSpan.FromMinutes(1));
            var initial = HostManagerRollbackStateDocument.Empty with
            {
                LastRunAt = time.GetUtcNow(),
                Coordinator = new(1, 746, 0, 0)
            };
            await store.SaveAsync(initial, CancellationToken.None);
            Assert.False(File.Exists(path));

            time.Advance(TimeSpan.FromMinutes(1));
            await store.SaveAsync(
                initial with
                {
                    LastRunAt = time.GetUtcNow(),
                    Coordinator = new(1, 1064, 0, 0)
                },
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(new HostManagerCoordinatorFacts(1, 1064, 0, 0), ReadCoordinator(await File.ReadAllTextAsync(path)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_PersistsPlacementRecoveryChangesImmediately()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var now = new DateTimeOffset(2026, 7, 22, 20, 0, 0, TimeSpan.Zero);
        try
        {
            using (var store = new JsonHostManagerRollbackStateStore(
                       path,
                       new ManualTimeProvider(now),
                       TimeSpan.FromMinutes(1)))
            {
                var reserved = await store.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
                var state = reserved with
                {
                    AppliedPlacements = [Placement("cpu", "cpu-record")],
                    Coordinator = new(1, 953, 0, 0)
                };
                await store.SaveAsync(state, CancellationToken.None);
            }

            Assert.True(File.Exists(path));
            using var reopened = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(now),
                TimeSpan.FromMinutes(1));
            var restored = await reopened.LoadAsync(CancellationToken.None);
            Assert.Single(restored.AppliedPlacements);
            Assert.Equal<ulong>(1, restored.NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CpuAndGpuPlacementsWithSameTargetSurviveSaveReload()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var now = new DateTimeOffset(2026, 7, 22, 20, 0, 0, TimeSpan.Zero);
        try
        {
            using (var writer = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(now),
                TimeSpan.FromMinutes(1)))
            {
                await writer.SaveAsync(
                    HostManagerRollbackStateDocument.Empty with
                    {
                        AppliedPlacements = [Placement("cpu", "cpu-record"), Placement("gpu", "gpu-record")]
                    },
                    CancellationToken.None);
            }

            using var reader = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(now),
                TimeSpan.FromMinutes(1));
            var loaded = await reader.LoadAsync(CancellationToken.None);

            Assert.Equal(2, loaded.AppliedPlacements.Count);
            Assert.Equal(["cpu", "gpu"], loaded.AppliedPlacements.Select(static item => item.ResourceKind));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicatePlacementOrRecordIdentityIsRejected()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var now = new DateTimeOffset(2026, 7, 22, 20, 5, 0, TimeSpan.Zero);
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(now),
                TimeSpan.FromMinutes(1));
            var placement = Placement("cpu", "record");

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
                HostManagerRollbackStateDocument.Empty with { AppliedPlacements = [placement, placement] },
                CancellationToken.None));

            var duplicateRecordPlacement = placement with
            {
                Records = [placement.Records[0], placement.Records[0]]
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
                HostManagerRollbackStateDocument.Empty with { AppliedPlacements = [duplicateRecordPlacement] },
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not-json")]
    public async Task LoadAsync_RejectsUnrecognizedJsonWithoutMigration(string json)
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1));
            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(CancellationToken.None));
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.backup"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_UpgradesEmptyVersion1AuthorityOnceAndPreservesPlacements()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        await File.WriteAllTextAsync(path, CreateLegacyVersion1Json());
        try
        {
            using (var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1)))
            {
                var migrated = await store.LoadAsync(CancellationToken.None);
                Assert.Equal(HostManagerRollbackStateDocument.CurrentVersion, migrated.Version);
                Assert.Single(migrated.AppliedPlacements);
                Assert.Equal<ulong>(7, migrated.NativeHostSessionIncarnation);
            }

            var firstWrite = await File.ReadAllBytesAsync(path);
            using (var json = JsonDocument.Parse(firstWrite))
            {
                var state = json.RootElement.GetProperty("state");
                Assert.Equal(
                    HostManagerRollbackStateEnvelopeCodec.Magic,
                    json.RootElement.GetProperty("magic").GetString());
                Assert.Equal(HostManagerRollbackStateEnvelopeCodec.SchemaVersion,
                    json.RootElement.GetProperty("schemaVersion").GetUInt32());
                Assert.Equal<ulong>(1, json.RootElement.GetProperty("revision").GetUInt64());
                Assert.True(json.RootElement.GetProperty("payloadLength").GetInt32() > 0);
                Assert.Equal(
                    HostManagerRollbackStateDocument.CurrentVersion,
                    state.GetProperty("version").GetInt32());
                Assert.False(state.TryGetProperty("pendingChanges", out _));
                Assert.False(state.TryGetProperty("appliedTargets", out _));
                Assert.False(state.TryGetProperty("nativeActionAttempts", out _));
                Assert.Equal(64, json.RootElement.GetProperty("checksumSha256").GetString()!.Length);
            }

            using (var reopened = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1)))
            {
                var reloaded = await reopened.LoadAsync(CancellationToken.None);
                Assert.Equal(HostManagerRollbackStateDocument.CurrentVersion, reloaded.Version);
            }

            Assert.Equal(firstWrite, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("pendingChanges")]
    [InlineData("appliedTargets")]
    [InlineData("nativeActionAttempts")]
    public async Task LoadAsync_PreservesNonEmptyVersion1AuthorityAndStartsEmpty(string authorityProperty)
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var original = CreateLegacyVersion1Json(authorityProperty);
        await File.WriteAllTextAsync(path, original);
        var originalBytes = await File.ReadAllBytesAsync(path);
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1));
            Assert.Empty((await store.LoadAsync(CancellationToken.None)).AppliedPlacements);

            var backup = Assert.Single(Directory.GetFiles(root, "smart-optimization-state.json.*.backup"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(99, false)]
    public async Task LoadAsync_PreservesUnknownOldFormatAndStartsEmpty(
        int version,
        bool includeUnknownField)
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var original = CreateLegacyVersion1Json(
            authorityProperty: null,
            version,
            includeUnknownField);
        await File.WriteAllTextAsync(path, original);
        var originalBytes = await File.ReadAllBytesAsync(path);
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1));
            if (version == 1)
            {
                Assert.Empty((await store.LoadAsync(CancellationToken.None)).AppliedPlacements);
                var backup = Assert.Single(Directory.GetFiles(root, "smart-optimization-state.json.*.backup"));
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
            }
            else
            {
                await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(CancellationToken.None));
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
                Assert.Empty(Directory.GetFiles(root, "*.backup"));
            }
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MigrationCommitFailurePreservesVersion1FileAtomically()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        await File.WriteAllTextAsync(path, CreateLegacyVersion1Json());
        var originalBytes = await File.ReadAllBytesAsync(path);
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                new ManualTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(1),
                () => throw new IOException("simulated commit interruption"));
            var exception = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(
                () => store.LoadAsync(CancellationToken.None));
            Assert.Equal(
                HostManagerRollbackStateCommitOutcome.NotCommitted,
                exception.Outcome);

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCheckpointDoesNotPublishOrDisposeCommitTheRejectedCandidate()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "smart-optimization-state.json");
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var failNextCommit = true;
        JsonHostManagerRollbackStateStore? store = null;
        try
        {
            store = new JsonHostManagerRollbackStateStore(
                path,
                time,
                TimeSpan.FromMinutes(1),
                () =>
                {
                    if (failNextCommit)
                    {
                        failNextCommit = false;
                        throw new IOException("simulated checkpoint interruption");
                    }
                });
            var accepted = HostManagerRollbackStateDocument.Empty with
            {
                LastRunAt = time.GetUtcNow(),
                Coordinator = new(1, 825, 0, 0)
            };
            await store.SaveAsync(accepted, CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(1));

            var exception = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => store.SaveAsync(
                accepted with
                {
                    LastRunAt = time.GetUtcNow(),
                    Coordinator = new(1, 838, 0, 0)
                },
                CancellationToken.None));
            Assert.Equal(
                HostManagerRollbackStateCommitOutcome.NotCommitted,
                exception.Outcome);

            Assert.Equal(new HostManagerCoordinatorFacts(1, 825, 0, 0), (await store.LoadAsync(CancellationToken.None)).Coordinator);
            store.Dispose();
            Assert.Equal(new HostManagerCoordinatorFacts(1, 825, 0, 0), ReadCoordinator(await File.ReadAllTextAsync(path)));
        }
        finally
        {
            store?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static HostManagerAppliedPlacementReceipt Placement(string resourceKind, string recordId)
        => new(
            "target",
            "software",
            resourceKind,
            [new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.CpuAffinity, recordId)],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static HostManagerCoordinatorFacts? ReadCoordinator(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("state")
            .GetProperty("coordinator")
            .Deserialize<HostManagerCoordinatorFacts>(WebJson);
    }

    private static string CreateLegacyVersion1Json(
        string? authorityProperty = null,
        int version = 1,
        bool includeUnknownField = false)
    {
        var empty = Array.Empty<object>();
        var authority = new object[] { new { legacy = true } };
        var document = new Dictionary<string, object?>
        {
            ["version"] = version,
            ["pendingChanges"] = authorityProperty == "pendingChanges" ? authority : empty,
            ["appliedTargets"] = authorityProperty == "appliedTargets" ? authority : empty,
            ["lastRunAt"] = null,
            ["lastRestoreAt"] = null,
            ["message"] = "legacy",
            ["appliedPlacements"] = new[] { Placement("cpu", "legacy-placement") },
            ["nativeActionAttempts"] = authorityProperty == "nativeActionAttempts" ? authority : empty,
            ["nativeHostSessionIncarnation"] = 7UL
        };
        if (includeUnknownField)
        {
            document["unknownAuthority"] = empty;
        }

        return JsonSerializer.Serialize(document, WebJson);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resource-manager-host-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
