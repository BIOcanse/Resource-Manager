using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Files.Indexing;
using Xunit;
namespace Resource_Manager_APP.Tests;

public sealed class IndexedFileOwnerLibraryIntegrationTests
{
    [Fact] public async Task OriginalNativeQueryProjectsExactRecordedIndexedValues()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "index.db"), new IndexedFileDatabase(Path.Combine(fixture.DirectoryPath, "index.db")).Identity);
        var backend = fixture.Query.Backend;
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(3, catalog.Fields.Length);
        Assert.All(catalog.Fields, field => { Assert.True(field.Read.Supported); Assert.False(field.Write.Supported); });
        var readings = await backend.CollectAsync([.. catalog.Fields.Select(field => field.Field)], default);
        Assert.All(readings, reading => Assert.Equal(FieldObservationStatus.Current, reading.Status));
        Assert.Equal("9007199254740993", readings.Single(r => r.Field.FieldName == "file_all_indexed_size_actual").Value!.Integer);
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "alpha.txt"), readings.Single(r => r.Field.FieldName == "file_all_indexed_path_actual").Value!.Text);
        Assert.Equal(Fixture.Modified.ToString("O"), readings.Single(r => r.Field.FieldName == "file_all_indexed_modified_time_actual").Value!.Text);
        Assert.Equal(typeof(SqliteIndexedFileOwner).Assembly, typeof(NativeFileQueryLease).Assembly);
        Assert.NotEqual(typeof(IndexedFileOwnerLibraryIntegrationTests).Assembly, typeof(SqliteIndexedFileOwner).Assembly);
        Assert.Single(await fixture.Owner.SearchAsync("alpha", 10, default)); // same original native pool can be reused after Reset/finally
    }
    [Fact] public async Task RetiredBoundRowBecomesUnavailableWithoutRediscoveryOrRefresh()
    {
        using var fixture = await Fixture.Create();
        using var borrow = fixture.Query.BorrowSubscriptionCapture();
        var catalog = await borrow.Backend.DiscoverAsync(default);
        var fields = catalog.Fields.Select(field => field.Field).ToImmutableArray();
        _ = borrow.Bind(fields);
        await fixture.Sql("DELETE FROM software_index_entries;");
        borrow.SynchronizeDemand(fields);
        var result = await borrow.CollectAsync(fields, new Permit());
        Assert.True(result.Committed);
        Assert.All(result.Readings, reading => { Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); });
        Assert.Single(fixture.Query.ReadInformation().Devices);
    }
    [Fact] public async Task AuthorizationIsAppliedBeforeNativeSubmissionAndAgainForBoundRows()
    {
        using var fixture = await Fixture.Create();
        var allowed = true;
        using var query = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) => allowed);
        using var borrow = query.BorrowSubscriptionCapture();
        var catalog = await borrow.Backend.DiscoverAsync(default);
        var fields = catalog.Fields.Select(field => field.Field).ToImmutableArray();
        _ = borrow.Bind(fields);
        allowed = false;
        var result = await borrow.CollectAsync(fields, new Permit());
        Assert.All(result.Readings, reading => Assert.Equal(FieldObservationStatus.Unavailable, reading.Status));
        using var denied = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) => false);
        Assert.Empty((await denied.Backend.DiscoverAsync(default)).Fields);
        Assert.Single(await fixture.Owner.SearchAsync("alpha", 10, default)); // raw original product contract unchanged
    }
    [Fact] public async Task RejectedAndCanceledCollectionPreservesTheSameCurrent()
    {
        using var fixture = await Fixture.Create();
        using var borrow = fixture.Query.BorrowSubscriptionCapture();
        var fields = (await borrow.Backend.DiscoverAsync(default)).Fields.Select(field => field.Field).ToImmutableArray();
        _ = borrow.Bind(fields); var before = fixture.Query.ReadInformation();
        await fixture.Sql("UPDATE software_index_entries SET size_bytes=0;");
        var rejected = await borrow.CollectAsync(fields, new Permit(false));
        Assert.False(rejected.Committed);
        Assert.Equal(fields.Select(borrow.Backend.ReadCached).ToArray(), rejected.Readings.ToArray());
        Assert.Same(before, fixture.Query.ReadInformation());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync(fields, new Permit(), cancel.Token));
        Assert.Same(before, fixture.Query.ReadInformation());
        var result = await borrow.CollectAsync(fields, new Permit());
        Assert.Equal("0", result.Readings.Single(r => r.Field.FieldName == "file_all_indexed_size_actual").Value!.Integer);
    }
    [Fact] public async Task QueryFailureClearsCurrentAndDoesNotCacheSuccess()
    {
        using var fixture = await Fixture.Create();
        await fixture.Query.CaptureAsync();
        await fixture.Sql("DROP TABLE software_index_entries;");
        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Query.CaptureAsync());
        Assert.Null(fixture.Query.ReadInformation().ObservedAt);
        Assert.All(fixture.Query.ReadInformation().Devices.SelectMany(d => d.Fields), field => Assert.Null(field.Value));
    }
    [Fact] public async Task BorrowDisposalDoesNotDisposeTheOriginalOwnerOrAnotherBorrow()
    {
        using var fixture = await Fixture.Create();
        var first = fixture.Query.BorrowSubscriptionCapture();
        using var second = fixture.Query.BorrowSubscriptionCapture();
        var fields = (await second.Backend.DiscoverAsync(default)).Fields.Select(field => field.Field).ToImmutableArray();
        _ = first.Bind(fields); _ = second.Bind(fields); first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await first.CollectAsync(fields, new Permit()));
        Assert.True((await second.CollectAsync(fields, new Permit())).Committed);
        Assert.Single(await fixture.Owner.SearchAsync("alpha", 10, default));
    }
    [Fact] public async Task RetiringOwnerDuringNativeQueryRejectsCanonicalCommitButRetainsDisplay()
    {
        using var fixture = await Fixture.Create();
        var retire = false; var calls = 0;
        using var retiring = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) => { calls++; if (retire) fixture.Owner.Dispose(); return true; });
        await retiring.CaptureAsync(); var before = retiring.ReadInformation(); retire = true;
        var returned = await retiring.CaptureAsync();
        Assert.Same(before, returned);
        Assert.True(calls > 0); Assert.Same(before, retiring.ReadInformation());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Query.CaptureAsync());
    }
    [Fact] public async Task OriginalExplicitRefreshRetainsLastCompleteRootAfterEnumerationFailure()
    {
        using var fixture = await Fixture.Create();
        var data = Path.Combine(fixture.DirectoryPath, "data"); var file = Path.Combine(data, "fresh.txt");
        Directory.CreateDirectory(data);
        try
        {
            await File.WriteAllTextAsync(file, "12345");
            var request = new ResourceManager.App.Domain.Indexing.SoftwareFileIndexRequest("fresh", "Fresh", [new(data, "install")]);
            var before = await fixture.Owner.GetOrRefreshAsync(request, TimeSpan.Zero, true, default);
            Assert.Equal(5, before.TotalBytes); Assert.Equal(1, before.FileCount);
            File.Delete(file); Directory.Delete(data);
            var failed = await fixture.Owner.GetOrRefreshAsync(request, TimeSpan.Zero, true, default);
            Assert.Equal(before.TotalBytes, failed.TotalBytes); Assert.Equal(before.FileCount, failed.FileCount); Assert.Equal(before.LastIndexedAt, failed.LastIndexedAt);
            Assert.Single(await fixture.Owner.SearchAsync("fresh", 10, default));
        }
        finally { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(data)) Directory.Delete(data); }
    }
    [Fact] public async Task DirectCommitAndFailureClearWakeTheSameBorrowPublication()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fixture = await Fixture.Create();
        using var borrow = fixture.Query.BorrowSubscriptionCapture();
        Assert.Same(fixture.Query.Publications, borrow.Publications);
        using var notices = borrow.Publications.Subscribe([IndexedFileInformationQuery.InformationItemId]);
        await fixture.Query.CaptureAsync(timeout.Token);
        await notices.WaitAsync(timeout.Token);
        Assert.Equal(3, borrow.Backend.Catalog.Fields.Length);
        await fixture.Sql("DROP TABLE software_index_entries;");
        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Query.CaptureAsync(timeout.Token));
        await notices.WaitAsync(timeout.Token);
        Assert.Null(fixture.Query.ReadInformation().ObservedAt);
    }
    // Multiple-query/partial-current contract regressions; runtime remains unverified.
    [Fact] public async Task QueryBackendsRetainOnlyTheirOwnDiscoveredMembership()
    {
        using var fixture = await Fixture.Create(); await fixture.AddBeta();
        using var alphaQuery = fixture.Owner.CreateInformationQuery("alpha", 10);
        var alpha = alphaQuery.Backend;
        var alphaCatalog = await alpha.DiscoverAsync(default);
        using var betaQuery = fixture.Owner.CreateInformationQuery("beta", 10);
        var beta = betaQuery.Backend;
        Assert.Empty(beta.Catalog.Fields); // no discovery in this scope yet
        var betaCatalog = await beta.DiscoverAsync(default);
        Assert.Equal(alphaCatalog.Fields.Select(f => f.Field).ToArray(), alpha.Catalog.Fields.Select(f => f.Field).ToArray());
        Assert.Equal(3, betaCatalog.Fields.Length);
        Assert.NotEqual(alphaCatalog.Devices[0].Identity, betaCatalog.Devices[0].Identity);
    }
    [Fact] public async Task UndiscoveredDeniedBackendCannotReadAnotherBackendCachedFields()
    {
        using var fixture = await Fixture.Create();
        using var permittedQuery = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) => true);
        var permitted = permittedQuery.Backend;
        var field = (await permitted.DiscoverAsync(default)).Fields[0].Field;
        using var deniedQuery = fixture.Owner.CreateInformationQuery("beta", 10, (_, _) => false);
        var denied = deniedQuery.Backend;
        Assert.Throws<ArgumentException>(() => denied.ReadCached(field));
    }
    [Fact] public async Task SelectedSingleFieldDoesNotRefreshUnselectedSameRowValuesOrTimestamp()
    {
        using var fixture = await Fixture.Create();
        var backend = fixture.Query.Backend;
        var fields = (await backend.DiscoverAsync(default)).Fields;
        var size = fields.Single(f => f.Field.FieldName == "file_all_indexed_size_actual").Field;
        var path = fields.Single(f => f.Field.FieldName == "file_all_indexed_path_actual").Field;
        var before = backend.ReadCached(size);
        await fixture.Sql("UPDATE software_index_entries SET size_bytes=0 WHERE id=1;");
        _ = await backend.CollectAsync([path], default);
        Assert.Equal(before, backend.ReadCached(size));
        Assert.Equal("0", (await backend.CollectAsync([size], default))[0].Value!.Integer);
    }
    [Fact] public async Task MultipleBorrowsPreserveUnrequestedRowsAcrossDifferentSelectedBatches()
    {
        using var fixture = await Fixture.Create(); await fixture.AddBeta();
        using var alpha = fixture.Query.BorrowSubscriptionCapture();
        var alphaFields = (await alpha.Backend.DiscoverAsync(default)).Fields.Select(f => f.Field).ToImmutableArray();
        _ = alpha.Bind(alphaFields);
        using var betaQuery = fixture.Owner.CreateInformationQuery("beta", 10);
        using var beta = betaQuery.BorrowSubscriptionCapture();
        var betaFields = (await beta.Backend.DiscoverAsync(default)).Fields.Select(f => f.Field).ToImmutableArray();
        _ = beta.Bind(betaFields);
        var before = beta.Backend.ReadCached(betaFields[0]);
        var onlyAlphaSize = alphaFields.Single(f => f.FieldName == "file_all_indexed_size_actual");
        Assert.True((await alpha.CollectAsync([onlyAlphaSize], new Permit())).Committed);
        Assert.Equal(before, beta.Backend.ReadCached(betaFields[0]));
        Assert.True((await beta.CollectAsync([betaFields[0]], new Permit())).Committed);
        Assert.Equal(FieldObservationStatus.Current, alpha.Backend.ReadCached(onlyAlphaSize).Status);
    }
    [Fact] public async Task SelectedFailureClearsOnlyRequestedFieldAndPreservesOtherCurrentReadings()
    {
        using var fixture = await Fixture.Create();
        using var borrow = fixture.Query.BorrowSubscriptionCapture();
        var fields = (await borrow.Backend.DiscoverAsync(default)).Fields.Select(f => f.Field).ToImmutableArray();
        _ = borrow.Bind(fields);
        var path = fields.Single(f => f.FieldName == "file_all_indexed_path_actual");
        var size = fields.Single(f => f.FieldName == "file_all_indexed_size_actual");
        var before = borrow.Backend.ReadCached(size);
        await fixture.Sql("DROP TABLE software_index_entries;");
        await Assert.ThrowsAnyAsync<Exception>(async () => await borrow.CollectAsync([path], new Permit()));
        Assert.Equal(FieldObservationStatus.Unavailable, borrow.Backend.ReadCached(path).Status);
        Assert.Equal(before, borrow.Backend.ReadCached(size));
    }
    [Fact] public async Task SameLogicalRowAcrossQueriesHasSameIdentityButIndependentCurrent()
    {
        using var fixture = await Fixture.Create();
        using var first = fixture.Owner.CreateInformationQuery("alpha", 10);
        using var second = fixture.Owner.CreateInformationQuery("alpha", 10);
        var firstFields = (await first.Backend.DiscoverAsync(default)).Fields;
        var secondFields = (await second.Backend.DiscoverAsync(default)).Fields;
        Assert.Equal(firstFields[0].Field.DeviceIdentity, secondFields[0].Field.DeviceIdentity);
        var size = firstFields.Single(f => f.Field.FieldName == "file_all_indexed_size_actual").Field;
        var before = second.Backend.ReadCached(size);
        await fixture.Sql("UPDATE software_index_entries SET size_bytes=0 WHERE id=1;");
        Assert.Equal("0", (await first.Backend.CollectAsync([size], default))[0].Value!.Integer);
        Assert.Equal(before, second.Backend.ReadCached(size));
        using var denied = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) => false);
        Assert.Empty((await denied.Backend.DiscoverAsync(default)).Fields);
        Assert.Throws<ArgumentException>(() => denied.Backend.ReadCached(size));
    }
    [Fact] public async Task FailureInOneQueryDoesNotClearOtherQueryCurrent()
    {
        using var fixture = await Fixture.Create(); await fixture.AddBeta();
        using var first = fixture.Owner.CreateInformationQuery("alpha", 10);
        using var second = fixture.Owner.CreateInformationQuery("beta", 10);
        _ = await first.Backend.DiscoverAsync(default);
        var beta = (await second.Backend.DiscoverAsync(default)).Fields[0].Field;
        var before = second.Backend.ReadCached(beta);
        await fixture.Sql("DROP TABLE software_index_entries;");
        await Assert.ThrowsAnyAsync<Exception>(async () => await first.CaptureAsync());
        Assert.Equal(before, second.Backend.ReadCached(beta));
    }
    [Fact] public async Task InFlightRevocationAndCancellationPreserveQueryCurrent()
    {
        using var fixture = await Fixture.Create();
        var permit = new Permit(); var revoke = false; var cancel = false;
        using var cancellation = new CancellationTokenSource();
        using var query = fixture.Owner.CreateInformationQuery("alpha", 10, (_, _) =>
        {
            if (revoke) permit.Revoke(); if (cancel) cancellation.Cancel(); return true;
        });
        using var borrow = query.BorrowSubscriptionCapture();
        var fields = (await borrow.Backend.DiscoverAsync(default)).Fields.Select(f => f.Field).ToImmutableArray();
        _ = borrow.Bind(fields); var before = query.ReadInformation(); revoke = true;
        var revoked = await borrow.CollectAsync(fields, permit);
        Assert.False(revoked.Committed);
        Assert.Equal(fields.Select(borrow.Backend.ReadCached).ToArray(), revoked.Readings.ToArray());
        Assert.Same(before, query.ReadInformation()); revoke = false; cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync(fields, new Permit(), cancellation.Token));
        Assert.Same(before, query.ReadInformation());
    }
    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public void Revoke() => active = false;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Modified = new(638600000000000001, TimeSpan.Zero);
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "indexed-owner-" + Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(DirectoryPath, "index.db");
        private readonly NativeFileQueryWorkspace pool = NativeFileQueryTestWorkspaceFactory.Create();
        private readonly IndexedFileDatabase database;
        internal SqliteIndexedFileOwner Owner { get; }
        internal IndexedFileInformationQuery Query { get; }
        private Fixture() { database = new(DatabasePath); Owner = new(database, pool); Query = Owner.CreateInformationQuery("alpha", 10); }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            try
            {
                await using var connection = await fixture.database.OpenConnectionAsync(default);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO software_index_roots(id,software_id,software_name,root_path,root_path_key,root_kind,total_bytes,file_count,last_indexed_utc_ticks)
                    VALUES(1,'recorded','Recorded application',$root,$root,'install',9007199254740993,1,$time);
                    INSERT INTO software_index_entries(id,root_id,relative_path,relative_path_key,file_name,extension,size_bytes,last_write_utc_ticks,attributes)
                    VALUES(1,1,'alpha.txt','ALPHA.TXT','alpha.txt','.txt',9007199254740993,$time,0);
                    """;
                command.Parameters.AddWithValue("$root", fixture.DirectoryPath); command.Parameters.AddWithValue("$time", Modified.UtcTicks);
                await command.ExecuteNonQueryAsync(); return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal Task AddBeta() => Sql("INSERT INTO software_index_entries(id,root_id,relative_path,relative_path_key,file_name,extension,size_bytes,last_write_utc_ticks,attributes) VALUES(2,1,'beta.txt','BETA.TXT','beta.txt','.txt',7,638600000000000002,0);");
        internal async Task Sql(string sql) { await using var connection = await database.OpenConnectionAsync(default); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        public void Dispose()
        {
            Query.Dispose(); Owner.Dispose(); pool.Dispose();
            foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath);
        }
    }
}
