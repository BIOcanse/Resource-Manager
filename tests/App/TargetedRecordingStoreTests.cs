using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"rm-targeted-{Guid.NewGuid():N}");

    [Fact]
    public async Task MigrationStoresIntervalsResourcesAndReusableStatisticsWithExports()
    {
        var database = CreateDatabase();
        var store = new TargetedRecordingStore(database);
        var started = DateTimeOffset.UtcNow;
        var header = Header("one", started) with { SoftwareName = "Example, \"Game\"" };
        await store.CreateAsync(header, CancellationToken.None);
        var intervals = new List<FrameIntervalSample>();
        var time = started;
        for (var index = 0; index < 100; index++)
        {
            var duration = index == 99 ? 100d : 10d;
            time = time.AddMilliseconds(duration);
            intervals.Add(new FrameIntervalSample(101, 9876, FramePresentSource.Dxgi, 1, time, duration));
        }
        var resource = new TargetedResourceSample(time,
            new Dictionary<string, double?> { ["cpu.usage"] = 40, ["gpu.0.vram"] = null },
            new Dictionary<string, double?> { ["cpu.temperature"] = 55 },
            [new TargetedProcessIdentity(101, 9876)]);
        await store.AppendAsync(header.Id, intervals, resource, CancellationToken.None);
        await store.FinishAsync(header.Id, "user", incomplete: false, CancellationToken.None);

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA user_version;";
        Assert.Equal(12L, Convert.ToInt64(await schema.ExecuteScalarAsync()));
        Assert.Equal(3, await CountTargetedTablesAsync(connection));

        var report = Assert.IsType<TargetedRecordingReport>(await store.GetAsync(header.Id, CancellationToken.None));
        Assert.Equal("completed", report.Recording.Status);
        Assert.Equal(FrameIntervalStatistics.Compute(intervals.Select(static item => item.DurationMs).ToArray()), report.Summary);
        Assert.Equal(100, report.FrameIntervals.Count);
        Assert.Equal(10, report.Summary!.OnePercentLowFps, 6);
        Assert.Equal(10, report.Summary.PointOnePercentLowFps, 6);
        Assert.Equal(40, Assert.Single(report.ResourceSamples).Software["cpu.usage"]);
        Assert.Equal(55, report.ResourceSamples[0].System["cpu.temperature"]);
        var csv = Encoding.UTF8.GetString(TargetedRecordingCsv.Write(report));
        Assert.StartsWith("kind,utc,processId,processStartKey,source,swapChain,frameTimeMs,scope,metric,value", csv);
        Assert.Contains("recording,,,,,,,,softwareName,\"Example, \"\"Game\"\"\"", csv);
        Assert.Contains("summary,,,,,,,,onePercentLowFps,10", csv);
        Assert.Contains("resource,", csv);
        Assert.Contains(",software,cpu.usage,40", csv);
        Assert.Contains(",software,gpu.0.vram,\r\n", csv);

        Assert.True(await store.DeleteAsync(header.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(header.Id, CancellationToken.None));
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM targeted_frame_interval_chunks;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InterruptedRecordingIsRecoveredAndRetentionKeepsLastFiftyCompleted()
    {
        var store = new TargetedRecordingStore(CreateDatabase());
        var started = DateTimeOffset.UtcNow.AddMinutes(-60);
        await store.CreateAsync(Header("interrupted", started), CancellationToken.None);
        await store.RecoverInterruptedAsync(CancellationToken.None);
        var recovered = Assert.IsType<TargetedRecordingReport>(await store.GetAsync("interrupted", CancellationToken.None));
        Assert.True(recovered.Recording.Incomplete);
        Assert.Equal("interrupted", recovered.Recording.StopReason);

        for (var index = 0; index < 51; index++)
        {
            var header = Header($"recording-{index}", started.AddMinutes(index + 1));
            await store.CreateAsync(header, CancellationToken.None);
            await store.FinishAsync(header.Id, "user", false, CancellationToken.None);
        }
        var list = await store.ListAsync(CancellationToken.None);
        Assert.Equal(TargetedRecordingStore.CompletedRetention, list.Count);
        Assert.DoesNotContain(list, item => item.Id is "interrupted" or "recording-0");
        Assert.Contains(list, item => item.Id == "recording-50");
    }

    private ResourceManagerDatabase CreateDatabase()
        => new(new TestHostEnvironment(Path.Combine(root, "Resource Manager-APP")));

    private static TargetedRecordingHeader Header(string id, DateTimeOffset started)
        => new(id, "software", "Example", started, null, 600, "recording", null, false);

    private static async Task<int> CountTargetedTablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'targeted_%';";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
