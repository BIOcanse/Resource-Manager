using System.Diagnostics;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeBridgeStreamingCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public async Task TenHzStreamUsesOneBoundedBufferAndWaitsForSlowConsumer(int writeDelayMs)
    {
        const int frames = 20;
        const int bytesPerFrame = 4096;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var source = new FrameSource(frames, bytesPerFrame);
        using var destination = new SlowDestination(source, writeDelayMs);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        await FrontendLoopbackProxy.CopyStreamingAsync(source, destination, deadline.Token);
        var elapsed = Stopwatch.GetElapsedTime(start);
        Assert.Equal(frames, destination.Writes);
        Assert.Equal(frames * bytesPerFrame, destination.Bytes);
        Assert.Equal(16 * 1024, source.MaximumBuffer);
        Assert.Equal(1, destination.MaximumAhead);
        Assert.Equal(frames, destination.Flushes);
        output.WriteLine($"10Hz copy, write delay={writeDelayMs}ms: frames={frames}, bytes={destination.Bytes}, elapsed={elapsed.TotalSeconds:F3}s, process allocations={GC.GetTotalAllocatedBytes(precise: true) - allocated} bytes, max producer frames ahead={destination.MaximumAhead}. Includes timer/async allocations; excludes HTTP/TLS/WebView and concurrent process allocations are possible.");
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FrameSource(int frames, int bytesPerFrame) : TestStream
    {
        private readonly long started = Stopwatch.GetTimestamp();
        internal int Reads { get; private set; }
        internal int MaximumBuffer { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            MaximumBuffer = Math.Max(MaximumBuffer, buffer.Length);
            if (Reads == frames) return 0;
            var due = TimeSpan.FromMilliseconds((Reads + 1) * 100) - Stopwatch.GetElapsedTime(started);
            if (due > TimeSpan.Zero) await Task.Delay(due, token);
            buffer.Span[..bytesPerFrame].Fill((byte)'x');
            Reads++;
            return bytesPerFrame;
        }
    }

    private sealed class SlowDestination(FrameSource source, int delay) : TestStream
    {
        internal int Writes { get; private set; }
        internal int Bytes { get; private set; }
        internal int Flushes { get; private set; }
        internal int MaximumAhead { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            MaximumAhead = Math.Max(MaximumAhead, source.Reads - Writes);
            if (delay != 0) await Task.Delay(delay, token);
            Writes++;
            Bytes += buffer.Length;
        }
        public override Task FlushAsync(CancellationToken token) { Flushes++; return Task.CompletedTask; }
    }
}
