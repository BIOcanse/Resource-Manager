namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// The real-time kernel session's ETW buffer pool (kernel_etw_limits). ETW keeps it in
// kernel nonpaged memory while the session exists. TraceEvent takes the pool as whole MiB,
// divides it by the buffer size into MinimumBuffers and sets MaximumBuffers to 5/4 of
// that plus 10; ETW raises MinimumBuffers to at least 2 per logical processor.
public sealed record KernelEtwSessionBuffers
{
    public KernelEtwSessionBuffers(int bufferKib, int minimumBuffers)
    {
        if (bufferKib <= 0 || minimumBuffers <= 0 || (long)bufferKib * minimumBuffers % 1024 != 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBuffers),
                $"The kernel ETW buffer pool must be whole MiB: {minimumBuffers} buffers of {bufferKib} KiB.");
        BufferKib = bufferKib;
        MinimumBuffers = minimumBuffers;
    }

    public int BufferKib { get; }
    public int MinimumBuffers { get; }
    public int PoolMib => checked(BufferKib * MinimumBuffers / 1024);
}
