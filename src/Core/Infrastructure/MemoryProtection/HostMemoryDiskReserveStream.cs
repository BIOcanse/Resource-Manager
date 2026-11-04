namespace ResourceManager.App.Infrastructure.MemoryProtection;

/// <summary>A seekable dump stream whose allocation growth first releases owned reserve clusters.</summary>
internal sealed class HostMemoryDiskReserveStream(
    FileStream file, string path, ulong cluster, object sync, Action<ulong> charge, Action ensureLease) : Stream
{
    private ulong _chargedAllocation;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length { get { lock (sync) { EnsureOpen(); return file.Length; } } }
    public override long Position
    {
        get { lock (sync) { EnsureOpen(); return file.Position; } }
        set { lock (sync) { EnsureOpen(); file.Position = value; } }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ensureLease();
    }

    private void Fund(long end)
    {
        var required = HostMemoryDiskReserve.RoundUp(checked((ulong)end), cluster);
        if (required <= _chargedAllocation) return;
        charge(required - _chargedAllocation);
        // The charge is irrevocable even if this allocation or the following write fails.
        _chargedAllocation = required;
        HostMemoryDiskReserveNative.Allocate(file, checked((long)required));
    }

    private void VerifyAllocation()
    {
        if (HostMemoryDiskReserveNative.Verify(file, path) > _chargedAllocation)
            throw new IOException("memory-slice-allocation-exceeds-reserve-charge");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range.");
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (sync)
        {
            EnsureOpen();
            if (buffer.Length == 0) return;
            var end = checked(file.Position + buffer.Length);
            Fund(end);
            // Grow EOF inside the explicitly allocated range before writing, avoiding the normal
            // filesystem growth heuristic allocating more data clusters than the charged range.
            if (end > file.Length) file.SetLength(end);
            file.Write(buffer);
            VerifyAllocation();
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        lock (sync)
        {
            EnsureOpen();
            Fund(value);
            file.SetLength(value);
            // Truncation does not refund the reserve. Retain the already paid allocation so a
            // later overwrite/regrowth can reuse it without depending on new free space.
            HostMemoryDiskReserveNative.Allocate(file, checked((long)_chargedAllocation));
            VerifyAllocation();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    { lock (sync) { EnsureOpen(); return file.Read(buffer, offset, count); } }

    public override int Read(Span<byte> buffer)
    { lock (sync) { EnsureOpen(); return file.Read(buffer); } }

    public override long Seek(long offset, SeekOrigin origin)
    { lock (sync) { EnsureOpen(); return file.Seek(offset, origin); } }

    public override void Flush()
    { lock (sync) { EnsureOpen(); file.Flush(true); } }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        lock (sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing) file.Dispose();
        }
        base.Dispose(disposing);
    }
}
