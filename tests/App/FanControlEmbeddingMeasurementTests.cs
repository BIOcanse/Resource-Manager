using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

/// <summary>Run alone in a fresh test host for a package-backed client/description allocation measurement.</summary>
public sealed class FanControlEmbeddingMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FirstDescribeMeasuresTheEmbeddedClientInsideTheBackendRuntime()
    {
        _ = JsonSerializer.Serialize(new { ok = true });
        await Task.Run(() => { });
        using var process = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var libraryAlreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "FanControlCore.Library");
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var heapBefore = GC.GetTotalMemory(false);
        var watch = Stopwatch.StartNew();
        await using var client = CreateClient();
        var fans = await Task.Run(client.Describe);
        watch.Stop();
        Assert.Single(fans);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var after = process.PrivateMemorySize64;
        var result = JsonSerializer.Serialize(new
        {
            beforePrivateBytes = before, afterPrivateBytes = after, addedPrivateBytes = after - before,
            managedHeapAdded = GC.GetTotalMemory(false) - heapBefore, libraryAlreadyLoaded,
            firstDescribeMs = watch.Elapsed.TotalMilliseconds,
            scope = "actual Resource Manager client and packaged core, fake hardware, fresh isolated test host"
        });
        output.WriteLine(result);
        if (Environment.GetEnvironmentVariable("RM_FAN_CORE_MEMORY_RESULT") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FanControlCoreClient CreateClient()
        => new(new FanTestEnvironment(Path.Combine(Path.GetTempPath(), "rm-fan-measure-" + Guid.NewGuid().ToString("N"))),
            (state, path) => new LibraryFanSession(new FanControlCore.Discovery.FanCore(new ProbeFanBackend(),
                new FanControlCore.FanCoreOptions(state, path, () => "fixture-machine"))));
}
