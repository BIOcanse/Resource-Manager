using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Theory]
    [InlineData(800)] // Below the absolute floor is still a sufficient judgment.
    [InlineData(1200)]
    public async Task SufficientNegativeJudgmentKeepsLastEvidenceAndReactivationReusesIdentity(int settled)
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await fixture.Deliver(reader, 0, 1000);
        await fixture.Deliver(reader, 1, 1100);
        var active = Assert.Single((await fixture.Deliver(reader, 2, 1200)).Items);
        Assert.Equal(KernelLeakItemState.Active, active.State);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), active.FirstObservedAt);
        Assert.Equal(active.FirstObservedAt, active.LastObservedAt);
        var lastSuspected = active;
        for (var second = 3; second <= 6; second++)
        {
            var itemAtFrame = Assert.Single((await fixture.Deliver(reader, second, settled)).Items);
            if (itemAtFrame.State == KernelLeakItemState.Active) lastSuspected = itemAtFrame;
        }
        var history = Assert.Single(reader.Current.Items);
        Assert.Equal(lastSuspected with { State = KernelLeakItemState.History }, history);
        Assert.Equal(KernelLeakReportStatus.Empty, reader.Current.Status);
        for (var second = 7; second <= 10; second++) await fixture.Deliver(reader, second, settled + (second - 6) * 100);
        var reactivated = Assert.Single(reader.Current.Items);
        Assert.Equal(KernelLeakItemState.Active, reactivated.State);
        Assert.Equal(active.FirstObservedAt, reactivated.FirstObservedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(10), reactivated.LastObservedAt);
        Assert.Equal((ulong)(settled + 400), reactivated.CurrentValue);
        using var wire = JsonDocument.Parse(SerializeProduction(reader.Current));
        var item = wire.RootElement.GetProperty("items")[0];
        Assert.Equal("active", item.GetProperty("state").GetString());
        Assert.Equal(reactivated.FirstObservedAt, item.GetProperty("firstObservedAt").GetDateTimeOffset());
        Assert.Equal(0, wire.RootElement.GetProperty("unrecordedItemCount").GetInt32());
    }

    [Fact]
    public async Task ReusedPidMakesOnlyOldInstanceHistoryAndItemsRemainOrdered()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        for (var i = 0; i < 3; i++) await fixture.Deliver(reader, i, 1000 + i * 100);
        var original = Assert.Single(reader.Current.Items);
        for (var i = 3; i < 6; i++) await fixture.Deliver(reader, i, 1000 + i * 100, "43");
        Assert.Equal(2, reader.Current.Items.Count);
        Assert.Equal(original with { State = KernelLeakItemState.History }, reader.Current.Items[0]);
        Assert.Equal("43", reader.Current.Items[1].Holder.StartKey);
        Assert.Equal(KernelLeakItemState.Active, reader.Current.Items[1].State);
        Assert.True(reader.Current.Items[0].FirstObservedAt < reader.Current.Items[1].FirstObservedAt);
    }

    [Theory]
    [InlineData(KernelLeakFailure.SourceUnavailable)]
    [InlineData(KernelLeakFailure.InvalidObservation)]
    [InlineData(KernelLeakFailure.CapacityExceeded)]
    public async Task FailedJudgmentInsufficientSamplesAndStopPreserveEntireItem(KernelLeakFailure failure)
    {
        using var fixture = new Fixture(rawSamples: true);
        for (var i = 0; i < 3; i++) await PushFrame(fixture, i, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + i * 100))]);
        var item = Assert.Single(fixture.Reports.Read().Items);
        var bad = failure == KernelLeakFailure.InvalidObservation
            ? new KernelLeakSample[] { new(7, "42", KernelLeakSampleKind.ProcessHandles, 1), new(7, "42", KernelLeakSampleKind.ProcessHandles, 1) }
            : failure == KernelLeakFailure.CapacityExceeded
                ? Enumerable.Range(1, 9).Select(pid => new KernelLeakSample(pid, "42", KernelLeakSampleKind.ProcessHandles, 1)).ToArray() : [];
        await PushFrame(fixture, 3, failure != KernelLeakFailure.SourceUnavailable, bad);
        Assert.Equal(failure, fixture.Reports.Read().Failure);
        Assert.Equal(item, Assert.Single(fixture.Reports.Read().Items));
        await PushFrame(fixture, 4, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1)]);
        Assert.Equal(KernelLeakReportStatus.Observing, fixture.Reports.Read().Status);
        Assert.Equal(item, Assert.Single(fixture.Reports.Read().Items));
        await fixture.StopAsync();
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        Assert.Equal(item, Assert.Single(fixture.Reports.Read().Items));
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().SeriesBytes);
    }

    [Theory]
    [InlineData("42")]
    [InlineData(null)]
    public async Task UnavailableHolderValueOrIdentityIsNotAProcessExit(string? startKey)
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 3; i++) await DeliverWithoutReader(fixture, i, 1000 + i * 100);
        var item = Assert.Single(fixture.Reports.Read().Items);
        var at = DateTimeOffset.UnixEpoch.AddSeconds(3);
        fixture.Processes.Push(new(at, []) { ProcessFields = new(at, null, [],
            [new(7, startKey, [new(KernelLeakProcessSampleSource.HandleField, null, null)]),
             new(8, "43", [new(KernelLeakProcessSampleSource.HandleField, new ResourceManager.App.Domain.Metrics.MetricInteger(1200), null)])]) });
        await WaitForFrame(fixture, at);
        Assert.Equal(item, Assert.Single(fixture.Reports.Read().Items));
        Assert.Equal(1, fixture.Reports.Read().UnavailableHolderCount);
        Assert.Equal(1, fixture.Reports.ReadDiagnostics().HolderCount);
    }

    [Fact]
    public async Task CapacityCountsUnrecordedWithoutEvictingHistoryAndStillUpdatesExistingIdentity()
    {
        using var fixture = new Fixture(options: Fast with { ReportedItemCapacity = 1 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        for (var i = 0; i < 3; i++) await fixture.Deliver(reader, i, 1000 + i * 100);
        var original = Assert.Single(reader.Current.Items);
        for (var i = 3; i < 6; i++) await fixture.Deliver(reader, i, 1000 + i * 100, "43");
        Assert.Equal(original with { State = KernelLeakItemState.History }, Assert.Single(reader.Current.Items));
        Assert.Equal(1, reader.Current.UnrecordedItemCount);
        Assert.Equal(KernelLeakReportStatus.Empty, reader.Current.Status);
        for (var i = 6; i < 9; i++) await fixture.Deliver(reader, i, 1000 + i * 100);
        Assert.Equal(KernelLeakItemState.Active, Assert.Single(reader.Current.Items).State);
        Assert.Equal(original.FirstObservedAt, reader.Current.Items[0].FirstObservedAt);
        Assert.Equal(0, reader.Current.UnrecordedItemCount);
    }

    [Fact]
    public void ReportedItemCapacityIsCompiledAndBoundsAreRejected()
    {
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, "resource-manager/backend/monitoring/build/complex/0")["value"]!["reported_item_capacity"] = 4096);
        Assert.Equal(4096, changed.KernelLeak!.ReportedItemCapacity);
        Assert.NotEqual(HostManagerTestPlanFactory.CreatePlan().BuildSha256, changed.BuildSha256);
        Assert.Throws<InvalidDataException>(() => (Fast with { ReportedItemCapacity = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fast with { ReportedItemCapacity = 4097 }).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRestoresStateEvidenceAndAttributionBeforeNextJudgment(bool history)
    {
        using var files = new HistoryFiles();
        KernelLeakSuspect saved;
        using (var fixture = new Fixture(packageRoot: files.Root, mutablePersistence: true))
        {
            for (var i = 0; i < 3; i++) await DeliverWithoutReader(fixture, i, 1000 + i * 100);
            if (history) await PushProcessExit(fixture, 3);
            saved = Assert.Single(fixture.Reports.Read().Items);
            // The committed file already matches the published item while observation is running.
            using var committed = JsonDocument.Parse(File.ReadAllText(files.Path));
            Assert.Equal(1, committed.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(history ? "history" : "active", committed.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
        }
        using var restored = new Fixture(packageRoot: files.Root, mutablePersistence: true, options: Fast with { Enabled = false });
        Assert.Equal(KernelLeakReportStatus.NotObserved, restored.Reports.Read().Status);
        var item = Assert.Single(restored.Reports.Read().Items);
        Assert.Equal(SerializeProduction(new(KernelLeakReportStatus.Empty, null, [saved], KernelLeakFailure.None, new(null, 0, 0, 0, 0, null))),
            SerializeProduction(new(KernelLeakReportStatus.Empty, null, [item], KernelLeakFailure.None, new(null, 0, 0, 0, 0, null))));
        Assert.Empty(restored.Plans.Publish(restored.Plan with { Version = 2,
            HostManager = restored.Plan.HostManager with { KernelLeak = Fast }
        }).DeliveryFailures);
        await DeliverWithoutReader(restored, 10, 800);
        Assert.Equal(KernelLeakReportStatus.Observing, restored.Reports.Read().Status);
        Assert.Same(item, Assert.Single(restored.Reports.Read().Items));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"items\":[]}")]
    [InlineData("{\"version\":1,\"items\":[null]}")]
    [InlineData("{\"version\":1,\"items\":[{}]}")]
    public void CorruptFileIsLoggedAndRetainedAsNoFile(string contents)
    {
        using var files = new HistoryFiles();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(files.Path)!);
        File.WriteAllText(files.Path, contents);
        var logger = new HistoryLogger();
        using var fixture = new Fixture(packageRoot: files.Root, mutablePersistence: true, logger: logger, options: Fast with { Enabled = false });
        Assert.Empty(fixture.Reports.Read().Items);
        Assert.Equal(1, logger.Errors);
        Assert.Equal(contents, File.ReadAllText(files.Path));
    }

    [Fact]
    public async Task ReadOnlyProfileKeepsItemsInMemoryWithoutReadingOrWritingFile()
    {
        using var files = new HistoryFiles();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(files.Path)!);
        File.WriteAllText(files.Path, "existing file must remain untouched");
        var logger = new HistoryLogger();
        using (var fixture = new Fixture(packageRoot: files.Root, mutablePersistence: false, logger: logger))
        {
            Assert.Empty(fixture.Reports.Read().Items);
            for (var i = 0; i < 3; i++) await DeliverWithoutReader(fixture, i, 1000 + i * 100);
            Assert.Equal(KernelLeakItemState.Active, Assert.Single(fixture.Reports.Read().Items).State);
            await PushProcessExit(fixture, 3);
            Assert.Equal(KernelLeakItemState.History, Assert.Single(fixture.Reports.Read().Items).State);
            Assert.Equal(0, logger.Errors);
            Assert.Equal("existing file must remain untouched", File.ReadAllText(files.Path));
        }
        using var restarted = new Fixture(packageRoot: files.Root, mutablePersistence: false);
        Assert.Empty(restarted.Reports.Read().Items);
    }

    [Fact]
    public async Task FailedAtomicSaveDoesNotPublishChangedItem()
    {
        using var files = new HistoryFiles();
        using var fixture = new Fixture(packageRoot: files.Root, mutablePersistence: true);
        for (var i = 0; i < 3; i++) await DeliverWithoutReader(fixture, i, 1000 + i * 100);
        var saved = Assert.Single(fixture.Reports.Read().Items);
        var contents = File.ReadAllText(files.Path);
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        try
        {
            await DeliverWithoutReader(fixture, 3, 1300);
            Assert.Equal(KernelLeakReportStatus.Unavailable, fixture.Reports.Read().Status);
            Assert.Equal(saved, Assert.Single(fixture.Reports.Read().Items));
            Assert.Equal(contents, File.ReadAllText(files.Path));
        }
        finally { File.SetAttributes(files.Path, FileAttributes.Normal); }
        await DeliverWithoutReader(fixture, 4, 1400);
        Assert.Equal(saved, Assert.Single(fixture.Reports.Read().Items));
    }

    private static async Task PushProcessExit(Fixture fixture, int second)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(second);
        fixture.Processes.Push(new(at, []) { ProcessFields = new(at, null, [], []) });
        await WaitForFrame(fixture, at);
    }
    private static async Task PushFrame(Fixture fixture, int second, bool available, KernelLeakSample[] samples)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(second);
        fixture.Raw!.Push(new(at, available, samples));
        await WaitForFrame(fixture, at);
    }
    private static async Task WaitForFrame(Fixture fixture, DateTimeOffset at)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (fixture.Reports.Read().CapturedAt != at) await Task.Delay(5, timeout.Token);
    }
    private sealed class KernelLeakTestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class HistoryFiles : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rm-kernel-history-{Guid.NewGuid():N}");
        internal string Path => System.IO.Path.Combine(Root, "UserData", "KernelLeaks", "reported-items.json");
        public void Dispose()
        {
            WindowsNativeAtomicFileCommitter.DeleteExact(Path);
            foreach (var directory in new[] { System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.Combine(Root, "UserData"), Root })
                if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private sealed class HistoryLogger : ILogger<KernelLeakObserver>
    {
        internal int Errors;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Error) Interlocked.Increment(ref Errors); }
    }
}
