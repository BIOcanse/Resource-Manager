namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// One real-time ETW session's buffer pool: the kernel session (kernel_etw_limits) and the
// VidMm, GPU allocation and Present user sessions (monitoring *_session_buffers). ETW keeps
// it in kernel nonpaged memory while the session exists. TraceEvent takes the pool as whole
// MiB, divides it by the buffer size into MinimumBuffers and sets MaximumBuffers to 5/4 of
// that plus 10; it raises the buffer size to at least 1 KiB per logical processor, and ETW
// raises MinimumBuffers to at least 2 per logical processor.
public sealed record EtwSessionBuffers
{
    public EtwSessionBuffers(int bufferKib, int minimumBuffers)
    {
        if (bufferKib <= 0 || minimumBuffers <= 0 || (long)bufferKib * minimumBuffers % 1024 != 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBuffers),
                $"An ETW session buffer pool must be whole MiB: {minimumBuffers} buffers of {bufferKib} KiB.");
        BufferKib = bufferKib;
        MinimumBuffers = minimumBuffers;
    }

    public int BufferKib { get; }
    public int MinimumBuffers { get; }
    public int PoolMib => checked(BufferKib * MinimumBuffers / 1024);
}
