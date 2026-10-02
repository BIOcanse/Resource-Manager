using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// 真实 ETW 会话对固定 100 FPS 测试窗口的帧率。需要提权，并把
/// RESOURCE_MANAGER_PRESENT_GENERATOR 设为 tests/Native/FramePresentGenerator.cpp 编译出的 exe。
/// </summary>
public sealed class PresentFrameEtwLiveSmokeTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_PRESENT_GENERATOR")]
    public void Direct3D11FramesAreMeasuredFromTheDxgiRuntime()
        => MeasureGenerator("dxgi", FramePresentSource.Dxgi);

    [EnvironmentVariableFact("RESOURCE_MANAGER_PRESENT_GENERATOR")]
    public void OpenGlFramesFallBackToTheGraphicsKernel()
        => MeasureGenerator("gl", FramePresentSource.GraphicsKernel);

    // 生产上最后一个订阅释放就停、下一个订阅再开，会话名不变；重开后必须照常采集。
    [EnvironmentVariableFact("RESOURCE_MANAGER_PRESENT_GENERATOR")]
    public void ARestartedSessionKeepsMeasuring()
    {
        Assert.True(TraceEventSession.IsElevated() == true, "Live frame-timing ETW test requires elevation.");
        var logger = new RecordingLogger();
        using var source = new PresentFrameEtwSource(logger);
        foreach (var mode in new[] { "gl", "dxgi" })
        {
            using var lease = source.AcquireSubscription();
            var stream = MeasureOnce(source, mode);
            Assert.True(stream is not null, $"{mode}: generator not measured after restart. Warnings: {string.Join(" | ", logger.Messages)}");
            Assert.InRange(stream.Statistics.AverageFps, 95, 105);
        }
    }

    private static void MeasureGenerator(string mode, FramePresentSource expectedSource)
    {
        Assert.True(TraceEventSession.IsElevated() == true, "Live frame-timing ETW test requires elevation.");
        using var source = new PresentFrameEtwSource(NullLogger<PresentFrameEtwSource>.Instance);
        using var lease = source.AcquireSubscription();
        var stream = MeasureOnce(source, mode);
        Assert.NotNull(stream);
        Assert.Equal(expectedSource, stream.Source);
        Assert.InRange(stream.Statistics.AverageFps, 95, 105);
    }

    private static FrameTimingStream? MeasureOnce(PresentFrameEtwSource source, string mode)
    {
        var generator = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PRESENT_GENERATOR")!;
        Assert.True(File.Exists(generator), $"Present generator not found: {generator}");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        using var child = Process.Start(new ProcessStartInfo(generator, $"{mode} 100 5")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        var startKey = child.StartTime.ToFileTimeUtc();
        Thread.Sleep(TimeSpan.FromSeconds(4));
        var snapshot = source.Read(TimeSpan.FromSeconds(2));
        child.WaitForExit();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Complete);
        var process = snapshot.Processes.SingleOrDefault(candidate => candidate.ProcessId == child.Id);
        if (process is null)
        {
            return null;
        }

        Assert.Equal(startKey, process.ProcessStartKey);
        return Assert.Single(process.Streams);
    }

    private sealed class RecordingLogger : ILogger<PresentFrameEtwSource>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
        }
    }
}
