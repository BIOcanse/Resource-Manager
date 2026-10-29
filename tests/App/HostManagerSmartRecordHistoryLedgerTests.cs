using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartRecordHistoryLedgerTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-smart-history-ledger-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalLedgerWrittenBeforeHistoryFieldsRemainsReadableWithoutReencodingItsAuthority()
    {
        // The exact v3 shape emitted before Sequence/RecordedAt existed. The envelope
        // checksum covers the canonical JSON payload, including its byte length.
        const string state = """
            {"version":3,"lastRunAt":null,"lastRestoreAt":null,"appliedPlacements":[{"targetId":"one","softwareId":"software","resourceKind":"cpu","records":[{"kind":"cpu_affinity","recordId":"original","metadata":null}],"appliedAt":"2026-10-28T12:00:00+00:00","updatedAt":"2026-10-28T12:00:00+00:00","processId":null,"processStartKey":null,"processName":null}],"coordinator":null,"nativeHostSessionIncarnation":0}
            """;
        var payload = System.Text.Encoding.UTF8.GetBytes(state);
        var header = new byte[sizeof(uint) + sizeof(ulong) + sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(sizeof(uint)), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(sizeof(uint) + sizeof(ulong)), payload.Length);
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(header.Concat(payload).ToArray()));
        var envelope = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"magic\":\"RMROLLBACK01\",\"schemaVersion\":1,\"revision\":1,\"payloadLength\":{payload.Length},\"state\":{state},\"checksumSha256\":\"{checksum}\"}}");
        var decoded = HostManagerRollbackStateEnvelopeCodec.Decode(envelope);
        var record = Assert.Single(Assert.Single(decoded.State.AppliedPlacements).Records);
        Assert.Null(record.Sequence);
        Assert.Null(record.RecordedAt);
        Assert.Equal(envelope, decoded.CanonicalImage);
    }

    [Fact]
    public async Task DisabledHistorySettingCommitDoesNotOpenRollbackOrHistoryStores()
    {
        var settings = new SettingsStore(false);
        using var ledger = CreateLedger(CreateHistory(), settings);
        await ApplyCommittedHistorySettingAsync(ledger, settings);
        Assert.False(File.Exists(StatePath + ".owner.lock"));
        Assert.False(File.Exists(CreateDatabase().DatabasePath));
    }

    [Fact]
    public async Task DisabledRetentionDoesNotCreateHistoryRows()
    {
        var history = CreateHistory();
        using var ledger = CreateLedger(history, new SettingsStore(false));
        await ledger.SaveAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        var saved = await ledger.LoadAsync(default);
        Assert.Null(SingleRecord(saved).Sequence);
        Assert.Null(SingleRecord(saved).RecordedAt);
        await ledger.SaveAsync(saved with { AppliedPlacements = [] }, default);
        Assert.Empty((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(0L, await CountRowsAsync());
    }

    [Fact]
    public async Task EnablingRetentionImmediatelyBackfillsExistingLedgerAndDisablingKeepsTrackingIt()
    {
        var history = CreateHistory();
        var settings = new SettingsStore(false);
        using var ledger = CreateLedger(history, settings);
        await ledger.SaveAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        settings.Keep = true;
        await ApplyCommittedHistorySettingAsync(ledger, settings);
        var tracked = await ledger.LoadAsync(default);
        var placement = Assert.Single(tracked.AppliedPlacements);
        var enrolled = Assert.Single(placement.Records);
        Assert.Equal(1L, enrolled.Sequence);
        Assert.Equal(now, enrolled.RecordedAt);

        settings.Keep = false;
        await ApplyCommittedHistorySettingAsync(ledger, settings);
        var mixed = await ledger.SaveAndReadAsync(tracked with
        {
            AppliedPlacements = [placement with
            {
                Records = [enrolled, new(HostManagerAppliedRecordKinds.ProcessPriority, "off-sibling")]
            }]
        }, default);
        var active = Assert.Single(mixed.AppliedPlacements);
        Assert.Equal(1L, active.Records.Single(record => record.RecordId == "original").Sequence);
        Assert.Null(active.Records.Single(record => record.RecordId == "off-sibling").Sequence);
        var wire = Assert.Single(HostManagerSmartStateWireProjection.Project(mixed).AppliedPlacements);
        var enrolledWire = wire.Records.Single(record => record.RecordId == "original");
        Assert.Equal(1L, enrolledWire.Sequence);
        Assert.Equal("active", enrolledWire.State);
        Assert.Equal(now, enrolledWire.RecordedAt);
        var unenrolledWire = wire.Records.Single(record => record.RecordId == "off-sibling");
        Assert.Null(unenrolledWire.Sequence);
        Assert.Null(unenrolledWire.State);
        Assert.Null(unenrolledWire.RecordedAt);

        await ledger.SaveAsync(mixed with
        {
            AppliedPlacements = [active with { Records = [active.Records.Single(record => record.RecordId == "off-sibling")] }],
            RestoreResults = new Dictionary<long, string> { [enrolled.Sequence!.Value] = "restored" }
        }, default);
        var archived = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal("one", archived.TargetId);
        Assert.Equal("original", archived.RecordId);
        Assert.Equal("restored", archived.RestoreResult);
    }

    [Fact]
    public async Task CurrentCommittedSettingAppliesBeforeSubscriberDelivery()
    {
        var history = CreateHistory();
        var settings = new SettingsStore(false);
        using var ledger = CreateLedger(history, settings);
        await ledger.SaveAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        settings.Keep = true;
        var backfilled = await ledger.LoadAsync(default);
        Assert.Equal(1L, SingleRecord(backfilled).Sequence);
        settings.Keep = false;
        await ledger.SaveAsync(WithPlacement(backfilled, "two"), default);
        var mixed = await ledger.LoadAsync(default);
        Assert.Equal(1L, Assert.Single(mixed.AppliedPlacements.Single(static row => row.TargetId == "one").Records).Sequence);
        Assert.Null(Assert.Single(mixed.AppliedPlacements.Single(static row => row.TargetId == "two").Records).Sequence);
    }

    [Fact]
    public async Task RemovalPersistsActualRestoreResultWithLastReceiptContent()
    {
        var history = CreateHistory();
        using var ledger = CreateLedger(history, new SettingsStore(true));
        await ledger.SaveAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        var tracked = await ledger.LoadAsync(default);
        var sequence = SingleRecord(tracked).Sequence!.Value;
        var latest = await ledger.SaveAndReadAsync(tracked with
        {
            AppliedPlacements = [Assert.Single(tracked.AppliedPlacements) with { ProcessName = "last-process-name" }]
        }, default);
        await ledger.SaveAsync(latest with
        {
            AppliedPlacements = [],
            RestoreResults = new Dictionary<long, string> { [sequence] = "restored" }
        }, default);
        var entry = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(sequence, entry.Sequence);
        Assert.Equal("restored", entry.RestoreResult);
        Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, entry.Kind);
        Assert.Equal("original", entry.RecordId);
        Assert.Equal("last-process-name", entry.ProcessName);
        Assert.Equal(now, entry.RestoredAt);
        Assert.Empty((await ledger.LoadAsync(default)).AppliedPlacements);
    }

    [Fact]
    public async Task SameCycleRemovalBindsActualResultToTheSequenceAssignedByTheLedger()
    {
        var history = CreateHistory();
        using var ledger = CreateLedger(history, new SettingsStore(true));
        var local = WithPlacement(HostManagerRollbackStateDocument.Empty, "one");
        await ledger.SaveAsync(local, default);
        var localPlacement = Assert.Single(local.AppliedPlacements);
        var localRecord = Assert.Single(localPlacement.Records);
        Assert.Null(localRecord.Sequence);
        await ledger.SaveAsync(local with
        {
            AppliedPlacements = [],
            RecordRestoreResults = new Dictionary<HostManagerRecordHistoryKey, string>
            {
                [HostManagerRecordHistoryKey.Create(localPlacement, localRecord)] = "restored"
            }
        }, default);
        var entry = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(1L, entry.Sequence);
        Assert.Equal("restored", entry.RestoreResult);
        var published = await ledger.LoadAsync(default);
        Assert.Null(published.RestoreResults);
        Assert.Null(published.RecordRestoreResults);
    }

    [Fact]
    public async Task SaveAndReadReturnsTwoCanonicalRecordIdentitiesThatSurviveRestart()
    {
        var history = CreateHistory();
        var settings = new SettingsStore(true);
        var input = HostManagerRollbackStateDocument.Empty with
        {
            AppliedPlacements = [TwoRecordPlacement()]
        };
        IReadOnlyDictionary<string, long> issued;
        using (var ledger = CreateLedger(history, settings))
        {
            var saved = await ledger.SaveAndReadAsync(input, default);
            var records = Assert.Single(saved.AppliedPlacements).Records;
            Assert.Equal(new[] { "affinity", "priority" }, records.Select(record => record.RecordId).OrderBy(id => id));
            Assert.Equal(new long?[] { 1, 2 }, records.Select(record => record.Sequence).OrderBy(sequence => sequence));
            Assert.All(records, record => Assert.Equal(now, record.RecordedAt));
            Assert.All(Assert.Single(input.AppliedPlacements).Records, record => Assert.Null(record.Sequence));
            Assert.Equal(2L, await CountRowsAsync());
            Assert.Empty((await history.ReadHistoryAsync(null, 20, default)).Records);
            issued = records.ToDictionary(record => record.RecordId, record => record.Sequence!.Value);
        }

        using var reopened = CreateLedger(CreateHistory(), settings);
        var recovered = Assert.Single((await reopened.LoadAsync(default)).AppliedPlacements);
        Assert.Equal(issued.Count, recovered.Records.Count);
        Assert.All(recovered.Records, record => Assert.Equal(issued[record.RecordId], record.Sequence));
    }

    [Fact]
    public async Task PartialRestoreArchivesOneRecordWhileItsEnrolledSiblingStaysActive()
    {
        var history = CreateHistory();
        using var ledger = CreateLedger(history, new SettingsStore(true));
        var saved = await ledger.SaveAndReadAsync(HostManagerRollbackStateDocument.Empty with
        {
            AppliedPlacements = [TwoRecordPlacement()]
        }, default);
        var placement = Assert.Single(saved.AppliedPlacements);
        var restoredRecord = placement.Records.Single(record => record.RecordId == "affinity");
        var sibling = placement.Records.Single(record => record.RecordId == "priority");
        var remaining = await ledger.SaveAndReadAsync(saved with
        {
            AppliedPlacements = [placement with { Records = [sibling] }],
            RestoreResults = new Dictionary<long, string> { [restoredRecord.Sequence!.Value] = "restored" }
        }, default);

        var historyEntry = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(restoredRecord.Sequence, historyEntry.Sequence);
        Assert.Equal(restoredRecord.Kind, historyEntry.Kind);
        Assert.Equal(restoredRecord.RecordId, historyEntry.RecordId);
        Assert.Equal("restored", historyEntry.RestoreResult);
        Assert.Equal(now, historyEntry.RestoredAt);
        var active = SingleRecord(remaining);
        Assert.Equal(sibling.RecordId, active.RecordId);
        Assert.Equal(sibling.Sequence, active.Sequence);
        var wire = Assert.Single(Assert.Single(HostManagerSmartStateWireProjection.Project(remaining).AppliedPlacements).Records);
        Assert.Equal(sibling.Sequence, wire.Sequence);
        Assert.Equal("active", wire.State);
        Assert.Equal(now, wire.RecordedAt);
        Assert.Equal(2L, await CountRowsAsync());

        Assert.Equal(1L, await history.ClearHistoryAsync(default));
        Assert.Equal(1L, await CountRowsAsync());
        Assert.Equal(sibling.Sequence, SingleRecord(await ledger.LoadAsync(default)).Sequence);
    }

    [Fact]
    public async Task RestartSeparatesRestoredAndReappliedRecordsWithTheSameDeterministicId()
    {
        var history = CreateHistory();
        HostManagerRollbackStateDocument original;
        using (var ledger = CreateLedger(history, new SettingsStore(true)))
            original = await ledger.SaveAndReadAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        var oldRecord = SingleRecord(original);
        var empty = original with { AppliedPlacements = [] };
        await history.StageRemovalsAsync(original, empty, now, default,
            new Dictionary<long, string> { [oldRecord.Sequence!.Value] = "restored" });

        // Both ledger commits happen before the process stops: retire the old
        // receipt, then admit the new one, with identical target, action ID and time.
        // The DB completions are interrupted, so restart must link by sequence.
        long newSequence;
        using (var canonicalOnly = new JsonHostManagerRollbackStateStore(StatePath, new FixedTimeProvider(now), TimeSpan.FromMinutes(1)))
        {
            await canonicalOnly.SaveAsync(empty, default);
            var reapplied = await history.PrepareAsync(empty, WithPlacement(empty, "one"), true, now, default);
            var newRecord = SingleRecord(reapplied);
            Assert.Equal(oldRecord.RecordId, newRecord.RecordId);
            Assert.Equal(oldRecord.Sequence!.Value + 1, newRecord.Sequence);
            newSequence = newRecord.Sequence!.Value;
            await canonicalOnly.SaveAsync(reapplied, default);
        }

        using var reopened = CreateLedger(CreateHistory(), new SettingsStore(false));
        var recovered = await reopened.LoadAsync(default);
        var active = SingleRecord(recovered);
        Assert.Equal(oldRecord.RecordId, active.RecordId);
        Assert.Equal(newSequence, active.Sequence);
        var historical = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(oldRecord.Sequence, historical.Sequence);
        Assert.Equal(oldRecord.RecordId, historical.RecordId);
        Assert.Equal("restored", historical.RestoreResult);
        Assert.Equal(2L, await CountRowsAsync());
        var wire = Assert.Single(Assert.Single(HostManagerSmartStateWireProjection.Project(recovered).AppliedPlacements).Records);
        Assert.Equal(newSequence, wire.Sequence);
        Assert.Equal("active", wire.State);
        Assert.Equal(now, wire.RecordedAt);
    }

    [Fact]
    public async Task RestartKeepsIdentityAndContinuesSequenceAfterHistoryClear()
    {
        var history = CreateHistory();
        var settings = new SettingsStore(true);
        long sequence;
        using (var ledger = CreateLedger(history, settings))
        {
            var canonical = await ledger.SaveAndReadAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
            var record = SingleRecord(canonical);
            Assert.Equal("original", record.RecordId);
            Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, record.Kind);
            Assert.Equal(now, record.RecordedAt);
            sequence = record.Sequence!.Value;
        }
        using (var reopened = CreateLedger(CreateHistory(), settings))
        {
            var restored = await reopened.LoadAsync(default);
            Assert.Equal(sequence, SingleRecord(restored).Sequence);
            await reopened.SaveAsync(restored with { AppliedPlacements = [] }, default);
        }
        Assert.Equal(1L, await history.ClearHistoryAsync(default));
        using var nextRun = CreateLedger(CreateHistory(), settings);
        var empty = await nextRun.LoadAsync(default);
        await nextRun.SaveAsync(WithPlacement(empty, "one"), default);
        Assert.Equal(sequence + 1, SingleRecord(await nextRun.LoadAsync(default)).Sequence);
    }

    [Fact]
    public async Task RestartReconcilesCanonicalRemovalCommittedWithoutHistoryCompletion()
    {
        var history = CreateHistory();
        using (var ledger = CreateLedger(history, new SettingsStore(true)))
            await ledger.SaveAsync(WithPlacement(HostManagerRollbackStateDocument.Empty, "one"), default);
        // This yields the durable state of a process stopping after the ledger commit
        // and before its history completion, using the real canonical writer.
        using (var canonicalOnly = new JsonHostManagerRollbackStateStore(StatePath, new FixedTimeProvider(now), TimeSpan.FromMinutes(1)))
        {
            var restored = await canonicalOnly.LoadAsync(default);
            await canonicalOnly.SaveAsync(restored with { AppliedPlacements = [] }, default);
        }
        using var reopened = CreateLedger(CreateHistory(), new SettingsStore(false));
        Assert.Empty((await reopened.LoadAsync(default)).AppliedPlacements);
        var entry = Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal("unknown", entry.RestoreResult);
    }

    [Fact]
    public async Task FailedCanonicalCommitDoesNotPublishOrArchiveAndRetryUsesNewSequence()
    {
        var history = CreateHistory();
        var fail = false;
        using var ledger = CreateLedger(history, new SettingsStore(true), () =>
        {
            if (fail) throw new IOException("Injected canonical commit failure.");
        });
        var baseline = await ledger.ReserveNativeHostSessionIncarnationAsync(default);
        await ledger.LoadAsync(default);
        fail = true;
        await Assert.ThrowsAnyAsync<IOException>(() => ledger.SaveAsync(WithPlacement(baseline, "one"), default));
        fail = false;
        Assert.Empty((await ledger.LoadAsync(default)).AppliedPlacements);
        Assert.Empty((await history.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(0L, await CountRowsAsync());
        await ledger.SaveAsync(WithPlacement(baseline, "one"), default);
        var retry = await ledger.LoadAsync(default);
        Assert.Equal(2L, SingleRecord(retry).Sequence);
        await ledger.SaveAsync(retry with { AppliedPlacements = [] }, default);
        Assert.Equal(2L, Assert.Single((await history.ReadHistoryAsync(null, 20, default)).Records).Sequence);
    }

    private string StatePath => Path.Combine(testRoot, "rollback-state.json");

    private static HostManagerAppliedRecord SingleRecord(HostManagerRollbackStateDocument document)
        => Assert.Single(Assert.Single(document.AppliedPlacements).Records);

    private static Task ApplyCommittedHistorySettingAsync(JsonHostManagerRollbackStateStore ledger, IAppSettingsStore settings)
        => new ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimeSpecializationCoordinator(
            null!, settings, null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimeSpecializationCoordinator>.Instance,
            smartRollbackStateStore: ledger).SynchronizeSmartRecordHistorySettingAsync(default);

    private JsonHostManagerRollbackStateStore CreateLedger(
        SqliteHostManagerSmartRecordHistoryStore history,
        IAppSettingsStore settings,
        Action? beforeCommit = null)
    {
        var ledger = new JsonHostManagerRollbackStateStore(StatePath, new FixedTimeProvider(now),
            TimeSpan.FromMinutes(1), beforeCommit);
        ledger.ConfigureHistory(history, settings);
        return ledger;
    }

    private SqliteHostManagerSmartRecordHistoryStore CreateHistory() => new(CreateDatabase());

    private ResourceManagerDatabase CreateDatabase()
        => new(new TestHostEnvironment(Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName));

    private async Task<long> CountRowsAsync()
    {
        await using var connection = await CreateDatabase().OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_manager_smart_record_history;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private HostManagerRollbackStateDocument WithPlacement(HostManagerRollbackStateDocument document, string target)
        => document with
        {
            AppliedPlacements = document.AppliedPlacements.Append(new HostManagerAppliedPlacementReceipt(
                target, "software", "cpu", [new(HostManagerAppliedRecordKinds.CpuAffinity, "original")], now, now)).ToArray()
        };

    private HostManagerAppliedPlacementReceipt TwoRecordPlacement()
        => new("one", "software", "cpu",
            [new(HostManagerAppliedRecordKinds.CpuAffinity, "affinity"),
             new(HostManagerAppliedRecordKinds.ProcessPriority, "priority")], now, now);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(testRoot, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class SettingsStore(bool keep) : IAppSettingsStore
    {
        public bool Keep { get; set; } = keep;

        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken)
        {
            var settings = AppSettingsDefaults.Create();
            settings = settings with { Performance = settings.Performance with { KeepSmartRecordHistory = Keep } };
            return Task.FromResult(new AppSettingsUpdateResult(settings, DateTimeOffset.UtcNow, "fixture",
                new(AppSettingsSourceKind.TestFixture, settings.Version, "fixture", "fixture", false)));
        }

        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);
        public Task<AppSettingsUpdateResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> SaveValidatedAsync(AppSettings settings,
            Func<AppSettingsUpdateResult, CancellationToken, Task> validateBeforeCommit, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
