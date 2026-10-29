using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class SqlitePersistenceTests
{
    [Fact]
    public async Task SmartRecordHistoryDefaultsOffAndEnablingCapturesExistingLedger()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var untracked = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), false, now, default);
        Assert.Null(SmartHistoryOnlyRecord(untracked).Sequence);
        Assert.Null(SmartHistoryOnlyRecord(untracked).RecordedAt);

        var tracked = await store.PrepareAsync(untracked, untracked, true, now, default);
        await store.CompleteAsync(untracked, tracked, now, default);
        Assert.Equal(1L, SmartHistoryOnlyRecord(tracked).Sequence);
        Assert.Equal(now.ToUniversalTime(), SmartHistoryOnlyRecord(tracked).RecordedAt);
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
    }

    [Fact]
    public async Task SmartRecordHistoryAssignsTwoRowsAndSequencesToTwoRecordsOfOnePlacement()
    {
        var database = CreateDatabase();
        var store = new SqliteHostManagerSmartRecordHistoryStore(database);
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now, "first", "second")), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);

        var records = Assert.Single(ledger.AppliedPlacements).Records;
        Assert.Equal(new long?[] { 1, 2 }, records.Select(static record => record.Sequence));
        Assert.All(records, record => Assert.Equal(now.ToUniversalTime(), record.RecordedAt));
        Assert.Equal(2L, await SmartHistoryCountAsync("active"));
        await using var connection = await database.OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM host_manager_smart_record_history ORDER BY sequence;";
        await using var reader = await command.ExecuteReaderAsync();
        var index = 0;
        while (await reader.ReadAsync())
        {
            using var payload = JsonDocument.Parse(reader.GetString(0));
            var item = Assert.Single(payload.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal(records[index].RecordId, item.GetProperty("recordId").GetString());
            Assert.Equal(records[index].Sequence, item.GetProperty("sequence").GetInt64());
            index++;
        }
        Assert.Equal(2, index);
    }

    [Fact]
    public async Task SmartRecordHistoryRestoresOneSiblingWhileOtherRecordStaysActive()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var both = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now, "first", "second")), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, both, now, default);
        var first = both.AppliedPlacements[0].Records[0];
        var second = both.AppliedPlacements[0].Records[1];
        var active = SmartHistoryLedger(both.AppliedPlacements[0] with { Records = [second] });
        await store.StageRemovalsAsync(both, active, now.AddSeconds(1), default,
            new Dictionary<long, string> { [first.Sequence!.Value] = HostManagerSmartRecordRestoreResults.Restored });
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
        await store.CompleteAsync(both, active, now.AddSeconds(2), default);

        var row = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(first.Sequence, row.Sequence);
        Assert.Equal("first", row.RecordId);
        Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, row.Kind);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Restored, row.RestoreResult);
        Assert.Equal(now.AddSeconds(1), row.RestoredAt);
        Assert.Equal(second.Sequence, SmartHistoryOnlyRecord(active).Sequence);
        Assert.Equal(1L, await SmartHistoryCountAsync("active"));
        Assert.Equal(1L, await store.ClearHistoryAsync(default));
        Assert.Equal(1L, await SmartHistoryCountAsync("active"));
    }

    [Fact]
    public async Task SmartRecordHistoryRetainsEnrolledRecordAfterSwitchOffButDoesNotEnrollNewSibling()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var first = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, first, now, default);
        var enrolled = SmartHistoryOnlyRecord(first);
        var changed = SmartHistoryLedger(first.AppliedPlacements[0] with
        {
            Records = [enrolled, new(HostManagerAppliedRecordKinds.ProcessPriority, "off-sibling")],
            SoftwareId = "latest-software",
            ProcessName = "latest-process",
            UpdatedAt = now.AddSeconds(1)
        });
        changed = await store.PrepareAsync(first, changed, false, now.AddSeconds(1), default);
        await store.CompleteAsync(first, changed, now.AddSeconds(1), default);
        Assert.Equal(enrolled.Sequence, changed.AppliedPlacements[0].Records[0].Sequence);
        Assert.Equal(enrolled.RecordedAt, changed.AppliedPlacements[0].Records[0].RecordedAt);
        Assert.Null(changed.AppliedPlacements[0].Records[1].Sequence);
        Assert.Null(changed.AppliedPlacements[0].Records[1].RecordedAt);

        var active = SmartHistoryLedger(changed.AppliedPlacements[0] with
            { Records = [changed.AppliedPlacements[0].Records[1]] });
        await store.StageRemovalsAsync(changed, active, now.AddSeconds(2), default,
            new Dictionary<long, string> { [enrolled.Sequence!.Value] = HostManagerSmartRecordRestoreResults.Restored });
        await store.CompleteAsync(changed, active, now.AddSeconds(2), default);
        var history = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(enrolled.Sequence, history.Sequence);
        Assert.Equal("original", history.RecordId);
        Assert.Equal(now, history.RecordedAt);
        Assert.Equal(now.AddSeconds(2), history.RestoredAt);
        Assert.Equal("history", history.State);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Restored, history.RestoreResult);
        Assert.Equal("latest-software", history.SoftwareId);
        Assert.Equal("latest-process", history.ProcessName);
        Assert.Equal(123, history.ProcessId);
        Assert.Equal("123456789012345678", history.ProcessStartKey);
        await store.CompleteAsync(active, HostManagerRollbackStateDocument.Empty, now.AddSeconds(3), default);
        Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
    }

    [Fact]
    public async Task SmartRecordHistoryPaginatesBySequenceWhenClockRunsBackward()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
        {
            var earlier = now.AddDays(-index);
            var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
                SmartHistoryLedger(SmartHistoryPlacement("same-target", earlier)), true, earlier, default);
            await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, earlier, default);
            await store.CompleteAsync(ledger, HostManagerRollbackStateDocument.Empty, earlier, default);
        }

        var first = await store.ReadHistoryAsync(null, 2, default);
        var second = await store.ReadHistoryAsync(first.NextBeforeSequence, 2, default);
        var last = await store.ReadHistoryAsync(second.NextBeforeSequence, 2, default);
        Assert.Equal(new long[] { 5, 4 }, first.Records.Select(static row => row.Sequence));
        Assert.Equal(4L, first.NextBeforeSequence);
        Assert.Equal(new long[] { 3, 2 }, second.Records.Select(static row => row.Sequence));
        Assert.Equal(2L, second.NextBeforeSequence);
        Assert.Equal(1L, Assert.Single(last.Records).Sequence);
        Assert.Null(last.NextBeforeSequence);
        Assert.True(first.Records[0].RecordedAt < last.Records[0].RecordedAt);
        Assert.Equal(5, (await store.ReadHistoryAsync(null, 500, default)).Records.Count);
        var beforeFirst = await store.ReadHistoryAsync(1, 1, default);
        Assert.Empty(beforeFirst.Records);
        Assert.Null(beforeFirst.NextBeforeSequence);
    }

    [Fact]
    public async Task SmartRecordHistoryClearPreservesActiveAndNeverResetsSequenceAcrossReopen()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var both = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now, "first", "second")), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, both, now, default);
        var active = SmartHistoryLedger(both.AppliedPlacements[0] with { Records = [both.AppliedPlacements[0].Records[1]] });
        await store.CompleteAsync(both, active, now, default);
        Assert.Equal(1L, await store.ClearHistoryAsync(default));
        Assert.Equal(0L, await store.ClearHistoryAsync(default));

        store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        await store.ReconcileAsync(active, now, default);
        var added = await store.PrepareAsync(active,
            SmartHistoryLedger(active.AppliedPlacements[0] with
                { Records = [SmartHistoryOnlyRecord(active), new(HostManagerAppliedRecordKinds.CpuAffinity, "first")] }),
            true, now, default);
        Assert.Equal(new long?[] { 2, 3 }, added.AppliedPlacements[0].Records.Select(static record => record.Sequence));
        await store.CompleteAsync(active, added, now, default);
        await store.CompleteAsync(added, HostManagerRollbackStateDocument.Empty, now, default);
        Assert.Equal(new long[] { 3, 2 }, (await store.ReadHistoryAsync(null, 20, default)).Records.Select(static row => row.Sequence));
    }

    [Fact]
    public async Task SmartRecordHistoryReconcileDiscardsUncommittedReservationAndKeepsItsSequenceUsed()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("never-committed", now)), true, now, default);
        await store.ReconcileAsync(HostManagerRollbackStateDocument.Empty, now, default);
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(0L, await SmartHistoryCountAsync("active"));
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("committed", now)), true, now, default);
        Assert.Equal(2L, SmartHistoryOnlyRecord(ledger).Sequence);
    }

    [Fact]
    public async Task SmartRecordHistoryReconcileHonorsLedgerCommitBeforeHistoryCompletion()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.ReconcileAsync(ledger, now.AddSeconds(1), default);
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
        await store.ReconcileAsync(HostManagerRollbackStateDocument.Empty, now.AddSeconds(2), default);
        var history = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(SmartHistoryOnlyRecord(ledger).Sequence, history.Sequence);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Unknown, history.RestoreResult);
        Assert.Equal(now.AddSeconds(2), history.RestoredAt);
    }

    [Fact]
    public async Task SmartRecordHistoryRestartDistinguishesSameDeterministicIdAfterRestoreAndReapply()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var first = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, first, now, default);
        var firstRecord = SmartHistoryOnlyRecord(first);
        await store.StageRemovalsAsync(first, HostManagerRollbackStateDocument.Empty, now.AddSeconds(1), default,
            new Dictionary<long, string> { [firstRecord.Sequence!.Value] = HostManagerSmartRecordRestoreResults.Restored });
        // Canonical removal and reapplication committed, but history completion did not.
        // Neither deterministic RecordId nor the deliberately identical AppliedAt can identify the episode.
        var second = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        var secondRecord = SmartHistoryOnlyRecord(second);
        Assert.Equal(firstRecord.RecordId, secondRecord.RecordId);
        Assert.Equal(1L, firstRecord.Sequence);
        Assert.Equal(2L, secondRecord.Sequence);

        store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        await store.ReconcileAsync(second, now.AddHours(1), default);
        var historical = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(firstRecord.Sequence, historical.Sequence);
        Assert.Equal(now.AddSeconds(1), historical.RestoredAt);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Restored, historical.RestoreResult);
        Assert.Equal(1L, await SmartHistoryCountAsync("active"));
        Assert.Equal(1L, await store.ClearHistoryAsync(default));
        await store.ReconcileAsync(second, now.AddHours(2), default);
        await store.CompleteAsync(second, HostManagerRollbackStateDocument.Empty, now.AddHours(3), default,
            new Dictionary<long, string> { [secondRecord.Sequence!.Value] = HostManagerSmartRecordRestoreResults.TargetExited });
        var latest = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(secondRecord.Sequence, latest.Sequence);
        Assert.Equal(HostManagerSmartRecordRestoreResults.TargetExited, latest.RestoreResult);
    }

    [Fact]
    public async Task SmartRecordHistoryPrepareDoesNotPublishUncommittedContentOrTrustCallerSequence()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var original = SmartHistoryPlacement("one", now);
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(original with { Records = [original.Records[0] with { Sequence = 999 }] }), true, now, default);
        Assert.Equal(1L, SmartHistoryOnlyRecord(ledger).Sequence);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);
        await store.PrepareAsync(ledger, SmartHistoryLedger(ledger.AppliedPlacements[0] with
        {
            Records = [SmartHistoryOnlyRecord(ledger) with { Sequence = 999 }],
            ProcessName = "uncommitted"
        }), true, now, default);
        await store.ReconcileAsync(HostManagerRollbackStateDocument.Empty, now, default);
        var history = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal("original", history.RecordId);
        Assert.Equal("test-process", history.ProcessName);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 501)]
    public async Task SmartRecordHistoryRejectsInvalidPageBounds(long before, int limit)
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadHistoryAsync(before, limit, default));
    }

    [Theory]
    [InlineData("restored")]
    [InlineData("targetExited")]
    [InlineData("ownershipLost")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public async Task SmartRecordHistoryPersistsOnlyTheFixedResultVocabulary(string result)
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);
        await store.StageRemovalsAsync(ledger, HostManagerRollbackStateDocument.Empty, now.AddSeconds(1), default,
            new Dictionary<long, string> { [SmartHistoryOnlyRecord(ledger).Sequence!.Value] = result });
        await store.CompleteAsync(ledger, HostManagerRollbackStateDocument.Empty, now.AddSeconds(2), default);
        Assert.Equal(result, Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records).RestoreResult);
    }

    [Fact]
    public async Task SmartRecordHistoryRejectsDescriptiveResultsWithoutPublishingTheRemoval()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);
        var result = new Dictionary<long, string> { [SmartHistoryOnlyRecord(ledger).Sequence!.Value] = "Restored: descriptive detail" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageRemovalsAsync(ledger,
            HostManagerRollbackStateDocument.Empty, now, default, result));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(ledger,
            HostManagerRollbackStateDocument.Empty, now, default, result));
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(1L, await SmartHistoryCountAsync("active"));
    }

    [Fact]
    public async Task SmartRecordHistoryCrashBeforeCanonicalRemovalClearsStagedRestoration()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);
        var sequence = SmartHistoryOnlyRecord(ledger).Sequence!.Value;
        await store.StageRemovalsAsync(ledger, HostManagerRollbackStateDocument.Empty, now.AddSeconds(1), default,
            new Dictionary<long, string> { [sequence] = HostManagerSmartRecordRestoreResults.Restored });
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);

        await store.ReconcileAsync(ledger, now.AddSeconds(2), default);
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);
        await store.CompleteAsync(ledger, HostManagerRollbackStateDocument.Empty, now.AddSeconds(3), default);
        var history = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Unknown, history.RestoreResult);
        Assert.Equal(now.AddSeconds(3), history.RestoredAt);
    }

    [Fact]
    public async Task SmartRecordHistoryCrashAfterCanonicalRemovalKeepsStagedTimeAndResult()
    {
        var store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.PrepareAsync(HostManagerRollbackStateDocument.Empty,
            SmartHistoryLedger(SmartHistoryPlacement("one", now)), true, now, default);
        await store.CompleteAsync(HostManagerRollbackStateDocument.Empty, ledger, now, default);
        var stagedAt = now.AddSeconds(1);
        var sequence = SmartHistoryOnlyRecord(ledger).Sequence!.Value;
        await store.StageRemovalsAsync(ledger, HostManagerRollbackStateDocument.Empty, stagedAt, default,
            new Dictionary<long, string> { [sequence] = HostManagerSmartRecordRestoreResults.Failed });
        Assert.Empty((await store.ReadHistoryAsync(null, 20, default)).Records);

        store = new SqliteHostManagerSmartRecordHistoryStore(CreateDatabase());
        await store.ReconcileAsync(HostManagerRollbackStateDocument.Empty, now.AddHours(1), default);
        var history = Assert.Single((await store.ReadHistoryAsync(null, 20, default)).Records);
        Assert.Equal(sequence, history.Sequence);
        Assert.Equal(stagedAt, history.RestoredAt);
        Assert.Equal(HostManagerSmartRecordRestoreResults.Failed, history.RestoreResult);
        Assert.Equal("original", history.RecordId);
    }

    private async Task<long> SmartHistoryCountAsync(string state)
    {
        await using var connection = await CreateDatabase().OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_manager_smart_record_history WHERE state = $state;";
        command.Parameters.AddWithValue("$state", state);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static HostManagerAppliedRecord SmartHistoryOnlyRecord(HostManagerRollbackStateDocument ledger)
        => Assert.Single(Assert.Single(ledger.AppliedPlacements).Records);

    private static HostManagerRollbackStateDocument SmartHistoryLedger(params HostManagerAppliedPlacementReceipt[] placements)
        => HostManagerRollbackStateDocument.Empty with { AppliedPlacements = placements };

    private static HostManagerAppliedPlacementReceipt SmartHistoryPlacement(string target, DateTimeOffset at, params string[] recordIds)
        => new(target, "software", "cpu", (recordIds.Length == 0 ? new[] { "original" } : recordIds)
            .Select(id => new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.CpuAffinity, id)).ToArray(), at, at)
        {
            ProcessId = 123,
            ProcessStartKey = "123456789012345678",
            ProcessName = "test-process"
        };
}
