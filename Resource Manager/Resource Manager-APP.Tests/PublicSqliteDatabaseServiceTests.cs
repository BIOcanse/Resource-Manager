using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.PublicServices.Sqlite;
using ResourceManager.App.Infrastructure.PublicServices.Sqlite;

namespace Resource_Manager_APP.Tests;

public sealed class PublicSqliteDatabaseServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-public-sqlite-{Guid.NewGuid():N}");

    [Fact]
    public async Task ManagedDatabase_SupportsParametersBlobsFtsAndTransactions()
    {
        var service = CreateService();
        var database = await service.CreateAsync("sample.client", CancellationToken.None);
        Assert.Equal("sample.client", database.DatabaseId);

        await service.ExecuteAsync(
            database.DatabaseId,
            Request("CREATE TABLE item(id INTEGER PRIMARY KEY, name TEXT NOT NULL, payload BLOB);"),
            CancellationToken.None);
        await service.ExecuteAsync(
            database.DatabaseId,
            Request(
                "INSERT INTO item(name, payload) VALUES($name, $payload);",
                new Dictionary<string, JsonElement>
                {
                    ["name"] = Json("\"alpha\""),
                    ["payload"] = Json("{\"type\":\"blob\",\"base64\":\"AQID\"}")
                }),
            CancellationToken.None);

        var query = await service.QueryAsync(
            database.DatabaseId,
            Request("SELECT id, name, payload FROM item;"),
            CancellationToken.None);
        var row = Assert.Single(query.Rows);
        Assert.Equal("integer", row[0].Type);
        Assert.Equal("alpha", row[1].Value);
        Assert.Equal("AQID", row[2].Value);

        await service.ExecuteAsync(
            database.DatabaseId,
            Request("CREATE VIRTUAL TABLE item_search USING fts5(name); INSERT INTO item_search(name) VALUES('telemetry cache');"),
            CancellationToken.None);
        var fts = await service.QueryAsync(
            database.DatabaseId,
            Request("SELECT name FROM item_search WHERE item_search MATCH 'telemetry';"),
            CancellationToken.None);
        Assert.Equal("telemetry cache", Assert.Single(Assert.Single(fts.Rows)).Value);
    }

    [Fact]
    public async Task FailedBatch_RollsBackAndAuthorizerBlocksExternalFiles()
    {
        var service = CreateService();
        await service.CreateAsync("rollback-test", CancellationToken.None);
        await service.ExecuteAsync(
            "rollback-test",
            Request("CREATE TABLE item(value TEXT);"),
            CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => service.BatchAsync(
            "rollback-test",
            new PublicSqliteBatchRequest(
                "immediate",
                [
                    new PublicSqliteBatchCommand("execute", "INSERT INTO item(value) VALUES('should rollback');", null, null),
                    new PublicSqliteBatchCommand("execute", "THIS IS NOT SQL", null, null)
                ]),
            CancellationToken.None));
        var count = await service.QueryAsync(
            "rollback-test",
            Request("SELECT COUNT(*) FROM item;"),
            CancellationToken.None);
        Assert.Equal(0L, Assert.Single(Assert.Single(count.Rows)).Value);

        var externalPath = Path.Combine(testRoot, "outside.db");
        await Assert.ThrowsAsync<SqliteException>(() => service.ExecuteAsync(
            "rollback-test",
            Request($"ATTACH DATABASE '{externalPath.Replace("'", "''")}' AS outside;"),
            CancellationToken.None));
        Assert.False(File.Exists(externalPath));

        var vacuumPath = Path.Combine(testRoot, "vacuum-outside.db");
        await Assert.ThrowsAsync<SqliteException>(() => service.ExecuteAsync(
            "rollback-test",
            Request($"VACUUM INTO '{vacuumPath.Replace("'", "''")}';"),
            CancellationToken.None));
        Assert.False(File.Exists(vacuumPath));
    }

    [Fact]
    public async Task DatabaseId_CannotEscapeManagedRoot()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync("../outside", CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException)
        {
        }
    }

    private PublicSqliteDatabaseService CreateService()
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new PublicSqliteDatabaseService(new TestHostEnvironment(appRoot));
    }

    private static PublicSqliteCommandRequest Request(
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters = null) =>
        new(sql, parameters, MaxRows: null);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
