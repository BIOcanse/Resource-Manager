using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Files.Indexing;

internal static class IndexedFileOwnerConsumer
{
    // Actual packaged SQLite + original native file-query pool. Recorded index rows,
    // no product database, filesystem scan, MFT/USN or native test hooks.
    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), "indexed-consumer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "index.db");
        try
        {
            var database = new IndexedFileDatabase(path);
            var configuration = Configuration();
            using var pool = new NativeFileQueryPool(in configuration, 1, 12UL * 1024 * 1024);
            using var owner = new SqliteIndexedFileOwner(database, pool);
            await using (var connection = await database.OpenConnectionAsync(timeout.Token))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO software_index_roots(id,software_id,software_name,root_path,root_path_key,root_kind,total_bytes,file_count,last_indexed_utc_ticks)
                    VALUES(1,'recorded','Recorded software',$root,$root,'install',9007199254741000,2,$time);
                    INSERT INTO software_index_entries(id,root_id,relative_path,relative_path_key,file_name,extension,size_bytes,last_write_utc_ticks,attributes)
                    VALUES(1,1,'alpha.txt','ALPHA.TXT','alpha.txt','.txt',9007199254740993,$time,0);
                    INSERT INTO software_index_entries(id,root_id,relative_path,relative_path_key,file_name,extension,size_bytes,last_write_utc_ticks,attributes)
                    VALUES(2,1,'beta.txt','BETA.TXT','beta.txt','.txt',7,$time,0);
                    """;
                command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$time", 638600000000000001L);
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
            using var query = owner.CreateInformationQuery("alpha", 10);
            var backend = query.Backend;
            var catalog = await backend.DiscoverAsync(timeout.Token);
            Require(catalog.Fields.Length == 3 && catalog.Fields.All(f => f.Read.Supported && !f.Write.Supported), "three actual indexed fields");
            var fields = catalog.Fields.Select(f => f.Field).ToImmutableArray();
            var values = await backend.CollectAsync(fields, timeout.Token);
            Require(values.All(v => v.Status == FieldObservationStatus.Current), "actual same database selected capture");
            Require(values.Single(v => v.Field.FieldName == "file_all_indexed_size_actual").Value!.Integer == MetricInteger.Parse("9007199254740993"), "exact indexed size");
            Require(values.Single(v => v.Field.FieldName == "file_all_indexed_path_actual").Value!.Text == Path.Combine(root, "alpha.txt"), "indexed path");
            Require(values.Single(v => v.Field.FieldName == "file_all_indexed_modified_time_actual").Value!.Text == new DateTimeOffset(638600000000000001L, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture), "indexed UTC 100ns timestamp");
            using var betaQuery = owner.CreateInformationQuery("beta", 10);
            Require(betaQuery.Backend.Catalog.Fields.IsEmpty, "another query does not inherit alpha membership");
            var betaFields = (await betaQuery.Backend.DiscoverAsync(timeout.Token)).Fields;
            Require(backend.Catalog.Devices.SequenceEqual(catalog.Devices), "alpha directory survives beta discovery");
            var betaBefore = betaQuery.Backend.ReadCached(betaFields[0].Field);
            var alphaPath = fields.Single(f => f.FieldName == "file_all_indexed_path_actual");
            var alphaSize = fields.Single(f => f.FieldName == "file_all_indexed_size_actual");
            var alphaPathBefore = backend.ReadCached(alphaPath);
            _ = await backend.CollectAsync([alphaSize], timeout.Token);
            Require(backend.ReadCached(alphaPath) == alphaPathBefore, "unselected alpha value and timestamp preserved");
            Require(betaQuery.Backend.ReadCached(betaFields[0].Field) == betaBefore, "other query current preserved");
            using var deniedQuery = owner.CreateInformationQuery("alpha", 10, (_, _) => false);
            Require((await deniedQuery.Backend.DiscoverAsync(timeout.Token)).Fields.IsEmpty, "separate denied query never sees alpha values");
            using var borrow = query.BorrowSubscriptionCapture();
            _ = borrow.Bind([alphaSize]);
            Require(ReferenceEquals(query.Publications, borrow.Publications), "direct and subscription share query publications");
            Require((await owner.SearchAsync("alpha", 10, timeout.Token)).Count == 1, "original native lease reset and reuse");
            Require(typeof(NativeFileQueryPool).Assembly == typeof(SqliteIndexedFileOwner).Assembly, "real SDK resource owner");
            Console.WriteLine("Indexed file owner actual SQLite/native canonical consumer passed: 3 fields; recorded indexed rows, no MFT/USN/live scan.");
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
    private static NativeFileQueryConfiguration Configuration() => new()
    {
        AbiVersion = NativeFileQueryAbi.Version, StructSize = (uint)Marshal.SizeOf<NativeFileQueryConfiguration>(), Generation = 1,
        MaximumQueryUtf8ByteCount = 4096, MaximumQueryRuneCount = 1024, MaximumPlanUtf8ByteCount = 131072,
        MaximumSourcePlanCount = 3, MaximumCandidateCountPerSource = 1000, MaximumSubmittedCandidateCount = 3000,
        MaximumUniqueCandidateCount = 3000, MaximumCandidateSubmitBatchCount = 128, MaximumCandidateSubmitUtf8ByteCount = 1024 * 1024,
        CandidateTextArenaByteCount = 8 * 1024 * 1024, MaximumFileNameUtf8ByteCount = 1024, MaximumResultCount = 200,
        EntryIndexCapacity = 4096, OrdinalIndexCapacity = 4096, ShortQueryRuneThreshold = 3,
        UnicodeTokenizerVersion = NativeFileQueryAbi.UnicodeTokenizerVersion, UnicodeRemoveDiacriticsMode = NativeFileQueryAbi.UnicodeRemoveDiacriticsMode,
        TrigramTokenizerContractVersion = NativeFileQueryAbi.TrigramTokenizerContractVersion, TextMatchingVersion = NativeFileQueryAbi.TextMatchingVersion,
        CandidateLimitMultiplier = 8, CandidateLimitFloor = 128, CandidateLimitCeiling = 1000,
        FileNamePriority = 1, RelativePathPriority = 2, SoftwareNamePriority = 3, ResidentByteBudget = 12UL * 1024 * 1024
    };
    private static void Require(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }
}
