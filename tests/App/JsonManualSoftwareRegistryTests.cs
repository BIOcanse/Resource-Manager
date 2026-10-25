using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class JsonManualSoftwareRegistryTests : IDisposable
{
    [Fact]
    public async Task ChangingClassificationPreservesSourceAndStandaloneIdentities()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var classified = await registry.AddOrUpdateAsync(new("Installed", SoftwareKinds.Game, [], "windows-installed:one"), default);
        var reclassified = await registry.AddOrUpdateAsync(new("Installed", SoftwareKinds.Other, [], classified.Id), default);
        Assert.Equal("windows-installed:one", reclassified.Id);
        var standalone = await registry.AddOrUpdateAsync(new("Portable", SoftwareKinds.Game, [@"D:\Apps\Portable"], null), default);
        var changed = await registry.AddOrUpdateAsync(new("Portable", SoftwareKinds.Other, [@"D:\Apps\Portable"], null), default);
        Assert.Equal(standalone.Id, changed.Id);
        Assert.Equal(2, (await registry.GetAllAsync(default)).Count);
    }

    [Fact]
    public async Task AbsentFileIsNotReopenedAndCreatedFileAndWritesAreObserved()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var exceptions = 0;
        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException or DirectoryNotFoundException
                && args.Exception.Message.Contains(testRoot, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref exceptions);
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            var empty = await registry.GetAllAsync(default);
            for (var i = 0; i < 20; i++) Assert.Same(empty, await registry.GetAllAsync(default));
            Assert.Equal(0, exceptions);
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            var record = new ManualSoftwareRecord("manual:created", "Created", SoftwareKinds.Game, [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(StoragePath, SerializeRecords(record));
            var loaded = await registry.GetAllAsync(default);
            Assert.Equal("Created", Assert.Single(loaded).Name);
            for (var i = 0; i < 20; i++) Assert.Same(loaded, await registry.GetAllAsync(default));
            Assert.True(await registry.RemoveAsync(record.Id, default));
            Assert.Empty(await registry.GetAllAsync(default));
            File.Delete(StoragePath);
            Assert.Empty(await registry.GetAllAsync(default));
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-manual-software-{Guid.NewGuid():N}");
    private string StoragePath => Path.Combine(testRoot, "Config", "manual-software.json");

    [Theory]
    [InlineData("game")]
    [InlineData("high_performance")]
    public async Task VersionedFactsLoadWithoutPresentationFields(string storedKind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var record = new ManualSoftwareRecord("manual:legacy", "Legacy", storedKind, [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(StoragePath, SerializeRecords(record));

        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var loaded = Assert.Single(await registry.GetAllAsync(default));
        Assert.Equal(storedKind, loaded.Kind);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("游戏")]
    [InlineData("Game")]
    [InlineData("HighPerformance")]
    public async Task UnknownStoredKindPublishesEmptyWithoutChangingTheFile(string storedKind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var record = new ManualSoftwareRecord("manual:legacy", "Legacy", storedKind, [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var json = SerializeRecords(record);
        await File.WriteAllTextAsync(StoragePath, json);
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        var empty = await registry.GetAllAsync(default);
        Assert.Empty(empty);
        Assert.Same(empty, await registry.GetAllAsync(default));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(json, await File.ReadAllTextAsync(StoragePath));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("游戏")]
    [InlineData("Game")]
    [InlineData("highperformance")]
    [InlineData("")]
    public async Task RequestsRejectUnknownOrLegacyKinds(string kind)
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddOrUpdateAsync(
            new("New", kind, [], "runtime:app"), default));
        Assert.False(File.Exists(StoragePath));
    }

    [Fact]
    public async Task RuntimeSourceCanBeClassifiedAsGeneralWithoutAKnownRoot()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        var record = await registry.AddOrUpdateAsync(new("Runtime App", SoftwareKinds.Other, [], "runtime:app"), default);
        Assert.Equal("runtime:app", record.SourceSoftwareId);
        Assert.Equal(SoftwareKinds.Other, Assert.Single(await registry.GetAllAsync(default)).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddOrUpdateAsync(
            new("No source", SoftwareKinds.Other, [], null), default));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task CorruptRegistryReadsAreCachedEmptyAndExplicitAddBacksUpBeforeReset(string invalidJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, invalidJson);
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        var empty = await registry.GetAllAsync(default);
        Assert.Empty(empty);
        for (var index = 0; index < 10; index++) Assert.Same(empty, await registry.GetAllAsync(default));
        Assert.Single(logger.Entries);
        Assert.Contains(StoragePath, logger.Entries[0].Message);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(StoragePath));
        Assert.Empty(BackupPaths());

        var added = await registry.AddOrUpdateAsync(new("New game", SoftwareKinds.Game, [], null), default);
        var backupPath = Assert.Single(BackupPaths());
        Assert.Equal(Encoding.UTF8.GetBytes(invalidJson), await File.ReadAllBytesAsync(backupPath));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Explicit mutation") && entry.Message.Contains(backupPath));
        Assert.Equal(added.Id, Assert.Single(await registry.GetAllAsync(default)).Id);
        await registry.AddOrUpdateAsync(new("Second game", SoftwareKinds.Game, [], null), default);
        Assert.Single(BackupPaths());
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    public async Task AddOrUpdateAtomicallyRetainsExistingManualRecords()
    {
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));

        var first = await registry.AddOrUpdateAsync(new("First Game", SoftwareKinds.Game, [], null), default);
        var second = await registry.AddOrUpdateAsync(new("High Performance", SoftwareKinds.HighPerformance, [], null), default);
        var loaded = await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(StoragePath));

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, record => record.Id == first.Id);
        Assert.Contains(loaded, record => record.Id == second.Id);
        Assert.Equal(JsonManualSoftwareRegistry.FormatVersion, persisted.RootElement.GetProperty("formatVersion").GetInt32());
        var persistedRecords = persisted.RootElement.GetProperty("records");
        Assert.Equal(2, persistedRecords.GetArrayLength());
        foreach (var record in persistedRecords.EnumerateArray())
        foreach (var field in new[] { "sources", "message", "displayKind", "state" })
            Assert.False(record.TryGetProperty(field, out _));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StoragePath)!, "manual-software.json.*.tmp"));
    }

    private static string SerializeRecords(params ManualSoftwareRecord[] records)
        => JsonSerializer.Serialize(new { formatVersion = JsonManualSoftwareRegistry.FormatVersion, records }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData("{\"formatVersion\":1,\"records\":[]}")]
    [InlineData("{\"formatVersion\":2,\"records\":[],\"message\":\"legacy\"}")]
    public async Task UnsupportedEnvelopesPublishEmptyAndArePreserved(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, json);
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot));
        Assert.Empty(await registry.GetAllAsync(default));
        Assert.Equal(json, await File.ReadAllTextAsync(StoragePath));
        Assert.Empty(BackupPaths());
    }

    [Fact]
    public async Task HeadWriterFixtureMigratesAllFactsKeepsExactBackupAndDoesNotWriteOnSecondStart()
    {
        // Fixture reproduced from HEAD 586aa2f3's actual writer and 11-field record:
        // git show HEAD:src/Core/Infrastructure/Software/JsonManualSoftwareRegistry.cs
        // Web JSON + WriteIndented, original source/message branches and ID hashing.
        var original = await ReadHeadWriterFixtureAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllBytesAsync(StoragePath, original);
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        var migrated = await registry.GetAllAsync(default);
        using var old = JsonDocument.Parse(original);
        Assert.Equal(old.RootElement.GetArrayLength(), migrated.Count);
        foreach (var input in old.RootElement.EnumerateArray())
        {
            var record = Assert.Single(migrated, record => record.Id == input.GetProperty("id").GetString());
            Assert.Equal(input.GetProperty("name").GetString(), record.Name);
            Assert.Equal(input.GetProperty("kind").GetString(), record.Kind);
            Assert.Equal(input.GetProperty("sourceSoftwareId").GetString(), record.SourceSoftwareId);
            Assert.Equal(input.GetProperty("rootPaths").EnumerateArray().Select(root => root.GetString()).ToArray(), record.RootPaths);
            Assert.Equal(input.GetProperty("createdAt").GetDateTimeOffset().ToString("O"), record.CreatedAt.ToString("O"));
            Assert.Equal(input.GetProperty("updatedAt").GetDateTimeOffset().ToString("O"), record.UpdatedAt.ToString("O"));
        }
        var backupPath = Assert.Single(BackupPaths());
        Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains(backupPath));
        var current = await File.ReadAllBytesAsync(StoragePath);
        using var persisted = JsonDocument.Parse(current);
        Assert.Equal(2, persisted.RootElement.GetProperty("formatVersion").GetInt32());
        foreach (var record in persisted.RootElement.GetProperty("records").EnumerateArray())
        foreach (var field in new[] { "displayKind", "state", "sources", "message" })
            Assert.False(record.TryGetProperty(field, out _));

        var sourceStamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var backupStamp = sourceStamp.AddDays(-1);
        File.SetLastWriteTimeUtc(StoragePath, sourceStamp);
        File.SetLastWriteTimeUtc(backupPath, backupStamp);
        var secondLogger = new RecordingLogger();
        var second = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), secondLogger);
        Assert.Equal(migrated.Count, (await second.GetAllAsync(default)).Count);
        Assert.Empty(secondLogger.Entries);
        Assert.Equal(current, await File.ReadAllBytesAsync(StoragePath));
        Assert.Equal(sourceStamp, File.GetLastWriteTimeUtc(StoragePath));
        Assert.Equal(backupStamp, File.GetLastWriteTimeUtc(backupPath));
        await second.AddOrUpdateAsync(new("Added later", SoftwareKinds.Game, [], null), default);
        Assert.Single(BackupPaths());
        Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
        Assert.Equal(backupStamp, File.GetLastWriteTimeUtc(backupPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StoragePath)!, "manual-software.json.*.tmp"));
    }

    [Fact]
    public async Task LegacyMigrationDoesNotRewriteUserIdentityNamesOrLexicalPaths()
    {
        var legacy = JsonNode.Parse(await ReadHeadWriterFixtureAsync())!.AsArray();
        var first = legacy[0]!.AsObject();
        first["id"] = "manual:user-edited-id";
        first["sourceSoftwareId"] = "runtime:user-source-id";
        first["name"] = "  My exact user name  ";
        first["rootPaths"] = new JsonArray("D:/Mixed/../Kept/", @"%USERPROFILE%\custom");
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, legacy.ToJsonString());
        var record = Assert.Single(await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default), record => record.Id == "manual:user-edited-id");
        Assert.Equal("runtime:user-source-id", record.SourceSoftwareId);
        Assert.Equal("  My exact user name  ", record.Name);
        Assert.Equal(new[] { "D:/Mixed/../Kept/", @"%USERPROFILE%\custom" }, record.RootPaths);
        var reloaded = Assert.Single(await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default), item => item.Id == record.Id);
        Assert.Equal(record.RootPaths, reloaded.RootPaths);
        Assert.Equal(record.SourceSoftwareId, reloaded.SourceSoftwareId);
    }

    [Fact]
    public async Task ExplicitRemoveResetsBadInputWithCollisionSafeBackupWithoutOverwritingOlderBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        const string badInput = "[{bad original";
        var olderBackup = StoragePath + ".oldformatbackup";
        await File.WriteAllTextAsync(olderBackup, "older backup must remain");
        await File.WriteAllTextAsync(StoragePath, badInput);
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        Assert.Empty(await registry.GetAllAsync(default));
        Assert.Equal(badInput, await File.ReadAllTextAsync(StoragePath));
        Assert.False(await registry.RemoveAsync("missing", default));
        var backupPaths = BackupPaths();
        Assert.Equal(2, backupPaths.Length);
        Assert.Equal("older backup must remain", await File.ReadAllTextAsync(olderBackup));
        var newBackup = Assert.Single(backupPaths, path => path != olderBackup);
        Assert.Equal(badInput, await File.ReadAllTextAsync(newBackup));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Explicit mutation") && entry.Message.Contains(newBackup));
        var afterReset = await File.ReadAllBytesAsync(StoragePath);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StoragePath, stamp);
        Assert.False(await registry.RemoveAsync("missing", default));
        Assert.Empty(await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StoragePath));
        Assert.Equal(afterReset, await File.ReadAllBytesAsync(StoragePath));
        Assert.Equal(2, BackupPaths().Length);
    }

    [Fact]
    public async Task EmptyLegacyArrayMigratesOnceRatherThanBeingTreatedAsUnreadable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, "[]");
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        Assert.Empty(await registry.GetAllAsync(default));
        Assert.Equal("[]", await File.ReadAllTextAsync(Assert.Single(BackupPaths())));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task UnconvertibleLegacyRecordDoesNotPublishOrRewriteOnlyTheValidPrefix()
    {
        var legacy = JsonNode.Parse(await ReadHeadWriterFixtureAsync())!.AsArray();
        legacy[1]!["kind"] = "unknown-kind";
        var original = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllBytesAsync(StoragePath, original);
        var logger = new RecordingLogger();
        var registry = new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot), logger);
        var empty = await registry.GetAllAsync(default);
        Assert.Empty(empty);
        Assert.Same(empty, await registry.GetAllAsync(default));
        Assert.Equal(original, await File.ReadAllBytesAsync(StoragePath));
        Assert.Empty(BackupPaths());
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("Game", "game")]
    [InlineData("HighPerformance", "high_performance")]
    public async Task LegacyStoredKindAliasesRetainTheirClassificationMeaning(string oldKind, string currentKind)
    {
        var legacy = JsonNode.Parse(await ReadHeadWriterFixtureAsync())!.AsArray();
        legacy[0]!["kind"] = oldKind;
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        await File.WriteAllTextAsync(StoragePath, legacy.ToJsonString());
        var records = await new JsonManualSoftwareRegistry(new TestHostEnvironment(testRoot)).GetAllAsync(default);
        Assert.Equal(currentKind, records[0].Kind);
    }

    private string[] BackupPaths() => Directory.GetFiles(Path.GetDirectoryName(StoragePath)!, "manual-software.json.oldformatbackup*");

    private static async Task<byte[]> ReadHeadWriterFixtureAsync()
    {
        var assembly = typeof(JsonManualSoftwareRegistryTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith("manual-software-head-unversioned.json", StringComparison.Ordinal));
        await using var source = assembly.GetManifestResourceStream(name)!;
        using var output = new MemoryStream();
        await source.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class RecordingLogger : ILogger<JsonManualSoftwareRegistry>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
