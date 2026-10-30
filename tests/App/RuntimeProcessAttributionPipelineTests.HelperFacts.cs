using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class RuntimeProcessAttributionPipelineTests
{
    private static RuntimeProcessIdentity[] HelperFactsProcesses() => [
        new(53000, null, "host", @"D:\Apps\Host\host.exe", false, StartKey: 10),
        new(53001, 53000, "tool", @"D:\Apps\Other\tool.exe", false, StartKey: 20, CommandLine: "tool --flag")];

    [Fact]
    public void CachedHelperFactsAreUsedWithoutMatchingTheCommandLineAgain()
    {
        using var pipeline = CreatePipeline([CreateSoftware("installed:host", "Host", [@"D:\Apps\Host"])]);
        var processes = HelperFactsProcesses();
        var read = RuntimeProcessAttributionPipeline.MatchBatch(processes, _ => pipeline);
        // The child's command line names no helper type, so a fresh read never puts it under the host.
        Assert.False(read.HelperFactsByProcessId[53001].IsHelper);
        Assert.False(read.HelperOwnerByProcessId.ContainsKey(53001));

        // A cached entry for the same instance and inputs that says otherwise is what the chain follows, which
        // only holds when the regex is not run for it again.
        var cached = read.HelperFactsByProcessId[53001] with { IsHelper = true };
        var reused = RuntimeProcessAttributionPipeline.MatchBatch(processes, _ => pipeline,
            cachedHelperFacts: new Dictionary<int, RuntimeProcessHelperFacts> { [53001] = cached });
        Assert.Same(cached, reused.HelperFactsByProcessId[53001]);
        Assert.Equal(53000, reused.HelperOwnerByProcessId[53001]);
        Assert.Equal("installed:host", reused.SoftwareByProcessId[53001].Id);
    }

    [Fact]
    public void CachedHelperFactsOfAnotherInstanceOrCommandLineAreReadAgain()
    {
        using var pipeline = CreatePipeline([CreateSoftware("installed:host", "Host", [@"D:\Apps\Host"])]);
        var processes = HelperFactsProcesses();
        var facts = RuntimeProcessAttributionPipeline.ReadHelperFacts(processes[1]) with { IsHelper = true };
        foreach (var stale in new[] { facts with { StartKey = 21 }, facts with { CommandLine = "tool --other" } })
        {
            var batch = RuntimeProcessAttributionPipeline.MatchBatch(processes, _ => pipeline,
                cachedHelperFacts: new Dictionary<int, RuntimeProcessHelperFacts> { [53001] = stale });
            Assert.NotSame(stale, batch.HelperFactsByProcessId[53001]);
            Assert.False(batch.HelperFactsByProcessId[53001].IsHelper);
            Assert.False(batch.HelperOwnerByProcessId.ContainsKey(53001));
        }
    }

    [Fact]
    public void HelperFactsStayInTheInstanceEntryAcrossIdentityAndDirectMatchUpdates()
    {
        var cache = new ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution>();
        var process = HelperFactsProcesses()[1];
        WindowsResourceBreakdownSampler.CacheRuntimeIdentity(cache, process);
        var facts = RuntimeProcessAttributionPipeline.ReadHelperFacts(process);
        WindowsResourceBreakdownSampler.CacheHelperFacts(cache, process, facts);
        Assert.Same(facts, WindowsResourceBreakdownSampler.TryGetCachedHelperFacts(cache, process));
        // A changed window title rewrites the identity; the facts stay and are still checked against it on use.
        var retitled = process with { WindowTitle = "title" };
        WindowsResourceBreakdownSampler.CacheRuntimeIdentity(cache, retitled);
        Assert.Same(facts, WindowsResourceBreakdownSampler.TryGetCachedHelperFacts(cache, retitled));
        Assert.True(facts.Describes(retitled));
        Assert.True(cache.TryGet(new ProcessInstanceKey(process.ProcessId, process.StartKey!.Value), out var entry));
        Assert.Same(retitled, entry!.Identity);
    }
}
