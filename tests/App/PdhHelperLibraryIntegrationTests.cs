using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class PdhHelperLibraryIntegrationTests
{
    [Fact]
    public void OriginalReadersAndTransportAreOwnedByReadWriteLibrary()
    {
        Assert.All(new[] { typeof(NativePdhSession), typeof(NativePdhApi), typeof(NativePdhSnapshot),
            typeof(NativePdhGpuUsage), typeof(PdhGpuEngineUsageReader), typeof(PdhSystemIoReader),
            typeof(PdhCpuPerformanceReader), typeof(HardwareBridgeProcessClient), typeof(HelperErrorOutput) },
            type => Assert.Same(typeof(ReadWriteCore).Assembly, type.Assembly));
    }

    [Fact]
    public async Task HelperOptionsAreExplicitAndDoNotStartProcessOnConstructionOrPathQuery()
    {
        var starts = 0; var paths = 0;
        using var helper = new HardwareBridgeProcessClient(() => { paths++; return null; },
            new(TimeSpan.FromSeconds(2), TimeSpan.Zero, new(16, 32, 64, "runtime", "version", "missing", "failed")),
            startFactory: _ => { starts++; throw new InvalidOperationException(); });
        Assert.Equal(0, paths); Assert.False(helper.IsInstalled); Assert.Equal(1, paths);
        Assert.Null(await helper.SendAsync("recorded", null, default));
        Assert.Equal(0, starts); Assert.Equal(2, paths);
    }

    [Fact]
    public async Task DiagnosticLimitsAndRuntimeIdentificationComeFromCaller()
    {
        var output = new HelperErrorOutput(new StringReader("sample-runtime v3\n" + new string('x', 100000)),
            new(8, 32, 64, "sample-runtime", "v3", "caller-missing", "caller-failed"));
        await output.Completion;
        Assert.Equal("caller-missing", output.FailureReason);
        Assert.InRange(output.Detail.Length, 1, 97);
    }
}
