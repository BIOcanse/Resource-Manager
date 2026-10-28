using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ClearHistoryPreservesActiveItemsAndPublishesAfterCommitEvenWhenStopped(bool persistent, bool stopped)
    {
        using var files = new HistoryFiles();
        KernelLeakSuspect active;
        using (var fixture = new Fixture(rawSamples: true, packageRoot: files.Root, mutablePersistence: persistent))
        {
            await CreateMixedHistory(fixture);
            active = fixture.Reports.Read().Items.Single(item => item.State == KernelLeakItemState.Active);
            if (stopped) await fixture.StopAsync();
            var before = fixture.Reports.Read();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            var observer = Assert.IsType<KernelLeakObserver>(fixture.Reports);
            Assert.Equal(1, observer.ClearHistory());
            if (persistent)
            {
                using var input = WindowsNativeAtomicFileCommitter.OpenCommittedRead(files.Path);
                using var committed = JsonDocument.Parse(input);
                var item = Assert.Single(committed.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("active", item.GetProperty("state").GetString());
                Assert.Equal(8, item.GetProperty("holder").GetProperty("processId").GetInt32());
            }
            else Assert.False(File.Exists(files.Path));
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(active, Assert.Single(reader.Current.Items));
            Assert.Equal(before with { Items = reader.Current.Items }, reader.Current);
            Assert.Equal(0, observer.ClearHistory());
            Assert.Equal(active, Assert.Single(observer.Read().Items));
        }
        using var restarted = new Fixture(packageRoot: files.Root, mutablePersistence: persistent, options: Fast with { Enabled = false });
        if (persistent)
            Assert.Equal(JsonSerializer.Serialize(active), JsonSerializer.Serialize(Assert.Single(restarted.Reports.Read().Items)));
        else Assert.Empty(restarted.Reports.Read().Items);
    }

    [Fact]
    public async Task FailedClearCommitLeavesPersistedAndPublishedItemsUntouched()
    {
        using var files = new HistoryFiles();
        using var fixture = new Fixture(rawSamples: true, packageRoot: files.Root, mutablePersistence: true);
        await CreateMixedHistory(fixture);
        var before = fixture.Reports.Read();
        var bytes = File.ReadAllBytes(files.Path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var next = reader.MoveNextAsync().AsTask();
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<IOException>(() => Assert.IsType<KernelLeakObserver>(fixture.Reports).ClearHistory());
            Assert.Same(before, fixture.Reports.Read());
            Assert.Equal(bytes, File.ReadAllBytes(files.Path));
            Assert.False(next.IsCompleted);
        }
        finally { File.SetAttributes(files.Path, FileAttributes.Normal); }
        Assert.Equal(1, Assert.IsType<KernelLeakObserver>(fixture.Reports).ClearHistory());
        Assert.True(await next);
        Assert.Single(reader.Current.Items);
    }

    [Fact]
    public async Task ClearHistoryFreesCapacityForNextJudgmentAndDeletedIdentityCanBeRecordedAgain()
    {
        using var fixture = new Fixture(rawSamples: true, options: Fast with { ReportedItemCapacity = 1 });
        for (var second = 0; second < 3; second++)
            await PushFrame(fixture, second, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + second * 100))]);
        for (var second = 3; second < 6; second++)
            await PushFrame(fixture, second, true, [new(8, "43", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + second * 100))]);
        Assert.Equal(1, fixture.Reports.Read().UnrecordedItemCount);
        Assert.Equal(KernelLeakItemState.History, Assert.Single(fixture.Reports.Read().Items).State);
        Assert.Equal(1, Assert.IsType<KernelLeakObserver>(fixture.Reports).ClearHistory());
        await PushFrame(fixture, 6, true, [new(8, "43", KernelLeakSampleKind.ProcessHandles, 1600)]);
        Assert.Equal(0, fixture.Reports.Read().UnrecordedItemCount);
        Assert.Equal(8, Assert.Single(fixture.Reports.Read().Items).Holder.ProcessId);
        Assert.Equal(KernelLeakReportStatus.Suspected, fixture.Reports.Read().Status);
        await PushFrame(fixture, 7, true, []);
        Assert.Equal(1, Assert.IsType<KernelLeakObserver>(fixture.Reports).ClearHistory());
        for (var second = 8; second < 11; second++)
            await PushFrame(fixture, second, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + second * 100))]);
        var recreated = Assert.Single(fixture.Reports.Read().Items);
        Assert.Equal(7, recreated.Holder.ProcessId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(10), recreated.FirstObservedAt);
    }

    [Fact]
    public void RestoreAcceptsMoreThanOldCapacityAndClearWorksBeforeBlockRuns()
    {
        using var files = new HistoryFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Path)!);
        var items = Enumerable.Range(1, 4097).Select(pid => new KernelLeakSuspect(KernelLeakResourceKind.ProcessHandles,
            1200, 100, 2, 3, 200, KernelLeakHolder.Unknown(pid, "42"))
        {
            State = KernelLeakItemState.History,
            FirstObservedAt = DateTimeOffset.UnixEpoch,
            LastObservedAt = DateTimeOffset.UnixEpoch
        }).ToArray();
        File.WriteAllText(files.Path, JsonSerializer.Serialize(new { version = 1, items }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var fixture = new Fixture(packageRoot: files.Root, mutablePersistence: true, options: Fast with { Enabled = false });
        Assert.Equal(4097, fixture.Reports.Read().Items.Count);
        Assert.Equal(4097, Assert.IsType<KernelLeakObserver>(fixture.Reports).ClearHistory());
        Assert.Empty(fixture.Reports.Read().Items);
        Assert.Equal(0, fixture.Processes.Starts);
        using var document = JsonDocument.Parse(File.ReadAllText(files.Path));
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearHistoryEndpointUsesSessionAndProductionReplyMetadata(bool allowed)
    {
        using var fixture = new Fixture(rawSamples: true);
        await CreateMixedHistory(fixture);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, allowed ? AccessKeyScopes.Session : AccessKeyScopes.ProcessesRead);
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(Assert.IsType<KernelLeakObserver>(fixture.Reports));
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        app.MapKernelLeakEndpoints();
        await app.StartAsync();
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var response = await client.PostAsync("/api/kernel-leaks/history/clear", null);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(document.RootElement.EnumerateObject());
            Assert.Equal(1, document.RootElement.GetProperty("removedCount").GetInt32());
            Assert.Equal(KernelLeakItemState.Active, Assert.Single(fixture.Reports.Read().Items).State);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-scope-required", document.RootElement.GetProperty("error").GetString());
            Assert.Equal([AccessKeyScopes.Session], document.RootElement.GetProperty("missingScopes").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(2, fixture.Reports.Read().Items.Count);
        }
    }

    private static async Task CreateMixedHistory(Fixture fixture)
    {
        for (var second = 0; second < 3; second++)
            await PushFrame(fixture, second, true,
                [new(7, "42", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + second * 100)),
                 new(8, "43", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + second * 100))]);
        await PushFrame(fixture, 3, true, [new(8, "43", KernelLeakSampleKind.ProcessHandles, 1300)]);
        Assert.Equal(2, fixture.Reports.Read().Items.Count);
    }
}
