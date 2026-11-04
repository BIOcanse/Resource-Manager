using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

// Logical offsets span entries in user order. All segments before the last occupy whole clusters.
internal sealed class HostMemorySegmentedSliceStream(IHostMemoryProtectionReserveLease[] entries, string fileName) : Stream
{
    private readonly ulong[] capacities = entries.Select(entry => entry.RemainingBytes).ToArray();
    private readonly Stream?[] segments = new Stream?[entries.Length];
    private long length;
    private long position;
    private bool disposed;
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => !disposed;
    public override long Length { get { EnsureOpen(); return length; } }
    public override long Position
    {
        get { EnsureOpen(); return position; }
        set { EnsureOpen(); ArgumentOutOfRangeException.ThrowIfNegative(value); position = value; }
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    private (int Index, long Offset) Locate(long offset)
    {
        var remaining = (ulong)offset;
        for (var index = 0; index < capacities.Length; index++)
        {
            if (remaining < capacities[index]) return (index, (long)remaining);
            remaining -= capacities[index];
        }
        throw new IOException("memory-slice-exceeds-remaining-reserve");
    }
    private Stream Open(int index)
        => segments[index] ??= entries[index].CreateSlice(index == 0 ? fileName : fileName + $".part{index + 1:D4}");

    public override void SetLength(long value)
    {
        EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var remaining = (ulong)value;
        ulong fulfilled = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            var desired = Math.Min(remaining, capacities[index]);
            if (desired > 0 || segments[index] is not null)
            {
                Open(index).SetLength(checked((long)desired));
                fulfilled += desired;
                length = Math.Max(length, checked((long)fulfilled));
            }
            remaining -= desired;
        }
        if (remaining > 0) throw new IOException("memory-slice-exceeds-remaining-reserve");
        length = value;
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureOpen();
        if (buffer.Length == 0) return;
        var end = checked(position + buffer.Length);
        if (end > length) SetLength(end);
        while (buffer.Length > 0)
        {
            var part = Locate(position);
            var count = (int)Math.Min((ulong)buffer.Length, capacities[part.Index] - (ulong)part.Offset);
            var stream = Open(part.Index);
            stream.Position = part.Offset;
            stream.Write(buffer[..count]);
            position += count;
            buffer = buffer[count..];
        }
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        var read = 0;
        while (buffer.Length > 0 && position < length)
        {
            var part = Locate(position);
            var count = (int)Math.Min((ulong)Math.Min(buffer.Length, length - position), capacities[part.Index] - (ulong)part.Offset);
            var stream = Open(part.Index);
            stream.Position = part.Offset;
            var actual = stream.Read(buffer[..count]);
            if (actual == 0) break;
            read += actual;
            position += actual;
            buffer = buffer[actual..];
        }
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return position;
    }
    public override void Flush() { EnsureOpen(); foreach (var stream in segments) stream?.Flush(); }
    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        if (disposing) foreach (var stream in segments) stream?.Dispose();
        base.Dispose(disposing);
    }
}
